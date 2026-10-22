// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using SharpEmu.Libs.Gpu.Images;
using SharpEmu.Logging;

namespace SharpEmu.Gpu.Metal.V2;

// SHARPEMU_METAL_V2_DUMP_BC_UPLOAD=<dir>|1 (1 = metal-bc-uploads under the logs folder): after every whole-image upload of a BC6H array or
// cube image, logs the content hash of the guest bytes the upload read and, for the first
// few distinct contents of an address, writes those bytes and (once the GPU has finished)
// the compressed blocks of every (level, layer) of the Metal texture. A watcher then
// re-reads each tracked guest range every few seconds and logs when the guest bytes no
// longer match what was last uploaded, which is what a texture the cache failed to
// refresh looks like. Debug only.
internal static class MetalBlockCubeDump
{
    private const int MaxDistinctPerAddress = 6;
    private const int MaxDriftPerAddress = 4;

    private static readonly string? Directory = SharpEmuPaths.ResolveDumpPath("SHARPEMU_METAL_V2_DUMP_BC_UPLOAD", "metal-bc-uploads");
    private static readonly nint SelContents = MetalNative.Selector("contents");
    private static readonly nint SelCopy = MetalNative.Selector(
        "copyFromTexture:sourceSlice:sourceLevel:sourceOrigin:sourceSize:" +
        "toBuffer:destinationOffset:destinationBytesPerRow:destinationBytesPerImage:options:");
    private static readonly nint SelNewBuffer = MetalNative.Selector("newBufferWithLength:options:");
    private static readonly Dictionary<ulong, Tracked> Seen = [];
    private static bool _watcher;

    public delegate bool PeekGuest(ulong address, Span<byte> destination);

    public static PeekGuest? Peek;

    private sealed class Tracked
    {
        public ulong Size;
        public ulong LastUploadedHash;
        public ulong LastSeenHash;
        public int Uploads;
        public int Distinct;
        public int Drift;
        public string Shape = string.Empty;
    }

