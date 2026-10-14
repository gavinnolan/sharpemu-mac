// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpEmu.HLE.DsTrace;

[StructLayout(LayoutKind.Sequential, Size = 128)]
internal struct DsSlot
{
    public long Seq;
    public long Ts;
    public ushort Type;
    public ushort Pad;
    public int Tid;
    public ulong P0, P1, P2, P3, P4, P5, P6, P7, P8, P9, P10, P11, P12;
}

/// <summary>
/// DSTRACE: the diagnostic event sink for one instrumented run (SHARPEMU_DSTRACE=1).
/// Every hook in the emulator is gated on the <c>static readonly</c> switches below,
/// so with the variable unset the JIT folds the hooks away. Records go into one
/// bounded lock-free MPSC ring (never blocks, never allocates, drops when full) and a
/// single writer thread formats them as one text line each.
/// </summary>
public static unsafe class DsTrace
{
    // ---- switches (§1.6, §2) ----

    public static readonly bool On = Env("SHARPEMU_DSTRACE") == "1";
    private static readonly string Groups = Env("SHARPEMU_DSTRACE_GROUPS") ?? "all";
    public static readonly bool Sched = On && Has(Groups, "sched");
    public static readonly bool Hle = On && Has(Groups, "hle");
    public static readonly bool Fiber = On && Has(Groups, "fiber");
    public static readonly bool Mem = On && Has(Groups, "mem");
    public static readonly bool Sync = On && Has(Groups, "sync");
    public static readonly bool Gpu = On && Has(Groups, "gpu");
    public static readonly bool Host = On && Has(Groups, "host");
    public static readonly bool Sampler = On && Has(Groups, "sampler");
    public static readonly bool Io = On && Has(Groups, "io");

    /// <summary>SHARPEMU_DSTRACE_RAW_READS=1: DsGuestMem reads validated pages raw instead of through ICpuMemory.TryRead.</summary>
    public static readonly bool RawReads = On && Env("SHARPEMU_DSTRACE_RAW_READS") == "1";

    public static readonly int ArmSeconds = EnvInt("SHARPEMU_DSTRACE_ARM_S", 30, 0, 100000);
    public static readonly int FlushSeconds = EnvInt("SHARPEMU_DSTRACE_FLUSH_S", 2, 1, 3600);
    public static readonly int RingLog2 = EnvInt("SHARPEMU_DSTRACE_RING_LOG2", 18, 10, 24);
    public static readonly int SamplerHz = EnvInt("SHARPEMU_DSTRACE_SAMPLER_HZ", 1000, 1, 20000);
    public static readonly int WcpuHz = EnvInt("SHARPEMU_DSTRACE_WCPU_HZ", 1000, 0, 20000);
    public static readonly int HleSlowUs = EnvInt("SHARPEMU_DSTRACE_HLE_SLOW_US", 1000, 0, int.MaxValue);
    public static readonly int HleCpuEvery = EnvInt("SHARPEMU_DSTRACE_HLE_CPU_EVERY", 64, 1, int.MaxValue);
    public static readonly int MlwUs = EnvInt("SHARPEMU_DSTRACE_MLW_US", 50, 0, int.MaxValue);
    public static readonly int AbRipCacheSeconds = EnvInt("SHARPEMU_DSTRACE_AB_RIPCACHE", 0, 0, 100000);
    public static readonly int AbStartSeconds = EnvInt("SHARPEMU_DSTRACE_AB_START_S", 70, 0, 100000);

    /// <summary>Stopwatch ticks equal nanoseconds (true on macOS).</summary>
    internal static readonly bool NsClock = Stopwatch.Frequency == 1_000_000_000L;
    internal static readonly long HleSlowTicks = NsToTicks(HleSlowUs * 1000L);
    internal static readonly long MlwTicks = NsToTicks(MlwUs * 1000L);

    private static int _armed;

