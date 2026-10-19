// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Diagnostics;
using SharpEmu.Libs.Gpu.Buffers;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;

namespace SharpEmu.Gpu.Metal.V2;

// A V#-described image table the guest indexes per lane (GTA V's ray-traced passes hold tens
// of thousands of entries, thousands of them distinct) is read through a device table of
// texture handles: one 16-byte entry per guest entry, a gpuResourceID and the class variant
// that reads it, and a null entry after the last. The shader indexes it directly, so the
// table's size never reaches the pipeline.
//
// Why handles in a device buffer and not an argument-buffer texture array: on Apple GPUs both
// hold the same 64-bit resource IDs, but an array's length is part of the shader, so every
// table size would be a new pipeline, and the shader could not read one entry as another
// texture type. Here the table is plain memory the shader reinterprets per variant.
//
// The textures are declared to each encoder that reads them with one useResources:count:
// message per table rather than a residency set: a residency set makes memory resident but
// takes no part in hazard tracking, and the tables hold render targets the same frame
// writes, which the reading pass has to be ordered after (and any later writer after it).
internal sealed partial class MetalRenderHost
{
    private static readonly nint SelUseResourcesUsage = MetalNative.Selector("useResources:count:usage:");
    private static readonly nint SelUseResourcesStages = MetalNative.Selector("useResources:count:usage:stages:");
    private static readonly nint SelBindlessContents = MetalNative.Selector("contents");
    private static readonly nint SelBindlessNewBuffer = MetalNative.Selector("newBufferWithLength:options:");

    // Device tables kept for tables seen recently; the oldest idle ones go first.
    private const int MaxBindlessDeviceTables = 64;

    private sealed class BindlessDeviceTable
    {
        public BindlessImageTable? Source;
        public nint Buffer;
        public nint Contents;
        public ulong GpuAddress;
        public ulong Capacity;
        public ulong LastUseSerial;
        public ulong[] WrittenIds = [];
        public ulong WrittenNullId;
        public bool Written;
        public nint[] Textures = [];
        public ulong[] ResourceIds = [];
        public long DeclaredEncoder = -1;

        // Each descriptor's image and request as the last full resolve found them, reused
        // while the image cache has neither added nor dropped an image since (Invalid for
        // one a lookup must repeat on every bind).
        public ResourceSlotIdentifier[] Images = [];
        public ImageRequest[] Requests = [];
        public long ResolvedImageSet = -1;
    }

    private readonly Dictionary<BindlessImageTable, BindlessDeviceTable> _bindlessTables =
        new(ReferenceEqualityComparer.Instance);
    private readonly List<(nint Buffer, ulong Capacity, ulong RetireSerial)> _retiredBindlessBuffers = [];
    private readonly List<BindlessDeviceTable> _bindlessDeclarations = [];
    private long _encoderEpoch;

    private long _bindlessResolveTicks;
    private long _bindlessResolves;
    private long _bindlessDescriptors;
    private long _bindlessLookups;

    /// <summary>
    /// The host time spent turning bindless tables into device tables, how many tables and
    /// descriptors that covered, and how many of those descriptors were looked up in the image
    /// cache rather than reused, since the last call.
    /// </summary>
    internal (TimeSpan Elapsed, long Tables, long Descriptors, long Lookups) TakeBindlessCost() =>
        (Stopwatch.GetElapsedTime(0, Interlocked.Exchange(ref _bindlessResolveTicks, 0)),
         Interlocked.Exchange(ref _bindlessResolves, 0),
         Interlocked.Exchange(ref _bindlessDescriptors, 0),
         Interlocked.Exchange(ref _bindlessLookups, 0));

