// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using MslImageResourceClass = SharpEmu.ShaderCompiler.Resources.ImageResourceClass;

namespace SharpEmu.Gpu.Metal.V2;

// One Tier 2 argument buffer per stage, filled field by field in the layout's
// order: window pointers for buffers, gpuResourceIDs for textures and samplers.
// The argument buffer binds at index 0 and the shared push block at index 1.
internal sealed partial class MetalRenderHost
{
    private const int StageVertex = 0;
    private const int StageFragment = 1;
    private const int StageCompute = 2;
    private const int StageCount = 3;

    private static readonly nint SelGpuResourceId = MetalNative.Selector("gpuResourceID");
    private static readonly nint SelGpuAddress = MetalNative.Selector("gpuAddress");
    private static readonly nint SelNewSamplerState = MetalNative.Selector("newSamplerStateWithDescriptor:");
    private static readonly nint SelSetMeshBuffer = MetalNative.Selector("setMeshBuffer:offset:atIndex:");

    // The host descriptors of one stage, reused across draws so a draw allocates nothing.
    private sealed class MetalStageBindings : IPreparedBindings
    {
        public ShaderStageResources StageResources;
        public ShaderProgramInfo Program = null!;
        public nint[] Textures = [];
        public uint[] TextureLevels = [];
        public ulong[] TextureIds = [];
        public nint[] Samplers = [];
        public ulong[] SamplerIds = [];
        public ResourceSlotIdentifier[] Images = [];
        public ImageRequest[] Requests = [];
        public bool[] IsStorage = [];
        public MetalGuestRange[] Buffers = [];
        public uint[] BufferLengths = [];
        public uint[] ShaderData = [];
        public ulong[] BindlessTableAddresses = [];
        public int ImageCount;
        public int SamplerCount;
        public int BufferCount;
        public bool Committed;

        // The images a host-decoded movie frame replaces in this stage, or -1.
        public int MovieLuma = -1;
        public int MovieChroma = -1;

        public ShaderStageResources Stage => StageResources;

        public void EnsureCapacity(int images, int samplers, int buffers, int shaderDwords)
        {
            if (Textures.Length < images)
            {
                Textures = new nint[images];
                TextureIds = new ulong[images];
                TextureLevels = new uint[images];
                Images = new ResourceSlotIdentifier[images];
                Requests = new ImageRequest[images];
                IsStorage = new bool[images];
            }

            if (Samplers.Length < samplers)
            {
                Samplers = new nint[samplers];
                SamplerIds = new ulong[samplers];
            }

            if (Buffers.Length < buffers)
            {
                Buffers = new MetalGuestRange[buffers];
                BufferLengths = new uint[buffers];
            }

            if (ShaderData.Length < shaderDwords)
            {
                ShaderData = new uint[shaderDwords];
            }

            ImageCount = images;
            SamplerCount = samplers;
            BufferCount = buffers;
        }
    }

    private sealed class MetalResourcePreparation(MetalRenderHost owner) : IResourcePreparation
    {
        public void Dispose() => owner.EndPreparation();
    }

    private readonly MetalStageBindings[] _stageBindings =
        [new MetalStageBindings(), new MetalStageBindings(), new MetalStageBindings()];

    // The argument layout of one binding layout, with the per-field resource indices that
    // a draw would otherwise look up by scanning the descriptors.
    private sealed class ArgumentPlan(Gen5MslArgumentLayout layout, int[]?[] fieldResources)
    {
        public readonly Gen5MslArgumentLayout Layout = layout;
        public readonly int[]?[] FieldResources = fieldResources;
    }

    private readonly Dictionary<BindingLayout, ArgumentPlan> _argumentLayouts = new();
    private readonly Dictionary<(uint, uint, uint, uint, bool), (nint Handle, ulong ResourceId)> _samplers = new();
    private readonly nint[] _stageArgumentBuffers = new nint[StageCount];
    private readonly ulong[] _stageArgumentOffsets = new ulong[StageCount];
    private readonly bool[] _stageArgumentValid = new bool[StageCount];
    private nint _pushBuffer;
    private ulong _pushOffset;
    private bool _hasPushData;
    private nint[] _usedResources = new nint[64];
    private nuint[] _usedResourceUsage = new nuint[64];
    private int _usedResourceCount;
    private MetalResourcePreparation? _preparation;
    private int _preparedStageMask;

    public IResourcePreparation BeginPreparation()
    {
        if (_preparation is not null)
        {
            throw Fatal("A Metal resource preparation is already open.");
        }

        // Acquire the decoded frame before any stage selects its movie planes.
        PumpHostMovieFrame();

        // Keep each submission's command buffer bounded so completions and flips are
        // not stuck behind a frame's worth of recording, and so the rings and staging
        // a draw's uploads borrowed return before the next draw borrows more.
        if (_recordedCommands >= 2048)
        {
            FlushCommandBuffer(MetalFlushCause.Budget);
        }
        else
        {
            CommitIfTransientOverBudget();
            // Not inside a render pass: ending one stores and reloads every attachment,
            // and the pass reaches a boundary of its own soon enough.
            if (_renderEncoder == 0)
            {
                CommitCoalescedIfOverdue();
            }
        }

        _ = EnsureCommandBuffer();
        _preparation ??= new MetalResourcePreparation(this);
        _preparedStageMask = 0;
        _usedResourceCount = 0;
        _bindlessDeclarations.Clear();
        Array.Clear(_stageArgumentValid);
        _hasPushData = false;
        // The attachment subresources are recorded as the draw acquires them, which
        // happens inside this preparation.
        _attachmentCount = 0;
        _depthAttachmentLevel = 0;
        _depthAttachmentSlice = 0;
        return _preparation;
    }

    private void EndPreparation()
    {
        // A draw that acquired its attachments and then never began its pass leaves no
        // upload waiting past its own preparation.
        _imageCache.DrainDeferredRefreshes();
        // The draw or dispatch is recorded, so the stores it makes into staged copies
        // can be written back at the next encoder boundary.
        _stagedStorageCopies?.Arm();
        _preparation = null;
        _preparedStageMask = 0;
    }

    public void ResetBindings()
    {
        _imageCache.DrainDeferredRefreshes();
        DisarmPendingCopy();
        _pendingGraphicsPipeline = null;
        _pendingComputePipeline = null;
        _vertexBufferCount = 0;
        _indexBuffer = 0;
        _attachmentCount = 0;
        _depthAttachmentLevel = 0;
        _depthAttachmentSlice = 0;
    }

    private static int StageIndexOf(ShaderStageKind kind) => kind switch
    {
        ShaderStageKind.Vertex => StageVertex,
        ShaderStageKind.Pixel => StageFragment,
        ShaderStageKind.Compute => StageCompute,
        _ => throw GpuFatal.Fatal($"The shader stage kind is unknown: stage={kind}."),
    };

    private static ShaderProgramInfo RequireProgram(ShaderStageResources stage)
    {
        var program = stage.Program ?? throw GpuFatal.Fatal("The stage has no program.");
        if (program.Resources is null || program.Bindings is null)
        {
            throw GpuFatal.Fatal(
                $"The stage program has no resource plan: stage={program.Stage} hash=0x{program.Hash:X16}.");
        }

        return program;
    }

