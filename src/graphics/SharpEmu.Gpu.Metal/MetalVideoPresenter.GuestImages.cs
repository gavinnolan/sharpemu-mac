// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.HLE;
using SharpEmu.ShaderCompiler;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Scheduling;

namespace SharpEmu.Gpu.Metal;

// Guest work ordering and guest images, mirroring the Vulkan presenter's model:
// AGC submissions become queued work items consumed in logical-guest-queue order
// by the render loop; guest images are Metal textures keyed by guest address,
// seeded once from guest memory (PS5 render targets alias guest memory) and kept
// coherent through explicit write/fill mirroring; ordered flips capture the named
// image into an immutable version at their exact queue position so later work
// cannot change the frame a flip selected.
internal static partial class MetalVideoPresenter
{
    private const int MaxPendingGuestWork = 64;
    private const int MaxGuestWorkPerRender = 256;
    private const int MaxPendingGuestFlipVersions = 4;
    private const ulong MaxPendingGuestWorkBytes = 256UL * 1024 * 1024;
    private static readonly long _renderWorkBudgetTicks =
        12L * System.Diagnostics.Stopwatch.Frequency / 1000L;

    private readonly record struct GuestQueueIdentity(string Name, ulong SubmissionId)
    {
        public static GuestQueueIdentity Default { get; } = new("host.default", 0);
    }

    private readonly record struct PendingGuestWork(
        object Work,
        ulong PayloadBytes,
        long Sequence,
        GuestQueueIdentity Queue);

    private sealed record OrderedGuestAction(Action Action, string DebugName);

    private sealed record GuestImageWrite(ulong Address, byte[]? Pixels, uint FillValue);

    private sealed record OrderedGuestFlip(
        long Version,
        int VideoOutHandle,
        int DisplayBufferIndex,
        ulong Address,
        uint Width,
        uint Height,
        uint PitchInPixel);

    private sealed record GuestImageBlit(ulong SourceAddress, ulong DestinationAddress);

    /// <summary>A guest-addressed Metal texture (or an immutable captured version).</summary>
    private sealed class GuestImage
    {
        public nint Texture;
        public uint Width;
        public uint Height;
        public uint TextureType = MtlTextureType2D;
        public uint Depth = 1;
        public uint ArrayLength = 1;
        public MtlPixelFormat Format;
        public bool Initialized;
        // Only the finite R16 selected class transfers its construction retain
        // after exact-owner/post-hook validation. Not a guest backing lease.
        public bool R16SelectedOwnsRetain;

        /// <summary>True once GPU work (draw, blit, dispatch) or an explicit
        /// guest write produced this content; false while it only holds a
        /// speculative guest-memory seed. Flips prefer produced content.</summary>
        public bool GpuWritten;

        /// <summary>Bumped whenever anything changes this image's content;
        /// feedback-read snapshots are reused until it moves. Games that
        /// composite by sampling their render target otherwise force a
        /// full-texture blit on every draw.</summary>
        public int ContentVersion;
        public ImageDiagnosticOrigin DiagnosticOrigin;
        public ImageDiagnosticWork DiagnosticMutation;
        // Always-present creation layout; independent of diagnostic opt-in.
        public MetalStorageCpuLayout? StorageCpuLayout;
        // Optional ORIGINAL mapping copy and observation, never synthesized at drain.
        public StorageCpuSeedOrigin? StorageCpuOrigin;

        /// <summary>Cached feedback-read snapshot (one retain held here) and
        /// the content version it captured. Command buffers that sampled it
        /// retain it through completion, so replacing releases immediately.</summary>
        public nint SnapshotTexture;
        public int SnapshotVersion;

        public void MarkContentChanged() => MarkContentChangedWithDiagnostic("unspecified");

        public void MarkContentChangedWithDiagnostic(string diagnosticKind)
        {
            Initialized = true;
            GpuWritten = true;
            ContentVersion++;
            NoteSingletonVolumeOriginalVersion(this, diagnosticKind);
            if (_imageProvenanceEnabled) DiagnosticMutation = ImageDiagnosticContext(diagnosticKind);
        }

        public void ReleaseSnapshot()
        {
            if (SnapshotTexture != 0)
            {
                MetalNative.SendVoid(SnapshotTexture, MetalNative.Selector("release"));
                SnapshotTexture = 0;
            }
        }
    }

    private static void Require2DGuestImage(GuestImage image, string operation)
    {
        if (image.TextureType != MtlTextureType2D || image.Depth != 1)
            throw new NotSupportedException(
                $"Metal {operation} requires a 2D guest image; native_type={image.TextureType} depth={image.Depth}.");
    }

    // PS5 exposes independent graphics and asynchronous-compute queues; keep FIFO
    // order within each logical guest queue and schedule ready queues round-robin
    // so one slow queue cannot delay another (same policy as the Vulkan backend).
    private static readonly Dictionary<string, LinkedList<PendingGuestWork>>
        _pendingGuestWorkByQueue = new(StringComparer.Ordinal);
    private static readonly List<string> _pendingGuestQueueSchedule = [];
    private static int _pendingGuestQueueCursor;
    private static int _pendingGuestWorkCount;
    private static ulong _pendingGuestWorkBytes;
    private static long _enqueuedGuestWorkSequence;
    private static long _completedGuestWorkSequence;
    private static readonly HashSet<long> _completedGuestWorkOutOfOrder = [];
    private static readonly Dictionary<string, long> _lastEnqueuedGuestWorkByQueue =
        new(StringComparer.Ordinal);
    private static long _executingGuestWorkSequence;
    private static Exception? _guestWorkFailure;
    [ThreadStatic]
    private static GuestQueueIdentity? _submittingGuestQueue;
    [ThreadStatic]
    private static bool _enqueueAsImmediateQueueFollowup;
    [ThreadStatic]
    private static LinkedListNode<PendingGuestWork>? _immediateFollowupTail;

    private static readonly Dictionary<ulong, uint> _availableGuestImages = new();
    private static readonly Dictionary<ulong, (uint Width, uint Height, ulong ByteCount)>
        _guestImageExtents = new();
    private static readonly Dictionary<ulong, GuestImage> _guestImages = new();
    private static readonly Dictionary<ulong, byte[]> _pendingGuestImageInitialData = new();
    private static readonly Dictionary<ulong, long> _guestImageWorkSequences = new();
    private static readonly Queue<Presentation> _pendingGuestImagePresentations = new();
    private static readonly Dictionary<long, GuestImage> _guestImageVersions = new();
    private static readonly Dictionary<ulong, ulong> _untrackedGuestImageContentProbes = new();
    private static long _orderedGuestFlipVersionSequence;
    private static volatile ICpuMemory? _guestMemory;

    private sealed class GuestQueueScope : IDisposable
    {
        private readonly GuestQueueIdentity? _previous;
        private bool _disposed;

        public GuestQueueScope(GuestQueueIdentity queue)
        {
            _previous = _submittingGuestQueue;
            _submittingGuestQueue = queue;
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _submittingGuestQueue = _previous;
        }
    }

