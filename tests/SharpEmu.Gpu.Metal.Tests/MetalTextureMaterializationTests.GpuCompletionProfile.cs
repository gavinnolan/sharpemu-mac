// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using System.Runtime.InteropServices;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

public sealed class GpuCompletionProfileNativeFactAttribute : FactAttribute
{
    public GpuCompletionProfileNativeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_GPU_PROFILE") != "1")
            Skip = "Exclusive native GPU timestamp profile worker required.";
    }
}

public sealed partial class MetalTextureMaterializationTests
{
    [GpuCompletionProfileNativeFact]
    public void ActualSuccessfulNativeWaitCapturesTimestampsWithoutDoubleCountingRepeatedWait()
    {
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_TEST_REQUIRE_DEVICE"));
        Assert.Equal("1", Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE"));
        Assert.True(RequireDevice());
        Assert.Null(Presenter.GetField("_guestCommandBufferStatusOverride", Methods)!.GetValue(null));
        Assert.Null(Presenter.GetField("_guestCommandBufferWaitOverride", Methods)!.GetValue(null));
        var profile = Presenter.Assembly.GetType("SharpEmu.Gpu.Metal.MetalGpuCompletionProfile")!;
        var storage = profile.GetNestedType("Storage", BindingFlags.NonPublic)!;
        Assert.True((bool)profile.GetField("Enabled", Methods)!.GetValue(null)!);
        profile.GetMethod("Begin", Methods)!.Invoke(null, null);
        var pool = NullSend(NullSend(NullClass("NSAutoreleasePool"), Selector("alloc")), Selector("init"));
        nint library = 0, function = 0, pipeline = 0, buffer = 0, queue = 0, command = 0, encoder = 0;
        var ended = false; var committed = false; Exception? primary = null;
        try
        {
            const string source = "#include <metal_stdlib>\nusing namespace metal;\nkernel void stamp(device uint* out [[buffer(0)]], uint i [[thread_position_in_grid]]) { out[i] = i * 17u + 3u; }";
            library = NullLibrary(Device.Value, Selector("newLibraryWithSource:options:error:"), NullString(NullClass("NSString"), Selector("stringWithUTF8String:"), source), 0, out var error);
            Assert.NotEqual(0, library); Assert.Equal(0, error);
            function = NullSendArgument(library, Selector("newFunctionWithName:"), NullString(NullClass("NSString"), Selector("stringWithUTF8String:"), "stamp"));
            Assert.NotEqual(0, function);
            pipeline = NullPipeline(Device.Value, Selector("newComputePipelineStateWithFunction:error:"), function, out error);
            Assert.NotEqual(0, pipeline); Assert.Equal(0, error);
            buffer = NullBuffer(Device.Value, Selector("newBufferWithLength:options:"), 256, 0); Assert.NotEqual(0, buffer);
            var pointer = NullSend(buffer, Selector("contents")); Assert.NotEqual(0, pointer);
            Marshal.Copy(Enumerable.Repeat(-1, 64).ToArray(), 0, pointer, 64);
            queue = NullSend(Device.Value, Selector("newCommandQueue")); Assert.NotEqual(0, queue);
            command = NullSend(queue, Selector("commandBuffer")); Assert.NotEqual(0, command); _ = NullSend(command, Selector("retain"));
            encoder = NullSend(command, Selector("computeCommandEncoder")); Assert.NotEqual(0, encoder);
            NullSetObject(encoder, Selector("setComputePipelineState:"), pipeline);
            NullSetBuffer(encoder, Selector("setBuffer:offset:atIndex:"), buffer, 0, 0);
            NullDispatch(encoder, Selector("dispatchThreadgroups:threadsPerThreadgroup:"), new NullSize { Width = 1, Height = 1, Depth = 1 }, new NullSize { Width = 64, Height = 1, Depth = 1 });
            SendVoid(encoder, Selector("endEncoding")); ended = true;
            committed = true; SendVoid(command, Selector("commit"));
            var wait = Presenter.GetMethod("WaitForCommittedCommandBuffer", Methods)!;
            wait.Invoke(null, [command]);
            Assert.Equal(4UL, ReadInteger(command, Selector("status")));
            var actual = new int[64]; Marshal.Copy(pointer, actual, 0, actual.Length);
            Assert.Equal(Enumerable.Range(0, 64).Select(static i => i * 17 + 3), actual);
            wait.Invoke(null, [command]);
            long Read(string field) => (long)storage.GetField(field, Methods)!.GetValue(null)!;
            Assert.Equal(2, Read("Calls")); Assert.Equal(1, Read("Count")); Assert.Equal(1, Read("Repeated")); Assert.Equal(0, Read("Invalid"));
            var rows = ((double Start, double End)[])storage.GetField("Intervals", Methods)!.GetValue(null)!;
            Assert.Equal(ProfileDouble(command, Selector("GPUStartTime")), rows[0].Start);
            Assert.Equal(ProfileDouble(command, Selector("GPUEndTime")), rows[0].End);
            Assert.True(rows[0].Start > 0 && rows[0].End > rows[0].Start);
            wait.Invoke(null, [nint.Zero]); Assert.Equal(2, Read("Calls"));
            output.WriteLine(FormattableString.Invariant($"native-gpu-profile status=4 all64words=true calls=2 unique_intervals=1 repeated=1 zero_handle_ignored=true start_s={rows[0].Start:R} end_s={rows[0].End:R}"));
            profile.GetMethod("End", Methods)!.Invoke(null, null);
        }
        catch (Exception failure) { primary = failure; throw; }
        finally
        {
            Exception? cleanupFirst = null;
            void Attempt(Action action)
            {
                try { action(); }
                catch (Exception cleanup) { cleanupFirst ??= cleanup; if (primary is not null) try { primary.Data["gpu-profile-cleanup-" + primary.Data.Count] = cleanup; } catch { } }
            }
            if (encoder != 0 && !ended) Attempt(() => SendVoid(encoder, Selector("endEncoding")));
            if (command != 0 && committed) Attempt(() => SendVoid(command, Selector("waitUntilCompleted")));
            foreach (var handle in new[] { command, queue, buffer, pipeline, function, library }) if (handle != 0) Attempt(() => SendVoid(handle, Selector("release")));
            if (pool != 0) Attempt(() => SendVoid(pool, Selector("drain")));
            if (primary is null && cleanupFirst is not null) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(cleanupFirst).Throw();
        }
    }

    [LibraryImport("/usr/lib/libobjc.A.dylib", EntryPoint = "objc_msgSend")]
    private static partial double ProfileDouble(nint receiver, nint selector);
}