    // Every table of the stage: each distinct descriptor's view, then the device table that
    // names them, rewritten only when the handles it holds change.
    private void BindBindlessImages(MetalStageBindings stage, ShaderResourceInfo info, ResourceSnapshot snapshot)
    {
        var tables = snapshot.BindlessImages;
        var layout = stage.Program.Bindings!;
        if (tables.Length != layout.BindlessImageTableCount)
        {
            throw Fatal(
                $"The bindless image tables do not match the program: tables={tables.Length} " +
                $"layout={layout.BindlessImageTableCount} hash=0x{stage.Program.Hash:X16}.");
        }

        if (stage.BindlessTableAddresses.Length < tables.Length)
        {
            stage.BindlessTableAddresses = new ulong[tables.Length];
        }

        var started = Stopwatch.GetTimestamp();
        var serial = SubmissionSerial;
        foreach (var table in tables)
        {
            if (table.Slot >= tables.Length || table.VariantImages.Length == 0 ||
                table.Descriptors.Length != table.DescriptorVariants.Length)
            {
                throw Fatal($"A bindless image table is malformed: slot={table.Slot} hash=0x{stage.Program.Hash:X16}.");
            }

            var device = DeviceTableOf(table);
            var count = table.Descriptors.Length;
            // A lookup finds what it found last time unless an image came or went since: the
            // descriptors are fixed per table, and only the cache's image set can change.
            var imageSet = _imageCache.ImageSetVersion;
            var reuse = device.ResolvedImageSet == imageSet;
            for (var index = 0; index < count; index++)
            {
                ResourceSlotIdentifier identifier;
                ImageRequest request;
                if (reuse && device.Images[index].IsValid)
                {
                    identifier = device.Images[index];
                    request = device.Requests[index];
                }
                else
                {
                    var variant = table.DescriptorVariants[index];
                    if (variant >= table.VariantImages.Length || table.VariantImages[variant] >= (uint)info.Images.Count)
                    {
                        throw Fatal(
                            $"A bindless descriptor names an unknown variant: slot={table.Slot} descriptor={index} " +
                            $"variant={variant} hash=0x{stage.Program.Hash:X16}.");
                    }

                    var image = info.Images[(int)table.VariantImages[variant]];
                    var resolution = ImageRequestBuilders.Texture(table.Descriptors[index], ShapeOf(image));
                    request = resolution.Request;
                    identifier = _imageCache.FindImage(ref request, resolution.ExactFormat);
                    _bindlessLookups++;
                    resolution = resolution with { Request = request };
                    identifier = ImageRequestBuilders.ValidateTextureOwner(
                        _imageCache.GetImage(identifier).Description, ResourceSlotIdentifier.Invalid, identifier, resolution);
                    // A DCC surface's lookup materializes a clear the GPU may have left since.
                    device.Images[index] = request.Description.Metadata.Kind == MetadataKind.Dcc
                        ? ResourceSlotIdentifier.Invalid
                        : identifier;
                    device.Requests[index] = request;
                }

                var texture = _imageCache.AcquireTextureView(
                    identifier, request with { Role = ImageRole.Texture }, out device.ResourceIds[index]);
                if (device.Textures[index] != texture)
                {
                    // The encoder has not been told about this one yet.
                    device.Textures[index] = texture;
                    device.DeclaredEncoder = -1;
                }
            }

            // Only a pass that saw the image set hold still is one to reuse.
            device.ResolvedImageSet = _imageCache.ImageSetVersion == imageSet ? imageSet : -1;

            // A null or past-the-end entry reads the root's null texture, of variant 0's class.
            var root = (int)table.VariantImages[0];
            if (root >= stage.ImageCount || stage.TextureIds[root] == 0)
            {
                throw Fatal($"A bindless image table has no null texture: slot={table.Slot} hash=0x{stage.Program.Hash:X16}.");
            }

            WriteDeviceTable(device, table, stage.TextureIds[root], serial);
            device.LastUseSerial = serial;
            stage.BindlessTableAddresses[table.Slot] = device.GpuAddress;
            NoteUsedResource(device.Buffer, Mtl.ResourceUsageRead);
            _bindlessDeclarations.Add(device);
            _bindlessResolves++;
            _bindlessDescriptors += count;
        }

        _bindlessResolveTicks += Stopwatch.GetTimestamp() - started;
    }

    private BindlessDeviceTable DeviceTableOf(BindlessImageTable table)
    {
        if (!_bindlessTables.TryGetValue(table, out var device))
        {
            ReleaseIdleBindlessTables();
            device = new BindlessDeviceTable { Source = table };
            _bindlessTables.Add(table, device);
        }

        var count = table.Descriptors.Length;
        if (device.Textures.Length != count)
        {
            device.Textures = new nint[count];
            device.ResourceIds = new ulong[count];
            device.Images = new ResourceSlotIdentifier[count];
            device.Requests = new ImageRequest[count];
            device.ResolvedImageSet = -1;
        }

        return device;
    }

    // Rewrites the entries when a handle changed. A buffer the GPU may still read - by a
    // submitted command buffer or by work recorded into the open one - is retired and a
    // fresh one written, so no recorded dispatch sees another's entries.
    private void WriteDeviceTable(BindlessDeviceTable device, BindlessImageTable table, ulong nullId, ulong serial)
    {
        var unchanged = device.Written && device.WrittenNullId == nullId &&
            device.WrittenIds.AsSpan().SequenceEqual(device.ResourceIds);
        var bytes = ((ulong)table.Entries.Length + 1) * Gen5MslArgumentLayout.BindlessEntryBytes;
        if (unchanged && device.Capacity >= bytes)
        {
            return;
        }

        var completed = CompletedSerial;
        var busy = device.Buffer != 0 && (device.LastUseSerial == serial || device.LastUseSerial > completed);
        if (device.Buffer == 0 || busy || device.Capacity < bytes)
        {
            if (device.Buffer != 0)
            {
                _retiredBindlessBuffers.Add((device.Buffer, device.Capacity, device.LastUseSerial));
            }

            device.Buffer = TakeBindlessBuffer(bytes, completed, out device.Capacity);
            device.Contents = MetalNative.Send(device.Buffer, SelBindlessContents);
            device.GpuAddress = MetalNative.SendGpuAddress(device.Buffer, SelGpuAddress);
        }

        unsafe
        {
            var span = new Span<byte>((void*)device.Contents, checked((int)bytes));
            var entries = table.Entries;
            for (var entry = 0; entry < entries.Length; entry++)
            {
                var descriptor = entries[entry];
                var slot = span.Slice(entry * Gen5MslArgumentLayout.BindlessEntryBytes, Gen5MslArgumentLayout.BindlessEntryBytes);
                if (descriptor < 0)
                {
                    BinaryPrimitives.WriteUInt64LittleEndian(slot, nullId);
                    BinaryPrimitives.WriteUInt64LittleEndian(slot[8..], 0);
                    continue;
                }

                BinaryPrimitives.WriteUInt64LittleEndian(slot, device.ResourceIds[descriptor]);
                BinaryPrimitives.WriteUInt64LittleEndian(slot[8..], table.DescriptorVariants[descriptor]);
            }

            var sentinel = span.Slice(entries.Length * Gen5MslArgumentLayout.BindlessEntryBytes, Gen5MslArgumentLayout.BindlessEntryBytes);
            BinaryPrimitives.WriteUInt64LittleEndian(sentinel, nullId);
            BinaryPrimitives.WriteUInt64LittleEndian(sentinel[8..], 0);
        }

        if (device.WrittenIds.Length != device.ResourceIds.Length)
        {
            device.WrittenIds = new ulong[device.ResourceIds.Length];
        }

        device.ResourceIds.AsSpan().CopyTo(device.WrittenIds);
        device.WrittenNullId = nullId;
        device.Written = true;
    }

