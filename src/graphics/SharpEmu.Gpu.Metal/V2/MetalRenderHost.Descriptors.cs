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
        public nint[] Samplers = [];
        public ResourceSlotIdentifier[] Images = [];
        public ImageRequest[] Requests = [];
        public bool[] IsStorage = [];
        public MetalGuestRange[] Buffers = [];
        public uint[] BufferLengths = [];
        public uint[] ShaderData = [];
        public int ImageCount;
        public int SamplerCount;
        public int BufferCount;
        public bool Committed;

        public ShaderStageResources Stage => StageResources;

        public void EnsureCapacity(int images, int samplers, int buffers, int shaderDwords)
        {
            if (Textures.Length < images)
            {
                Textures = new nint[images];
                TextureLevels = new uint[images];
                Images = new ResourceSlotIdentifier[images];
                Requests = new ImageRequest[images];
                IsStorage = new bool[images];
            }

            if (Samplers.Length < samplers)
            {
                Samplers = new nint[samplers];
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

    private readonly Dictionary<BindingLayout, Gen5MslArgumentLayout> _argumentLayouts = new();
    private readonly Dictionary<(uint, uint, uint, uint, bool), nint> _samplers = new();
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

        // Keep each submission's command buffer bounded so completions and flips are
        // not stuck behind a frame's worth of recording.
        if (_recordedCommands >= 2048)
        {
            FlushCommandBuffer();
        }

        _ = EnsureCommandBuffer();
        _preparation ??= new MetalResourcePreparation(this);
        _preparedStageMask = 0;
        _usedResourceCount = 0;
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
        _preparation = null;
        _preparedStageMask = 0;
    }

    public void ResetBindings()
    {
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
        for (var index = 0; index < info.Images.Count; index++)
        {
            ResolveImageBinding(prepared, info.Images[index], snapshot.Images[index], index);
        }

        for (var index = 0; index < info.Samplers.Count; index++)
        {
            prepared.Samplers[index] = ResolveSampler(
                info, prepared, info.Samplers[index], snapshot.Samplers[index], index);
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
        int index)
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
        return GetSampler(in descriptor, SamplesIntegerViews(info, prepared, index));
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

            stage.Textures[index] = _imageCache.AcquireTextureView(identifier, request with { Role = role });
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

            var size = ClampMappedSize(descriptor.Address, requested);
            var written = info.Buffers[index].Written;
            FlushGpuWrites(descriptor.Address, size);
            var range = _guestMemory.Resolve(descriptor.Address, size);
            if (written)
            {
                _imageCache.InvalidateMemoryFromGpu(descriptor.Address, size);
                NoteGuestMemoryWrite();
            }

            stage.Buffers[index] = range;
            stage.BufferLengths[index] = (uint)Math.Min(size, uint.MaxValue);
        }
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
        var argumentLayout = ArgumentLayoutOf(layout);
        if (argumentLayout.Fields.Count == 0)
        {
            _stageArgumentValid[stageIndex] = false;
            return;
        }

        var range = _guestMemory.Allocate(Math.Max(argumentLayout.ByteSize, 16u), 256, out var argument);
        argument.Clear();
        var imageCursor = 0;
        foreach (var field in argumentLayout.Fields)
        {
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
                    WriteTextureField(stage, field, span, ref imageCursor, stageIndex);
                    break;
                case MslArgumentFieldKind.Samplers:
                    for (var index = 0; index < field.Count && index < stage.SamplerCount; index++)
                    {
                        var sampler = stage.Samplers[index];
                        if (sampler != 0)
                        {
                            BinaryPrimitives.WriteUInt64LittleEndian(
                                span[(index * sizeof(ulong))..],
                                MetalNative.SendGpuResourceId(sampler, SelGpuResourceId));
                        }
                    }

                    break;
                case MslArgumentFieldKind.Pointer:
                    BinaryPrimitives.WriteUInt64LittleEndian(span, PointerFieldValue(stage, field.Kind));
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
        Span<byte> span,
        ref int imageCursor,
        int stageIndex)
    {
        var layout = stage.Program.Bindings!;
        var binding = FindBinding(layout, field.Kind);
        for (var index = 0; index < field.Count; index++)
        {
            var resource = binding is not null && index < binding.Resources.Count
                ? (int)binding.Resources[index]
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

            BinaryPrimitives.WriteUInt64LittleEndian(
                span[(index * sizeof(ulong))..], MetalNative.SendGpuResourceId(texture, SelGpuResourceId));
            var usage = Mtl.ResourceUsageRead | (stage.IsStorage[resource] ? Mtl.ResourceUsageWrite : 0);
            NoteUsedResource(texture, usage);
        }

        _ = stageIndex;
        imageCursor += (int)field.Count;
    }

    private static DescriptorBinding? FindBinding(BindingLayout layout, DescriptorBindingKind kind)
    {
        foreach (var binding in layout.Descriptors)
        {
            if (binding.Kind == kind)
            {
                return binding;
            }
        }

        return null;
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
    private const int MaxAddressRanges = 4096;
    private long _deviceAddressSpanVersion = long.MinValue;

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

        _deviceAddressTable = range.GpuAddress;
        _deviceAddressEntryCount = (uint)_deviceAddressEntries.Count;
        _deviceAddressTableSerial = _submissionSerial;
        return _deviceAddressTable;
    }

    // Each guest span becomes the fewest window-backed runs that cover it. A span whose
    // backing is not contiguous (fragmented flexible memory) or crosses a window splits
    // into several entries; pages with no window are left out, never copied, because a
    // shader store through a copied page would not reach guest memory.
    private void RebuildDeviceAddressEntries(SharpEmu.HLE.GpuMemory.GuestGpuMemory? gpuMemory)
    {
        _deviceAddressEntries.Clear();
        gpuMemory?.ForEachSpan((address, size) =>
        {
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
        _deviceAddressEntries.Sort(static (left, right) => left.FirstPage.CompareTo(right.FirstPage));
    }

    private ulong UploadDwords(ReadOnlySpan<uint> words)
    {
        var bytes = System.Runtime.InteropServices.MemoryMarshal.AsBytes(words);
        var range = _guestMemory.Allocate((ulong)Math.Max(bytes.Length, 16), 256, out var span);
        span.Clear();
        bytes.CopyTo(span);
        return range.GpuAddress;
    }

    private Gen5MslArgumentLayout ArgumentLayoutOf(BindingLayout layout)
    {
        if (_argumentLayouts.TryGetValue(layout, out var found))
        {
            return found;
        }

        var built = Gen5MslArgumentLayout.Build(layout);
        _argumentLayouts[layout] = built;
        return built;
    }

    private void NoteUsedResource(nint resource, nuint usage)
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
                MetalNative.SendUseResource(
                    _renderEncoder, SelUseResourceStages, window,
                    Mtl.ResourceUsageRead | Mtl.ResourceUsageWrite, stages);
            }

            for (var index = 0; index < _usedResourceCount; index++)
            {
                MetalNative.SendUseResource(
                    _renderEncoder, SelUseResourceStages, _usedResources[index], _usedResourceUsage[index], stages);
            }

            return;
        }

        BindStageBuffers(_computeEncoder, StageCompute, SelSetComputeBuffer);
        foreach (var window in _guestMemory.ResidentBuffers)
        {
            MetalNative.SendUseResourceUsage(
                _computeEncoder, SelUseResourceUsage, window, Mtl.ResourceUsageRead | Mtl.ResourceUsageWrite);
        }

        for (var index = 0; index < _usedResourceCount; index++)
        {
            MetalNative.SendUseResourceUsage(
                _computeEncoder, SelUseResourceUsage, _usedResources[index], _usedResourceUsage[index]);
        }
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
    private nint GetSampler(in SamplerDescriptorWords words, bool integerView)
    {
        var key = (words[0], words[1], words[2], words[3], integerView);
        if (_samplers.TryGetValue(key, out var existing))
        {
            return existing;
        }

        var sampler = CreateSampler(in words, integerView);
        _samplers[key] = sampler;
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