    public static IDisposable EnterGuestQueue(string queueName, ulong submissionId) =>
        new GuestQueueScope(new GuestQueueIdentity(
            string.IsNullOrWhiteSpace(queueName) ? "guest.unknown" : queueName,
            submissionId));

    public static void AttachGuestMemory(ICpuMemory memory) =>
        _guestMemory = memory;

    private static int _cpuWrittenGuestImageSyncRequested;
    private static long _guestImageCpuSyncTraceCount;

    public static void RequestCpuWrittenGuestImageSync(
        ulong scopeAddress = 0,
        ulong scopeByteCount = ulong.MaxValue)
    {
        _ = scopeAddress;
        if (scopeByteCount == 0 || !GuestImageWriteTracker.Enabled)
        {
            return;
        }

        Volatile.Write(ref _cpuWrittenGuestImageSyncRequested, 1);
    }

    public static long SubmitOrderedGuestAction(Action action, string debugName)
    {
        ArgumentNullException.ThrowIfNull(action);
        lock (_gate)
        {
            return _closed || _thread is null
                ? 0
                : EnqueueGuestWorkLocked(new OrderedGuestAction(action, debugName));
        }
    }

    public static bool WaitForGuestWork(long workSequence, int timeoutMilliseconds)
    {
        if (workSequence <= 0)
        {
            return false;
        }

        var waitIndefinitely = timeoutMilliseconds == Timeout.Infinite;
        var deadline = waitIndefinitely
            ? long.MaxValue
            : Environment.TickCount64 + Math.Max(timeoutMilliseconds, 1);
        lock (_gate)
        {
            while (!_closed && _guestWorkFailure is null && !IsGuestWorkCompletedLocked(workSequence))
            {
                var remaining = waitIndefinitely ? 1_000 : deadline - Environment.TickCount64;
                if (remaining <= 0)
                {
                    Console.Error.WriteLine(
                        $"[LOADER][WARN] Metal guest work wait timed out sequence={workSequence} " +
                        $"contiguous_completed={_completedGuestWorkSequence}");
                    return false;
                }

                // Closing the presenter pulses this monitor, so an unbounded
                // correctness wait remains interruptible.
                Monitor.Wait(_gate, checked((int)Math.Min(remaining, 1_000)));
            }

            return IsGuestWorkCompletedLocked(workSequence);
        }
    }

    public static void RegisterKnownDisplayBuffer(ulong address, uint guestFormat)
    {
        if (address == 0 || guestFormat == 0)
        {
            return;
        }

        lock (_gate)
        {
            _availableGuestImages[address] = guestFormat;
        }
    }

    public static bool IsGuestImageAvailable(ulong address, uint format, uint numberType)
    {
        var guestFormat = GetGuestTextureFormat(format, numberType);
        if (address == 0 || guestFormat == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return _availableGuestImages.TryGetValue(address, out var availableFormat) &&
                availableFormat == guestFormat;
        }
    }

    public static bool IsGuestImageUploadKnown(ulong address, uint format, uint numberType)
    {
        var guestFormat = GetGuestTextureFormat(format, numberType);
        if (address == 0 || guestFormat == 0)
        {
            return false;
        }

        ulong probeByteCount = 0;
        lock (_gate)
        {
            if (!_availableGuestImages.TryGetValue(address, out var availableFormat) ||
                availableFormat != guestFormat)
            {
                return false;
            }

            if (GuestImageWriteTracker.Enabled)
            {
                return true;
            }

            if (_guestImageExtents.TryGetValue(address, out var extent))
            {
                probeByteCount = extent.ByteCount;
            }
        }

        return IsUntrackedGuestImageContentUnchanged(address, probeByteCount);
    }

    private static bool IsUntrackedGuestImageContentUnchanged(ulong address, ulong byteCount)
    {
        var memory = _guestMemory;
        if (memory is null || byteCount == 0)
        {
            return true;
        }

        var probe = ComputeSparseGuestContentProbe(memory, address, byteCount);
        lock (_gate)
        {
            if (!_untrackedGuestImageContentProbes.TryGetValue(address, out var previous))
            {
                _untrackedGuestImageContentProbes[address] = probe;
                return true;
            }

            if (previous == probe)
            {
                return true;
            }

            _untrackedGuestImageContentProbes[address] = probe;
            return false;
        }
    }

    private static ulong ComputeSparseGuestContentProbe(
        ICpuMemory memory,
        ulong address,
        ulong byteCount)
    {
        Span<byte> sample = stackalloc byte[64];
        ulong hash = 14695981039346656037UL;
        Span<ulong> offsets = stackalloc ulong[3];
        var offsetCount = 0;
        offsets[offsetCount++] = 0;
        if (byteCount > 128)
        {
            offsets[offsetCount++] = byteCount / 2;
        }

        if (byteCount > 64)
        {
            offsets[offsetCount++] = byteCount - 64;
        }

        for (var o = 0; o < offsetCount; o++)
        {
            var offset = offsets[o];
            if (offset >= byteCount)
            {
                continue;
            }

            var length = (int)Math.Min(64UL, byteCount - offset);
            if (!memory.TryRead(address + offset, sample[..length]))
            {
                hash ^= 0x9E3779B97F4A7C15UL + offset;
                continue;
            }

            for (var i = 0; i < length; i++)
            {
                hash ^= sample[i];
                hash *= 1099511628211UL;
            }

            hash ^= (ulong)length + offset;
        }

        return hash ^ byteCount;
    }

    public static bool GuestImageWantsInitialData(ulong address)
    {
        if (address == 0)
        {
            return false;
        }

        lock (_gate)
        {
            return !_availableGuestImages.ContainsKey(address) &&
                !_pendingGuestImageInitialData.ContainsKey(address);
        }
    }

    public static void ProvideGuestImageInitialData(ulong address, byte[] rgbaPixels)
    {
        lock (_gate)
        {
            _pendingGuestImageInitialData[address] = rgbaPixels;
        }
    }

    public static bool TryGetGuestImageExtent(ulong address, out uint width, out uint height, out ulong byteCount)
    {
        lock (_gate)
        {
            if (_guestImageExtents.TryGetValue(address, out var extent))
            {
                (width, height, byteCount) = extent;
                return true;
            }
        }

        width = 0;
        height = 0;
        byteCount = 0;
        return false;
    }

    public static IReadOnlyList<(ulong Address, uint Width, uint Height, ulong ByteCount)> GetGuestImageExtents()
    {
        lock (_gate)
        {
            var extents = new (ulong, uint, uint, ulong)[_guestImageExtents.Count];
            var index = 0;
            foreach (var entry in _guestImageExtents)
            {
                extents[index++] = (entry.Key, entry.Value.Width, entry.Value.Height, entry.Value.ByteCount);
            }

            return extents;
        }
    }

