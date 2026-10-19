// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.Libs.Tests.Gpu.Scheduling;
using SharpEmu.ShaderCompiler;
using SharpEmu.ShaderCompiler.Metal;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Libs.Tests.Gpu.Pipelines;

// A pixel program the game drew is recorded in the graphics prewarm list, and the prewarm's
// replay of the record translates to the very MSL the draw compiled: the library the
// prewarm makes is the one the game asks for. The draw's translation depends on the
// process-wide mip-statistics switch, so the replay must run with it set the same way.
[Collection(SchedulingStateCollection.Name)]
public sealed class GraphicsPrewarmReplayTests : IDisposable
{
    private const ulong CodeAddress = PipelineTestGuest.MemoryBase + 0x1_0000;
    private const ulong HeaderAddress = PipelineTestGuest.MemoryBase + 0x8000;
    private const uint ImageType2D = 9;

    // MRT0 = the 2D image T# s[0:7] sampled through S# s[8:11] at PARAM0.xy (s12 is the primitive mask).
    private static readonly uint[] SampledTexture =
    [
        0xBEFC030C,                // s_mov_b32 m0, s12
        0xC8080000,                // v_interp_p1_f32_e32 v2, v0, attr0.x
        0xC8090001,                // v_interp_p2_f32_e32 v2, v1, attr0.x
        0xC80C0100,                // v_interp_p1_f32_e32 v3, v0, attr0.y
        0xC80D0101,                // v_interp_p2_f32_e32 v3, v1, attr0.y
        0xF0800F08, 0x00400402,    // image_sample v[4:7], v[2:3], s[0:7], s[8:11] dmask:0xf dim:SQ_RSRC_IMG_2D
        0xBF8C3F70,                // s_waitcnt vmcnt(0)
        0xF800180F, 0x07060504,    // exp mrt0 v4, v5, v6, v7 done vm
        0xBF810000,                // s_endpgm
    ];

    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));
    private readonly FatalScope _fatal = new();
    private readonly bool _previousMipStatistics = ResourceMaterializer.RecordMipStatistics;

    public void Dispose()
    {
        ResourceMaterializer.RecordMipStatistics = _previousMipStatistics;
        _fatal.Dispose();
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static uint[] UserData()
    {
        var userData = new uint[13];
        userData[0] = 0x20;                                                         // T# base
        userData[1] = 71u << 20;                                                    // 8_8_8_8 unorm
        userData[2] = (63u << 14) | 63u;                                            // 64 x 64
        userData[3] = (ImageType2D << 28) | Gen5ShaderTranslator.IdentityImageDstSelect;
        userData[5] = 1u << 25;                                                     // MIP_STATS_ENABLE
        return userData;
    }

    // The draw: translates with the switch as the Metal host sets it, and records the program.
    private string RecordDraw(bool mipStatistics)
    {
        ResourceMaterializer.RecordMipStatistics = mipStatistics;
        var drawn = new List<string>();
        var guest = new PipelineTestGuest(request =>
        {
            Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
            drawn.Add(shader.Source);
            return Encoding.UTF8.GetBytes(shader.Source);
        });
        using (var list = GraphicsPrewarmList.Open(_directory))
        {
            Assert.NotNull(list);
            guest.Host.GraphicsPrewarm = list;
            guest.RegisterProgram(CodeAddress, HeaderAddress, SampledTexture);
            var source = guest.Source(CodeAddress, ShaderStage.Pixel, UserData());
            var options = new StageCompileOptions { PixelInfo = new PixelInputInfo() };
            var cursor = 0u;
            guest.Programs.GetOrCompile(source, options, ref cursor, out _);
        }

        return Assert.Single(drawn);
    }

    // The prewarm: replays every recorded item through the shared request builders.
    private string Replay(bool mipStatistics)
    {
        ResourceMaterializer.RecordMipStatistics = mipStatistics;
        using var list = GraphicsPrewarmList.Open(_directory);
        Assert.NotNull(list);
        var item = Assert.Single(list.Loaded());
        var compiler = new FakeShaderCompiler(request =>
        {
            Assert.True(Gen5MslTranslator.TryCompileProgram(request, out var shader, out var error), error);
            return Encoding.UTF8.GetBytes(shader.Source);
        });
        Assert.True(ShaderPrewarmCompiler.TryCompileGraphicsPrewarm(item, compiler, out var compiled, out var replayError), replayError);
        Assert.NotNull(compiled);
        return Encoding.UTF8.GetString(((FakeCompiledShader)compiled).Spirv);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void ARecordedPixelProgramReplaysToTheMslTheDrawCompiled(bool mipStatistics)
    {
        var drawn = RecordDraw(mipStatistics);
        Assert.Equal(mipStatistics, drawn.Contains("mip_stats", StringComparison.Ordinal));
        Assert.Equal(drawn, Replay(mipStatistics));
    }

    [Fact]
    public void AReplayWithoutTheDrawsMipStatisticsSwitchMakesALibraryTheGameNeverAsksFor()
    {
        var drawn = RecordDraw(mipStatistics: true);
        Assert.NotEqual(drawn, Replay(mipStatistics: false));
    }
}
