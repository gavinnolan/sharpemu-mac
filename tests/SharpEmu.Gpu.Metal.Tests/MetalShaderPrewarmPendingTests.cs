// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using SharpEmu.Gpu.Metal.V2;
using SharpEmu.HLE;
using SharpEmu.Libs.Gpu.Pipelines;
using SharpEmu.Libs.Gpu.Rendering;
using SharpEmu.ShaderCompiler.Resources;
using Xunit;

namespace SharpEmu.Gpu.Metal.Tests;

// What a v2 start prewarms. The driver's shader cache is the OS's and evicts by itself,
// so a stamp left by an earlier finished prewarm must not skip the recorded pipelines.
public sealed class MetalShaderPrewarmPendingTests : IDisposable
{
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "SharpEmuTests", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    [Fact]
    public void ACurrentStampStillPrewarmsEveryRecordedCompute()
    {
        using (var list = Open())
        {
            Record(list, 0x1000);
            Record(list, 0x2000);
            list.WriteStamp("build-a");
        }

        using var reopened = Open();
        Assert.True(reopened.IsStampCurrent("build-a"));
        var pending = MetalShaderPrewarm.PendingComputes(reopened, "build-a", out var resumed);
        Assert.Equal(0, resumed);
        Assert.Equal(new ulong[] { 0x1000, 0x2000 }, pending.Select(item => item.Record.Address).Order());
    }

    [Fact]
    public void AnInterruptedPrewarmResumesWithTheComputesItHadNotFinished()
    {
        using (var list = Open())
        {
            Record(list, 0x1000);
            Record(list, 0x2000);
        }

        using var reopened = Open();
        var finished = reopened.LoadedComputes().Single(item => item.Record.Address == 0x1000);
        reopened.AppendProgress("build-a", [ShaderPrewarmList.Identity(finished.Record)]);
        var pending = MetalShaderPrewarm.PendingComputes(reopened, "build-a", out var resumed);
        Assert.Equal(1, resumed);
        Assert.Equal(0x2000UL, Assert.Single(pending).Record.Address);
    }

    private ShaderPrewarmList Open() =>
        ShaderPrewarmList.Open(_directory) ?? throw new InvalidOperationException("The list did not open.");

    private static void Record(ShaderPrewarmList list, ulong address)
    {
        var code = new ShaderCodeCapture
        {
            Hash = address,
            CodeSize = 4,
            Address = address,
            Generation = Generation.Gen5,
            Ranges = [new CodeRange(address, [0, 0, 0, 0])],
        };
        list.RecordCompute(code, new ComputePrewarmRecord
        {
            Hash = address,
            CodeSize = 4,
            Address = address,
            UserDataBase = 0,
            UserDataCount = 0,
            PushDataCursor = 0,
            Info = new ComputeInputInfo { ThreadsX = 64, ThreadsY = 1, ThreadsZ = 1 },
            Specialization = new ResourceSpecialization(),
        });
    }
}