    public static void SubmitGuestImageFill(ulong address, uint fillValue)
    {
        lock (_gate)
        {
            if (_closed || !_guestImageExtents.ContainsKey(address))
            {
                return;
            }

            _guestImageWorkSequences[address] = EnqueueGuestWorkLocked(
                new GuestImageWrite(address, null, fillValue));
        }
    }

    public static void SubmitGuestImageWrite(ulong address, byte[] pixels)
    {
        lock (_gate)
        {
            if (_closed || !_guestImageExtents.ContainsKey(address))
            {
                return;
            }

            _guestImageWorkSequences[address] = EnqueueGuestWorkLocked(
                new GuestImageWrite(address, pixels, 0));
        }
    }

    public static bool TrySubmitGuestImage(ulong address, uint width, uint height, uint pitchInPixel)
    {
        lock (_gate)
        {
            WaitForInitialGuestCompletionLocked("guest image presentation admission");
            if (_closed || !_availableGuestImages.ContainsKey(address))
            {
                return false;
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            // Wait only for the work that last wrote this image, not the global
            // queue tail — requiring the tail lets a fast guest permanently
            // outrun the renderer.
            var requiredWorkSequence = _guestImageWorkSequences.TryGetValue(
                address,
                out var imageWorkSequence)
                ? imageWorkSequence
                : _completedGuestWorkSequence;
            var presentation = new Presentation(
                null,
                width,
                height,
                sequence,
                IsSplash: false,
                IsGuestFrame: true,
                GuestImageAddress: address,
                GuestImagePitch: pitchInPixel,
                RequiredGuestWorkSequence: requiredWorkSequence);
            PublishGuestPresentationLocked(presentation);
            _pendingGuestImagePresentations.Enqueue(presentation);
            while (_pendingGuestImagePresentations.Count > MaxPendingGuestWork)
            {
                RetireSkippedPresentationLocked(_pendingGuestImagePresentations.Dequeue());
            }
        }

        return true;
    }

    public static bool TrySubmitOrderedGuestImageFlip(
        int videoOutHandle,
        int displayBufferIndex,
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel)
    {
        lock (_gate)
        {
            if (_closed || _thread is null || !_availableGuestImages.ContainsKey(address))
            {
                return false;
            }

            var version = ++_orderedGuestFlipVersionSequence;
            return EnqueueGuestWorkLocked(
                new OrderedGuestFlip(
                    version,
                    videoOutHandle,
                    displayBufferIndex,
                    address,
                    width,
                    height,
                    pitchInPixel)) > 0;
        }
    }

    /// <summary>Same-extent, same-format image copies only; anything else returns
    /// false and the caller keeps its CPU fallback.</summary>
    public static bool TrySubmitGuestImageBlit(
        ulong sourceAddress,
        uint sourceWidth,
        uint sourceHeight,
        uint sourceFormat,
        uint sourceNumberType,
        ulong destinationAddress,
        uint destinationWidth,
        uint destinationHeight,
        uint destinationFormat,
        uint destinationNumberType)
    {
        if (sourceWidth != destinationWidth ||
            sourceHeight != destinationHeight ||
            GetGuestTextureFormat(sourceFormat, sourceNumberType) !=
                GetGuestTextureFormat(destinationFormat, destinationNumberType))
        {
            return false;
        }

        lock (_gate)
        {
            if (_closed ||
                _thread is null ||
                !_guestImages.TryGetValue(sourceAddress, out var source) ||
                !source.Initialized ||
                !_guestImages.TryGetValue(destinationAddress, out var destination) ||
                source.Width != destination.Width ||
                source.Height != destination.Height ||
                source.Format != destination.Format)
            {
                return false;
            }

            Require2DGuestImage(source, "guest image blit admission source");
            Require2DGuestImage(destination, "guest image blit admission destination");
            _guestImageWorkSequences[destinationAddress] = EnqueueGuestWorkLocked(
                new GuestImageBlit(sourceAddress, destinationAddress));
            return true;
        }
    }

    private static bool IsGuestWorkCompletedLocked(long sequence) =>
        sequence <= 0 ||
        sequence <= _completedGuestWorkSequence ||
        _completedGuestWorkOutOfOrder.Contains(sequence);

    private static long EnqueueGuestWorkLocked(object work)
    {
        WaitForInitialGuestCompletionLocked("guest queue admission");
        var payloadBytes = GetGuestWorkPayloadBytes(work);
        // Work executed by the render-loop consumer can enqueue an ordered
        // same-queue follow-up; blocking the consumer on producer backpressure
        // would deadlock a full queue, so follow-ups are always admitted.
        while (!_enqueueAsImmediateQueueFollowup &&
               !_closed &&
               _guestWorkFailure is null &&
               _thread is not null &&
               (_pendingGuestWorkCount >= MaxPendingGuestWork ||
                // Always admit one item when no payload is outstanding, even
                // when that single item exceeds the configured budget: with
                // nothing left to drain, waiting for room would never return.
                // The budget bounds the normal multi-item backlog.
                (_pendingGuestWorkBytes != 0 &&
                 payloadBytes > MaxPendingGuestWorkBytes -
                     Math.Min(_pendingGuestWorkBytes, MaxPendingGuestWorkBytes))))
        {
            // Full queue: ask the render loop to drain now rather than at its
            // next timer tick, or this producer stalls a frame per admission.
            ScheduleGuestWorkDrain();
            Monitor.Wait(_gate);
        }

        if (_closed || _guestWorkFailure is not null)
        {
            return 0;
        }

        var queue = _submittingGuestQueue ?? GuestQueueIdentity.Default;
        if (_textureSourceGraphEnabled)
            return EnqueueTextureSourceWorkLocked(work, payloadBytes, queue);
        var selectedMipAdmission = PrepareSelectedStorageMipAdmissionLocked(work, queue);
        var retainedVolume = RetainQueuedSingletonVolumeOriginalsLocked(work);
        var oldSequence = _enqueuedGuestWorkSequence;
        var oldCount = _pendingGuestWorkCount;
        var oldBytes = _pendingGuestWorkBytes;
        var oldTail = _immediateFollowupTail;
        var hadLast = _lastEnqueuedGuestWorkByQueue.TryGetValue(queue.Name, out var oldLast);
        var newQueue = !_pendingGuestWorkByQueue.TryGetValue(queue.Name, out var pendingQueue);
        LinkedListNode<PendingGuestWork>? node = null;
        try
        {
            var sequence = ++_enqueuedGuestWorkSequence;
            _lastEnqueuedGuestWorkByQueue[queue.Name] = sequence;
            if (newQueue)
            {
                pendingQueue = new LinkedList<PendingGuestWork>();
                _pendingGuestWorkByQueue.Add(queue.Name, pendingQueue);
                _pendingGuestQueueSchedule.Add(queue.Name);
            }

            var pending = new PendingGuestWork(work, payloadBytes, sequence, queue);
            node = new LinkedListNode<PendingGuestWork>(pending);
            if (_enqueueAsImmediateQueueFollowup &&
                _immediateFollowupTail is { List: not null } tail &&
                ReferenceEquals(tail.List, pendingQueue))
            {
                pendingQueue!.AddAfter(tail, node);
                _immediateFollowupTail = node;
            }
            else if (_enqueueAsImmediateQueueFollowup)
            {
                pendingQueue!.AddFirst(node);
                _immediateFollowupTail = node;
            }
            else
            {
                pendingQueue!.AddLast(node);
            }

            if (retainedVolume) _singletonVolumeAfterOrdinaryQueueInsert?.Invoke();
            PublishSelectedStorageMipAdmissionLocked(selectedMipAdmission);
            _pendingGuestWorkCount++;
            var total = _pendingGuestWorkBytes + payloadBytes;
            _pendingGuestWorkBytes = total < _pendingGuestWorkBytes ? ulong.MaxValue : total;
            if (!_enqueueAsImmediateQueueFollowup)
            {
                // Drain promptly: guests that submit work and then wait on its
                // side effects (release-mem labels, write-backs) round-trip
                // through this queue several times per frame, and a timer-tick
                // drain cadence turns each round-trip into a full frame interval.
                ScheduleGuestWorkDrain();
            }

            return sequence;
        }
        catch (Exception failure) when (retainedVolume)
        {
            if (node?.List is { } admittedQueue) admittedQueue.Remove(node);
            if (newQueue)
            {
                _pendingGuestWorkByQueue.Remove(queue.Name);
                _pendingGuestQueueSchedule.Remove(queue.Name);
            }
            if (hadLast) _lastEnqueuedGuestWorkByQueue[queue.Name] = oldLast;
            else _lastEnqueuedGuestWorkByQueue.Remove(queue.Name);
            _enqueuedGuestWorkSequence = oldSequence;
            _pendingGuestWorkCount = oldCount;
            _pendingGuestWorkBytes = oldBytes;
            _immediateFollowupTail = oldTail;
            // Selected metadata may already be installed. Fatal admission
            // forbids its execution rather than publishing a partial success.
            _guestWorkFailure ??= failure;
            CancelQueuedSingletonVolumeOriginalsLocked(failure);
            Monitor.PulseAll(_gate);
            throw;
        }
    }

    internal static ulong GetGuestWorkPayloadBytes(object work)
    {
        if (work is GuestImageWrite writePayload)
            return (ulong)(writePayload.Pixels?.LongLength ?? 0);
        if (work is GlobalDataShareCopyFromGuest gdsPayload)
            return (ulong)gdsPayload.Bytes.LongLength;
        if (work is not OffscreenGuestDraw and not ComputeGuestDispatch)
            return 0;
        // Count owned backing arrays, not guest allocation sizes or logical
        // buffer lengths. Shared per-work snapshots count once; pooled arrays
        // retain their full allocation until this work leaves the queue.
        var arrays = new HashSet<Array>(ReferenceEqualityComparer.Instance);
        ulong total = 0;
        void Add(Array? array, uint elementBytes = 1)
        {
            if (array is not null && arrays.Add(array))
                total = checked(total + (ulong)array.LongLength * elementBytes);
        }
        void AddTextures(GuestDrawTexture[] textures)
        {
            foreach (var texture in textures)
            {
                Add(texture.RgbaPixels);
                Add(texture.TiledSource);
                Add(texture.Descriptor, 4);
                Add(texture.MipUploads, 24);
                if (texture.Detile is { } detile)
                {
                    Add(detile.XByteTerm, 4);
                    Add(detile.YByteTerm, 4);
                    Add(detile.BlockTable, 4);
                }
            }
        }
        void AddBuffers(GuestMemoryBuffer[] buffers)
        {
            foreach (var buffer in buffers) Add(buffer.Data);
        }
        void AddStage(GuestStageBindings stage)
        {
            Add(stage.BufferIndices, 4);
            Add(stage.ImageElements, 4);
            Add(stage.Samplers, 16);
            Add(stage.ShaderData, 4);
            Add(stage.PushData, 4);
            Add(stage.FlattenedTable, 4);
            Add(stage.AddressRanges, 12);
        }
        switch (work)
        {
            case OffscreenGuestDraw draw:
                AddTextures(draw.Draw.Textures);
                AddBuffers(draw.Draw.GlobalMemoryBuffers);
                foreach (var vertex in draw.Draw.VertexBuffers) Add(vertex.Data);
                Add(draw.Draw.IndexBuffer?.Data);
                foreach (var stage in draw.Draw.Stages) AddStage(stage);
                break;
            case ComputeGuestDispatch dispatch:
                AddTextures(dispatch.Textures);
                AddBuffers(dispatch.GlobalMemoryBuffers);
                if (dispatch.StageBindings is { } dispatchStage) AddStage(dispatchStage);
                break;
        }
        return total;
    }

    private static bool TryTakeGuestWork(out PendingGuestWork work)
    {
        lock (_gate)
        {
            if (_guestWorkFailure is not null)
            {
                work = default;
                return false;
            }
            // Visit each logical queue head at most once. A dependent head
            // cannot expose later work in its own FIFO queue, but must not
            // prevent its producer or an unrelated queue from progressing.
            var remainingHeads = _pendingGuestQueueSchedule.Count;
            PendingGuestWork? blockedHead = null;
            while (remainingHeads-- > 0 && _pendingGuestQueueSchedule.Count > 0)
            {
                if (_pendingGuestQueueCursor >= _pendingGuestQueueSchedule.Count)
                {
                    _pendingGuestQueueCursor = 0;
                }

                var queueName = _pendingGuestQueueSchedule[_pendingGuestQueueCursor];
                if (!_pendingGuestWorkByQueue.TryGetValue(queueName, out var queue) ||
                    queue.First is not { } first)
                {
                    _pendingGuestWorkByQueue.Remove(queueName);
                    _pendingGuestQueueSchedule.RemoveAt(_pendingGuestQueueCursor);
                    continue;
                }

                bool sourcesReady;
                try
                {
                    sourcesReady = GuestTextureSourcesReadyLocked(first.Value);
                }
                catch (Exception exception) when (_guestWorkFailure is null)
                {
                    // Dependency lookup can allocate or expose an invalid
                    // owner. Latch before Drain's finally can commit a prior
                    // batch; intended fatal paths have already latched.
                    FailGuestTextureSourceSelectionLocked(first.Value, "dependency readiness lookup failed", exception);
                    throw;
                }
                if (!sourcesReady)
                {
                    blockedHead ??= first.Value;
                    _pendingGuestQueueCursor =
                        (_pendingGuestQueueCursor + 1) % _pendingGuestQueueSchedule.Count;
                    continue;
                }

                work = first.Value;
                queue.RemoveFirst();
                _pendingGuestWorkCount--;
                if (queue.Count == 0)
                {
                    _pendingGuestWorkByQueue.Remove(queueName);
                    _pendingGuestQueueSchedule.RemoveAt(_pendingGuestQueueCursor);
                }
                else
                {
                    _pendingGuestQueueCursor =
                        (_pendingGuestQueueCursor + 1) % _pendingGuestQueueSchedule.Count;
                }

                return true;
            }

            // Pending means producer work still needs encoding, not a native
            // completion event. If every live head depends on another pending
            // queue, none can make progress: fail instead of stalling forever.
            if (blockedHead is { } blocked)
                FailGuestTextureSourceSelectionLocked(blocked, "all logical queue heads are blocked by a source dependency cycle");
            work = default;
            return false;
        }
    }

    private static void CompleteGuestWork(in PendingGuestWork pending)
    {
        lock (_gate)
        {
            if (_textureSourceGraphEnabled)
            {
                ReleaseGuestPayloadWork(pending.Work);
                ReleaseTextureSourceWorkOwnerLocked(pending.Work);
            }
            else
            {
                try
                {
                    if (_singletonVolumeOriginalCleanupFailure is { } priorCleanup)
                        System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(priorCleanup).Throw();
                    ReleaseQueuedSingletonVolumeOriginalsLocked(pending.Work);
                }
                catch (Exception failure)
                {
                    _guestWorkFailure ??= failure;
                    CancelQueuedSingletonVolumeOriginalsLocked(failure);
                    Monitor.PulseAll(_gate);
                    throw; // no completed-sequence acknowledgement
                }
            }
            _pendingGuestWorkBytes = pending.PayloadBytes >= _pendingGuestWorkBytes
                ? 0
                : _pendingGuestWorkBytes - pending.PayloadBytes;
            if (pending.Sequence == _completedGuestWorkSequence + 1)
            {
                _completedGuestWorkSequence = pending.Sequence;
                while (_completedGuestWorkOutOfOrder.Remove(_completedGuestWorkSequence + 1))
                {
                    _completedGuestWorkSequence++;
                }
            }
            else if (pending.Sequence > _completedGuestWorkSequence)
            {
                _completedGuestWorkOutOfOrder.Add(pending.Sequence);
            }

            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Drains queued guest work on the render loop, bounded by count and a
    /// wall-clock budget so a backlog cannot starve the event pump or the present.</summary>
    internal static void DrainGuestWork(nint device, nint queue)
    {
        if (!MetalPresentationProfile.AllocationsEnabled)
        {
            DrainGuestWorkCore(device, queue, _renderWorkBudgetTicks);
            return;
        }

        var allocationScope = MetalPresentationProfile.MeasureDrainAllocations();
        var completed = false;
        try
        {
            DrainGuestWorkCore(device, queue, _renderWorkBudgetTicks);
            completed = true;
        }
        finally { allocationScope.Finish(completed); }
    }

    private static void DrainGuestWorkCore(nint device, nint queue, long budgetTicks)
    {
        lock (_gate) WaitForInitialGuestCompletionLocked("guest queue execution");
        if (_guestWorkFailure is { } previousFailure)
        {
            throw new GpuFatalException("Metal guest execution has already failed.", previousFailure);
        }

        var deadline = System.Diagnostics.Stopwatch.GetTimestamp() + budgetTicks;
        var completedWork = 0;
        try
        {
            if (_textureSourceGraphEnabled) RecycleTextureSourceNativeBatches();
            RecycleCompletedUploadPages();
            RecycleCompletedSnapshotResources();
            ScanCompletedFaultBitmaps();
            DrainGuestImageCpuSync(device);
        }
        catch (Exception exception) when (_textureSourceGraphEnabled)
        {
            lock (_gate)
            {
                LatchTextureSourceDrainFailureLocked(exception);
            }
            if (exception is GpuFatalException) throw;
            throw GpuFatal.Fatal("Metal guest drain preparation failed.", exception);
        }
        try
        {
            while (completedWork < MaxGuestWorkPerRender)
            {
                if (!TryTakeGuestWork(out var pendingGuestWork))
                {
                    return;
                }

                Volatile.Write(ref _executingGuestWorkSequence, pendingGuestWork.Sequence);
                using var guestQueueScope = EnterGuestQueue(
                    pendingGuestWork.Queue.Name,
                    pendingGuestWork.Queue.SubmissionId);
                var previousDiagnosticWork = BeginImageDiagnosticWork(pendingGuestWork.Sequence);
                _enqueueAsImmediateQueueFollowup = true;
                _immediateFollowupTail = null;
                if (_textureSourceGraphEnabled) _executingTextureSourceWork = pendingGuestWork.Work;
                try
                {
                    if (_textureSourceGraphEnabled) RequireQualifiedTextureSourceConsumerWork(pendingGuestWork.Work);
                    // Draws and compute dispatches encode into the shared batch
                    // command buffer; everything else must observe their output
                    // on the serial queue, so it flushes the batch first.
                    switch (pendingGuestWork.Work)
                    {
                        case GuestImageWrite write:
                            FlushBatchedGuestCommands();
                            ExecuteGuestImageWrite(device, queue, write);
                            break;
                        case OrderedGuestAction action:
                            FlushBatchedGuestCommands();
                            ExecuteOrderedGuestAction(action);
                            break;
                        case OrderedGuestFlip flip:
                            FlushBatchedGuestCommands();
                            ExecuteOrderedGuestFlip(device, queue, flip);
                            break;
                        case GuestImageBlit blit:
                            FlushBatchedGuestCommands();
                            ExecuteGuestImageBlit(queue, blit);
                            break;
                        case OffscreenGuestDraw offscreenDraw:
                            ExecuteOffscreenDraw(device, queue, offscreenDraw);
                            break;
                        case ComputeGuestDispatch computeDispatch:
                            ExecuteComputeDispatch(device, queue, computeDispatch);
                            break;
                        case GlobalDataShareFill fill:
                            ExecuteGlobalDataShareFill(device, queue, fill);
                            break;
                        case GlobalDataShareCopyFromGuest copyFromGuest:
                            ExecuteGlobalDataShareCopyFromGuest(device, queue, copyFromGuest);
                            break;
                        case GlobalDataShareCopyToGuest copyToGuest:
                            ExecuteGlobalDataShareCopyToGuest(device, queue, copyToGuest);
                            break;
                    }
                }
                catch (Exception exception)
                {
                    lock (_gate)
                    {
                        if (_textureSourceGraphEnabled) LatchTextureSourceDrainFailureLocked(exception);
                        else
                        {
                            _guestWorkFailure = exception;
                            CancelQueuedSingletonVolumeOriginalsLocked(exception);
                            Monitor.PulseAll(_gate);
                        }
                    }

                    // commandBuffer is autoreleased by its creating pool. Drop
                    // the borrowed batch handle; never commit partial work.
                    _batchOpen = false;
                    _batchCommandBuffer = 0;
                    var actionName = pendingGuestWork.Work is OrderedGuestAction ordered
                        ? $" action='{ordered.DebugName}'" : string.Empty;
                    throw GpuFatal.Fatal(
                        $"Metal guest work failed: kind={pendingGuestWork.Work.GetType().Name}" +
                        $" sequence={pendingGuestWork.Sequence} queue='{pendingGuestWork.Queue.Name}'" +
                        $" submission={pendingGuestWork.Queue.SubmissionId}{actionName}.", exception);
                }
                finally
                {
                    _enqueueAsImmediateQueueFollowup = false;
                    _immediateFollowupTail = null;
                    if (_textureSourceGraphEnabled) _executingTextureSourceWork = null;
                    EndImageDiagnosticWork(previousDiagnosticWork);
                    Volatile.Write(ref _executingGuestWorkSequence, 0);
                }

                CompleteGuestWork(pendingGuestWork);
                completedWork++;
                if (MetalPresentationProfile.AllocationsEnabled)
                    MetalPresentationProfile.RecordCompletedDrainWork(pendingGuestWork.Work switch
                    {
                        OffscreenGuestDraw => MetalPresentationProfile.DrainWorkKind.Draw,
                        ComputeGuestDispatch => MetalPresentationProfile.DrainWorkKind.Compute,
                        _ => MetalPresentationProfile.DrainWorkKind.Other,
                    });
                if (System.Diagnostics.Stopwatch.GetTimestamp() >= deadline)
                {
                    return;
                }
            }
        }
        catch (Exception exception) when (_textureSourceGraphEnabled && _guestWorkFailure is null)
        {
            lock (_gate)
            {
                LatchTextureSourceDrainFailureLocked(exception);
            }
            if (exception is GpuFatalException) throw;
            throw GpuFatal.Fatal("Metal guest drain completion failed.", exception);
        }
        finally
        {
            // Successful drains commit their encoded work. A failed item must
            // never issue partially encoded commands or acknowledge success.
            if (_guestWorkFailure is null)
            {
                try { _ = FlushBatchedGuestCommands(); }
                catch (Exception exception) when (_textureSourceGraphEnabled)
                {
                    lock (_gate)
                    {
                        LatchTextureSourceDrainFailureLocked(exception);
                    }
                    if (exception is GpuFatalException) throw;
                    throw GpuFatal.Fatal("Metal guest drain final flush failed.", exception);
                }
            }
        }
    }

    private static void ExecuteOrderedGuestAction(OrderedGuestAction ordered) => ordered.Action();

    private static void ExecuteGuestImageWrite(nint device, nint queue, GuestImageWrite write)
    {
        RejectAddressOnlySelectedStorageAlias(write.Address);
        GuestImage? image;
        lock (_gate)
        {
            _guestImages.TryGetValue(write.Address, out image);
        }

        if (image is null)
        {
            return;
        }

        Require2DGuestImage(image, "CPU image write/clear");
        if (write.Pixels is { } pixels)
        {
            var bytesPerPixel = MetalRenderTargetFormat.GetBytesPerPixel(image.Format);
            if ((ulong)pixels.Length < (ulong)image.Width * image.Height * bytesPerPixel)
            {
                return;
            }

            // Swap in a freshly written texture instead of mutating one an
            // in-flight present may still sample (the command buffer keeps its
            // own reference to the old texture until it completes).
            var replacement = CreateGuestTexture(device, image.Format, image.Width, image.Height);
            if (replacement == 0)
            {
                return;
            }

            ReplaceTextureContents(replacement, image.Width, image.Height, pixels, image.Width, bytesPerPixel);
            var previous = image.Texture;
            image.Texture = replacement;
            MetalNative.SendVoid(previous, MetalNative.Selector("release"));
        }
        else
        {
            ClearTexture(queue, image.Texture, write.FillValue);
        }

        // The pixel path swapped the texture out entirely; either way the
        // cached snapshot no longer reflects this image.
        image.ReleaseSnapshot();
        image.MarkContentChangedWithDiagnostic(write.Pixels is null ? "guest-clear" : "guest-pixel-write");
    }

    private static void ExecuteOrderedGuestFlip(nint device, nint queue, OrderedGuestFlip flip)
    {
        // The flipped VideoOut address is the display buffer's start, but games
        // render into the pixel surface, which sits past the buffer's surface
        // metadata (64KB+ on PS5). Prefer a drawn image inside the buffer's
        // extent over seeding a new (empty) image at the exact start address.
        GuestImage? image;
        lock (_gate)
        {
            image = FindGuestImageForFlipLocked(flip.Address, flip.Width, flip.Height);
        }

        image ??= EnsureGuestImage(device, flip.Address, flip.Width, flip.Height, flip.PitchInPixel);
        if (image is null || !image.Initialized)
        {
            return;
        }

        Require2DGuestImage(image, "VideoOut flip");
        // Capture the mutable image into an immutable generation at this exact
        // queue position; later work cannot change the frame this flip selected.
        var captured = new GuestImage
        {
            Texture = CreateGuestTexture(device, image.Format, image.Width, image.Height),
            Width = image.Width,
            Height = image.Height,
            Format = image.Format,
            Initialized = true,
        };
        if (captured.Texture == 0)
        {
            return;
        }

        CopyTexture(queue, image.Texture, captured.Texture);

        lock (_gate)
        {
            _guestImageVersions[flip.Version] = captured;
            // Retain a short version history, always preserving the newest.
            while (_guestImageVersions.Count > MaxPendingGuestFlipVersions)
            {
                var oldest = 0L;
                foreach (var version in _guestImageVersions.Keys)
                {
                    if (oldest == 0 || version < oldest)
                    {
                        oldest = version;
                    }
                }

                if (oldest == flip.Version)
                {
                    break;
                }

                if (_guestImageVersions.Remove(oldest, out var retired))
                {
                    MetalNative.SendVoid(retired.Texture, MetalNative.Selector("release"));
                }
            }

            var sequence = (_latestPresentation?.Sequence ?? 0) + 1;
            var presentation = new Presentation(
                null,
                image.Width,
                image.Height,
                sequence,
                IsSplash: false,
                IsGuestFrame: true,
                GuestImageAddress: flip.Address,
                GuestImageVersion: flip.Version);
            PublishGuestPresentationLocked(presentation);
            _pendingGuestImagePresentations.Enqueue(presentation);
            while (_pendingGuestImagePresentations.Count > MaxPendingGuestWork)
            {
                RetireSkippedPresentationLocked(_pendingGuestImagePresentations.Dequeue());
            }
        }
    }

    private static void ExecuteGuestImageBlit(nint queue, GuestImageBlit blit)
    {
        RejectAddressOnlySelectedStorageAlias(blit.SourceAddress);
        RejectAddressOnlySelectedStorageAlias(blit.DestinationAddress);
        GuestImage? source, destination;
        lock (_gate)
        {
            _guestImages.TryGetValue(blit.SourceAddress, out source);
            _guestImages.TryGetValue(blit.DestinationAddress, out destination);
        }

        if (source is null || destination is null || !source.Initialized)
        {
            return;
        }

        Require2DGuestImage(source, "guest image blit source");
        Require2DGuestImage(destination, "guest image blit destination");
        CopyTexture(queue, source.Texture, destination.Texture);
        destination.MarkContentChangedWithDiagnostic("guest-blit");
    }

    private static bool _tracedFlipAlias;

    /// <summary>Resolves a flip to produced content: the exact-address image when
    /// GPU work wrote it, else the nearest same-extent produced image within the
    /// display buffer's plausible metadata window above the start address (games
    /// render into the pixel surface past the buffer's metadata block), else the
    /// exact-address image even if it only holds a speculative seed.</summary>
    private static GuestImage? FindGuestImageForFlipLocked(ulong address, uint width, uint height)
    {
        _guestImages.TryGetValue(address, out var exact);
        if (exact is not null) Require2DGuestImage(exact, "VideoOut exact image");
        if (exact is { Initialized: true, GpuWritten: true })
        {
            return exact;
        }

        GuestImage? best = null;
        var bestDelta = ulong.MaxValue;
        ulong bestAddress = 0;
        foreach (var entry in _guestImages)
        {
            if (entry.Key <= address)
            {
                continue;
            }

            var delta = entry.Key - address;
            if (delta <= 0x20_0000 &&
                delta < bestDelta &&
                entry.Value.Width == width &&
                entry.Value.Height == height &&
                entry.Value.TextureType == MtlTextureType2D && entry.Value.Depth == 1 &&
                entry.Value is { Initialized: true, GpuWritten: true })
            {
                best = entry.Value;
                bestDelta = delta;
                bestAddress = entry.Key;
            }
        }

        if (best is not null)
        {
            if (!_tracedFlipAlias)
            {
                _tracedFlipAlias = true;
                Console.Error.WriteLine(
                    $"[LOADER][INFO] Metal flip alias: display buffer 0x{address:X16} " +
                    $"presents drawn surface 0x{bestAddress:X16} (+0x{bestDelta:X}).");
            }

            return best;
        }

        return exact is { Initialized: true } ? exact : null;
    }

    /// <summary>Returns the mutable image for a guest address, creating and seeding
    /// it (pending initial data first, guest memory second) on first use.</summary>
    private static GuestImage? EnsureGuestImage(
        nint device,
        ulong address,
        uint width,
        uint height,
        uint pitchInPixel)
    {
        RejectAddressOnlySelectedStorageAlias(address);
        uint formatTag;
        byte[]? initialData;
        lock (_gate)
        {
            if (_guestImages.TryGetValue(address, out var existing))
            {
                Require2DGuestImage(existing, "display image materialization");
                return existing;
            }

            if (!_availableGuestImages.TryGetValue(address, out formatTag))
            {
                return null;
            }

            _pendingGuestImageInitialData.Remove(address, out initialData);
        }

        if (width == 0 || height == 0 || width > 16384 || height > 16384)
        {
            return null;
        }

        var format = DecodeGuestFormatTag(formatTag);
        var image = new GuestImage
        {
            Texture = CreateGuestTexture(device, format, width, height),
            Width = width,
            Height = height,
            Format = format,
            DiagnosticOrigin = CaptureDisplayImageOrigin(address, width, height, pitchInPixel, formatTag),
        };
        if (image.Texture == 0)
        {
            return null;
        }

        var pitch = pitchInPixel == 0 ? width : Math.Max(pitchInPixel, width);
        var bytesPerPixel = MetalRenderTargetFormat.GetBytesPerPixel(format);
        if (initialData is not null &&
            bytesPerPixel == 4 &&
            (ulong)initialData.Length >= (ulong)width * height * 4)
        {
            // Pending initial data is RGBA8; only 4-byte-texel images can take
            // it verbatim. Wider formats seed from guest memory below, whose
            // layout is the image's native one.
            ReplaceTextureContents(image.Texture, width, height, initialData, width, bytesPerPixel);
            image.Initialized = true;
            image.ContentVersion++;
        }
        else if (_guestMemory is { } memory)
        {
            // PS5 render targets alias guest memory: CPU-prefilled pixels are
            // visible before the first draw, so the first use seeds from there.
            var byteCount = checked((int)((ulong)pitch * height * bytesPerPixel));
            var guestPixels = GuestDataPool.Shared.Rent(byteCount);
            try
            {
                if (memory.TryRead(address, guestPixels.AsSpan(0, byteCount)))
                {
                    ReplaceTextureContents(image.Texture, width, height, guestPixels, pitch, bytesPerPixel);
                    image.Initialized = true;
                    image.ContentVersion++;
                }
            }
            finally
            {
                GuestDataPool.Shared.Return(guestPixels);
            }
        }

        lock (_gate)
        {
            if (_guestImages.TryGetValue(address, out var retired) && !ReferenceEquals(retired, image))
                NoteFiniteStorageOwnerRetirementLocked(retired);
            _guestImages[address] = image;
            _guestImageExtents[address] = (width, height, (ulong)pitch * height * bytesPerPixel);
        }

        return image;
    }

    private static void RetireSkippedPresentationLocked(Presentation presentation)
    {
        if (presentation.GuestImageVersion != 0 &&
            _guestImageVersions.Remove(presentation.GuestImageVersion, out var version))
        {
            MetalNative.SendVoid(version.Texture, MetalNative.Selector("release"));
        }
    }

    // Guest texture-format tags, byte-identical to the Vulkan backend's encoding so
    // VideoOut's registered display-buffer tags mean the same thing on both.
    private static uint GetGuestTextureFormat(uint format, uint numberType) =>
        IsKnownGuestTextureFormat(format)
            ? 0x8000_0000u | ((format & 0x1FFu) << 8) | (numberType & 0xFFu)
            : 0;

    private static bool IsKnownGuestTextureFormat(uint format) =>
        format is >= 1 and <= 19 or 34 or >= 169 and <= 182;

    private static MtlPixelFormat DecodeGuestFormatTag(uint tag)
    {
        var format = (tag >> 8) & 0x1FFu;
        var numberType = tag & 0xFFu;
        return MetalGuestFormats.TryDecodeRenderTargetFormat(format, numberType, out var decoded)
            ? decoded.Format
            : MtlPixelFormat.Rgba8Unorm;
    }

    /// <summary>Picks the newest ready queued guest presentation (retiring the ones
    /// it supersedes), falling back to the latest CPU frame or splash.</summary>
    private static bool TryTakePresentation(long presentedSequence, out Presentation presentation)
    {
        lock (_gate)
        {
            Presentation? selected = null;
            while (_pendingGuestImagePresentations.Count > 0)
            {
                var head = _pendingGuestImagePresentations.Peek();
                if (!IsGuestWorkCompletedLocked(head.RequiredGuestWorkSequence))
                {
                    break;
                }

                _pendingGuestImagePresentations.Dequeue();
                if (selected is not null)
                {
                    RetireSkippedPresentationLocked(selected);
                }

                selected = head;
            }

            if (selected is not null)
            {
                presentation = selected;
                return true;
            }

            if (_latestPresentation is { } latest &&
                latest.Sequence > presentedSequence &&
                (latest.Pixels is not null ||
                 (latest.TranslatedDraw is not null || latest.DrawKind != GuestDrawKind.None) &&
                 IsGuestWorkCompletedLocked(latest.RequiredGuestWorkSequence)))
            {
                presentation = latest;
                return true;
            }

            presentation = default!;
            return false;
        }
    }

    private static bool TryResolveGuestPresentation(
        nint device,
        Presentation presentation,
        out nint texture,
        out uint width,
        out uint height,
        out bool owned)
    {
        GuestImage? image = null;
        owned = false;
        lock (_gate)
        {
            if (presentation.GuestImageVersion != 0)
            {
                owned = _guestImageVersions.Remove(presentation.GuestImageVersion, out image);
            }

            if (image is null && presentation.GuestImageAddress != 0)
            {
                _guestImages.TryGetValue(presentation.GuestImageAddress, out image);
            }
        }

        // An unordered flip can name a registered display buffer that no work has
        // materialized yet; seed it from guest memory so CPU-rendered buffers show.
        image ??= presentation.GuestImageAddress != 0
            ? EnsureGuestImage(
                device,
                presentation.GuestImageAddress,
                presentation.Width,
                presentation.Height,
                presentation.GuestImagePitch)
            : null;

        if (image is not { Initialized: true })
        {
            if (owned && image is not null)
            {
                MetalNative.SendVoid(image.Texture, MetalNative.Selector("release"));
            }

            texture = 0;
            width = 0;
            height = 0;
            owned = false;
            return false;
        }

        Require2DGuestImage(image, "VideoOut presentation");
        texture = image.Texture;
        width = image.Width;
        height = image.Height;
        return true;
    }

    private static void SwitchPresentSource(
        nint texture,
        uint width,
        uint height,
        bool ownsTexture,
        ref nint presentTexture,
        ref uint presentWidth,
        ref uint presentHeight,
        ref nint ownedTexture)
    {
        if (ownedTexture != 0 && ownedTexture != texture)
        {
            // In-flight command buffers hold their own references; dropping ours
            // is safe even if the previous frame is still presenting.
            MetalNative.SendVoid(ownedTexture, MetalNative.Selector("release"));
            ownedTexture = 0;
        }

        if (ownsTexture)
        {
            ownedTexture = texture;
        }

        presentTexture = texture;
        presentWidth = width;
        presentHeight = height;
    }

    private static nint CreateGuestTexture(nint device, MtlPixelFormat format, uint width, uint height)
    {
        var descriptor = MetalNative.SendTextureDescriptor(
            MetalNative.Class("MTLTextureDescriptor"),
            MetalNative.Selector("texture2DDescriptorWithPixelFormat:width:height:mipmapped:"),
            (nuint)format,
            width,
            height,
            mipmapped: false);
        // ShaderRead | RenderTarget: the present pass samples these and fills
        // clear them through a render pass.
        MetalNative.Send(descriptor, MetalNative.Selector("setUsage:"), (nint)5);
        // MTLStorageModeShared (0): these textures are CPU-populated
        // (replaceRegion) and GPU-sampled. The MTLTextureDescriptor default is
        // Managed, which on unified memory needs an explicit host->device sync
        // we never issue, so the GPU reads stale (uninitialized/white) texels —
        // Xcode's frame capture flags exactly this. Shared is coherent on
        // Apple Silicon with no sync.
        MetalNative.Send(descriptor, MetalNative.Selector("setStorageMode:"), (nint)0);
        return MetalNative.Send(device, MetalNative.Selector("newTextureWithDescriptor:"), descriptor);
    }

    /// <summary>Uploads pixel rows sized by the texture's real texel width. The
    /// row count is clamped to what <paramref name="pixels"/> actually holds, so
    /// replaceRegion can never read past the managed buffer.</summary>
    private static void ReplaceTextureContents(
        nint texture,
        uint width,
        uint height,
        byte[] pixels,
        uint pitchInPixel,
        uint bytesPerPixel)
    {
        var bytesPerRow = (ulong)Math.Max(pitchInPixel, width) * bytesPerPixel;
        var lastRowBytes = (ulong)width * bytesPerPixel;
        if (lastRowBytes == 0 || (ulong)pixels.Length < lastRowBytes)
        {
            return;
        }

        var maxRows = (((ulong)pixels.Length - lastRowBytes) / bytesPerRow) + 1;
        var rows = (uint)Math.Min(height, maxRows);
        if (rows == 0)
        {
            return;
        }

        unsafe
        {
            fixed (byte* source = pixels)
            {
                MetalNative.SendReplaceRegion(
                    texture,
                    MetalNative.Selector("replaceRegion:mipmapLevel:withBytes:bytesPerRow:"),
                    new MtlRegion { X = 0, Y = 0, Z = 0, Width = width, Height = rows, Depth = 1 },
                    0,
                    (nint)source,
                    (nuint)bytesPerRow);
            }
        }
    }

    /// <summary>Clears via a render pass (GPU-side, hazard-tracked against in-flight
    /// sampling) using the raw 32-bit guest fill pattern interpreted as RGBA8.</summary>
    private static void ClearTexture(nint queue, nint texture, uint fillValue)
    {
        var color = new MtlClearColor
        {
            Red = (fillValue & 0xFF) / 255.0,
            Green = ((fillValue >> 8) & 0xFF) / 255.0,
            Blue = ((fillValue >> 16) & 0xFF) / 255.0,
            Alpha = ((fillValue >> 24) & 0xFF) / 255.0,
        };
        var commandBuffer = MetalNative.Send(queue, MetalNative.Selector("commandBuffer"));
        var encoder = MetalNative.Send(
            commandBuffer,
            MetalNative.Selector("renderCommandEncoderWithDescriptor:"),
            CreateClearPass(texture, color));
        MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
        MetalNative.SendVoid(commandBuffer, MetalNative.Selector("commit"));
    }

    private static void CopyTexture(nint queue, nint source, nint destination)
    {
        var commandBuffer = MetalNative.Send(queue, MetalNative.Selector("commandBuffer"));
        EncodeCopyTexture(commandBuffer, source, destination);
        MetalNative.SendVoid(commandBuffer, MetalNative.Selector("commit"));
    }

    /// <summary>Encodes a full-texture copy into an existing command buffer;
    /// used by the batched draw path, where the copy must be ordered after the
    /// batch's earlier passes rather than committed ahead of them.</summary>
    private static void EncodeCopyTexture(nint commandBuffer, nint source, nint destination)
    {
        var encoder = MetalNative.Send(commandBuffer, MetalNative.Selector("blitCommandEncoder"));
        MetalNative.SendVoidCopyTexture(
            encoder,
            MetalNative.Selector("copyFromTexture:toTexture:"),
            source,
            destination);
        MetalNative.SendVoid(encoder, MetalNative.Selector("endEncoding"));
    }
}
