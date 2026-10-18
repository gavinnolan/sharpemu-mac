// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.IO.Hashing;
using System.Runtime.InteropServices;
using SharpEmu.Libs.VideoOut;
using SharpEmu.Logging;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;

namespace SharpEmu.Gpu.Metal.V2;

/// <summary>
/// The title's own copy of what the driver compiled, kept in MTLBinaryArchive files under
/// the title's pipeline cache, so a start whose macOS Metal cache no longer holds a shader
/// (another bundle id, another title pushed it out, the cache was cleared) does not run the
/// compilers again.
///
/// A serialized archive is a fat file: an air64 slice, a metallib with the AIR of every
/// function in it, and a slice of GPU binaries for this device. That gives two caches:
///
/// <list type="bullet">
/// <item>Libraries. Each library the host compiles from MSL gets one archive file, named by
/// a hash of its source and compile options and written from the first pipeline made from
/// it. Opened as a library (newLibraryWithURL) its AIR slice stands in for the MSL front end:
/// measured on 159 Demon's Souls compute libraries with the macOS cache cleared, 0.13-0.44 ms
/// a library against 330-540 ms through newLibraryWithSource. A compute library's file also
/// holds its pipeline's binary, and opened as an archive (about 6 ms) it stands in for the
/// back end: 0.12 ms a pipeline against 106-181 ms. The name is the content, so a translator
/// change only misses the libraries whose MSL it changed.</item>
/// <item>Render pipelines. Pipelines no loaded archive had are added to a segment
/// (pipelines-STAMP-SEQ.mtla, a new one every <see cref="SegmentBytes"/>) that is serialized
/// off the GPU worker, every <see cref="FlushInterval"/> and at shutdown. The next start
/// loads the newest segments of the same translator stamp, up to
/// <see cref="ResidentSegmentBytes"/>: an open archive is read into memory whole (33 MB of
/// footprint for a 28 MB file). Pipeline creation asks them first with
/// FailOnBinaryArchiveMiss and compiles only on a miss: 0.2-0.6 ms against 7-21 ms.</item>
/// </list>
///
/// Every file ends in a trailer (magic, format, payload length, XxHash64 of the payload);
/// Metal ignores bytes past the slices, and a file whose trailer does not match is deleted
/// unread: a truncated archive still opens as a library, and a flipped byte goes unnoticed
/// by Metal. A directory written for another device, OS or format is removed at start. The
/// files are bounded to <see cref="DiskBudgetBytes"/>, least recently used first.
/// SHARPEMU_METAL_V2_BINARY_ARCHIVE=0 turns it off.
/// </summary>
internal sealed partial class MetalBinaryArchive : IDisposable
{
    internal const uint FormatVersion = 1;
    internal const string Extension = ".mtla";
    internal const string SegmentPrefix = "pipelines-";
    internal const long DiskBudgetBytes = 1L << 30;
    internal const long SegmentBytes = 8L << 20;
    internal const long ResidentSegmentBytes = 32L << 20;
    internal const int TrailerBytes = 24;
    internal static readonly TimeSpan FlushInterval = TimeSpan.FromSeconds(15);
    private const uint TrailerMagic = 0x41424553; // "SEBA"
    private const int FlushAfterPipelines = 64;

    // A queued capture keeps its descriptor's functions alive; past this many the newest
    // are dropped (their pipelines are stored by a later start).
    private const int MaxQueuedJobs = 256;

    // MTLPipelineOptionFailOnBinaryArchiveMiss.
    internal const nuint FailOnBinaryArchiveMiss = 1 << 2;

    internal static readonly bool Enabled =
        Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_BINARY_ARCHIVE") != "0";

    private static readonly nint SelRetain = MetalNative.Selector("retain");
    private static readonly nint SelRelease = MetalNative.Selector("release");
    private static readonly nint SelAlloc = MetalNative.Selector("alloc");
    private static readonly nint SelInit = MetalNative.Selector("init");

    internal enum PipelineKind : byte
    {
        Compute,
        Render,
        Mesh,
    }

    private enum JobKind : byte
    {
        Capture,
        Flush,
    }