    private static ShaderImageShape ShapeOf(ImageResource image) => new(
        Volume: image.Dimension == ImageDimension.Dim3D,
        Arrayed: image.Cube || image.Dimension is ImageDimension.Dim1DArray or ImageDimension.Dim2DArray or
            ImageDimension.Dim2DMsaaArray,
        Cube: image.Cube,
        Storage: image.ResourceClass == MslImageResourceClass.Storage,
        DynamicMip: image.MipMode == ImageMipMode.DynamicStorage,
        NumericClass: image.NumericClass switch
        {
            ImageNumericClass.Uint => TextureNumericClass.Uint,
            ImageNumericClass.Sint => TextureNumericClass.Sint,
            _ => TextureNumericClass.Float,
        },
        OneDimensional: image.Dimension is ImageDimension.Dim1D or ImageDimension.Dim1DArray,
        R128: image.R128,
        Multisampled: image.Dimension is ImageDimension.Dim2DMsaa or ImageDimension.Dim2DMsaaArray,
        DepthCompare: image.DepthCompare,
        Atomic: image.Atomic);

    public IPreparedBindings PrepareBindings(ShaderStageResources stage)
    {
        var program = RequireProgram(stage);
        var resources = program.Resources!;
        var layout = program.Bindings!;
        var snapshot = stage.Resources;
        var info = resources.Info;
        if (snapshot.Images.Length != info.Images.Count || snapshot.Samplers.Length != info.Samplers.Count ||
            snapshot.Buffers.Length != info.Buffers.Count)
        {
            throw Fatal(
                $"The resource snapshot does not match the program: hash=0x{program.Hash:X16} " +
                $"images={snapshot.Images.Length}/{info.Images.Count} samplers={snapshot.Samplers.Length}/{info.Samplers.Count} " +
                $"buffers={snapshot.Buffers.Length}/{info.Buffers.Count}.");
        }

        var stageIndex = StageIndexOf(program.Stage);
        var prepared = _stageBindings[stageIndex];
        prepared.StageResources = stage;
        prepared.Program = program;
        prepared.Committed = false;
        prepared.EnsureCapacity(
            info.Images.Count, info.Samplers.Count, info.Buffers.Count, checked((int)layout.ShaderDataDwordCount));
        _preparedStageMask |= 1 << stageIndex;
        var movie = FindHostMovieBindings(info, snapshot);
        prepared.MovieLuma = movie.Luma;
        prepared.MovieChroma = movie.Chroma;
        for (var index = 0; index < info.Images.Count; index++)
        {
            if (index == movie.Luma || index == movie.Chroma)
            {
                PrepareHostMovieBinding(prepared, info.Images[index], snapshot.Images[index], index);
                continue;
            }

            ResolveImageBinding(prepared, info.Images[index], snapshot.Images[index], index);
        }

        for (var index = 0; index < info.Samplers.Count; index++)
        {
            prepared.Samplers[index] = ResolveSampler(
                info, prepared, info.Samplers[index], snapshot.Samplers[index], index, out prepared.SamplerIds[index]);
        }

        var shaderData = prepared.ShaderData;
        Array.Clear(shaderData, 0, (int)layout.ShaderDataDwordCount);
        for (var index = 0; index < layout.UserDataRegisters.Count; index++)
        {
            var register = layout.UserDataRegisters[index];
            var userIndex = (int)(register - program.UserDataBase);
            if (register < program.UserDataBase || userIndex >= snapshot.UserData.Length)
            {
                throw Fatal(
                    $"A user register is outside the draw's user data: register={register} base={program.UserDataBase} " +
                    $"count={snapshot.UserData.Length} hash=0x{program.Hash:X16}.");
            }

            shaderData[index] = snapshot.UserData[userIndex];
        }

        if (layout.UsesShaderBase)
        {
            shaderData[layout.ShaderBaseDword] = (uint)stage.ShaderBase;
            shaderData[layout.ShaderBaseDword + 1] = (uint)(stage.ShaderBase >> 32);
        }

        stage.WriteDispatchThreadLimits(shaderData.AsSpan(0, (int)layout.ShaderDataDwordCount));
        return prepared;
    }

    // Finds the cache image of one shader image descriptor; the view follows in BindResources.
    private void ResolveImageBinding(MetalStageBindings prepared, ImageResource image, uint[] words, int index)
    {
        if (words.Length < 4)
        {
            throw Fatal(
                $"An image descriptor is too short: image={index} words={words.Length} hash=0x{prepared.Program.Hash:X16}.");
        }

        var storage = image.ResourceClass == MslImageResourceClass.Storage;
        var resolution = ImageRequestBuilders.Texture(words, ShapeOf(image));
        var request = resolution.Request;
        var imageIdentifier = _imageCache.FindImage(ref request, resolution.ExactFormat);
        resolution = resolution with { Request = request };
        var found = _imageCache.GetImage(imageIdentifier);
        imageIdentifier = ImageRequestBuilders.ValidateTextureOwner(
            found.Description, ResourceSlotIdentifier.Invalid, imageIdentifier, resolution);
        prepared.Images[index] = imageIdentifier;
        prepared.Requests[index] = request;
        prepared.IsStorage[index] = storage;
        prepared.Textures[index] = 0;
    }

    private nint ResolveSampler(
        ShaderResourceInfo info,
        MetalStageBindings prepared,
        SamplerResource sampler,
        uint[] words,
        int index,
        out ulong resourceId)
    {
        if (words.Length < 4)
        {
            throw Fatal(
                $"A sampler descriptor is too short: sampler={index} words={words.Length} hash=0x{prepared.Program.Hash:X16}.");
        }

        Span<uint> native = stackalloc uint[4] { words[0], words[1], words[2], words[3] };
        if (!sampler.DepthCompare)
        {
            native[0] &= ~(0x7u << 12);
        }

        if (sampler.ForcePointFiltering)
        {
            var mipmapped = ((native[2] >> 26) & 0x3u) != 0;
            native[2] &= ~(0xFFu << 20);
            native[2] |= 1u << 24;
            if (mipmapped)
            {
                native[2] |= 1u << 26;
            }
        }

        var descriptor = new SamplerDescriptorWords(native);
        return GetSampler(in descriptor, SamplesIntegerViews(info, prepared, index), out resourceId);
    }

    // A sampler takes the numeric class of the views it samples; integer only when
    // every paired view is integer, since a float view needs filtering.
    private static bool SamplesIntegerViews(ShaderResourceInfo info, MetalStageBindings prepared, int sampler)
    {
        var paired = false;
        foreach (var pair in info.SampledPairs)
        {
            if (pair.Sampler != sampler || pair.Image >= prepared.ImageCount)
            {
                continue;
            }

            if (!ViewFormatRules.IsIntegerFormat(prepared.Requests[pair.Image].View.Format))
            {
                return false;
            }

            paired = true;
        }

        return paired;
    }