    private nint TakeBindlessBuffer(ulong bytes, ulong completed, out ulong capacity)
    {
        for (var index = 0; index < _retiredBindlessBuffers.Count; index++)
        {
            var retired = _retiredBindlessBuffers[index];
            if (retired.RetireSerial > completed || retired.Capacity < bytes)
            {
                continue;
            }

            _retiredBindlessBuffers.RemoveAt(index);
            capacity = retired.Capacity;
            return retired.Buffer;
        }

        // Rounded up so a table that grows a little reuses its buffer.
        capacity = Math.Max(4096UL, System.Numerics.BitOperations.RoundUpToPowerOf2(bytes));
        var buffer = MetalNative.SendNewBuffer(_device, SelBindlessNewBuffer, (nuint)capacity, Mtl.ResourceStorageModeShared);
        if (buffer == 0)
        {
            throw Fatal($"A bindless image table buffer could not be created: bytes=0x{capacity:X}.");
        }

        SetLabel(buffer, "SharpEmu bindless image table");
        return buffer;
    }

    // Drops device tables of snapshots no longer drawn once the GPU is done with them.
    private void ReleaseIdleBindlessTables()
    {
        var completed = CompletedSerial;
        if (_bindlessTables.Count >= MaxBindlessDeviceTables)
        {
            var oldest = _bindlessTables
                .Where(pair => pair.Value.LastUseSerial <= completed)
                .OrderBy(pair => pair.Value.LastUseSerial)
                .Take(_bindlessTables.Count - MaxBindlessDeviceTables + 1)
                .ToArray();
            foreach (var (table, device) in oldest)
            {
                _bindlessTables.Remove(table);
                if (device.Buffer != 0)
                {
                    _retiredBindlessBuffers.Add((device.Buffer, device.Capacity, device.LastUseSerial));
                }
            }
        }

        // Keep a few completed buffers for reuse and release the rest.
        var kept = 0;
        for (var index = _retiredBindlessBuffers.Count - 1; index >= 0; index--)
        {
            var retired = _retiredBindlessBuffers[index];
            if (retired.RetireSerial > completed || ++kept <= 4)
            {
                continue;
            }

            MetalNative.SendVoid(retired.Buffer, SelRelease);
            _retiredBindlessBuffers.RemoveAt(index);
        }
    }

    // Each table's textures, declared once per encoder in one message.
    private unsafe void DeclareBindlessImages(nint encoder, bool graphics, nuint stages)
    {
        foreach (var device in _bindlessDeclarations)
        {
            if (device.DeclaredEncoder == _encoderEpoch || device.Textures.Length == 0)
            {
                continue;
            }

            device.DeclaredEncoder = _encoderEpoch;
            fixed (nint* textures = device.Textures)
            {
                if (graphics)
                {
                    MetalNative.SendUseResourcesStages(
                        encoder, SelUseResourcesStages, textures, (nuint)device.Textures.Length, Mtl.ResourceUsageRead, stages);
                }
                else
                {
                    MetalNative.SendUseResources(
                        encoder, SelUseResourcesUsage, textures, (nuint)device.Textures.Length, Mtl.ResourceUsageRead);
                }
            }
        }
    }

    // Shutdown only, once every submission has drained.
    private void DisposeBindlessImages()
    {
        foreach (var device in _bindlessTables.Values)
        {
            if (device.Buffer != 0)
            {
                MetalNative.SendVoid(device.Buffer, SelRelease);
            }
        }

        _bindlessTables.Clear();
        foreach (var retired in _retiredBindlessBuffers)
        {
            MetalNative.SendVoid(retired.Buffer, SelRelease);
        }

        _retiredBindlessBuffers.Clear();
    }
}