    public static void Capture(
        IMetalSubmissionContext context, MetalTiler tiler, in ImageDescription description,
        MetalGuestRange source, nint texture, ulong guestBase, ulong pieceSize)
    {
        if (Directory is null || texture == 0 || description.Resources.Layers < 6 ||
            description.GuestFormat is not (GuestPixelFormat.Bc6UFloat or GuestPixelFormat.Bc6SFloat) ||
            guestBase != 0 || pieceSize != description.Data.Size || !source.IsValid)
        {
            return;
        }

        var address = description.Data.Address;
        var contents = MetalNative.Send(source.Buffer, SelContents) + (nint)source.Offset;
        var guest = new byte[source.Size];
        Marshal.Copy(contents, guest, 0, guest.Length);
        var hash = Hash(guest);
        var levels = description.Resources.Levels;
        var layers = description.Resources.Layers;
        var width = description.Extent.Width;
        var height = description.Extent.Height;
        var shape = string.Create(CultureInfo.InvariantCulture,
            $"address=0x{address:X} format={description.GuestFormat} tile={description.TileMode} " +
            $"extent={width}x{height} levels={levels} layers={layers} guestBytes={source.Size} mtlFormat={description.PixelFormat}");
        Tracked tracked;
        int sequence;
        lock (Seen)
        {
            if (!Seen.TryGetValue(address, out tracked!))
            {
                if (Seen.Count >= 96)
                {
                    return;
                }

                tracked = new Tracked { Size = source.Size, Shape = shape };
                Seen[address] = tracked;
            }

            tracked.Uploads++;
            var changed = hash != tracked.LastUploadedHash;
            tracked.LastUploadedHash = hash;
            tracked.LastSeenHash = hash;
            Console.Error.WriteLine($"[METAL-V2][BC-DUMP] upload #{tracked.Uploads} {shape} hash={hash:X16} changed={changed}");
            if (!changed || tracked.Distinct >= MaxDistinctPerAddress)
            {
                StartWatcher();
                return;
            }

            sequence = tracked.Distinct++;
            StartWatcher();
        }

        System.IO.Directory.CreateDirectory(Directory);
        File.WriteAllBytes(Path.Combine(Directory, $"guest-{address:X}-{sequence}.bin"), guest);
        File.WriteAllText(Path.Combine(Directory, $"meta-{address:X}-{sequence}.txt"), shape + "\n");

        var offsets = new ulong[levels];
        ulong total = 0;
        for (uint level = 0; level < levels; level++)
        {
            offsets[level] = total;
            total += BlocksWide(width, level) * BlocksHigh(height, level) * 16UL * layers;
        }

        var buffer = MetalNative.SendNewBuffer(context.Device, SelNewBuffer, (nuint)total, 0);
        var blit = tiler.BeginBlit(context);
        for (uint level = 0; level < levels; level++)
        {
            var rowBytes = BlocksWide(width, level) * 16;
            var imageBytes = rowBytes * BlocksHigh(height, level);
            for (uint layer = 0; layer < layers; layer++)
            {
                MetalNative.SendCopyTextureToBufferOptions(
                    blit, SelCopy, texture, layer, level, default,
                    new MtlSize { Width = Math.Max(width >> (int)level, 1), Height = Math.Max(height >> (int)level, 1), Depth = 1 },
                    buffer, (nuint)(offsets[level] + layer * imageBytes), (nuint)rowBytes, (nuint)imageBytes, 0);
            }
        }

        MetalTiler.EndEncoding(blit);
        var serial = context.SubmissionSerial;
        var directory = Directory;
        _ = Task.Run(() =>
        {
            while (context.CompletedSerial < serial)
            {
                Thread.Sleep(20);
            }

            var bytes = new byte[total];
            Marshal.Copy(MetalNative.Send(buffer, SelContents), bytes, 0, bytes.Length);
            File.WriteAllBytes(Path.Combine(directory, $"mtl-{address:X}-{sequence}.bin"), bytes);
            Console.Error.WriteLine($"[METAL-V2][BC-DUMP] wrote mtl-{address:X}-{sequence}.bin");
        });
    }

    private static void StartWatcher()
    {
        if (_watcher)
        {
            return;
        }

        _watcher = true;
        _ = Task.Run(() =>
        {
            while (true)
            {
                Thread.Sleep(2000);
                KeyValuePair<ulong, Tracked>[] entries;
                lock (Seen)
                {
                    entries = [.. Seen];
                }

                foreach (var (address, tracked) in entries)
                {
                    var bytes = new byte[tracked.Size];
                    if (Peek is null || !Peek(address, bytes))
                    {
                        continue;
                    }

                    var hash = Hash(bytes);
                    if (hash == tracked.LastSeenHash)
                    {
                        continue;
                    }

                    tracked.LastSeenHash = hash;
                    Console.Error.WriteLine(
                        $"[METAL-V2][BC-DUMP] guest drift 0x{address:X} hash={hash:X16} uploadedHash={tracked.LastUploadedHash:X16} uploads={tracked.Uploads}");
                    if (tracked.Drift++ < MaxDriftPerAddress)
                    {
                        File.WriteAllBytes(Path.Combine(Directory!, $"guest-{address:X}-drift{tracked.Drift}.bin"), bytes);
                    }
                }
            }
        });
    }

    private static ulong Hash(byte[] bytes)
    {
        var hash = 14695981039346656037UL;
        for (var index = 0; index + 8 <= bytes.Length; index += 8)
        {
            hash = (hash ^ BitConverter.ToUInt64(bytes, index)) * 1099511628211UL;
        }

        return hash;
    }

    private static ulong BlocksWide(uint width, uint level) => (Math.Max(width >> (int)level, 1) + 3) / 4;

    private static ulong BlocksHigh(uint height, uint level) => (Math.Max(height >> (int)level, 1) + 3) / 4;
}