    public void PrepareDeviceAddresses()
    {
        // Guest memory is the GPU's memory, so a device-address program needs no page
        // table upload: the pointers the shader reads already address the windows.
    }

    public void BindResources(IPreparedBindings prepared)
    {
        var stage = (MetalStageBindings)prepared;
        var info = stage.Program.Resources!.Info;
        var snapshot = stage.StageResources.Resources;
        for (var index = 0; index < stage.ImageCount; index++)
        {
            if (index == stage.MovieLuma || index == stage.MovieChroma)
            {
                stage.Textures[index] = AcquireHostMovieView(index == stage.MovieLuma ? 0 : 1);
                stage.TextureIds[index] = MetalNative.SendGpuResourceId(stage.Textures[index], SelGpuResourceId);
                continue;
            }

            var request = stage.Requests[index];
            var identifier = stage.Images[index];
            if (!_imageCache.TryGetImage(identifier, out _))
            {
                ResolveImageBinding(stage, info.Images[index], snapshot.Images[index], index);
                request = stage.Requests[index];
                identifier = stage.Images[index];
            }

            var role = stage.IsStorage[index] ? ImageRole.StorageImage : ImageRole.Texture;
            if (stage.IsStorage[index])
            {
                request = request with { View = request.View with { LevelCount = 1 } };
                stage.Requests[index] = request;
            }

            stage.Textures[index] = _imageCache.AcquireTextureView(
                identifier, request with { Role = role }, out stage.TextureIds[index]);
            NotePendingInputDump(stage.Textures[index], identifier, index, role);
        }

        for (var index = 0; index < stage.BufferCount; index++)
        {
            var words = snapshot.Buffers[index];
            if (words.Length < 4)
            {
                throw Fatal(
                    $"A buffer descriptor is too short: buffer={index} words={words.Length} hash=0x{stage.Program.Hash:X16}.");
            }

            var descriptor = BufferDescriptorWords.From(words);
            var requested = descriptor.Footprint() ?? throw Fatal(
                $"A storage buffer descriptor footprint overflows: buffer={index} stride={descriptor.Stride} " +
                $"records={descriptor.RecordCount} hash=0x{stage.Program.Hash:X16}.");
            if (descriptor.Address == 0 || requested == 0)
            {
                stage.Buffers[index] = default;
                stage.BufferLengths[index] = 0;
                continue;
            }

            if (requested > ulong.MaxValue - descriptor.Address || !_memory.CanRead(descriptor.Address, 1))
            {
                var resource = info.Buffers[index];
                throw Fatal(
                    $"The buffer range starts in unmapped memory: address=0x{descriptor.Address:X16} size=0x{requested:X16} " +
                    $"stage={stage.Program.Stage} hash=0x{stage.Program.Hash:X16} shader=0x{stage.StageResources.ShaderBase:X16} " +
                    $"buffer={index} source={resource.Source} pc=0x{resource.FirstUsePc:X} written={resource.Written} " +
                    $"descriptor=[{string.Join(',', words.ToArray().Select(word => $"{word:X8}"))}] " +
                    $"user_data=[{string.Join(',', snapshot.UserData.ToArray().Select(word => $"{word:X8}"))}].");
            }

            var size = ClampMappedSize(descriptor.Address, requested);
            var written = info.Buffers[index].Written;
            FlushGpuWrites(descriptor.Address, size, MetalWritebackCause.StorageDescriptor);
            var range = ResolveStorageBuffer(descriptor.Address, size, written);
            if (written)
            {
                _imageCache.TraceDccWriter(
                    descriptor.Address, size,
                    $"program=0x{stage.Program.Hash:X16} buffer={index} stride={descriptor.Stride} records={descriptor.RecordCount} " +
                    $"format={descriptor.Format} buffers={stage.BufferCount} user_data=[{string.Join(',', snapshot.UserData.ToArray().Select(word => $"{word:X8}"))}]");
                _imageCache.InvalidateMemoryFromGpu(descriptor.Address, size);
                NoteGuestMemoryWrite(descriptor.Address, size, MetalGpuWriteOrigin.StorageBuffer);
            }

            _slotTrap?.NoteBinding(
                $"storage[{index}] hash=0x{stage.Program.Hash:X16}{(written ? " written" : string.Empty)}",
                descriptor.Address, size, (ulong)range.Buffer, range.Offset, range.GpuAddress);
            stage.Buffers[index] = range;
            stage.BufferLengths[index] = (uint)Math.Min(size, uint.MaxValue);
            ReportBufferBinding(stage, index, in descriptor, requested, size, range);
        }

        BindDeviceAddressRanges(stage, info, snapshot);
        if (snapshot.BindlessImages.Length != 0 || stage.Program.Bindings!.BindlessImageTableCount != 0)
        {
            BindBindlessImages(stage, info, snapshot);
        }
    }

    /// <summary>
    /// The guest ranges a device-address program reaches without a descriptor binding:
    /// a flat or global access, or a V# the shader loads out of guest memory when it
    /// runs. The resource planner evaluates each handle's base and extent for the draw,
    /// which is what lets these be named at all.
    ///
    /// They are not bindings here — guest memory is the GPU's memory, so the pointer in
    /// the page table already addresses the window — but they carry the same obligations
    /// a storage binding does, and without them the backend has a hole its own rules
    /// forbid: an image that owns the bytes never publishes them before the shader reads
    /// them, a shader store never invalidates the image or records its range, and a
    /// later command-processor poll or host read of those bytes does not wait for the
    /// submission still writing them. The twin of the Vulkan presenter's
    /// <c>BindDeviceAddressRanges</c>.
    /// </summary>
    private void BindDeviceAddressRanges(
        MetalStageBindings stage, ShaderResourceInfo info, ResourceSnapshot snapshot)
    {
        var unplanned = false;
        var declaredWrite = false;
        foreach (var range in snapshot.DeviceAddressRanges)
        {
            if (!range.Planned)
            {
                // Nothing evaluated this handle, so the store's destination is unknown, and
                // so is what it reads: every copy still owed lands first.
                OrderAfterDeferredCopies(0, ulong.MaxValue);
                unplanned |= range.Written;
                continue;
            }

            // A pointer to an unmapped page resolves to null in the shader, which drops
            // the access; there is nothing to publish, invalidate or wait for.
            if (range.Size == 0 || range.Base == 0 || !_memory.CanRead(range.Base, 1))
            {
                continue;
            }

            var size = ClampMappedSize(range.Base, range.Size);
            OrderAfterDeferredCopies(range.Base, size);
            OrderAfterStagedStores(range.Base, size);
            FlushGpuWrites(range.Base, size, MetalWritebackCause.StorageDescriptor);
            if (!range.Written)
            {
                continue;
            }

            _imageCache.InvalidateMemoryFromGpu(range.Base, size);
            NoteGuestMemoryWrite(range.Base, size, MetalGpuWriteOrigin.DeviceAddress);
            _slotTrap?.NoteBinding(
                $"device_address handle={range.Handle} hash=0x{stage.Program.Hash:X16} written",
                range.Base, size, 0, 0, 0);
            declaredWrite = true;
        }

        if (!unplanned && (!info.WritesDeviceAddresses || declaredWrite))
        {
            return;
        }

        // The program stores through the page table and no evaluated range says where.
        // Conservative is the only correct answer: every later read of any guest address
        // waits for this submission.
        NoteUnboundedGuestMemoryWrite();
        _deviceAddressWrites++;
        MetalV2Diagnostics.WarnOnce(
            "device-address-unbounded",
            $"A shader stores through the device-address page table with no evaluated range, so every " +
            $"later guest read waits for its submission: hash=0x{stage.Program.Hash:X16} " +
            $"unplanned={unplanned} ranges={snapshot.DeviceAddressRanges.Length}.");
    }