    private readonly record struct Job(
        JobKind Kind, PipelineKind Pipeline, nint Descriptor, UInt128 Key0, UInt128 Key1, bool AddToSegment,
        ManualResetEventSlim? Done);

    private static MetalBinaryArchive? _current;
    private static int _exitHooked;

    private readonly nint _device;
    private readonly string _root;
    private readonly string _directory;
    private readonly string _libraries;
    private readonly string _translatorStamp;
    private readonly long _budget;
    private readonly ConcurrentDictionary<UInt128, byte> _known = new();
    private readonly ConcurrentDictionary<UInt128, byte> _pending = new();
    private readonly BlockingCollection<Job> _jobs = new(MaxQueuedJobs);
    private readonly Thread _writer;
    private readonly ManualResetEventSlim _loaded = new(false);
    private readonly object _archivesLock = new();
    private nint _pipelineArchives;
    private int _disposed;

    // The writer thread's own.
    private nint _segment;
    private int _segmentSequence;
    private int _segmentDirty;
    private long _lastFlushTicks;
    private long _writtenSinceTrim;

    private int _libraryHits;
    private int _libraryMisses;
    private int _rejected;
    private int _librariesWritten;
    private int _pipelineHits;
    private int _pipelineMisses;
    private int _pipelinesAdded;
    private int _loadedSegments;
    private long _loadedSegmentBytes;

    private MetalBinaryArchive(nint device, string root, string translatorStamp, long budget)
    {
        _device = device;
        _root = root;
        _translatorStamp = Hex64(XxHash3.HashToUInt64(MemoryMarshal.AsBytes(translatorStamp.AsSpan())));
        _budget = budget;
        _directory = Path.Combine(root, Hex64(XxHash3.HashToUInt64(MemoryMarshal.AsBytes(DeviceStamp(device).AsSpan()))));
        _libraries = Path.Combine(_directory, "libraries");
        _lastFlushTicks = Stopwatch.GetTimestamp();
        _writer = new Thread(RunWriter)
        {
            IsBackground = true,
            Priority = ThreadPriority.BelowNormal,
            Name = "SharpEmu Metal v2 binary archive",
        };
    }

    /// <summary>The process's archive, once the presenter started it, for this device.</summary>
    public static MetalBinaryArchive? For(nint device) =>
        Volatile.Read(ref _current) is { } archive && archive._device == device ? archive : null;

    /// <summary>The library directory, for tests.</summary>
    internal string LibraryDirectory => _libraries;

    /// <summary>The device, OS and format directory, for tests.</summary>
    internal string Directory => _directory;

    public (int LibraryHits, int LibraryMisses, int Rejected, int LibrariesWritten,
        int PipelineHits, int PipelineMisses, int PipelinesAdded, int LoadedSegments) Counters =>
        (Volatile.Read(ref _libraryHits), Volatile.Read(ref _libraryMisses), Volatile.Read(ref _rejected),
            Volatile.Read(ref _librariesWritten), Volatile.Read(ref _pipelineHits), Volatile.Read(ref _pipelineMisses),
            Volatile.Read(ref _pipelinesAdded), Volatile.Read(ref _loadedSegments));

