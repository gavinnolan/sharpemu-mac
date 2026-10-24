// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Globalization;
using System.Runtime.InteropServices;
using SharpEmu.GpuRemote;

namespace SharpEmu.GpuHost;

/// <summary>
/// Entry point of the native GPU host (process B of the split).
/// Usage: <c>SharpEmu.GpuHost --shm &lt;region name&gt; --parent &lt;pid&gt; [--spin-ns &lt;n&gt;]</c>;
/// started by <see cref="GpuProcessHost"/>, not by hand.
/// </summary>
public static class GpuHostMain
{
    public static int Main(string[] args)
    {
        if (!OperatingSystem.IsMacOS())
        {
            Console.Error.WriteLine("[GPUHOST][ERROR] The GPU host runs only on macOS.");
            return GpuProcessClient.ExitUsage;
        }

        string? region = null;
        var parent = 0;
        var spin = GpuProcessClient.DefaultSpinNanoseconds;
        for (var i = 0; i < args.Length; i++)
        {
            var value = i + 1 < args.Length ? args[i + 1] : null;
            switch (args[i])
            {
                case "--shm" when value is not null:
                    region = value;
                    i++;
                    break;
                case "--parent" when value is not null && int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out parent):
                    i++;
                    break;
                case "--spin-ns" when value is not null && long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out spin):
                    i++;
                    break;
                default:
                    return Usage($"unexpected argument '{args[i]}'");
            }
        }

        if (region is null || parent <= 1)
        {
            return Usage("--shm and --parent are required");
        }

        GpuProcessClient client;
        try
        {
            client = GpuProcessClient.Attach(region, parent, TimeSpan.FromSeconds(10), spin);
        }
        catch (Exception exception) when (exception is IOException or InvalidDataException or ArgumentException)
        {
            Console.Error.WriteLine($"[GPUHOST][ERROR] Could not attach to {region}: {exception.Message}");
            return GpuProcessClient.ExitProtocolError;
        }

        using (client)
        {
            if (Environment.GetEnvironmentVariable("SHARPEMU_GPU_HOST_VERBOSE") == "1")
            {
                Console.Error.WriteLine(
                    $"[GPUHOST][INFO] Attached to {region}: {RuntimeInformation.ProcessArchitecture}, pid {Environment.ProcessId}, " +
                    $"host {client.Host}, doorbell spin {spin} ns.");
            }

            return client.Run();
        }
    }

    private static int Usage(string problem)
    {
        Console.Error.WriteLine($"[GPUHOST][ERROR] {problem}. Usage: SharpEmu.GpuHost --shm <region> --parent <pid> [--spin-ns <n>]");
        return GpuProcessClient.ExitUsage;
    }
}
