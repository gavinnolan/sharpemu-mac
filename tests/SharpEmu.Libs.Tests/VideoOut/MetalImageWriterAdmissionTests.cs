// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Collections;
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.VideoOut;

[Collection(SchedulingStateCollection.Name)]
public sealed class MetalImageWriterAdmissionTests
{
    private const ulong Address = 0x71F1_0000_00;
    private static readonly MetalCompiledGuestShader Shader = new(new Gen5MslShader("", "admission_test", Gen5MslStage.Compute, 0));

    [Theory]
    [InlineData(GuestImageAccess.Write)]
    [InlineData(GuestImageAccess.Read | GuestImageAccess.Write)]
    [InlineData(GuestImageAccess.Read | GuestImageAccess.Write | GuestImageAccess.Atomic)]
    public void WritableStorageIsPublishedOnlyWithItsAdmittedSequence(GuestImageAccess access)
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        var sequence = Submit(Texture(access));
        Assert.True(sequence > 0);
        Assert.True(Registry("_availableGuestImages").Contains(Address));
        Assert.Equal(sequence, Registry("_guestImageWorkSequences")[Address]);
    }

    [Fact]
    public void ReadOnlyStorageNeverAdvertisesOrChangesTheGPUContentVersion()
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        var imageType = typeof(MetalVideoPresenter).GetNestedType("GuestImage", BindingFlags.NonPublic)!;
        var image = Activator.CreateInstance(imageType, nonPublic: true)!;
        Registry("_guestImages")[Address] = image;
        try
        {
            Assert.True(Submit(Texture(GuestImageAccess.Read), writesBuffer: true) > 0);
            Assert.False(Registry("_availableGuestImages").Contains(Address));
            Assert.False(Registry("_guestImageWorkSequences").Contains(Address));
            MetalVideoPresenter.RecordStorageImageWrites([Texture(GuestImageAccess.Read)]);
            Assert.False((bool)imageType.GetField("GpuWritten")!.GetValue(image)!);
            Assert.Equal(0, imageType.GetField("ContentVersion")!.GetValue(image));
            MetalVideoPresenter.RecordStorageImageWrites([Texture(GuestImageAccess.Write), Texture(GuestImageAccess.Write)]);
            Assert.True((bool)imageType.GetField("GpuWritten")!.GetValue(image)!);
            Assert.Equal(1, imageType.GetField("ContentVersion")!.GetValue(image));
        }
        finally { Registry("_guestImages").Remove(Address); }
    }

    [Fact]
    public void RejectedComputeAdmissionDoesNotAdvertiseAProspectiveWriter()
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        Field("_guestWorkFailure").SetValue(null, new InvalidOperationException("previous guest work failed"));
        Assert.Equal(0, Submit(Texture(GuestImageAccess.Write)));
        Assert.False(Registry("_availableGuestImages").Contains(Address));
        Assert.False(Registry("_guestImageWorkSequences").Contains(Address));
    }

    [Fact]
    public void RejectedDrawAdmissionDoesNotAdvertiseItsTarget()
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        Field("_guestWorkFailure").SetValue(null, new InvalidOperationException("previous guest work failed"));
        MetalVideoPresenter.SubmitOffscreenTranslatedDraw(Shader, [], [], 0,
            [new GuestRenderTarget(Address, 4, 4, 10, 0)], null, 3, 1, 4, null, null, null, null, 0);
        Assert.False(Registry("_availableGuestImages").Contains(Address));
        Assert.False(Registry("_guestImageWorkSequences").Contains(Address));
    }

    [Theory]
    [InlineData(GuestImageAccess.Unknown)]
    [InlineData(GuestImageAccess.Atomic)]
    public void UnknownOrIncompleteAtomicIntentCannotBecomeAWriter(GuestImageAccess access)
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        Assert.Throws<NotSupportedException>(() => Submit(Texture(access)));
        Assert.Equal(0L, Field("_enqueuedGuestWorkSequence").GetValue(null));
        Assert.False(Registry("_availableGuestImages").Contains(Address));
    }

    [Fact]
    public void BackpressuredAdmissionDoesNotPublishItsWriterWhileWaiting()
    {
        using var state = new MetalGuestFailureTests.PresenterState();
        for (var index = 0; index < 64; index++)
            Assert.True(MetalVideoPresenter.SubmitOrderedGuestAction(static () => { }, "admission pressure") > 0);
        using var entered = new ManualResetEventSlim();
        var submit = Task.Run(() => { entered.Set(); return Submit(Texture(GuestImageAccess.Write)); });
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            // The bound is the actual production pending count; this task has
            // no room until DrainGuestWork acknowledges existing actions.
            Assert.False(submit.Wait(TimeSpan.FromMilliseconds(100)));
            Assert.False(Registry("_availableGuestImages").Contains(Address));
            Assert.False(Registry("_guestImageWorkSequences").Contains(Address));
            MetalVideoPresenter.DrainGuestWork(0, 0);
            Assert.True(submit.Wait(TimeSpan.FromSeconds(5)));
            Assert.True(submit.Result > 0);
            Assert.Equal(submit.Result, Registry("_guestImageWorkSequences")[Address]);
        }
        finally
        {
            if (!submit.IsCompleted)
            {
                lock (Field("_gate").GetValue(null)!)
                {
                    Field("_closed").SetValue(null, true);
                    Monitor.PulseAll(Field("_gate").GetValue(null)!);
                }
                _ = submit.Wait(TimeSpan.FromSeconds(5));
            }
        }
    }

    private static long Submit(GuestDrawTexture texture, bool writesBuffer = false) =>
        MetalVideoPresenter.SubmitComputeDispatch(0, Shader, [texture], [], 1, 1, 1, 0, 0, 0, writesBuffer, null);
    private static GuestDrawTexture Texture(GuestImageAccess access) =>
        new(Address, 4, 4, 10, 0, new byte[64], false, true, Access: access);
    private static FieldInfo Field(string name) => typeof(MetalVideoPresenter).GetField(name, BindingFlags.Static | BindingFlags.NonPublic)!;
    private static IDictionary Registry(string name) => (IDictionary)Field(name).GetValue(null)!;
}
