// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Buffers.Binary;
using System.Text;
using SharpEmu.HLE;
using SharpEmu.Libs.Kernel;
using Xunit;

namespace SharpEmu.Libs.Tests.Kernel;

// sceKernelGetTscFrequency must describe the same clock that sceKernelReadTsc returns. ReadTsc
// only returns the CPU's RDTSC when the host RDTSC reader is available (64-bit Windows) and
// otherwise falls back to the QPC-based Stopwatch, so the frequency selection has to follow suit.
public sealed class KernelRuntimeCompatExportsTests
{
    private const ulong MemoryBase = 0x1_0000_0000;
    private const ulong UnwindInfoAddress = MemoryBase + 0x400;
    private const int UnwindInfoSize = 0x130;

    private static KernelRuntimeCompatExports.TryGetFrequency Yields(ulong hz) =>
        (out ulong frequencyHz) =>
        {
            frequencyHz = hz;
            return true;
        };

    private static readonly KernelRuntimeCompatExports.TryGetFrequency Fails =
        (out ulong frequencyHz) =>
        {
            frequencyHz = 0;
            return false;
        };

    [Fact]
    public void WithoutHostRdtsc_ReportsStopwatchFrequency_NotHardwareTsc()
    {
        // Regression: on Linux/macOS ReadTsc returns the Stopwatch counter, so the reported
        // frequency must be the Stopwatch's, never the CPU's much larger hardware TSC frequency.
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: false,
            overrideHzText: null,
            tryCalibrate: Yields(2_400_000_000UL),
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(10_000_000UL, frequencyHz);
        Assert.Equal("qpc", source);
    }