    /// <summary>True once ARM_S seconds have passed since <see cref="Start"/>; "armed" events emit only then.</summary>
    public static bool Armed
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => Volatile.Read(ref _armed) != 0;
    }

    internal static void Arm() => Volatile.Write(ref _armed, 1);

    // ---- thread-local state (§1.6) ----

    [ThreadStatic] private static int _currentImportPlusOne;

    /// <summary>Import index being dispatched on this thread, -1 otherwise (set only while HLE tracing is armed).</summary>
    public static int CurrentImport
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _currentImportPlusOne - 1;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => _currentImportPlusOne = value + 1;
    }

    /// <summary>Return address of the import being dispatched (argPack+96); set by the gateway wrapper.</summary>
    [ThreadStatic] public static ulong CurrentRa1;

    /// <summary>1 guest worker, 2 guest other, 3 metal worker, 4 metal other, 0 other.</summary>
    [ThreadStatic] public static byte ThreadClass;

    /// <summary>Dispatch outcome: 0 plain, 1 block consumed, 2 context transfer, 3 entry exit.</summary>
    [ThreadStatic] public static byte LastOutcome;

    /// <summary>Set around the RIP probe of TryValidateGuestContextTransferTarget.</summary>
    [ThreadStatic] public static bool InRipProbe;

    [ThreadStatic] private static DsThreadData? _threadData;

    /// <summary>Per-thread aggregates (lock, HLE, pump counters, fiber scratch), registered once.</summary>
    public static DsThreadData ThreadData
    {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => _threadData ?? CreateThreadData();
    }

    private static readonly object _threadDataGate = new();
    private static DsThreadData[] _allThreadData = [];

    internal static DsThreadData[] AllThreadData => Volatile.Read(ref _allThreadData);

    private static DsThreadData CreateThreadData()
    {
        var data = new DsThreadData(Environment.CurrentManagedThreadId);
        lock (_threadDataGate)
        {
            var next = new DsThreadData[_allThreadData.Length + 1];
            Array.Copy(_allThreadData, next, _allThreadData.Length);
            next[^1] = data;
            Volatile.Write(ref _allThreadData, next);
        }

        _threadData = data;
        return data;
    }

    /// <summary>Precise CPU time of the calling thread (CLOCK_THREAD_CPUTIME_ID).</summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static ulong SelfCpuNs() => DsNative.clock_gettime_nsec_np(16);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static long TicksToNs(long ticks) =>
        NsClock ? ticks : (long)((double)ticks * 1_000_000_000.0 / Stopwatch.Frequency);

    internal static long NsToTicks(long ns) =>
        NsClock ? ns : (long)((double)ns * Stopwatch.Frequency / 1_000_000_000.0);

    // ---- TSC hook (Libs owns the TSC reader; Core wires it before Start) ----

    internal static Func<ulong>? TscReader;
    internal static ulong TscHz;

    public static void ConfigureTsc(ulong hz, Func<ulong> reader)
    {
        TscHz = hz;
        TscReader = reader;
        if (On && Volatile.Read(ref _started) != 0)
        {
            // The sink started (and wrote its header) before the TSC was wired.
            Comment($"# TSC tsc_hz={hz}");
        }
    }

    /// <summary>Queues a raw '#' comment line for the writer (rare, allocates).</summary>
    internal static void Comment(string line)
    {
        if (On && !_closed)
        {
            _strQueue!.Enqueue((Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId, 0, line));
        }
    }

    // ---- ring (§1.3) ----

    private static DsSlot[]? _ring;
    private static long _mask;
    private static long _enq;
    private static long _deq;
    private static long _dropped;
    private static long _written;
    private static volatile bool _closed;
    private static int _started;
    private static int _shutdown;
    private static readonly object _startGate = new();
    internal static long StartTimestamp;

    public static void EmitAt(
        long ts, DsEv type,
        ulong p0 = 0, ulong p1 = 0, ulong p2 = 0, ulong p3 = 0, ulong p4 = 0, ulong p5 = 0, ulong p6 = 0,
        ulong p7 = 0, ulong p8 = 0, ulong p9 = 0, ulong p10 = 0, ulong p11 = 0, ulong p12 = 0)
    {
        if (!On || _closed)
        {
            return;
        }

        if (Volatile.Read(ref _started) == 0)
        {
            Start();
        }

        var ring = _ring;
        if (ring is null)
        {
            return;
        }

        while (true)
        {
            var pos = Volatile.Read(ref _enq);
            ref var s = ref ring[pos & _mask];
            var dif = Volatile.Read(ref s.Seq) - pos;
            if (dif == 0)
            {
                if (Interlocked.CompareExchange(ref _enq, pos + 1, pos) != pos)
                {
                    continue;
                }

                s.Ts = ts;
                s.Type = (ushort)type;
                s.Tid = Environment.CurrentManagedThreadId;
                s.P0 = p0; s.P1 = p1; s.P2 = p2; s.P3 = p3; s.P4 = p4; s.P5 = p5; s.P6 = p6;
                s.P7 = p7; s.P8 = p8; s.P9 = p9; s.P10 = p10; s.P11 = p11; s.P12 = p12;
                Volatile.Write(ref s.Seq, pos + 1);
                return;
            }

            if (dif < 0)
            {
                Interlocked.Increment(ref _dropped);
                return;
            }
        }
    }

    public static void Emit(
        DsEv type,
        ulong p0 = 0, ulong p1 = 0, ulong p2 = 0, ulong p3 = 0, ulong p4 = 0, ulong p5 = 0, ulong p6 = 0,
        ulong p7 = 0, ulong p8 = 0, ulong p9 = 0, ulong p10 = 0, ulong p11 = 0, ulong p12 = 0)
    {
        if (!On)
        {
            return;
        }

        EmitAt(Stopwatch.GetTimestamp(), type, p0, p1, p2, p3, p4, p5, p6, p7, p8, p9, p10, p11, p12);
    }

    // ---- string interning (§1.5) ----

    private const int MaxStrings = 1 << 20;
    private static readonly ConcurrentDictionary<string, uint>? _strings = On ? new(StringComparer.Ordinal) : null;
    private static readonly ConcurrentQueue<(long Ts, int Tid, uint Id, string Text)>? _strQueue = On ? new() : null;
    private static int _nextStrId;

    /// <summary>Returns the STR id of <paramref name="text"/> (0 for null/empty or past the 1 M cap).</summary>
    public static uint Intern(string? text)
    {
        if (!On || string.IsNullOrEmpty(text))
        {
            return 0;
        }

        var map = _strings!;
        if (map.TryGetValue(text, out var id))
        {
            return id;
        }

        if (Volatile.Read(ref _nextStrId) >= MaxStrings)
        {
            return 0;
        }

        var next = (uint)Interlocked.Increment(ref _nextStrId);
        // Queue the definition before publishing the id, so no record can carry an id
        // whose STR line the writer has not seen yet. A lost race leaves an unused STR.
        _strQueue!.Enqueue((Stopwatch.GetTimestamp(), Environment.CurrentManagedThreadId, next, text));
        if (Volatile.Read(ref _started) == 0)
        {
            Start();
        }

        return map.TryAdd(text, next) ? next : map.TryGetValue(text, out id) ? id : 0;
    }

    // ---- thread registration (§3.1) ----

    /// <summary>
    /// Registers the calling thread: sets <see cref="ThreadClass"/>, records its Mach
    /// port for the worker CPU sampler and emits THR + TATTR.
    /// kind: 1 guest runner, 2 Metal GPU worker, 3 Metal completion, 4 flip pacer, 7 DsTrace.
    /// qosApply: 0 false, 1 true, 2 n/a.
    /// </summary>
    public static void RegisterThread(byte kind, ulong guestHandle, string name, int qosApply)
    {
        if (!On)
        {
            return;
        }

        try
        {
            var workerIdx = 0xffff;
            byte cls = 0;
            switch (kind)
            {
                case 1:
                    workerIdx = ParseWorkerIndex(name);
                    cls = workerIdx != 0xffff ? (byte)1 : (byte)2;
                    break;
                case 2:
                    cls = 3;
                    break;
                case 3:
                case 4:
                    cls = 4;
                    break;
            }

            ThreadClass = cls;
            var self = DsNative.pthread_self();
            var port = DsNative.pthread_mach_thread_np(self);
            _ = DsNative.pthread_threadid_np(0, out var threadId);
            if (cls == 1 && workerIdx < 13)
            {
                DsHost.RegisterWorkerPort(workerIdx, port);
            }
            else if (cls == 3)
            {
                DsHost.RegisterWorkerPort(13, port);
            }

            Emit(DsEv.THR, kind, port, threadId, guestHandle, Intern(name), (ulong)workerIdx);

            uint qos = 0;
            var relpri = 0;
            try { _ = DsNative.pthread_get_qos_class_np(self, out qos, out relpri); } catch (Exception) { }
            var policy = 0;
            DsNative.SchedParam param = default;
            try { _ = DsNative.pthread_getschedparam(self, out policy, out param); } catch (Exception) { }
            Emit(
                DsEv.TATTR,
                (ulong)qosApply,
                qos,
                unchecked((ulong)(long)relpri),
                unchecked((ulong)(long)policy),
                unchecked((ulong)(long)param.SchedPriority),
                (ulong)(int)Thread.CurrentThread.Priority);
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[DSTRACE] RegisterThread failed: {exception.GetType().Name}: {exception.Message}");
        }
    }

    private static int ParseWorkerIndex(string name)
    {
        const string prefix = "BPE JobWorkerThread CPU";
        var at = name.IndexOf(prefix, StringComparison.Ordinal);
        if (at < 0)
        {
            return 0xffff;
        }

        var digits = name.AsSpan(at + prefix.Length);
        var end = 0;
        while (end < digits.Length && char.IsAsciiDigit(digits[end]))
        {
            end++;
        }

        return end > 0 && int.TryParse(digits[..end], out var n) && n >= 0 && n < 0xffff ? n : 0xffff;
    }

    // ---- start / writer / shutdown (§1.4) ----

    private static FileStream? _file;
    private static string? _path;
    private static ManualResetEventSlim? _closeSignal;
    private static ManualResetEventSlim? _writerDone;

    /// <summary>Starts the sink (idempotent). Called from StartGuestFlowTraces, or lazily by the first Emit.</summary>
    public static void Start()
    {
        if (!On || Volatile.Read(ref _started) != 0)
        {
            return;
        }

        lock (_startGate)
        {
            if (_started != 0)
            {
                return;
            }

            try
            {
                var capacity = 1L << RingLog2;
                var ring = new DsSlot[capacity];
                for (var i = 0L; i < capacity; i++)
                {
                    ring[i].Seq = i;
                }

                _mask = capacity - 1;
                _path = ResolveOutputPath();
                _file = new FileStream(_path, FileMode.Create, FileAccess.Write, FileShare.Read, 1, FileOptions.None);
                StartTimestamp = Stopwatch.GetTimestamp();
                _closeSignal = new ManualResetEventSlim(false);
                _writerDone = new ManualResetEventSlim(false);
                _ring = ring;
                WriteHeader();
                Console.Error.WriteLine($"[DSTRACE] output={_path}");
                Volatile.Write(ref _started, 1);
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine($"[DSTRACE] start failed: {exception.GetType().Name}: {exception.Message}");
                _closed = true;
                Volatile.Write(ref _started, 1);
                return;
            }

            AppDomain.CurrentDomain.ProcessExit += static (_, _) => Shutdown();
            var writer = new Thread(WriterMain) { IsBackground = true, Name = "SharpEmu-DsTraceWriter" };
            writer.Start();
            DsHost.StartThread();
            if (Sampler)
            {
                DsSampler.StartThread();
            }
        }
    }

    /// <summary>Drains, writes the END line, flushes and closes (idempotent, 3 s budget).</summary>
    public static void Shutdown()
    {
        if (!On || Interlocked.Exchange(ref _shutdown, 1) != 0 || Volatile.Read(ref _started) == 0 || _ring is null)
        {
            return;
        }

        try
        {
            DsHost.FinalFlush();
        }
        catch (Exception)
        {
            // Aggregates are best effort at shutdown.
        }

        _closed = true;
        _closeSignal?.Set();
        _writerDone?.Wait(3000);
    }

    internal static bool Closed => _closed;

    private static string ResolveOutputPath()
    {
        var explicitPath = Env("SHARPEMU_DSTRACE_OUT");
        if (!string.IsNullOrWhiteSpace(explicitPath))
        {
            return Path.GetFullPath(explicitPath);
        }

        var runOut = Env("SHARPEMU_RUN_OUT");
        if (!string.IsNullOrWhiteSpace(runOut))
        {
            return Path.GetFullPath(Path.Combine(runOut, "dstrace.log"));
        }

        return Path.GetFullPath($"dstrace-{Environment.ProcessId}.log");
    }

    private static void WriteHeader()
    {
        var unixMs = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        var sb = new StringBuilder();
        sb.Append("# DSTRACE 1 freq=").Append(Stopwatch.Frequency)
            .Append(" pid=").Append(Environment.ProcessId)
            .Append(" start_ts=").Append(StartTimestamp)
            .Append(" start_unix_ms=").Append(unixMs)
            .Append(" tsc_hz=").Append(TscHz)
            .Append(" tb_hz=").Append(DsHost.TimebaseHz)
            .Append(" rev=").Append(SharpEmu.Logging.BuildInfo.CommitSha ?? "unknown")
            .Append('\n');
        sb.Append("# CONFIG groups=").Append(Groups.Replace(' ', '_'))
            .Append(" arm_s=").Append(ArmSeconds)
            .Append(" flush_s=").Append(FlushSeconds)
            .Append(" ring_log2=").Append(RingLog2)
            .Append(" sampler_hz=").Append(SamplerHz)
            .Append(" wcpu_hz=").Append(WcpuHz)
            .Append(" hle_slow_us=").Append(HleSlowUs)
            .Append(" hle_cpu_every=").Append(HleCpuEvery)
            .Append(" mlw_us=").Append(MlwUs)
            .Append(" ab_ripcache=").Append(AbRipCacheSeconds)
            .Append(" ab_start_s=").Append(AbStartSeconds)
            .Append(" raw_reads=").Append(RawReads ? 1 : 0)
            .Append('\n');
        foreach (var (type, fields) in DsSchema.Table)
        {
            sb.Append("# SCHEMA ").Append(type).Append(' ').Append(fields).Append('\n');
        }

        var bytes = Encoding.UTF8.GetBytes(sb.ToString());
        _file!.Write(bytes, 0, bytes.Length);
        _file.Flush();
    }

    // Writer-thread state.
    private static byte[] _buf = [];
    private static int _bufLen;
    private static long _maxOccupancy;

    private static void WriterMain()
    {
        RegisterThread(7, 0, "DsTraceWriter", 2);
        _buf = new byte[1 << 20];
        var flushTicks = Stopwatch.Frequency * FlushSeconds;
        var nextFlush = Stopwatch.GetTimestamp() + flushTicks;
        try
        {
            while (true)
            {
                var closing = _closeSignal!.Wait(100);
                Drain();
                var now = Stopwatch.GetTimestamp();
                if (closing)
                {
                    Drain();
                    AppendWstatLine(now);
                    AppendAscii(
                        $"# END written={Interlocked.Read(ref _written)} dropped={Interlocked.Read(ref _dropped)}\n");
                    FlushBuffer();
                    _file!.Flush(true);
                    _file.Dispose();
                    return;
                }

                if (now >= nextFlush)
                {
                    nextFlush = now + flushTicks;
                    FlushBuffer();
                    _file!.Flush(true);
                    EmitAt(
                        now,
                        DsEv.WSTAT,
                        (ulong)Interlocked.Read(ref _written),
                        (ulong)Interlocked.Read(ref _dropped),
                        (ulong)Interlocked.Exchange(ref _maxOccupancy, 0),
                        SelfCpuNs());
                }
            }
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine($"[DSTRACE] writer failed: {exception.GetType().Name}: {exception.Message}");
            _closed = true;
        }
        finally
        {
            _writerDone!.Set();
        }
    }

    private static void Drain()
    {
        var ring = _ring!;
        // Snapshot the producer position before the STR queue: every record reserved
        // before the snapshot interned its strings earlier, so their STR lines are
        // already queued and get written first.
        var enqSnapshot = Volatile.Read(ref _enq);
        var occupancy = enqSnapshot - _deq;
        if (occupancy > Interlocked.Read(ref _maxOccupancy))
        {
            Interlocked.Exchange(ref _maxOccupancy, occupancy);
        }

        while (_strQueue!.TryDequeue(out var str))
        {
            AppendStr(str.Ts, str.Tid, str.Id, str.Text);
        }

        var capacity = _mask + 1;
        while (_deq < enqSnapshot)
        {
            ref var s = ref ring[_deq & _mask];
            if (Volatile.Read(ref s.Seq) != _deq + 1)
            {
                break;
            }

            var copy = s;
            Volatile.Write(ref s.Seq, _deq + capacity);
            _deq++;
            AppendRecord(in copy);
            Interlocked.Increment(ref _written);
        }
    }

    private static void EnsureSpace(int bytes)
    {
        if (_bufLen + bytes > _buf.Length)
        {
            FlushBuffer();
        }
    }

    private static void FlushBuffer()
    {
        if (_bufLen > 0)
        {
            _file!.Write(_buf, 0, _bufLen);
            _bufLen = 0;
        }
    }

    private static void AppendByte(byte b) => _buf[_bufLen++] = b;

    private static void AppendSigned(long value)
    {
        value.TryFormat(_buf.AsSpan(_bufLen), out var n, default, System.Globalization.CultureInfo.InvariantCulture);
        _bufLen += n;
    }

    private static void AppendHex(ulong value)
    {
        _buf[_bufLen++] = (byte)'0';
        _buf[_bufLen++] = (byte)'x';
        value.TryFormat(_buf.AsSpan(_bufLen), out var n, "x", System.Globalization.CultureInfo.InvariantCulture);
        _bufLen += n;
    }

    private static void AppendAscii(string text)
    {
        EnsureSpace(Encoding.UTF8.GetMaxByteCount(text.Length));
        _bufLen += Encoding.UTF8.GetBytes(text, _buf.AsSpan(_bufLen));
    }

    private static void AppendRecord(in DsSlot s)
    {
        // 13 fields × ≤ 21 bytes + prefix: 512 is a safe upper bound.
        EnsureSpace(512);
        AppendSigned(s.Ts);
        AppendByte((byte)' ');
        var type = s.Type < DsSchema.Entries.Length ? DsSchema.Entries[s.Type] : default;
        int count;
        ushort hex;
        if (type.Name is null)
        {
            "UNK"u8.CopyTo(_buf.AsSpan(_bufLen));
            _bufLen += 3;
            count = 13;
            hex = 0x1fff;
        }
        else
        {
            type.Name.CopyTo(_buf, _bufLen);
            _bufLen += type.Name.Length;
            count = type.FieldCount;
            hex = type.HexMask;
        }

        AppendByte((byte)' ');
        AppendSigned(s.Tid);
        for (var i = 0; i < count; i++)
        {
            var v = i switch
            {
                0 => s.P0, 1 => s.P1, 2 => s.P2, 3 => s.P3, 4 => s.P4, 5 => s.P5, 6 => s.P6,
                7 => s.P7, 8 => s.P8, 9 => s.P9, 10 => s.P10, 11 => s.P11, _ => s.P12,
            };
            AppendByte((byte)' ');
            if ((hex & (1 << i)) != 0)
            {
                AppendHex(v);
            }
            else
            {
                AppendSigned(unchecked((long)v));
            }
        }

        AppendByte((byte)'\n');
    }

    private static void AppendStr(long ts, int tid, uint id, string text)
    {
        if (text.Length > 4096)
        {
            text = text[..4096];
        }

        if (id == 0)
        {
            AppendAscii(text.Replace('\n', ' ') + "\n");
            return;
        }

        EnsureSpace(64 + Encoding.UTF8.GetMaxByteCount(text.Length * 2));
        AppendSigned(ts);
        " STR "u8.CopyTo(_buf.AsSpan(_bufLen));
        _bufLen += 5;
        AppendSigned(tid);
        AppendByte((byte)' ');
        AppendSigned(id);
        AppendByte((byte)' ');
        foreach (var rune in text.EnumerateRunes())
        {
            switch (rune.Value)
            {
                case '\\':
                    AppendByte((byte)'\\'); AppendByte((byte)'\\');
                    break;
                case '\n':
                    AppendByte((byte)'\\'); AppendByte((byte)'n');
                    break;
                case '\r':
                    AppendByte((byte)'\\'); AppendByte((byte)'r');
                    break;
                default:
                    _bufLen += rune.EncodeToUtf8(_buf.AsSpan(_bufLen));
                    break;
            }
        }

        AppendByte((byte)'\n');
    }

    private static void AppendWstatLine(long now)
    {
        var slot = new DsSlot
        {
            Ts = now,
            Type = (ushort)DsEv.WSTAT,
            Tid = Environment.CurrentManagedThreadId,
            P0 = (ulong)Interlocked.Read(ref _written),
            P1 = (ulong)Interlocked.Read(ref _dropped),
            P2 = (ulong)Interlocked.Exchange(ref _maxOccupancy, 0),
            P3 = SelfCpuNs(),
        };
        AppendRecord(in slot);
    }

    // ---- env helpers ----

    internal static string? Env(string name) => Environment.GetEnvironmentVariable(name);

    private static bool Has(string groups, string group)
    {
        foreach (var part in groups.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (part.Equals("all", StringComparison.OrdinalIgnoreCase) ||
                part.Equals(group, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static int EnvInt(string name, int fallback, int min, int max)
    {
        if (!On)
        {
            return fallback;
        }

        return int.TryParse(Env(name), out var value) && value >= min && value <= max ? value : fallback;
    }
}

/// <summary>§7 in-run A/B switch. Only the host sampler flips it, and only when SHARPEMU_DSTRACE_AB_RIPCACHE &gt; 0.</summary>
public static class DsTraceAb
{
    /// <summary>JIT-foldable: false unless the A/B is configured, so the hook costs nothing otherwise.</summary>
    public static readonly bool Enabled = DsTrace.On && DsTrace.AbRipCacheSeconds > 0;

    public static volatile bool RipCacheActive;
}
