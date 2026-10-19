// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Reflection;
using SharpEmu.Gpu.Metal;
using SharpEmu.HLE;
using SharpEmu.Libs.Agc;
using SharpEmu.Libs.Gpu;
using SharpEmu.Libs.Gpu.Abstractions;
using SharpEmu.Libs.Gpu.GpuCommands;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler.Metal;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Metal;

public sealed partial class MetalRenderHostTests
{
    private sealed class ProfileReadMemory(FakeCpuMemory inner, ulong failAddress = 0, Exception? failure = null) : ICpuMemory
    {
        internal readonly List<ulong> Reads = [];
        public bool TryRead(ulong address, Span<byte> destination)
        {
            Reads.Add(address);
            if (address == failAddress) throw failure!;
            return inner.TryRead(address, destination);
        }
        public bool TryWrite(ulong address, ReadOnlySpan<byte> source) => inner.TryWrite(address, source);
        public bool CanRead(ulong address, ulong count) => inner.CanRead(address, count);
    }

    private void ProfileSubmit(ProfileReadMemory trace)
    {
        var host = new MetalCommandStreamHost(trace, _backend, _backend);
        host.AttachQueue(new CommandStreamQueue(host));
        host.BeginSubmission(0, 1, null);
        var render = (IRenderHost)host;
        var descriptor = new BufferDescriptorWords(unchecked((uint)VertexBase), (uint)(VertexBase >> 32) | 32u << 16,
            8, 77u << 12);
        var input = new VertexInputInfo
        {
            Buffers = [new(VertexBase, 32, 8)],
            Attributes = [new(descriptor, 0, 1, 0, 0, 0, 0), new(descriptor, 4, 1, 0, 1, 0, 8)],
        };
        _memory.TryWrite(VertexBase, Pattern(256, 17));
        _memory.TryWrite(IndexBase, new byte[] { 0, 0, 5, 0, 2, 0 });
        render.BindVertexBuffers([render.ObtainBuffer(VertexBase, 256, false)], input);
        render.BindIndexBuffer(render.ObtainBuffer(IndexBase, 6, false), GpuIndexType.Uint16);
        var type = typeof(MetalCommandStreamHost).GetNestedType("GraphicsPipelineRecord", BindingFlags.NonPublic)!;
        var pipeline = Activator.CreateInstance(type, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null,
            [Shader("prefix", Gen5MslStage.Vertex), Shader("pixel", Gen5MslStage.Pixel), new ShaderProgramInfo(), 0u,
                new PipelineStaticParameters { Topology = GpuPrimitiveTopology.TriangleList }], null)!;
        typeof(MetalCommandStreamHost).GetField("_boundPipeline", Members)!.SetValue(host, pipeline);
        PrefixInvocation.SetValue(host, true);
        try { render.DrawIndexed(3, 1, 0, 0, 0); }
        finally { PrefixInvocation.SetValue(host, false); }
    }

    [Fact]
    public void VertexProfileActualSubmitCountsOriginalStreamOnceAndHonorsCachedPolicy()
    {
        var before = MetalVertexPrefixProfile.Snapshot();
        var trace = new ProfileReadMemory(_memory);
        ProfileSubmit(trace);
        Assert.Equal(MetalVertexPrefixProfile.PrefixEnabled ? new[] { IndexBase, VertexBase } : new[] { VertexBase, IndexBase }, trace.Reads);
        var after = MetalVertexPrefixProfile.Snapshot();
        if (MetalVertexPrefixProfile.Enabled)
        {
            Assert.Equal(1, after.Attempted - before.Attempted);
            Assert.Equal(1, after.PacketEligible - before.PacketEligible);
            Assert.Equal(MetalVertexPrefixProfile.PrefixEnabled ? 1 : 0, after.Qualified - before.Qualified);
            Assert.Equal(1, after.Copied - before.Copied);
            Assert.Equal(MetalVertexPrefixProfile.PrefixEnabled ? 1 : 0, after.Applied - before.Applied);
            Assert.Equal(256, after.FullBytes - before.FullBytes); // two attributes, one original stream
            Assert.Equal(MetalVertexPrefixProfile.PrefixEnabled ? 184 : 256, after.ActualBytes - before.ActualBytes);
            Assert.Equal(before.Failed, after.Failed);
            Assert.Equal(before.Active, after.Active);
        }
        else Assert.Equal(default, after);
    }