    /// <summary>
    /// Opens the title's archive for the process before the shader prewarm starts, so the
    /// prewarm's libraries are taken from it too. Failure leaves the backend without one.
    /// </summary>
    public static void Start(nint device)
    {
        if (!Enabled || device == 0 || Volatile.Read(ref _current) is not null)
        {
            return;
        }

        try
        {
            var root = SharpEmuPaths.UserPath(
                "pipeline_cache",
                MetalShaderPrewarm.SanitizeTitleId(VideoOutBackendServices.GetApplicationTitleId()),
                "metal-v2",
                "binary-archive");
            var archive = Open(root, device, TranslatorStamp(), DiskBudgetBytes);
            if (Interlocked.CompareExchange(ref _current, archive, null) is not null)
            {
                archive.Dispose();
                return;
            }

            if (Interlocked.Exchange(ref _exitHooked, 1) == 0)
            {
                AppDomain.CurrentDomain.ProcessExit += static (_, _) => Stop();
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[LOADER][WARN] Metal binary archive could not open: {exception.Message}");
        }
    }

    /// <summary>Makes <paramref name="archive"/> the process's archive, for tests; returns the one it replaced.</summary>
    internal static MetalBinaryArchive? Install(MetalBinaryArchive? archive) => Interlocked.Exchange(ref _current, archive);

    /// <summary>Writes what is pending and closes the process's archive.</summary>
    public static void Stop() => Interlocked.Exchange(ref _current, null)?.Dispose();

    /// <summary>
    /// Opens (creating) the archive under <paramref name="root"/>: removes directories of
    /// other devices, OSes and formats, lists the library files and starts the writer, which
    /// loads the segments of <paramref name="translatorStamp"/>.
    /// </summary>
    internal static MetalBinaryArchive Open(string root, nint device, string translatorStamp, long budget)
    {
        var archive = new MetalBinaryArchive(device, root, translatorStamp, budget);
        archive.Prepare();
        archive._writer.Start();
        return archive;
    }

    /// <summary>The translators decide the MSL; segments of another stamp hold pipelines of
    /// functions this build no longer makes.</summary>
    internal static string TranslatorStamp() =>
        string.Join(
            '|',
            AssemblyCodeIdentity.Of(typeof(Gen5ShaderTranslator).Assembly),
            AssemblyCodeIdentity.Of(typeof(Gen5MslTranslator).Assembly));

    // The device and the OS decide what the driver makes of AIR, and what a binary slice is
    // for; the format is this file's.
    private static string DeviceStamp(nint device) =>
        string.Join(
            '|',
            FormatVersion.ToString(CultureInfo.InvariantCulture),
            MetalNative.ReadNsString(MetalNative.Send(device, MetalNative.Selector("name"))),
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture);

    /// <summary>
    /// The library key of a source: its text and the options it compiles with (fast math
    /// off, the MSL version the per-vertex-inputs flag picks).
    /// </summary>
    public static UInt128 LibraryKey(string source, bool usesPerVertexInputs)
    {
        var version = usesPerVertexInputs ? Gen5MslShader.PerVertexInputsLanguageVersion : MetalNative.MslLanguageVersion;
        var seed = (long)(((ulong)FormatVersion << 48) ^ version);
        return XxHash128.HashToUInt128(MemoryMarshal.AsBytes(source.AsSpan()), seed);
    }

    /// <summary>
    /// The library stored for <paramref name="key"/>, owned by the caller, or zero. A file
    /// that fails its trailer or that Metal will not open is deleted. Any thread.
    /// </summary>
    public nint TryLoadLibrary(UInt128 key)
    {
        if (key == default || !_known.ContainsKey(key))
        {
            Interlocked.Increment(ref _libraryMisses);
            return 0;
        }

        var path = LibraryPath(key);
        if (!TryVerify(path, out _))
        {
            Reject(key, path);
            return 0;
        }

        var pool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            nint error = 0;
            var library = MetalNative.Send(_device, MetalNative.Selector("newLibraryWithURL:error:"), FileUrl(path), ref error);
            if (library == 0)
            {
                Reject(key, path);
                return 0;
            }

            Touch(path);
            Interlocked.Increment(ref _libraryHits);
            return library;
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(pool);
        }
    }

    /// <summary>
    /// The library file of <paramref name="key"/> opened as an archive, for the compute
    /// pipeline its binary slice holds; owned by the caller, or zero.
    /// </summary>
    public nint OpenLibraryArchive(UInt128 key) =>
        key != default && _known.ContainsKey(key) ? OpenArchive(LibraryPath(key)) : 0;

    /// <summary>
    /// The loaded pipeline segments as an NSArray for a descriptor's binaryArchives,
    /// retained for the caller, or zero before they are loaded or when there are none.
    /// </summary>
    public nint AcquirePipelineArchives()
    {
        lock (_archivesLock)
        {
            if (_pipelineArchives == 0)
            {
                return 0;
            }

            MetalNative.Send(_pipelineArchives, SelRetain);
            return _pipelineArchives;
        }
    }