    // SHARPEMU_METAL_V2_DUMP_BUFFERS=<hash>[,<hash>...]: what one program's scalar and
    // storage buffers actually resolve to. A translated loop whose trip count comes out
    // of a constant buffer is only as right as the descriptor behind it, and the listing
    // alone cannot say whether the dword it loads is the guest's count or a stale window.
    private static readonly HashSet<ulong> _dumpBufferHashes = ParseDumpBufferHashes();
    private readonly Dictionary<(ulong Hash, int Buffer), int> _dumpedBufferCounts = new();
    private const int MaxBufferDumpsPerBinding = 4;

    // How much of each buffer to print. The head alone says nothing about the matrix a
    // vertex program loads from the middle of a constant buffer.
    private static readonly int _dumpBufferBytes =
        int.TryParse(Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_DUMP_BUFFER_BYTES"), out var bytes) &&
        bytes is > 0 and <= 4096
            ? (bytes + 3) & ~3
            : 32;

    private static HashSet<ulong> ParseDumpBufferHashes()
    {
        var value = Environment.GetEnvironmentVariable("SHARPEMU_METAL_V2_DUMP_BUFFERS");
        var hashes = new HashSet<ulong>();
        if (string.IsNullOrWhiteSpace(value))
        {
            return hashes;
        }

        foreach (var part in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var text = part.StartsWith("0x", StringComparison.OrdinalIgnoreCase) ? part[2..] : part;
            if (ulong.TryParse(text, System.Globalization.NumberStyles.HexNumber,
                    System.Globalization.CultureInfo.InvariantCulture, out var hash))
            {
                hashes.Add(hash);
            }
        }

        return hashes;
    }

    private void ReportBufferBinding(
        MetalStageBindings stage,
        int index,
        in BufferDescriptorWords descriptor,
        ulong requested,
        ulong size,
        in MetalGuestRange range)
    {
        var hash = stage.Program.Hash;
        if (_dumpBufferHashes.Count == 0 || !_dumpBufferHashes.Contains(hash))
        {
            return;
        }

        var key = (hash, index);
        _dumpedBufferCounts.TryGetValue(key, out var seen);
        if (seen >= MaxBufferDumpsPerBinding)
        {
            return;
        }

        _dumpedBufferCounts[key] = seen + 1;
        var count = (int)Math.Min((ulong)_dumpBufferBytes, Math.Max(size, 4));
        var host = new byte[count];
        var read = _guestMemory.TryPeek(descriptor.Address, host);
        var windowed = _guestMemory.ResolveWritable(descriptor.Address, size).IsValid;

        // What the shader will actually read: the bytes behind the pointer the argument
        // buffer carries. A window aliases guest memory, so the two agree; an upload ring
        // holds a copy taken when the binding was prepared, and a guest write after that
        // never reaches it. Printing both is the only way to tell those apart.
        var device = ReadDeviceBytes(range, count);

        Console.Error.WriteLine(string.Format(
            System.Globalization.CultureInfo.InvariantCulture,
            "[METAL-V2][BUF] hash=0x{0:X16} stage={1} buf={2} addr=0x{3:X16} stride={4} records={5} " +
            "fmt={6} req=0x{7:X} size=0x{8:X} window={9} resolved={10} read={11} " +
            "buffer=0x{12:X}+0x{13:X} same={14}\n  cpu={15}\n  gpu={16}",
            hash,
            stage.Program.Stage,
            index,
            descriptor.Address,
            descriptor.Stride,
            descriptor.RecordCount,
            descriptor.Format,
            requested,
            size,
            windowed,
            range.IsValid,
            read,
            range.Buffer,
            range.Offset,
            device is not null && host.AsSpan().SequenceEqual(device),
            DescribeDwords(host),
            device is null ? "private" : DescribeDwords(device)));
    }

    /// <summary>The first bytes the GPU sees behind a resolved range, or null when the
    /// buffer has no host-visible contents.</summary>
    private static unsafe byte[]? ReadDeviceBytes(in MetalGuestRange range, int count)
    {
        if (!range.IsValid)
        {
            return null;
        }

        var contents = MetalNative.Send(range.Buffer, MetalNative.Selector("contents"));
        if (contents == 0)
        {
            return null;
        }

        return new ReadOnlySpan<byte>((byte*)contents + range.Offset, count).ToArray();
    }

    private static string DescribeDwords(ReadOnlySpan<byte> bytes)
    {
        var dwords = new System.Text.StringBuilder();
        for (var offset = 0; offset + 3 < bytes.Length; offset += sizeof(uint))
        {
            if (offset % 64 == 0)
            {
                dwords.Append(System.Globalization.CultureInfo.InvariantCulture, $"\n    +{offset:X3}:");
            }

            dwords.Append(
                System.Globalization.CultureInfo.InvariantCulture,
                $" {BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]):X8}");
        }

        return dwords.ToString();
    }

    public void CommitBindings(GpuBindPoint bindPoint, in PipelineHandle pipeline, ReadOnlySpan<IPreparedBindings> stages)
    {
        _ = RequirePipelineEntry(in pipeline);
        Span<uint> pushData = stackalloc uint[(int)PushData.DwordCount];
        pushData.Clear();
        var hasPushData = false;
        foreach (var prepared in stages)
        {
            var stage = (MetalStageBindings)prepared;
            var stageIndex = StageIndexOf(stage.Program.Stage);
            if ((bindPoint == GpuBindPoint.Graphics && stageIndex == StageCompute) ||
                (bindPoint == GpuBindPoint.Compute && stageIndex != StageCompute))
            {
                throw Fatal($"A stage does not belong to the bind point: stage={stage.Program.Stage} bindPoint={bindPoint}.");
            }

            WriteArgumentBuffer(stage, stageIndex);
            var layout = stage.Program.Bindings!;
            if (layout.UsesPushData)
            {
                for (var index = 0; index < layout.ShaderDataDwordCount; index++)
                {
                    pushData[(int)layout.PushDataStartDword + index] = stage.ShaderData[index];
                }

                hasPushData = true;
            }

            stage.Committed = true;
        }

        if (hasPushData)
        {
            var range = _guestMemory.Allocate(PushData.ByteSize, 256, out var span);
            System.Runtime.InteropServices.MemoryMarshal.AsBytes(pushData).CopyTo(span);
            _pushBuffer = range.Buffer;
            _pushOffset = range.Offset;
            _hasPushData = true;
        }
    }

    // The stage's Tier 2 argument buffer, mirroring the binding layout in descriptor order.
    private void WriteArgumentBuffer(MetalStageBindings stage, int stageIndex)
    {
        var layout = stage.Program.Bindings!;
        var plan = ArgumentPlanOf(layout);
        var argumentLayout = plan.Layout;
        if (argumentLayout.Fields.Count == 0)
        {
            _stageArgumentValid[stageIndex] = false;
            return;
        }

        var range = _guestMemory.Allocate(Math.Max(argumentLayout.ByteSize, 16u), 256, out var argument);
        argument.Clear();
        var imageCursor = 0;
        var fields = argumentLayout.Fields;
        for (var fieldIndex = 0; fieldIndex < fields.Count; fieldIndex++)
        {
            var field = fields[fieldIndex];
            var span = argument.Slice((int)field.ByteOffset, (int)field.ByteSize);
            switch (field.FieldKind)
            {
                case MslArgumentFieldKind.BufferPointers:
                    for (var index = 0; index < field.Count && index < stage.BufferCount; index++)
                    {
                        BinaryPrimitives.WriteUInt64LittleEndian(
                            span[(index * sizeof(ulong))..], stage.Buffers[index].GpuAddress);
                    }

                    break;
                case MslArgumentFieldKind.BufferByteCounts:
                    for (var index = 0; index < field.Count && index < stage.BufferCount; index++)
                    {
                        BinaryPrimitives.WriteUInt32LittleEndian(
                            span[(index * sizeof(uint))..], stage.BufferLengths[index]);
                    }

                    break;
                case MslArgumentFieldKind.Textures:
                    WriteTextureField(stage, field, plan.FieldResources[fieldIndex], span, ref imageCursor, stageIndex);
                    break;
                case MslArgumentFieldKind.Samplers:
                    // The layout lists only canonical samplers (equal descriptors share one
                    // element), so each element names the resource it binds.
                    var samplerResources = plan.FieldResources[fieldIndex];
                    for (var index = 0; index < field.Count; index++)
                    {
                        var resource = samplerResources is not null && index < samplerResources.Length
                            ? samplerResources[index]
                            : index;
                        if (resource >= stage.SamplerCount)
                        {
                            continue;
                        }

                        var sampler = stage.Samplers[resource];
                        if (sampler != 0)
                        {
                            BinaryPrimitives.WriteUInt64LittleEndian(
                                span[(index * sizeof(ulong))..],
                                stage.SamplerIds[resource]);
                        }
                    }

                    break;
                case MslArgumentFieldKind.Pointer:
                    BinaryPrimitives.WriteUInt64LittleEndian(span, PointerFieldValue(stage, field.Kind));
                    break;
                case MslArgumentFieldKind.StepReport:
                    BinaryPrimitives.WriteUInt64LittleEndian(
                        span, StepReportAddress(stage.Program.Hash, StepReportStageName(stageIndex)));
                    break;
                case MslArgumentFieldKind.PairLocks:
                    // One table for every program, resident with the windows, so two programs
                    // reaching the same guest qword take the same lock.
                    BinaryPrimitives.WriteUInt64LittleEndian(span, _guestMemory.PairLockTableGpuAddress);
                    break;
                case MslArgumentFieldKind.BindlessImageTables:
                    for (var index = 0; index < field.Count; index++)
                    {
                        if (index >= stage.BindlessTableAddresses.Length || stage.BindlessTableAddresses[index] == 0)
                        {
                            throw Fatal(
                                $"A bindless image table was not bound: slot={index} hash=0x{stage.Program.Hash:X16}.");
                        }

                        BinaryPrimitives.WriteUInt64LittleEndian(
                            span[(index * sizeof(ulong))..], stage.BindlessTableAddresses[index]);
                    }

                    break;
                default:
                    BinaryPrimitives.WriteUInt32LittleEndian(span, WordFieldValue(stage, field.Kind));
                    break;
            }
        }

        _stageArgumentBuffers[stageIndex] = range.Buffer;
        _stageArgumentOffsets[stageIndex] = range.Offset;
        _stageArgumentValid[stageIndex] = true;
    }

    // A binding lists one resource index per declared element; a dynamic-mip storage
    // image lists the same index once per mip, and each occurrence takes its own view.
    private void WriteTextureField(
        MetalStageBindings stage,
        MslArgumentField field,
        int[]? bindingResources,
        Span<byte> span,
        ref int imageCursor,
        int stageIndex)
    {
        for (var index = 0; index < field.Count; index++)
        {
            var resource = bindingResources is not null && index < bindingResources.Length
                ? bindingResources[index]
                : imageCursor + index;
            if (resource < 0 || resource >= stage.ImageCount)
            {
                continue;
            }

            var texture = stage.Textures[resource];
            if (texture == 0)
            {
                continue;
            }

            var dummy = TinyPassDummyTexture(texture, resource);
            if (dummy != 0)
            {
                BinaryPrimitives.WriteUInt64LittleEndian(
                    span[(index * sizeof(ulong))..], MetalNative.SendGpuResourceId(dummy, SelGpuResourceId));
                NoteUsedResource(dummy, Mtl.ResourceUsageRead);
                continue;
            }

            BinaryPrimitives.WriteUInt64LittleEndian(
                span[(index * sizeof(ulong))..], stage.TextureIds[resource]);
            var usage = Mtl.ResourceUsageRead | (stage.IsStorage[resource] ? Mtl.ResourceUsageWrite : 0);
            NoteUsedResource(texture, usage);
        }

        _ = stageIndex;
        imageCursor += (int)field.Count;
    }

    private ulong PointerFieldValue(MetalStageBindings stage, DescriptorBindingKind kind)
    {
        switch (kind)
        {
            case DescriptorBindingKind.GlobalDataShare:
                return MetalNative.SendGpuAddress(_guestMemory.GdsBuffer, SelGpuAddress);
            case DescriptorBindingKind.DeviceAddressPageTable:
                return UploadDeviceAddressTable();
            case DescriptorBindingKind.FaultBuffer:
                return MetalNative.SendGpuAddress(_guestMemory.NullBuffer, SelGpuAddress);
            case DescriptorBindingKind.FlattenedResourceTable:
                return UploadDwords(stage.StageResources.Resources.FlattenedResourceTable);
            case DescriptorBindingKind.ShaderData:
                return UploadDwords(stage.ShaderData.AsSpan(0, (int)stage.Program.Bindings!.ShaderDataDwordCount));
            default:
                throw Fatal($"The argument layout has a pointer field of an unknown kind: kind={kind}.");
        }
    }

    private uint WordFieldValue(MetalStageBindings stage, DescriptorBindingKind kind) => kind switch
    {
        DescriptorBindingKind.GlobalDataShare => (uint)MetalGuestMemory.GdsBytes,
        DescriptorBindingKind.DeviceAddressPageTable => _deviceAddressEntryCount,
        DescriptorBindingKind.FaultBuffer => 0,
        DescriptorBindingKind.FlattenedResourceTable =>
            (uint)stage.StageResources.Resources.FlattenedResourceTable.Length,
        DescriptorBindingKind.ShaderData => stage.Program.Bindings!.ShaderDataDwordCount,
        _ => throw Fatal($"The argument layout has a word field of an unknown kind: kind={kind}."),
    };

    private const int AddressRangeEntryBytes = 24;
    private const int MaxAddressRanges = (int)Gen5MslTranslator.MaxAddressRangeCount;
    private long _deviceAddressSpanVersion = long.MinValue;

    private long _deviceAddressWrites;

    /// <summary>How many bound stages could store through the device-address page table.</summary>
    internal long DeviceAddressWrites => _deviceAddressWrites;

    private ulong _deviceAddressTable;
    private uint _deviceAddressEntryCount;
    private ulong _deviceAddressTableSerial;
    private readonly List<(ulong FirstPage, ulong PageCount, ulong Base)> _deviceAddressEntries = new();

    /// <summary>
    /// The sorted range table the shader binary-searches: one entry per mapped guest
    /// span, each giving the window address its first page lives at. The shader then
    /// computes base + (page - firstPage) * pageSize + pageOffset, which is exact
    /// because a span is contiguous in the backing as well as in guest space.
    /// </summary>
    private ulong UploadDeviceAddressTable()
    {
        if (_deviceAddressTable != 0 && _deviceAddressTableSerial == _submissionSerial)
        {
            return _deviceAddressTable;
        }

        var gpuMemory = SharpEmu.HLE.GpuMemory.GuestGpuMemoryHook.Current;
        var spanVersion = gpuMemory?.SpanVersion ?? -1;
        if (spanVersion != _deviceAddressSpanVersion)
        {
            RebuildDeviceAddressEntries(gpuMemory);
            _deviceAddressSpanVersion = spanVersion;
        }

        var bytes = Math.Max(_deviceAddressEntries.Count * AddressRangeEntryBytes, AddressRangeEntryBytes);
        var range = _guestMemory.Allocate((ulong)bytes, 256, out var span);
        span.Clear();
        for (var index = 0; index < _deviceAddressEntries.Count; index++)
        {
            var (firstPage, pageCount, address) = _deviceAddressEntries[index];
            var entry = span[(index * AddressRangeEntryBytes)..];
            BinaryPrimitives.WriteUInt64LittleEndian(entry, firstPage);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[8..], pageCount);
            BinaryPrimitives.WriteUInt64LittleEndian(entry[16..], address);
        }

        // A device-address program can dereference any mapped guest page, so every
        // window the table names has to be resident for this submission even though
        // nothing resolved a range through it.
        _guestMemory.MarkWindowsTouched(_deviceAddressWindows);
        _deviceAddressTable = range.GpuAddress;
        _deviceAddressEntryCount = (uint)_deviceAddressEntries.Count;
        _deviceAddressTableSerial = _submissionSerial;
        return _deviceAddressTable;
    }