    [Fact]
    public void WithHostRdtsc_PrefersCalibratedFrequency()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: true,
            overrideHzText: null,
            tryCalibrate: Yields(2_400_000_000UL),
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(2_400_000_000UL, frequencyHz);
        Assert.Equal("calibrated-rdtsc", source);
    }

    [Fact]
    public void WithHostRdtsc_FallsBackToCpuid_WhenCalibrationFails()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: true,
            overrideHzText: null,
            tryCalibrate: Fails,
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(3_000_000_000UL, frequencyHz);
        Assert.Equal("cpuid", source);
    }

    [Fact]
    public void WithHostRdtsc_UsesStopwatch_WhenRdtscFrequencyUnknown()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: true,
            overrideHzText: null,
            tryCalibrate: Fails,
            tryResolveCpuid: Fails,
            stopwatchFrequency: 10_000_000);

        Assert.Equal(10_000_000UL, frequencyHz);
        Assert.Equal("qpc", source);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void EnvOverride_Wins_WhenSane(bool rdtscAvailable)
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable,
            overrideHzText: "1500000000",
            tryCalibrate: Yields(2_400_000_000UL),
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(1_500_000_000UL, frequencyHz);
        Assert.Equal("env", source);
    }

    [Fact]
    public void EnvOverride_BelowMinimum_IsIgnored()
    {
        // 500 kHz is below the sanity floor, so it is dropped; with rdtsc unavailable the
        // hardware-TSC path is gated off and the Stopwatch frequency is used.
        var (frequencyHz, _) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: false,
            overrideHzText: "500000",
            tryCalibrate: Fails,
            tryResolveCpuid: Yields(3_000_000_000UL),
            stopwatchFrequency: 10_000_000);

        Assert.Equal(10_000_000UL, frequencyHz);
    }

    [Fact]
    public void NonPositiveStopwatchFrequency_FallsBackToDefault()
    {
        var (frequencyHz, source) = KernelRuntimeCompatExports.SelectKernelTscFrequency(
            rdtscAvailable: false,
            overrideHzText: null,
            tryCalibrate: Fails,
            tryResolveCpuid: Fails,
            stopwatchFrequency: 0);

        Assert.Equal(10_000_000UL, frequencyHz); // DefaultKernelTscFrequency
        Assert.Equal("qpc", source);
    }

    [Fact]
    public void ConvertUtcToLocaltime_AllowsNullLocalOutputAndWritesTimesec()
    {
        const long utcSeconds = 1_786_262_400;
        const ulong timesecAddress = MemoryBase + 0x100;
        const ulong dstSecondsAddress = MemoryBase + 0x200;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var timesec = new byte[17];
        Array.Fill(timesec, (byte)0xCC);
        Assert.True(memory.TryWrite(timesecAddress, timesec));

        context[CpuRegister.Rdi] = unchecked((ulong)utcSeconds);
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = timesecAddress;
        context[CpuRegister.Rcx] = dstSecondsAddress;

        var result = KernelRuntimeCompatExports.KernelConvertUtcToLocaltime(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.True(memory.TryRead(timesecAddress, timesec));
        Assert.Equal(utcSeconds, BinaryPrimitives.ReadInt64LittleEndian(timesec));
        Assert.Equal(
            unchecked((uint)GetStandardOffsetSeconds()),
            BinaryPrimitives.ReadUInt32LittleEndian(timesec.AsSpan(8)));
        Assert.Equal(0U, BinaryPrimitives.ReadUInt32LittleEndian(timesec.AsSpan(12)));
        Assert.Equal(0xCC, timesec[16]);

        Span<byte> dstSeconds = stackalloc byte[sizeof(ulong)];
        Assert.True(memory.TryRead(dstSecondsAddress, dstSeconds));
        Assert.Equal(0UL, BinaryPrimitives.ReadUInt64LittleEndian(dstSeconds));
    }

    [Theory]
    [InlineData(Generation.Gen4)]
    [InlineData(Generation.Gen5)]
    public void ConvertLocaltimeToUtc_WritesCompleteTimeResult(Generation generation)
    {
        const long localSeconds = 1_786_240_800;
        const ulong utcAddress = MemoryBase + 0x100;
        const ulong timeResultAddress = MemoryBase + 0x200;
        const ulong dstSecondsAddress = MemoryBase + 0x300;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, generation);
        var timeResult = new byte[17];
        var utc = new byte[9];
        var daylightSavingSeconds = new byte[5];
        Array.Fill(timeResult, (byte)0xCC);
        Array.Fill(utc, (byte)0xCC);
        Array.Fill(daylightSavingSeconds, (byte)0xCC);
        Assert.True(memory.TryWrite(timeResultAddress, timeResult));
        Assert.True(memory.TryWrite(utcAddress, utc));
        Assert.True(memory.TryWrite(dstSecondsAddress, daylightSavingSeconds));

        context[CpuRegister.Rdi] = unchecked((ulong)localSeconds);
        context[CpuRegister.Rsi] = 0;
        context[CpuRegister.Rdx] = utcAddress;
        context[CpuRegister.Rcx] = timeResultAddress;
        context[CpuRegister.R8] = dstSecondsAddress;

        var result = KernelRuntimeCompatExports.KernelConvertLocaltimeToUtc(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);

        Assert.True(memory.TryRead(utcAddress, utc));
        var expectedUtcSeconds = localSeconds - GetStandardOffsetSeconds();
        Assert.Equal(expectedUtcSeconds, BinaryPrimitives.ReadInt64LittleEndian(utc));
        Assert.Equal(0xCC, utc[8]);

        Assert.True(memory.TryRead(timeResultAddress, timeResult));
        Assert.Equal(expectedUtcSeconds, BinaryPrimitives.ReadInt64LittleEndian(timeResult));
        Assert.Equal(GetStandardOffsetSeconds(), BinaryPrimitives.ReadInt32LittleEndian(timeResult.AsSpan(8)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(timeResult.AsSpan(12)));
        Assert.Equal(0xCC, timeResult[16]);

        Assert.True(memory.TryRead(dstSecondsAddress, daylightSavingSeconds));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(daylightSavingSeconds));
        Assert.Equal(0xCC, daylightSavingSeconds[4]);
    }

    [Theory]
    [InlineData(0x00, -1)]
    [InlineData(0x01, 0)]
    [InlineData(0xCC, 1)]
    [InlineData(0xFF, -1)]
    public void TimeConversionRoundTrip_OverwritesStaleOffsetAndDaylightSavingFields(
        byte initialValue,
        int daylightSavingHint)
    {
        const ulong utcAddress = MemoryBase + 0x100;
        const ulong localAddress = MemoryBase + 0x200;
        const ulong forwardResultAddress = MemoryBase + 0x300;
        const ulong reverseResultAddress = MemoryBase + 0x400;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var forwardResult = new byte[16];
        var reverseResult = new byte[16];

        foreach (var localSeconds in new[] { 1_767_225_600L, 1_767_312_000L })
        {
            Array.Fill(forwardResult, initialValue);
            Array.Fill(reverseResult, (byte)~initialValue);
            Assert.True(memory.TryWrite(forwardResultAddress, forwardResult));
            Assert.True(memory.TryWrite(reverseResultAddress, reverseResult));
            context[CpuRegister.Rdi] = unchecked((ulong)localSeconds);
            context[CpuRegister.Rsi] = unchecked((ulong)daylightSavingHint);
            context[CpuRegister.Rdx] = utcAddress;
            context[CpuRegister.Rcx] = forwardResultAddress;
            context[CpuRegister.R8] = 0;

            Assert.Equal(0, KernelRuntimeCompatExports.KernelConvertLocaltimeToUtc(context));
            Assert.True(context.TryReadUInt64(utcAddress, out var utcSeconds));
            context[CpuRegister.Rdi] = utcSeconds;
            context[CpuRegister.Rsi] = localAddress;
            context[CpuRegister.Rdx] = reverseResultAddress;
            context[CpuRegister.Rcx] = 0;

            Assert.Equal(0, KernelRuntimeCompatExports.KernelConvertUtcToLocaltime(context));
            Assert.True(context.TryReadUInt64(localAddress, out var returnedLocalSeconds));
            Assert.Equal(unchecked((ulong)localSeconds), returnedLocalSeconds);
            Assert.True(memory.TryRead(forwardResultAddress, forwardResult));
            Assert.True(memory.TryRead(reverseResultAddress, reverseResult));
            // Calendar conversion compares both offsets before it selects the daylight-saving state.
            Assert.Equal(reverseResult.AsSpan(8, 8).ToArray(), forwardResult.AsSpan(8, 8).ToArray());
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(forwardResult.AsSpan(12)));
            Assert.Equal(reverseResult, forwardResult);
        }
    }

    [Theory]
    [InlineData(8)]
    [InlineData(15)]
    public void ConvertLocaltimeToUtc_RejectsTruncatedTimeResult(int availableBytes)
    {
        var memory = new FakeCpuMemory(MemoryBase, availableBytes);
        var context = new CpuContext(memory, Generation.Gen5);
        var initialBytes = new byte[availableBytes];
        Array.Fill(initialBytes, (byte)0xCC);
        Assert.True(memory.TryWrite(MemoryBase, initialBytes));
        context[CpuRegister.Rdi] = 1_767_225_600;
        context[CpuRegister.Rcx] = MemoryBase;

        var result = KernelRuntimeCompatExports.KernelConvertLocaltimeToUtc(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT, result);
        var remainingBytes = new byte[availableBytes];
        Assert.True(memory.TryRead(MemoryBase, remainingBytes));
        Assert.Equal(initialBytes, remainingBytes);
    }

    [Fact]
    public void ConvertLocaltimeToUtc_AllowsNullOptionalOutputs()
    {
        const long localSeconds = 1_767_225_600;
        var memory = new FakeCpuMemory(MemoryBase, 16);
        var context = new CpuContext(memory, Generation.Gen5);
        context[CpuRegister.Rdi] = unchecked((ulong)localSeconds);
        context[CpuRegister.Rcx] = MemoryBase;

        Assert.Equal(0, KernelRuntimeCompatExports.KernelConvertLocaltimeToUtc(context));
        var timeResult = new byte[16];
        Assert.True(memory.TryRead(MemoryBase, timeResult));
        Assert.Equal(localSeconds - GetStandardOffsetSeconds(), BinaryPrimitives.ReadInt64LittleEndian(timeResult));
        Assert.Equal(GetStandardOffsetSeconds(), BinaryPrimitives.ReadInt32LittleEndian(timeResult.AsSpan(8)));
        Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(timeResult.AsSpan(12)));
    }

    [Fact]
    public void ConvertLocaltimeToUtc_RequiresTimeResult()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = 1_767_225_600;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT,
            KernelRuntimeCompatExports.KernelConvertLocaltimeToUtc(context));
    }

    [Theory]
    [InlineData(CpuRegister.Rdx)]
    [InlineData(CpuRegister.R8)]
    public void ConvertLocaltimeToUtc_ReportsInvalidOptionalOutput(CpuRegister outputRegister)
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = 1_767_225_600;
        context[CpuRegister.Rcx] = MemoryBase;
        context[outputRegister] = MemoryBase + 0x1000;

        Assert.Equal(
            (int)OrbisGen2Result.ORBIS_GEN2_ERROR_MEMORY_FAULT,
            KernelRuntimeCompatExports.KernelConvertLocaltimeToUtc(context));
    }

    [Theory]
    [InlineData(360, 0, -21_600)]
    [InlineData(360, 3_600, -18_000)]
    [InlineData(-345, 0, 20_700)]
    public void TimeConversionArithmetic_HandlesOffsetsAndDst(
        int minutesWest,
        int dstSeconds,
        long expectedLocalDelta)
    {
        const long utcSeconds = 1_786_262_400;

        var localSeconds = KernelRuntimeCompatExports.ConvertUtcToLocaltimeSeconds(
            utcSeconds,
            minutesWest,
            dstSeconds);
        var roundTrip = KernelRuntimeCompatExports.ConvertLocaltimeToUtcSeconds(
            localSeconds,
            minutesWest,
            dstSeconds);

        Assert.Equal(utcSeconds + expectedLocalDelta, localSeconds);
        Assert.Equal(utcSeconds, roundTrip);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void GetModuleInfoForUnwind_HostAddressWritesSyntheticBoundary(bool useSysmoduleAlias)
    {
        const ulong queriedAddress = 0x00000243_A84C_FFFF;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        var payload = new byte[UnwindInfoSize + 1];
        Array.Fill(payload, (byte)0xCC);
        Assert.True(memory.TryWrite(UnwindInfoAddress, payload));
        Assert.True(context.TryWriteUInt64(UnwindInfoAddress, UnwindInfoSize));
        context[CpuRegister.Rdi] = queriedAddress;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = UnwindInfoAddress;

        var result = useSysmoduleAlias
            ? KernelRuntimeCompatExports.SysmoduleGetModuleInfoForUnwind(context)
            : KernelRuntimeCompatExports.KernelGetModuleInfoForUnwind(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
        Assert.True(memory.TryRead(UnwindInfoAddress, payload));
        Assert.Equal((ulong)UnwindInfoSize, BinaryPrimitives.ReadUInt64LittleEndian(payload));
        Assert.Equal("SharpEmuHostBoundary", ReadUtf8Z(payload.AsSpan(0x08, 0x100)));
        Assert.All(payload.AsSpan(0x108, 0x18).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(
            queriedAddress & ~0xF_FFFFUL,
            BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(0x120)));
        Assert.Equal(0x10_0000UL, BinaryPrimitives.ReadUInt64LittleEndian(payload.AsSpan(0x128)));
        Assert.Equal(0xCC, payload[UnwindInfoSize]);
    }

    [Fact]
    public void GetModuleInfoForUnwind_MissingGuestAddressReturnsNotFound()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(context.TryWriteUInt64(UnwindInfoAddress, UnwindInfoSize));
        context[CpuRegister.Rdi] = 0x00000008_7FFF_1234;
        context[CpuRegister.Rsi] = 1;
        context[CpuRegister.Rdx] = UnwindInfoAddress;

        var result = KernelRuntimeCompatExports.KernelGetModuleInfoForUnwind(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
    }

    [Fact]
    public void GetModuleInfoForUnwind_InvalidFlagsReturnsInvalidArgument()
    {
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(context.TryWriteUInt64(UnwindInfoAddress, UnwindInfoSize));
        context[CpuRegister.Rdi] = 0x00000243_A84C_FFFF;
        context[CpuRegister.Rsi] = 3;
        context[CpuRegister.Rdx] = UnwindInfoAddress;

        var result = KernelRuntimeCompatExports.KernelGetModuleInfoForUnwind(context);

        Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_INVALID_ARGUMENT, result);
    }

    [Fact]
    public void IsSignalReturn_ReportsNoSyntheticSignalFrame()
    {
        var context = new CpuContext(new FakeCpuMemory(MemoryBase, 0x1000), Generation.Gen5);
        context[CpuRegister.Rdi] = 0x00000243_A84C_FFFF;

        var result = KernelRuntimeCompatExports.KernelIsSignalReturn(context);

        Assert.Equal(0, result);
        Assert.Equal(0UL, context[CpuRegister.Rax]);
    }

    [Fact]
    public void LoadStartModule_UsesConfiguredLoaderForExactGuestPath()
    {
        const ulong pathAddress = MemoryBase + 0x500;
        const ulong resultAddress = MemoryBase + 0x600;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(pathAddress, Encoding.UTF8.GetBytes("/app0/5/tomb5.prx\0")));
        context[CpuRegister.Rdi] = pathAddress;
        context[CpuRegister.R9] = resultAddress;
        string? requestedPath = null;

        KernelModuleRegistry.Reset();
        try
        {
            KernelModuleRegistry.ConfigureModuleLoader(path =>
            {
                requestedPath = path;
                var handle = KernelModuleRegistry.RegisterModule(
                    "C:/games/app0/5/tomb5.prx",
                    0x8100_0000,
                    0x10_0000,
                    0x8100_0000,
                    0,
                    0,
                    0,
                    0,
                    isMain: false);
                return KernelModuleRegistry.ModuleLoadResult.Success(handle);
            });

            var result = KernelRuntimeCompatExports.KernelLoadStartModule(context);

            Assert.Equal(0, result);
            Assert.Equal("/app0/5/tomb5.prx", requestedPath);
            Assert.Equal(1UL, context[CpuRegister.Rax]);
            Span<byte> startResult = stackalloc byte[sizeof(int)];
            Assert.True(memory.TryRead(resultAddress, startResult));
            Assert.Equal(0, BinaryPrimitives.ReadInt32LittleEndian(startResult));
        }
        finally
        {
            KernelModuleRegistry.Reset();
        }
    }

    [Fact]
    public void LoadStartModule_DoesNotCreateSyntheticModuleWhenExactLoadFails()
    {
        const ulong pathAddress = MemoryBase + 0x500;
        var memory = new FakeCpuMemory(MemoryBase, 0x1000);
        var context = new CpuContext(memory, Generation.Gen5);
        Assert.True(memory.TryWrite(pathAddress, Encoding.UTF8.GetBytes("/app0/missing.prx\0")));
        context[CpuRegister.Rdi] = pathAddress;

        KernelModuleRegistry.Reset();
        try
        {
            KernelModuleRegistry.ConfigureModuleLoader(_ =>
                KernelModuleRegistry.ModuleLoadResult.Failure(
                    (int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND));

            var result = KernelRuntimeCompatExports.KernelLoadStartModule(context);

            Assert.Equal((int)OrbisGen2Result.ORBIS_GEN2_ERROR_NOT_FOUND, result);
            Assert.Empty(KernelModuleRegistry.GetModuleHandles(includeSystemModules: true));
        }
        finally
        {
            KernelModuleRegistry.Reset();
        }
    }

    [Fact]
    public void ModuleRegistry_ExactPathNormalizesEquivalentPaths()
    {
        var modulePath = Path.Combine(Path.GetTempPath(), "sharpemu", "app0", "PSNCommon.prx");
        var equivalentPath = Path.Combine(
            Path.GetDirectoryName(modulePath)!,
            ".",
            Path.GetFileName(modulePath));

        KernelModuleRegistry.Reset();
        try
        {
            var handle = KernelModuleRegistry.RegisterModule(
                modulePath,
                0x8100_0000,
                0x10_0000,
                0x8100_0000,
                0,
                0,
                0,
                0,
                isMain: false);

            Assert.True(KernelModuleRegistry.TryFindByExactPath(equivalentPath, out var module));
            Assert.Equal(handle, module.Handle);
        }
        finally
        {
            KernelModuleRegistry.Reset();
        }
    }

    [Fact]
    public void ModuleRegistry_ExactPathDoesNotFallBackToMatchingFileName()
    {
        var registeredPath = Path.Combine(Path.GetTempPath(), "sharpemu", "first", "PSNCommon.prx");
        var differentPath = Path.Combine(Path.GetTempPath(), "sharpemu", "second", "PSNCommon.prx");

        KernelModuleRegistry.Reset();
        try
        {
            _ = KernelModuleRegistry.RegisterModule(
                registeredPath,
                0x8100_0000,
                0x10_0000,
                0x8100_0000,
                0,
                0,
                0,
                0,
                isMain: false);

            Assert.False(KernelModuleRegistry.TryFindByExactPath(differentPath, out _));
        }
        finally
        {
            KernelModuleRegistry.Reset();
        }
    }

    [Fact]
    public void ModuleRegistry_ScopedLookupWalksNeededModulesBreadthFirst()
    {
        KernelModuleRegistry.Reset();
        try
        {
            int Register(string name, ulong symbolAddress, params string[] needed)
            {
                var handle = KernelModuleRegistry.RegisterModule(
                    Path.Combine(Path.GetTempPath(), "sharpemu-dag", name),
                    symbolAddress & ~0xFFFFUL,
                    0x10_0000,
                    0,
                    0,
                    0,
                    0,
                    0,
                    isMain: false);
                KernelModuleRegistry.RegisterModuleSymbols(
                    handle,
                    new Dictionary<string, ulong> { [$"own_{Path.GetFileNameWithoutExtension(name)}"] = symbolAddress, ["shared"] = symbolAddress });
                KernelModuleRegistry.RegisterModuleDependencies(handle, needed);
                return handle;
            }

            var leaf = Register("libLeaf.prx", 0x9000_1000);
            var middle = Register("libMiddle.sprx", 0x9100_1000, "libLeaf");
            var top = Register("libTop.prx", 0x9200_1000, "libMiddle", "libSceNotMapped");
            var unrelated = Register("libOther.prx", 0x9300_1000);

            // The handle itself wins over its dependencies.
            Assert.True(KernelModuleRegistry.TryResolveModuleSymbolInScope(top, ["shared"], out var address));
            Assert.Equal(0x9200_1000UL, address);
            // Dependencies are reached transitively, by name with or without extension.
            Assert.True(KernelModuleRegistry.TryResolveModuleSymbolInScope(top, ["own_libLeaf"], out address));
            Assert.Equal(0x9000_1000UL, address);
            // A module outside the handle's DAG is not visible.
            Assert.False(KernelModuleRegistry.TryResolveModuleSymbolInScope(top, ["own_libOther"], out _));
            Assert.False(KernelModuleRegistry.TryResolveModuleSymbolInScope(leaf, ["own_libMiddle"], out _));
            Assert.True(KernelModuleRegistry.HasModuleSymbols(middle));
            Assert.True(KernelModuleRegistry.HasModuleSymbols(unrelated));
            Assert.False(KernelModuleRegistry.HasModuleSymbols(
                KernelModuleRegistry.RegisterSyntheticModule("libSceHleOnly.sprx", isSystemModule: true)));
        }
        finally
        {
            KernelModuleRegistry.Reset();
        }
    }

    [Fact]
    public void Sysmodule_LoadIsRefcountedAndReportsSdkErrors()
    {
        const ushort id = 0x7AB1; // not a real module id; ids are not table-checked
        var memory = new FakeCpuMemory(0x1_0000, 0x1000);
        var ctx = new CpuContext(memory, Generation.Gen5);

        int Call(Func<CpuContext, int> export, ulong id)
        {
            ctx[CpuRegister.Rdi] = id;
            var result = export(ctx);
            Assert.Equal(unchecked((ulong)(long)result), ctx[CpuRegister.Rax]);
            return result;
        }

        Assert.Equal(KernelRuntimeCompatExports.SceSysmoduleErrorUnloaded, Call(KernelRuntimeCompatExports.SysmoduleIsLoaded, id));
        Assert.Equal(KernelRuntimeCompatExports.SceSysmoduleErrorUnloaded, Call(KernelRuntimeCompatExports.SysmoduleUnloadModule, id));

        // Upper register bits are not part of the uint16 id.
        Assert.Equal(0, Call(KernelRuntimeCompatExports.SysmoduleLoadModule, 0xDEAD_0000_0000_0000UL | id));
        Assert.Equal(0, Call(KernelRuntimeCompatExports.SysmoduleLoadModule, id));
        Assert.Equal(0, Call(KernelRuntimeCompatExports.SysmoduleIsLoaded, id));
        Assert.Equal(0, Call(KernelRuntimeCompatExports.SysmoduleUnloadModule, id));
        Assert.Equal(0, Call(KernelRuntimeCompatExports.SysmoduleIsLoaded, id));
        Assert.Equal(0, Call(KernelRuntimeCompatExports.SysmoduleUnloadModule, id));
        Assert.Equal(KernelRuntimeCompatExports.SceSysmoduleErrorUnloaded, Call(KernelRuntimeCompatExports.SysmoduleIsLoaded, id));

        Assert.Equal(KernelRuntimeCompatExports.SceSysmoduleErrorInvalidValue, Call(KernelRuntimeCompatExports.SysmoduleLoadModule, 0));
        Assert.Equal(KernelRuntimeCompatExports.SceSysmoduleErrorInvalidValue, Call(KernelRuntimeCompatExports.SysmoduleIsLoaded, 0x1_0000));
        Assert.Equal(unchecked((int)0x805A1000), KernelRuntimeCompatExports.SceSysmoduleErrorInvalidValue);
        Assert.Equal(unchecked((int)0x805A1001), KernelRuntimeCompatExports.SceSysmoduleErrorUnloaded);
    }

    [Theory]
    [InlineData("malloc", new[] { "malloc", "gQX+4GDQjpM" })]
    [InlineData("gQX+4GDQjpM#C#A", new[] { "gQX+4GDQjpM" })]
    [InlineData("UnityPluginLoad", new[] { "UnityPluginLoad", null })]
    public void Dlsym_LookupNamesStripTheNidSuffix(string symbol, string?[] expected)
    {
        var names = SharpEmu.Core.Cpu.Native.DirectExecutionBackend.DlsymLookupNames(symbol);
        Assert.Equal(expected[0], names[0]);
        if (expected.Length > 1 && expected[1] is { } nid)
        {
            Assert.Equal(nid, names[1]);
        }

        Assert.Equal(expected.Length, names.Length);
    }

    private static int GetMinutesWest() =>
        unchecked((int)-TimeZoneInfo.Local.BaseUtcOffset.TotalMinutes);

    private static int GetStandardOffsetSeconds() =>
        unchecked(-GetMinutesWest() * 60);

    private static string ReadUtf8Z(ReadOnlySpan<byte> value)
    {
        var length = value.IndexOf((byte)0);
        Assert.True(length >= 0);
        return Encoding.UTF8.GetString(value[..length]);
    }
}
