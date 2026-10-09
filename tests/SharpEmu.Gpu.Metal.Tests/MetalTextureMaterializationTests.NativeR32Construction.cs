// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Collections;
using System.Reflection;
using System.Runtime.CompilerServices;
using SharpEmu.Libs.Gpu;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed partial class MetalTextureMaterializationTests
{
    [Theory]
    [InlineData("none")]
    [InlineData("pending")]
    [InlineData("taken")]
    public void NativeR32SelectedConstructionCannotBorrowUnassociatedPublicConsumer(string scenario)
    {
        using var queue = new SelectedMipQueueScope();
        var fields = new[] { "_takenGuestWorkSequence", "_takenGuestPrefixProducerSequence", "_takenDepthCubeProducerWork", "_executingGuestWorkSequence" }
            .Select(name => Presenter.GetField(name, Methods)!).ToArray();
        var old = fields.Select(field => field.GetValue(null)).ToArray();
        var images = (IDictionary)Presenter.GetField("_guestImages", Methods)!.GetValue(null)!;
        Assert.False(images.Contains(SelectedMipAddress));
        var (bytes, _) = SelectedMipSeed();
        var memory = new CountingSelectedMemory(bytes);
        var snapshot = Assert.Single(SelectedMipSnapshots(memory));
        var selected = SelectedMipAssociation(snapshot);
        var image = RuntimeHelpers.GetUninitializedObject(images.GetType().GetGenericArguments()[1]);
        images.Add(SelectedMipAddress, image); // refusing metadata only, no native handle
        try
        {
            if (scenario != "none")
            {
                var buffer = new GuestMemoryBuffer(0x7092CC0000, new byte[4], 4, 4, false, Writable: true);
                var sequence = (long)Presenter.GetMethod("SubmitComputeDispatch", Methods)!.Invoke(null,
                    [0UL, null, Array.Empty<GuestDrawTexture>(), new[] { buffer }, 1u, 1u, 1u, 0u, 0u, 0u, true, null])!;
                Assert.True(sequence > 0);
                if (scenario == "taken")
                {
                    object?[] take = [null];
                    Assert.True((bool)Presenter.GetMethod("TryTakeGuestWork", Methods)!.Invoke(null, take)!);
                }
            }
            var reads = memory.Reads;
            var failure = Assert.Throws<TargetInvocationException>(() => Presenter.GetMethod(
                "RejectConflictingSelectedStorageConstructionLocked", Methods)!.Invoke(null, [selected]));
            Assert.IsType<NotSupportedException>(failure.InnerException);
            Assert.Contains("address-only live image", failure.InnerException!.Message);
            Assert.Equal(reads, memory.Reads);
            Assert.Empty((IDictionary)Presenter.GetField("_selectedStorageMipImages", Methods)!.GetValue(null)!);
            Assert.Equal(scenario == "pending" ? 1 : 0, (int)Presenter.GetField("_pendingGuestWorkCount", Methods)!.GetValue(null)!);
            // Public admission/take supplies no sealed copy or native authority.
        }
        finally
        {
            images.Remove(SelectedMipAddress);
            for (var i = 0; i < fields.Length; i++) fields[i].SetValue(null, old[i]);
        }
    }

    [Theory]
    [InlineData("owner")]
    [InlineData("image")]
    [InlineData("texture")]
    [InlineData("version")]
    public void NativeR32PrivateConstructionProofRejectsDifferentUntransferredOwner(string change)
    {
        var (bytes, _) = SelectedMipSeed();
        var snapshot = Assert.Single(SelectedMipSnapshots(new CountingSelectedMemory(bytes)));
        var owner = SelectedMipAssociation(snapshot);
        var imageType = Presenter.GetField("_guestImages", Methods)!.FieldType.GetGenericArguments()[1];
        var image = RuntimeHelpers.GetUninitializedObject(imageType);
        imageType.GetField("Texture")!.SetValue(image, (nint)31);
        imageType.GetField("ContentVersion")!.SetValue(image, 1);
        var seedType = Presenter.GetNestedType("SelectedStorageMipSeed", BindingFlags.NonPublic)!;
        var proofType = Presenter.GetNestedType("SelectedNativeOriginalProof", BindingFlags.NonPublic)!;
        var seed = RuntimeHelpers.GetUninitializedObject(seedType); // refusing identity only, never a content receipt
        var proof = Activator.CreateInstance(proofType, NativeR32Instance, null, [owner, seed, image], null)!;
        var requestedOwner = change == "owner" ? typeof(object).GetMethod("MemberwiseClone", NativeR32Instance)!.Invoke(owner, null)! : owner;
        var requestedImage = change == "image" ? RuntimeHelpers.GetUninitializedObject(imageType) : image;
        if (change == "texture") imageType.GetField("Texture")!.SetValue(image, (nint)32);
        if (change == "version") imageType.GetField("ContentVersion")!.SetValue(image, 2);
        object?[] args = [requestedOwner, requestedImage, proof, null];
        Assert.False((bool)Presenter.GetMethod("TrySelectedR32OriginalNativeProof", Methods)!.Invoke(null, args)!);
        Assert.Empty((IDictionary)Presenter.GetField("_selectedStorageMipImages", Methods)!.GetValue(null)!);
        SharpEmu.HLE.GuestImageWriteTracker.Untrack(SelectedMipAddress);
        // Synthetic handle integers never reach MetalNative. These are only
        // negative proof-substitution controls, not a positive native lease.
    }
}