    private ulong _deviceAddressWindows;

    // Each guest span becomes the fewest window-backed runs that cover it. A span whose
    // backing is not contiguous (fragmented flexible memory) or crosses a window splits
    // into several entries; pages with no window are left out, never copied, because a
    // shader store through a copied page would not reach guest memory.
    private void RebuildDeviceAddressEntries(SharpEmu.HLE.GpuMemory.GuestGpuMemory? gpuMemory)
    {
        _deviceAddressEntries.Clear();
        // The scan resolves every mapped span, which marks windows this submission may
        // not otherwise touch; remember the set the table needs and restore the rest.
        var touchedBefore = _guestMemory.TouchedWindows;
        _guestMemory.ClearTouchedWindows();
        var spanCount = 0;
        gpuMemory?.ForEachSpan((address, size) =>
        {
            spanCount++;
            var page = DeviceAddressPaging.PageSize;
            var start = (address + page - 1) & ~(page - 1);
            var end = (address + size) & ~(page - 1);
            while (start < end && _deviceAddressEntries.Count < MaxAddressRanges)
            {
                var remaining = end - start;
                var run = _guestMemory.ResolveWritable(start, remaining);
                var length = remaining;
                if (!run.IsValid)
                {
                    // Largest resolvable prefix, in whole pages.
                    var good = 0UL;
                    var bad = remaining;
                    while (bad - good > page)
                    {
                        var middle = ((good + bad) / 2) & ~(page - 1);
                        if (middle == good)
                        {
                            break;
                        }

                        if (_guestMemory.ResolveWritable(start, middle).IsValid)
                        {
                            good = middle;
                        }
                        else
                        {
                            bad = middle;
                        }
                    }

                    length = good;
                    if (length == 0)
                    {
                        start += page;
                        continue;
                    }

                    run = _guestMemory.ResolveWritable(start, length);
                }

                _deviceAddressEntries.Add((start >> DeviceAddressPaging.PageBits,
                    length >> DeviceAddressPaging.PageBits, run.GpuAddress));
                start += length;
            }
        });
        if (_deviceAddressEntries.Count >= MaxAddressRanges)
        {
            MetalV2Diagnostics.WarnOnce(
                "device-address-table-full",
                $"The device-address table is full and later guest spans are not addressable: " +
                $"limit={MaxAddressRanges} spans={spanCount}.");
        }

        _deviceAddressWindows = _guestMemory.TouchedWindows;
        _guestMemory.ClearTouchedWindows();
        _guestMemory.MarkWindowsTouched(touchedBefore | _deviceAddressWindows);
        _deviceAddressEntries.Sort(static (left, right) => left.FirstPage.CompareTo(right.FirstPage));
        // The windows the table names are what every device-address submission asks the
        // driver to keep resident, which it does by wiring them whole; say when that set
        // changes so a wired-memory jump can be read against it.
        if (_deviceAddressWindows != _deviceAddressWindowsLogged)
        {
            _deviceAddressWindowsLogged = _deviceAddressWindows;
            var spanBytes = 0UL;
            foreach (var (_, pageCount, _) in _deviceAddressEntries)
            {
                spanBytes += pageCount << DeviceAddressPaging.PageBits;
            }

            Console.Error.WriteLine(
                $"[METAL-V2][INFO] device-address table: spans={spanCount} entries={_deviceAddressEntries.Count} " +
                $"span_mb={spanBytes / (1024 * 1024)} windows=0x{_deviceAddressWindows:X} " +
                $"window_count={System.Numerics.BitOperations.PopCount(_deviceAddressWindows)}");
        }
    }

