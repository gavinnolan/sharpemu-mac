// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Runtime.InteropServices;
using SharpEmu.PerfAudit;
using SharpEmu.ShaderCompiler.Resources;

Console.WriteLine($"arch={RuntimeInformation.ProcessArchitecture} runtime={RuntimeInformation.FrameworkDescription}");
Harness.WarmupMs = double.TryParse(Environment.GetEnvironmentVariable("PA_WARMUP_MS"), out var warmup) ? warmup : 1500;
MaterializationBenches.Run();
Console.WriteLine(Harness.Table());
ResourceMaterializationProfile.WriteReport();