    /// <summary>Counts a pipeline created from (or missed by) an archive.</summary>
    public void NotePipelineLookup(bool hit) =>
        Interlocked.Increment(ref hit ? ref _pipelineHits : ref _pipelineMisses);

    /// <summary>
    /// Stores the library of <paramref name="key"/> with the compute pipeline of
    /// <paramref name="function"/>, off the caller's thread, unless it is stored already.
    /// </summary>
    public void CaptureCompute(UInt128 key, nint function)
    {
        if (!NeedsLibrary(key) || function == 0 || !_pending.TryAdd(key, 0))
        {
            return;
        }

        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLComputePipelineDescriptor"), SelAlloc), SelInit);
        if (descriptor == 0)
        {
            _pending.TryRemove(key, out _);
            return;
        }

        MetalNative.SendVoid(descriptor, MetalNative.Selector("setComputeFunction:"), function);
        if (!TryEnqueue(new Job(JobKind.Capture, PipelineKind.Compute, descriptor, key, default, false, null)))
        {
            MetalNative.SendVoid(descriptor, SelRelease);
            _pending.TryRemove(key, out _);
        }
    }

    /// <summary>
    /// Takes a render or mesh pipeline just created from <paramref name="descriptor"/>:
    /// added to the current segment when no loaded archive had it, and written as the
    /// library file of either stage key that has none. The descriptor is retained until
    /// the writer is done with it. The two stages' entry points must differ, so each file's
    /// library has one function of the name its module asks for.
    /// </summary>
    public void NoteRenderPipeline(nint descriptor, PipelineKind kind, UInt128 vertexKey, UInt128 pixelKey, bool missed)
    {
        if (descriptor == 0 || Volatile.Read(ref _disposed) != 0)
        {
            return;
        }

        // Before the segments are loaded nothing could hit; a pipeline they hold would be
        // stored twice, and one they do not is stored by a later start.
        missed &= _loaded.IsSet;
        var vertex = NeedsLibrary(vertexKey) && _pending.TryAdd(vertexKey, 0) ? vertexKey : default;
        var pixel = pixelKey != vertexKey && NeedsLibrary(pixelKey) && _pending.TryAdd(pixelKey, 0) ? pixelKey : default;
        if (!missed && vertex == default && pixel == default)
        {
            return;
        }

        MetalNative.Send(descriptor, SelRetain);
        if (!TryEnqueue(new Job(JobKind.Capture, kind, descriptor, vertex, pixel, missed, null)))
        {
            MetalNative.SendVoid(descriptor, SelRelease);
            Forget(vertex);
            Forget(pixel);
        }
    }

    /// <summary>Serializes the current segment now; waits when <paramref name="wait"/>.</summary>
    public void Flush(bool wait, TimeSpan? timeout = null)
    {
        // Not disposed: a wait that times out leaves the writer to set it later.
        var done = wait ? new ManualResetEventSlim(false) : null;
        var limit = timeout ?? TimeSpan.FromSeconds(30);
        if (!TryEnqueue(new Job(JobKind.Flush, default, 0, default, default, false, done), wait ? limit : TimeSpan.Zero))
        {
            return;
        }

        done?.Wait(limit);
    }

    /// <summary>Waits for the writer to have loaded the pipeline segments, for tests.</summary>
    internal bool WaitLoaded(TimeSpan timeout) => _loaded.Wait(timeout);

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        _jobs.CompleteAdding();
        if (!_writer.Join(TimeSpan.FromSeconds(30)))
        {
            Console.Error.WriteLine("[METAL-V2][ARCHIVE][WARN] The writer did not finish in 30 s; pending pipelines are dropped.");
            return;
        }

        lock (_archivesLock)
        {
            if (_pipelineArchives != 0)
            {
                MetalNative.SendVoid(_pipelineArchives, SelRelease);
                _pipelineArchives = 0;
            }
        }