    private ulong _deviceAddressWindowsLogged;

    private ulong UploadDwords(ReadOnlySpan<uint> words)
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(words);
        var range = _guestMemory.Allocate((ulong)Math.Max(bytes.Length, 16), 256, out var span);
        span.Clear();
        bytes.CopyTo(span);
        return range.GpuAddress;
    }

    private ArgumentPlan ArgumentPlanOf(BindingLayout layout)
    {
        if (_argumentLayouts.TryGetValue(layout, out var found))
        {
            return found;
        }

        var built = Gen5MslArgumentLayout.Build(layout);
        var fieldResources = new int[]?[built.Fields.Count];
        for (var index = 0; index < fieldResources.Length; index++)
        {
            var field = built.Fields[index];
            IReadOnlyList<uint>? resources = field.FieldKind switch
            {
                MslArgumentFieldKind.Textures => layout.Find(field.Kind)?.Resources,
                MslArgumentFieldKind.Samplers => layout.Find(DescriptorBindingKind.Samplers)?.Resources,
                _ => null,
            };
            if (resources is null)
            {
                continue;
            }

            var converted = new int[resources.Count];
            for (var item = 0; item < converted.Length; item++)
            {
                converted[item] = (int)resources[item];
            }

            fieldResources[index] = converted;
        }

        var plan = new ArgumentPlan(built, fieldResources);
        _argumentLayouts[layout] = plan;
        return plan;
    }

    internal void NoteUsedResource(nint resource, nuint usage)
    {
        for (var index = 0; index < _usedResourceCount; index++)
        {
            if (_usedResources[index] == resource)
            {
                _usedResourceUsage[index] |= usage;
                return;
            }
        }

        if (_usedResourceCount == _usedResources.Length)
        {
            Array.Resize(ref _usedResources, _usedResources.Length * 2);
            Array.Resize(ref _usedResourceUsage, _usedResourceUsage.Length * 2);
        }

        _usedResources[_usedResourceCount] = resource;
        _usedResourceUsage[_usedResourceCount] = usage;
        _usedResourceCount++;
    }

    // Argument-buffer references are indirect, so the encoder is told which textures
    // the draw touches. The guest windows live in the residency set instead.
    private void ApplyStageBindings(bool graphics)
    {
        if (graphics)
        {
            // A mesh pipeline has no vertex stage: the primitive shader's resources
            // bind to the mesh stage, and so does the residency of the windows it
            // fetches its own attributes through.
            var meshStage = _pendingGraphicsPipeline?.Mesh is not null;
            var stages = meshStage
                ? Mtl.RenderStageMesh | Mtl.RenderStageFragment
                : Mtl.RenderStageVertex | Mtl.RenderStageFragment;
            BindStageBuffers(_renderEncoder, StageVertex, meshStage ? SelSetMeshBuffer : SelSetVertexBuffer);
            BindStageBuffers(_renderEncoder, StageFragment, SelSetFragmentBuffer);
            foreach (var window in _guestMemory.ResidentBuffers)
            {
                if (TryNoteResident(window, Mtl.ResourceUsageRead | Mtl.ResourceUsageWrite, stages))
                {
                    MetalNative.SendUseResource(
                        _renderEncoder, SelUseResourceStages, window,
                        Mtl.ResourceUsageRead | Mtl.ResourceUsageWrite, stages);
                }
            }

            for (var index = 0; index < _usedResourceCount; index++)
            {
                if (TryNoteResident(_usedResources[index], _usedResourceUsage[index], stages))
                {
                    MetalNative.SendUseResource(
                        _renderEncoder, SelUseResourceStages, _usedResources[index], _usedResourceUsage[index], stages);
                }
            }

            DeclareBindlessImages(_renderEncoder, graphics: true, stages);
            return;
        }

        BindStageBuffers(_computeEncoder, StageCompute, SelSetComputeBuffer);
        foreach (var window in _guestMemory.ResidentBuffers)
        {
            if (TryNoteResident(window, Mtl.ResourceUsageRead | Mtl.ResourceUsageWrite, stages: 0))
            {
                MetalNative.SendUseResourceUsage(
                    _computeEncoder, SelUseResourceUsage, window, Mtl.ResourceUsageRead | Mtl.ResourceUsageWrite);
            }
        }

        for (var index = 0; index < _usedResourceCount; index++)
        {
            if (TryNoteResident(_usedResources[index], _usedResourceUsage[index], stages: 0))
            {
                MetalNative.SendUseResourceUsage(
                    _computeEncoder, SelUseResourceUsage, _usedResources[index], _usedResourceUsage[index]);
            }
        }

        DeclareBindlessImages(_computeEncoder, graphics: false, stages: 0);
    }

    // -useResource: is encoder state, not draw state: a resource named once stays
    // resident for the rest of the encoder. Naming every window and every texture
    // again on every draw was four to a dozen message sends per draw for nothing.
    private nint[] _encoderResidentResources = new nint[64];
    private nuint[] _encoderResidentUsage = new nuint[64];
    private nuint[] _encoderResidentStages = new nuint[64];
    private int _encoderResidentCount;
    private readonly Dictionary<nint, int> _encoderResidentIndex = new();
    private long _residencyCallsSkipped;

    private void ForgetResidentResources()
    {
        _encoderEpoch++;
        if (_encoderResidentCount != 0)
        {
            _encoderResidentIndex.Clear();
        }

        _encoderResidentCount = 0;
    }

    /// <summary>How many -useResource: calls the encoder cache skipped.</summary>
    public long TakeSkippedResidencyCalls() => Interlocked.Exchange(ref _residencyCallsSkipped, 0);

    private bool TryNoteResident(nint resource, nuint usage, nuint stages)
    {
        if (_encoderResidentIndex.TryGetValue(resource, out var index))
        {
            // A wider usage or a new stage has to be named again; the same one need not.
            if ((_encoderResidentUsage[index] & usage) == usage &&
                (_encoderResidentStages[index] & stages) == stages)
            {
                _residencyCallsSkipped++;
                return false;
            }

            _encoderResidentUsage[index] |= usage;
            _encoderResidentStages[index] |= stages;
            return true;
        }

        if (_encoderResidentCount == _encoderResidentResources.Length)
        {
            Array.Resize(ref _encoderResidentResources, _encoderResidentCount * 2);
            Array.Resize(ref _encoderResidentUsage, _encoderResidentCount * 2);
            Array.Resize(ref _encoderResidentStages, _encoderResidentCount * 2);
        }

        _encoderResidentIndex[resource] = _encoderResidentCount;
        _encoderResidentResources[_encoderResidentCount] = resource;
        _encoderResidentUsage[_encoderResidentCount] = usage;
        _encoderResidentStages[_encoderResidentCount] = stages;
        _encoderResidentCount++;
        return true;
    }

    private void BindStageBuffers(nint encoder, int stageIndex, nint setBufferSelector)
    {
        if (encoder == 0)
        {
            return;
        }

        if (_stageArgumentValid[stageIndex])
        {
            MetalNative.SendSetBuffer(
                encoder,
                setBufferSelector,
                _stageArgumentBuffers[stageIndex],
                (nuint)_stageArgumentOffsets[stageIndex],
                Gen5MslArgumentLayout.ResourcesBufferIndex);
        }

        if (_hasPushData && (_preparedStageMask & (1 << stageIndex)) != 0)
        {
            MetalNative.SendSetBuffer(
                encoder,
                setBufferSelector,
                _pushBuffer,
                (nuint)_pushOffset,
                Gen5MslArgumentLayout.PushDataBufferIndex);
        }
    }

    // One MTLSamplerState per distinct guest descriptor and view numeric class.
    private nint GetSampler(in SamplerDescriptorWords words, bool integerView, out ulong resourceId)
    {
        var key = (words[0], words[1], words[2], words[3], integerView);
        if (_samplers.TryGetValue(key, out var existing))
        {
            resourceId = existing.ResourceId;
            return existing.Handle;
        }

        var sampler = CreateSampler(in words, integerView);
        resourceId = sampler != 0 ? MetalNative.SendGpuResourceId(sampler, SelGpuResourceId) : 0;
        _samplers[key] = (sampler, resourceId);
        return sampler;
    }

    private nint CreateSampler(in SamplerDescriptorWords words, bool integerView)
    {
        var descriptor = MetalNative.Send(
            MetalNative.Send(MetalNative.Class("MTLSamplerDescriptor"), MetalNative.Selector("alloc")),
            MetalNative.Selector("init"));
        if (descriptor == 0)
        {
            throw Fatal("A Metal sampler descriptor could not be created.");
        }

        try
        {
            var magnify = integerView ? 0u : words.MagnifyFilter;
            var minify = integerView ? 0u : words.MinifyFilter;
            var mipFilter = words.MipFilter;
            if (integerView && mipFilter == 2)
            {
                mipFilter = 1;
            }

            RequireExactAddressModes(in words, magnify, minify);

            MetalV2Native.SendVoidValue(
                descriptor,
                MetalNative.Selector("setMinFilter:"),
                (nuint)(IsLinearFilter(minify) ? Mtl.SamplerFilterLinear : Mtl.SamplerFilterNearest));
            MetalV2Native.SendVoidValue(
                descriptor,
                MetalNative.Selector("setMagFilter:"),
                (nuint)(IsLinearFilter(magnify) ? Mtl.SamplerFilterLinear : Mtl.SamplerFilterNearest));
            MetalV2Native.SendVoidValue(
                descriptor,
                MetalNative.Selector("setMipFilter:"),
                (nuint)(mipFilter switch
                {
                    0 => Mtl.SamplerMipFilterNotMipmapped,
                    2 => Mtl.SamplerMipFilterLinear,
                    _ => Mtl.SamplerMipFilterNearest,
                }));
            MetalV2Native.SendVoidValue(
                descriptor, MetalNative.Selector("setSAddressMode:"), (nuint)MetalV2Formats.SamplerAddressMode(words.ClampX));
            MetalV2Native.SendVoidValue(
                descriptor, MetalNative.Selector("setTAddressMode:"), (nuint)MetalV2Formats.SamplerAddressMode(words.ClampY));
            MetalV2Native.SendVoidValue(
                descriptor, MetalNative.Selector("setRAddressMode:"), (nuint)MetalV2Formats.SamplerAddressMode(words.ClampZ));
            var anisotropy = IsAnisotropic(magnify) || IsAnisotropic(minify)
                ? words.MaxAnisotropyRatio switch { 0 => 1u, 1 => 2u, 2 => 4u, 3 => 8u, _ => 16u }
                : 1u;
            MetalV2Native.SendVoidValue(descriptor, MetalNative.Selector("setMaxAnisotropy:"), anisotropy);
            if (mipFilter != 0)
            {
                MetalV2Native.SendVoidFloatValue(
                    descriptor, MetalNative.Selector("setLodMinClamp:"), words.MinLod / 256.0f);
                MetalV2Native.SendVoidFloatValue(
                    descriptor, MetalNative.Selector("setLodMaxClamp:"), words.MaxLod / 256.0f);
            }

            if (words.DepthCompareFunction != 0)
            {
                MetalV2Native.SendVoidValue(
                    descriptor,
                    MetalNative.Selector("setCompareFunction:"),
                    (nuint)MetalV2Formats.CompareFunction((GpuCompareOp)words.DepthCompareFunction));
            }

            var border = words.BorderColorType switch
            {
                1 => Mtl.SamplerBorderOpaqueBlack,
                2 => Mtl.SamplerBorderOpaqueWhite,
                _ => Mtl.SamplerBorderTransparentBlack,
            };
            MetalV2Native.SendVoidValue(descriptor, MetalNative.Selector("setBorderColor:"), (nuint)border);
            MetalNative.SendVoidBool(
                descriptor, MetalNative.Selector("setNormalizedCoordinates:"), !words.ForceUnnormalizedCoordinates);
            // An argument buffer stores a sampler by gpuResourceID, which Metal only
            // provides for a sampler created with argument-buffer support.
            MetalNative.SendVoidBool(descriptor, MetalNative.Selector("setSupportArgumentBuffers:"), true);
            var sampler = MetalNative.Send(_device, SelNewSamplerState, descriptor);
            if (sampler == 0)
            {
                throw Fatal(
                    $"A Metal sampler state could not be created: words={words[0]:X8},{words[1]:X8},{words[2]:X8},{words[3]:X8}.");
            }

            return sampler;
        }
        finally
        {
            MetalNative.SendVoid(descriptor, SelRelease);
        }
    }

    // CLAMP_HALF_BORDER and MIRROR_ONCE_HALF_BORDER have no host address mode and no
    // exactly derivable edge behaviour, so they are refused. MIRROR_ONCE_BORDER samples
    // mirror-once-to-edge and the shader substitutes the border colour past the mirrored
    // copy, which is exact only while no filter blends a texel with the border.
    private void RequireExactAddressModes(in SamplerDescriptorWords words, uint magnify, uint minify)
    {
        foreach (var (axis, mode) in new[] { ('X', words.ClampX), ('Y', words.ClampY), ('Z', words.ClampZ) })
        {
            if (mode is 4 or 5)
            {
                throw Fatal(
                    $"The sampler address mode has no exact host form: clamp{axis}={mode} " +
                    $"words={words[0]:X8},{words[1]:X8},{words[2]:X8},{words[3]:X8}.");
            }
        }

        var pointXy = magnify == 0 && minify == 0;
        var zFilter = (words[2] >> 24) & 0x3;
        var pointZ = zFilter == 1 || (zFilter == 0 && pointXy);
        if (((words.ClampX == 7 || words.ClampY == 7) && !pointXy) || (words.ClampZ == 7 && !pointZ))
        {
            throw Fatal(
                $"A MIRROR_ONCE_BORDER sampler filters across the border, which has no exact host form: " +
                $"words={words[0]:X8},{words[1]:X8},{words[2]:X8},{words[3]:X8}.");
        }
    }

    private static bool IsLinearFilter(uint filter) => filter is 1 or 3;

    private static bool IsAnisotropic(uint filter) => filter is 2 or 3;

    public bool SamplesDepthAttachment(in DepthAttachmentState depth)
    {
        // Metal has no read-only depth attachment layout to pick, so the answer only
        // selects a layout the host does not model; a sampled depth attachment works.
        _ = depth;
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static uint Dword(ReadOnlySpan<byte> bytes, int index) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[(index * sizeof(uint))..]);
}
