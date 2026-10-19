// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Images;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void StoragePublicationFailureRestoresExactRegistries(bool selected)
    {
        using var state = new SelectedMipQueueScope();
        var (guest, _) = SelectedMipSeed();
        var descriptor = selected
            ? Assert.Single(SelectedMipSnapshots(new CountingSelectedMemory(guest)))
            : OrdinaryStorageSeed();
        var association = selected ? SelectedMipAssociation(descriptor) : null;
        var imageType = Presenter.GetNestedType("GuestImage", BindingFlags.NonPublic)!;
        var image = Activator.CreateInstance(imageType, nonPublic: true)!;
        var images = (IDictionary)Presenter.GetField(selected ? "_selectedStorageMipImages" : "_guestImages", Methods)!.GetValue(null)!;
        var extents = (IDictionary)Presenter.GetField("_guestImageExtents", Methods)!.GetValue(null)!;
        var owners = (IDictionary)Presenter.GetField("_selectedStorageMipOwners", Methods)!.GetValue(null)!;
        var hook = Presenter.GetField("_storageImageBeforeMetadataPublication", Methods)!;
        var previous = hook.GetValue(null);
        var cause = new InvalidOperationException("injected metadata publication failure");
        var calls = 0;
        extents[descriptor.Address] = (17u, 19u, 1292UL);
        hook.SetValue(null, (Action<nint>)(_ => { calls++; Assert.Single(images.Keys); throw cause; }));
        try
        {
            lock (Presenter.GetField("_gate", Methods)!.GetValue(null)!)
            {
                var ex = Assert.Throws<TargetInvocationException>(() => Presenter.GetMethod("PublishStorageImageLocked", Methods)!
                    .Invoke(null, [descriptor, association, image, (4u, 4u, 64UL)]));
                Assert.Same(cause, ex.InnerException);
            }
            Assert.Equal(1, calls); Assert.Empty(images); Assert.Empty(owners);
            Assert.Equal((17u, 19u, 1292UL), extents[descriptor.Address]);
        }
        finally { hook.SetValue(null, previous); extents.Remove(descriptor.Address); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SelectedMipNativePublicationFailureLeavesNoGhostAndRetainedProbeStillHasUploadedRows(bool selected)
    {
        using var state = new SelectedMipQueueScope();
        var (guest, expected) = SelectedMipSeed();
        var descriptor = selected ? Assert.Single(SelectedMipSnapshots(new CountingSelectedMemory(guest))) : OrdinaryStorageSeed();
        if (!selected) expected = descriptor.RgbaPixels;
        if (!RequireDevice()) return;
        var hook = Presenter.GetField("_storageImageBeforeMetadataPublication", Methods)!;
        var previous = hook.GetValue(null);
        var cause = new InvalidOperationException("actual native upload then publication failure");
        nint retainedProbe = 0;
        var images = (IDictionary)Presenter.GetField(selected ? "_selectedStorageMipImages" : "_guestImages", Methods)!.GetValue(null)!;
        var extents = (IDictionary)Presenter.GetField("_guestImageExtents", Methods)!.GetValue(null)!;
        var owners = (IDictionary)Presenter.GetField("_selectedStorageMipOwners", Methods)!.GetValue(null)!;
        hook.SetValue(null, (Action<nint>)(texture =>
        {
            Assert.NotEqual((nint)0, texture);
            retainedProbe = texture;
            SendVoid(texture, Selector("retain")); // Test owns independent original native authority.
            throw cause;
        }));
        try
        {
            var ex = Assert.Throws<TargetInvocationException>(() => Presenter.GetMethod("EnsureStorageImage", Methods)!
                .Invoke(null, [Device.Value, descriptor]));
            Assert.Same(cause, ex.InnerException);
            Assert.Empty(images); Assert.Empty(owners); Assert.False(extents.Contains(descriptor.Address));
            Assert.NotEqual((nint)0, retainedProbe);
            Assert.Equal(expected, ReadNativeRows(retainedProbe, descriptor.Width, descriptor.Height, 4));
            SendVoid(retainedProbe, Selector("release")); retainedProbe = 0;
            hook.SetValue(null, previous);
            var retry = Presenter.GetMethod("EnsureStorageImage", Methods)!.Invoke(null, [Device.Value, descriptor])!;
            var handle = (nint)retry.GetType().GetField("Texture")!.GetValue(retry)!;
            Assert.Equal(expected, ReadNativeRows(handle, descriptor.Width, descriptor.Height, 4));
            output.WriteLine($"Actual storage upload/publication failure selected={selected}: original cause retained, registries rolled back; separate test retain preserved every row, clean retry installed exactly one native owner.");
        }
        finally
        {
            hook.SetValue(null, previous);
            if (retainedProbe != 0) SendVoid(retainedProbe, Selector("release"));
            if (!selected && images.Contains(descriptor.Address))
            {
                var image = images[descriptor.Address]!;
                SendVoid((nint)image.GetType().GetField("Texture")!.GetValue(image)!, Selector("release"));
                images.Remove(descriptor.Address); extents.Remove(descriptor.Address);
            }
        }
    }

    private static object SelectedMipAssociation(GuestDrawTexture texture)
    {
        var table = Presenter.GetField("_selectedStorageMipAssociations", Methods)!.GetValue(null)!;
        var args = new object?[] { texture, null };
        Assert.True((bool)table.GetType().GetMethod("TryGetValue")!.Invoke(table, args)!);
        return args[1]!;
    }

    private static GuestDrawTexture OrdinaryStorageSeed() => new(0x7AA1000000, 4, 4, 10, 0,
        Enumerable.Range(0, 64).Select(static index => (byte)(index * 3 + 1)).ToArray(), false, true,
        Shape: new ShaderImageShape(false, false, true, false, TextureNumericClass.Float), Access: GuestImageAccess.Write);
}