    [Fact]
    public void VertexProfileActualReadFailurePreservesCauseAndRecordsIncompleteCopy()
    {
        var expected = new InvalidOperationException("controlled vertex read failure");
        var before = MetalVertexPrefixProfile.Snapshot();
        var trace = new ProfileReadMemory(_memory, VertexBase, expected);
        Assert.Same(expected, Assert.Throws<InvalidOperationException>(() => ProfileSubmit(trace)));
        var after = MetalVertexPrefixProfile.Snapshot();
        if (MetalVertexPrefixProfile.Enabled)
        {
            Assert.Equal(1, after.Attempted - before.Attempted);
            Assert.Equal(1, after.Failed - before.Failed);
            Assert.Equal(before.Copied, after.Copied);
            Assert.Equal(before.FullBytes, after.FullBytes);
            Assert.Equal(before.ActualBytes, after.ActualBytes);
            Assert.Equal(before.Active, after.Active);
        }
        else Assert.Equal(default, after);
    }

    [Fact]
    public void VertexProfileReportFailureDoesNotReplaceOriginalGuestFailureAndOffIsUnknown()
    {
        var field = typeof(MetalVertexPrefixProfile).GetField("_reportSinkOverride", BindingFlags.NonPublic | BindingFlags.Static)!;
        string? line = null;
        field.SetValue(null, (Action<string>)(value => { line = value; throw new IOException("closed diagnostic sink"); }));
        var expected = new InvalidOperationException("original guest failure");
        try
        {
            Assert.Same(expected, Assert.Throws<InvalidOperationException>((Action)(() =>
            {
                try { throw expected; }
                finally { MetalVertexPrefixProfile.EndReport(); }
            })));
            if (MetalVertexPrefixProfile.Enabled)
            {
                Assert.NotNull(line);
                Assert.Contains(MetalVertexPrefixProfile.PrefixEnabled ? "policy=on" : "policy=off", line);
                if (!MetalVertexPrefixProfile.PrefixEnabled) Assert.Contains("qualified=unknown", line);
                Assert.Contains("scope=process_producer_payload", line);
            }
            else Assert.Null(line);
        }
        finally { field.SetValue(null, null); }
    }

    private sealed class ClosedProfileWriter : TextWriter
    {
        public override System.Text.Encoding Encoding => System.Text.Encoding.UTF8;
        public override void WriteLine(string? value) => throw new IOException("existing presenter diagnostic stream closed");
    }

    [Fact]
    public void VertexProfileEndStillRunsWhenExistingPresenterOutputFails()
    {
        var sink = typeof(MetalVertexPrefixProfile).GetField("_reportSinkOverride", BindingFlags.NonPublic | BindingFlags.Static)!;
        var previous = Console.Error;
        string? line = null;
        try
        {
            sink.SetValue(null, (Action<string>)(value => line = value));
            Console.SetError(new ClosedProfileWriter());
            MetalPresentationProfile.End();
            Assert.Equal(MetalVertexPrefixProfile.Enabled, line is not null);
        }
        finally { Console.SetError(previous); sink.SetValue(null, null); }
    }

    [Fact]
    public void VertexProfileFlagsRemainCachedAfterEnvironmentChanges()
    {
        var policy = MetalVertexPrefixProfile.PrefixEnabled;
        var enabled = MetalVertexPrefixProfile.Enabled;
        var savedPolicy = Environment.GetEnvironmentVariable("SHARPEMU_METAL_VERTEX_PREFIX");
        var savedProfile = Environment.GetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE");
        try
        {
            Environment.SetEnvironmentVariable("SHARPEMU_METAL_VERTEX_PREFIX", policy ? "0" : "1");
            Environment.SetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE", enabled ? "0" : "1");
            Assert.Equal(policy, MetalVertexPrefixProfile.PrefixEnabled);
            Assert.Equal(enabled, MetalVertexPrefixProfile.Enabled);
        }
        finally
        {
            Environment.SetEnvironmentVariable("SHARPEMU_METAL_VERTEX_PREFIX", savedPolicy);
            Environment.SetEnvironmentVariable("SHARPEMU_PROFILE_PERFORMANCE", savedProfile);
        }
    }
}