        var (hits, misses, rejected, written, pipelineHits, pipelineMisses, added, segments) = Counters;
        Console.Error.WriteLine(
            $"[METAL-V2][ARCHIVE] closed library_hits={hits} library_misses={misses} rejected={rejected} " +
            $"libraries_written={written} pipeline_hits={pipelineHits} pipeline_misses={pipelineMisses} " +
            $"pipelines_added={added} segments_loaded={segments}");
    }

    private bool NeedsLibrary(UInt128 key) =>
        key != default && Volatile.Read(ref _disposed) == 0 && !_known.ContainsKey(key) && !_pending.ContainsKey(key);

    private void Forget(UInt128 key)
    {
        if (key != default)
        {
            _pending.TryRemove(key, out _);
        }
    }

    private bool TryEnqueue(Job job) => TryEnqueue(job, TimeSpan.Zero);

    private bool TryEnqueue(Job job, TimeSpan timeout)
    {
        try
        {
            return _jobs.TryAdd(job, timeout);
        }
        catch (InvalidOperationException)
        {
            // Closed: shutdown has begun.
            return false;
        }
    }

    private string LibraryPath(UInt128 key) =>
        Path.Combine(_libraries, key.ToString("x32", CultureInfo.InvariantCulture) + Extension);

    private string SegmentPath(int sequence) =>
        Path.Combine(_directory, $"{SegmentPrefix}{_translatorStamp}-{sequence.ToString("D5", CultureInfo.InvariantCulture)}{Extension}");

    private void Reject(UInt128 key, string path)
    {
        Interlocked.Increment(ref _rejected);
        _known.TryRemove(key, out _);
        TryDelete(path);
    }

    // Removes what other devices, OSes and formats wrote and what an interrupted write
    // left, then lists the libraries on disk.
    private void Prepare()
    {
        System.IO.Directory.CreateDirectory(_libraries);
        foreach (var other in System.IO.Directory.EnumerateDirectories(_root))
        {
            if (!string.Equals(Path.GetFullPath(other), Path.GetFullPath(_directory), StringComparison.Ordinal))
            {
                TryDeleteDirectory(other);
            }
        }

        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, "*", SearchOption.AllDirectories))
        {
            if (!path.EndsWith(Extension, StringComparison.Ordinal))
            {
                TryDelete(path);
            }
        }

        foreach (var path in System.IO.Directory.EnumerateFiles(_libraries, "*" + Extension))
        {
            if (UInt128.TryParse(Path.GetFileNameWithoutExtension(path), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var key))
            {
                _known.TryAdd(key, 0);
            }
            else
            {
                TryDelete(path);
            }
        }
    }

    private void RunWriter()
    {
        try
        {
            using (MetalAutoreleasePool.Enter())
            {
                LoadSegments();
                Trim();
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[METAL-V2][ARCHIVE][WARN] Loading the pipeline segments failed: {exception.Message}");
        }
        finally
        {
            _loaded.Set();
        }

        try
        {
            while (!_jobs.IsCompleted)
            {
                if (_jobs.TryTake(out var job, TimeSpan.FromSeconds(1)))
                {
                    using (MetalAutoreleasePool.Enter())
                    {
                        Run(job);
                    }
                }

                if (_segmentDirty != 0 &&
                    (_segmentDirty >= FlushAfterPipelines ||
                     Stopwatch.GetElapsedTime(_lastFlushTicks) >= FlushInterval))
                {
                    using (MetalAutoreleasePool.Enter())
                    {
                        FlushSegment();
                    }
                }
            }
        }
        catch (InvalidOperationException)
        {
            // Completed while taking.
        }

        using (MetalAutoreleasePool.Enter())
        {
            FlushSegment();
            if (_segment != 0)
            {
                MetalNative.SendVoid(_segment, SelRelease);
                _segment = 0;
            }
        }
    }

    private void Run(Job job)
    {
        try
        {
            if (job.Kind == JobKind.Flush)
            {
                FlushSegment();
                return;
            }

            if (job.AddToSegment)
            {
                AddToSegment(job.Pipeline, job.Descriptor);
            }

            if (job.Key0 != default || job.Key1 != default)
            {
                WriteLibraries(job.Pipeline, job.Descriptor, job.Key0, job.Key1);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[METAL-V2][ARCHIVE][WARN] {exception.Message}");
        }
        finally
        {
            if (job.Descriptor != 0)
            {
                MetalNative.SendVoid(job.Descriptor, SelRelease);
            }

            Forget(job.Key0);
            Forget(job.Key1);
            job.Done?.Set();
        }
    }

    private void AddToSegment(PipelineKind kind, nint descriptor)
    {
        if (_segment == 0)
        {
            _segment = NewArchive(null);
            if (_segment == 0)
            {
                return;
            }
        }

        if (Add(_segment, kind, descriptor))
        {
            _segmentDirty++;
            Interlocked.Increment(ref _pipelinesAdded);
        }
    }

    private void FlushSegment()
    {
        _lastFlushTicks = Stopwatch.GetTimestamp();
        if (_segment == 0 || _segmentDirty == 0)
        {
            return;
        }

        _segmentDirty = 0;
        var path = SegmentPath(_segmentSequence);
        var bytes = Serialize(_segment, path);
        if (bytes < 0 || bytes >= SegmentBytes)
        {
            // Full (or unwritable): the next pipeline starts the next segment, and this
            // one's memory goes with its archive.
            MetalNative.SendVoid(_segment, SelRelease);
            _segment = 0;
            _segmentSequence++;
        }

        NoteWritten(Math.Max(bytes, 0));
    }

    private void WriteLibraries(PipelineKind kind, nint descriptor, UInt128 first, UInt128 second)
    {
        if (kind != PipelineKind.Compute && !StageEntryPointsDiffer(kind, descriptor))
        {
            return;
        }

        var archive = NewArchive(null);
        if (archive == 0)
        {
            return;
        }

        try
        {
            if (!Add(archive, kind, descriptor))
            {
                return;
            }

            var target = first != default ? first : second;
            var path = LibraryPath(target);
            var bytes = Serialize(archive, path);
            if (bytes < 0)
            {
                return;
            }

            _known.TryAdd(target, 0);
            Interlocked.Increment(ref _librariesWritten);
            NoteWritten(bytes);
            if (first != default && second != default)
            {
                var copy = LibraryPath(second);
                File.Copy(path, copy, overwrite: true);
                _known.TryAdd(second, 0);
                Interlocked.Increment(ref _librariesWritten);
                NoteWritten(bytes);
            }
        }
        finally
        {
            MetalNative.SendVoid(archive, SelRelease);
        }
    }

    // A file's library is found by entry point name, so a stage whose name another stage
    // in the same pipeline shares cannot be told apart in it.
    private static bool StageEntryPointsDiffer(PipelineKind kind, nint descriptor)
    {
        var first = MetalNative.Send(descriptor, MetalNative.Selector(kind == PipelineKind.Mesh ? "meshFunction" : "vertexFunction"));
        var second = MetalNative.Send(descriptor, MetalNative.Selector("fragmentFunction"));
        if (first == 0 || second == 0)
        {
            return true;
        }

        return !string.Equals(
            MetalNative.ReadNsString(MetalNative.Send(first, MetalNative.Selector("name"))),
            MetalNative.ReadNsString(MetalNative.Send(second, MetalNative.Selector("name"))),
            StringComparison.Ordinal);
    }

    private bool Add(nint archive, PipelineKind kind, nint descriptor)
    {
        var selector = kind switch
        {
            PipelineKind.Compute => "addComputePipelineFunctionsWithDescriptor:error:",
            PipelineKind.Mesh => "addMeshRenderPipelineFunctionsWithDescriptor:error:",
            _ => "addRenderPipelineFunctionsWithDescriptor:error:",
        };
        nint error = 0;
        if (SendBoolError(archive, MetalNative.Selector(selector), descriptor, ref error))
        {
            return true;
        }

        Console.Error.WriteLine($"[METAL-V2][ARCHIVE][WARN] A {kind} pipeline could not be archived: {MetalNative.DescribeError(error)}");
        return false;
    }

    private void LoadSegments()
    {
        var segments = new List<(int Sequence, string Path)>();
        foreach (var path in System.IO.Directory.EnumerateFiles(_directory, SegmentPrefix + "*" + Extension))
        {
            var name = Path.GetFileNameWithoutExtension(path).AsSpan(SegmentPrefix.Length);
            var dash = name.IndexOf('-');
            if (dash < 0 || !name[..dash].SequenceEqual(_translatorStamp.AsSpan()) ||
                !int.TryParse(name[(dash + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var sequence))
            {
                // Another translator's pipelines: functions this build does not make.
                TryDelete(path);
                continue;
            }

            segments.Add((sequence, path));
        }

        segments.Sort(static (left, right) => right.Sequence.CompareTo(left.Sequence));
        _segmentSequence = segments.Count == 0 ? 0 : segments[0].Sequence + 1;
        var array = MetalNative.Send(MetalNative.Send(MetalNative.Class("NSMutableArray"), SelAlloc), SelInit);
        long resident = 0;
        var loaded = 0;
        foreach (var (_, path) in segments)
        {
            if (!TryVerify(path, out var length))
            {
                Interlocked.Increment(ref _rejected);
                TryDelete(path);
                continue;
            }

            if (resident + length > ResidentSegmentBytes)
            {
                // Past what is kept open: the oldest go first.
                TryDelete(path);
                continue;
            }

            var archive = OpenArchive(path);
            if (archive == 0)
            {
                Interlocked.Increment(ref _rejected);
                TryDelete(path);
                continue;
            }

            MetalNative.SendVoid(array, MetalNative.Selector("addObject:"), archive);
            MetalNative.SendVoid(archive, SelRelease);
            resident += length;
            loaded++;
        }

        _loadedSegments = loaded;
        _loadedSegmentBytes = resident;
        if (loaded == 0)
        {
            MetalNative.SendVoid(array, SelRelease);
        }
        else
        {
            lock (_archivesLock)
            {
                _pipelineArchives = array;
            }
        }

        Console.Error.WriteLine(
            $"[METAL-V2][ARCHIVE] opened {_directory} libraries={_known.Count} segments={loaded} " +
            $"segment_mb={resident / 1048576.0:F1}");
    }

    private void NoteWritten(long bytes)
    {
        _writtenSinceTrim += bytes;
        if (_writtenSinceTrim >= _budget / 16)
        {
            _writtenSinceTrim = 0;
            Trim();
        }
    }

    // Least recently used first: a library's file is touched when it is loaded, a segment
    // when it is written.
    private void Trim()
    {
        var files = new DirectoryInfo(_directory).GetFiles("*" + Extension, SearchOption.AllDirectories);
        var total = 0L;
        foreach (var file in files)
        {
            total += file.Length;
        }

        if (total <= _budget)
        {
            return;
        }

        var current = Path.GetFullPath(SegmentPath(_segmentSequence));
        Array.Sort(files, static (left, right) => left.LastWriteTimeUtc.CompareTo(right.LastWriteTimeUtc));
        foreach (var file in files)
        {
            if (total <= _budget - _budget / 10)
            {
                break;
            }

            if (string.Equals(file.FullName, current, StringComparison.Ordinal))
            {
                continue;
            }

            if (UInt128.TryParse(Path.GetFileNameWithoutExtension(file.Name), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var key))
            {
                _known.TryRemove(key, out _);
            }

            total -= file.Length;
            TryDelete(file.FullName);
        }
    }

    private nint NewArchive(string? path)
    {
        var descriptor = MetalNative.Send(MetalNative.Send(MetalNative.Class("MTLBinaryArchiveDescriptor"), SelAlloc), SelInit);
        if (descriptor == 0)
        {
            return 0;
        }

        try
        {
            if (path is not null)
            {
                MetalNative.SendVoid(descriptor, MetalNative.Selector("setUrl:"), FileUrl(path));
            }

            nint error = 0;
            return MetalNative.Send(_device, MetalNative.Selector("newBinaryArchiveWithDescriptor:error:"), descriptor, ref error);
        }
        finally
        {
            MetalNative.SendVoid(descriptor, SelRelease);
        }
    }

    private nint OpenArchive(string path)
    {
        var pool = MetalNative.objc_autoreleasePoolPush();
        try
        {
            return NewArchive(path);
        }
        finally
        {
            MetalNative.objc_autoreleasePoolPop(pool);
        }
    }

    // Serializes to a temporary name, appends the trailer and moves it into place, so a
    // reader never sees a half-written file. The final length, or -1.
    private long Serialize(nint archive, string path)
    {
        var temporary = $"{path}.{Environment.ProcessId.ToString(CultureInfo.InvariantCulture)}.tmp";
        nint error = 0;
        if (!SendBoolError(archive, MetalNative.Selector("serializeToURL:error:"), FileUrl(temporary), ref error))
        {
            Console.Error.WriteLine($"[METAL-V2][ARCHIVE][WARN] An archive could not be written: {MetalNative.DescribeError(error)}");
            TryDelete(temporary);
            return -1;
        }

        try
        {
            AppendTrailer(temporary);
            File.Move(temporary, path, overwrite: true);
            return new FileInfo(path).Length;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"[METAL-V2][ARCHIVE][WARN] An archive could not be written: {exception.Message}");
            TryDelete(temporary);
            return -1;
        }
    }

    /// <summary>Appends the trailer that <see cref="TryVerify"/> checks to a serialized archive.</summary>
    internal static void AppendTrailer(string path)
    {
        var payload = File.ReadAllBytes(path);
        Span<byte> trailer = stackalloc byte[TrailerBytes];
        WriteTrailer(payload, trailer);
        using var stream = new FileStream(path, FileMode.Append, FileAccess.Write);
        stream.Write(trailer);
    }

    internal static void WriteTrailer(ReadOnlySpan<byte> payload, Span<byte> trailer)
    {
        BinaryPrimitives.WriteUInt32LittleEndian(trailer, TrailerMagic);
        BinaryPrimitives.WriteUInt32LittleEndian(trailer[4..], FormatVersion);
        BinaryPrimitives.WriteInt64LittleEndian(trailer[8..], payload.Length);
        BinaryPrimitives.WriteUInt64LittleEndian(trailer[16..], XxHash64.HashToUInt64(payload));
    }

    /// <summary>Whether a file is a whole archive this format wrote; its length when it is.</summary>
    internal static bool TryVerify(string path, out long length)
    {
        length = 0;
        byte[] bytes;
        try
        {
            bytes = File.ReadAllBytes(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            return false;
        }

        if (bytes.Length <= TrailerBytes)
        {
            return false;
        }

        var payload = bytes.AsSpan(0, bytes.Length - TrailerBytes);
        var trailer = bytes.AsSpan(bytes.Length - TrailerBytes);
        if (BinaryPrimitives.ReadUInt32LittleEndian(trailer) != TrailerMagic ||
            BinaryPrimitives.ReadUInt32LittleEndian(trailer[4..]) != FormatVersion ||
            BinaryPrimitives.ReadInt64LittleEndian(trailer[8..]) != payload.Length ||
            BinaryPrimitives.ReadUInt64LittleEndian(trailer[16..]) != XxHash64.HashToUInt64(payload))
        {
            return false;
        }

        length = bytes.Length;
        return true;
    }

    // Autoreleased.
    private static nint FileUrl(string path) =>
        MetalNative.Send(MetalNative.Class("NSURL"), MetalNative.Selector("fileURLWithPath:"), MetalNative.NsString(path));

    private static void Touch(string path)
    {
        try
        {
            File.SetLastWriteTimeUtc(path, DateTime.UtcNow);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Only the eviction order depends on it.
        }
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // Another start may hold it; the next trim tries again.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            System.IO.Directory.Delete(path, recursive: true);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // The next start tries again.
        }
    }

    private static string Hex64(ulong value) => value.ToString("x16", CultureInfo.InvariantCulture);

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    [return: MarshalAs(UnmanagedType.I1)]
    private static partial bool SendBoolError(nint receiver, nint selector, nint argument, ref nint error);
}
