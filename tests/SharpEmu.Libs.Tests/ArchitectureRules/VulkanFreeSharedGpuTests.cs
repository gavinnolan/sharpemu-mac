// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using Xunit;

namespace SharpEmu.Libs.Tests.ArchitectureRules;

// The macOS build is Metal-only and carries no Vulkan code at all, and the GPU backends are
// on their way into separate SharpEmu.Gpu.Metal and SharpEmu.Gpu.Vulkan assemblies. Both
// need the shared GPU engine and the Metal backend to speak the backend-neutral vocabulary
// in Gpu/Abstractions rather than Silk.NET.Vulkan types, so this test scans the source tree
// and fails on any reference that creeps back in. It reads the sources rather than the
// compiled assembly because today everything still lives in one project.
public sealed class VulkanFreeSharedGpuTests
{
    // Folders that must stay free of the Vulkan graphics API. The Gpu folder itself is
    // scanned without recursing, because its backend-neutral entry points sit next to the
    // Vulkan-only resource caches in Gpu/Buffers, Gpu/Images and Gpu/Vulkan.
    private static readonly (string Path, SearchOption Depth)[] ScannedDirectories =
    [
        ("src/SharpEmu.Libs/Gpu", SearchOption.TopDirectoryOnly),
        ("src/SharpEmu.Libs/Gpu/Abstractions", SearchOption.AllDirectories),
        ("src/SharpEmu.Libs/Gpu/GpuCommands", SearchOption.AllDirectories),
        ("src/SharpEmu.Libs/Gpu/Metal", SearchOption.AllDirectories),
        ("src/SharpEmu.Libs/Gpu/Pipelines", SearchOption.AllDirectories),
        ("src/SharpEmu.Libs/Gpu/Rendering", SearchOption.AllDirectories),
        ("src/SharpEmu.Libs/Gpu/Scheduling", SearchOption.AllDirectories),
    ];

    // Files inside the scanned folders that only the Vulkan backend uses, with the reason
    // each one stays Vulkan. Keep this list short: a new entry means a file that the
    // assembly split will have to move into SharpEmu.Gpu.Vulkan.
    private static readonly Dictionary<string, string> Allowed = new(StringComparer.Ordinal)
    {
        // Writes VkWriteDescriptorSet arrays for the Vulkan descriptor path.
        ["src/SharpEmu.Libs/Gpu/Pipelines/DescriptorWriter.cs"] = "Vulkan descriptor writes",
        // Owns the Vulkan descriptor pools and set layouts.
        ["src/SharpEmu.Libs/Gpu/Pipelines/DescriptorHeap.cs"] = "Vulkan descriptor pools",
        // Records the Vulkan memory barrier that orders global-data-share traffic.
        ["src/SharpEmu.Libs/Gpu/Pipelines/GlobalDataShareBarrier.cs"] = "Vulkan memory barrier",
        // Maps guest vertex formats onto VkFormat for Vulkan vertex input state.
        ["src/SharpEmu.Libs/Gpu/Pipelines/VertexAttributeFormats.cs"] = "Vulkan vertex input formats",
        // Submits Vulkan command buffers to a VkQueue; the neutral parts of it (the fatal
        // reporter) live in Gpu/Scheduling/GpuFatal.cs.
        ["src/SharpEmu.Libs/Gpu/Scheduling/SubmissionScheduler.cs"] = "Vulkan queue submission",
        // Defers VkImageMemoryBarrier2 records until a dynamic rendering scope ends; only
        // the Vulkan presenter implements and uses it.
        ["src/SharpEmu.Libs/Gpu/Scheduling/IRenderingState.cs"] = "Vulkan image barriers",
    };

    private static readonly string[] ForbiddenReferences =
    [
        "Silk.NET",
        "SharpEmu.ShaderCompiler.Vulkan",
    ];

    [Fact]
    public void TheSharedGpuEngineAndTheMetalBackendDoNotReferenceVulkan()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();

        foreach (var (relativeDirectory, depth) in ScannedDirectories)
        {
            var directory = Path.Combine(root, relativeDirectory.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(Directory.Exists(directory), $"The scanned folder is missing: {relativeDirectory}.");

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", depth))
            {
                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (Allowed.ContainsKey(relative))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                foreach (var reference in ForbiddenReferences)
                {
                    if (text.Contains(reference, StringComparison.Ordinal))
                    {
                        offenders.Add($"{relative} references {reference}");
                    }
                }
            }
        }

        Assert.Empty(offenders);
    }

    // An allowlist entry that no longer names a Vulkan-only file would silently widen the
    // rule, so every entry must exist and must still need the exemption.
    [Fact]
    public void EveryAllowlistedFileExistsAndStillNeedsTheExemption()
    {
        var root = FindRepositoryRoot();
        foreach (var (relative, reason) in Allowed)
        {
            var file = Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));
            Assert.True(File.Exists(file), $"The allowlisted file is missing: {relative}.");

            var text = File.ReadAllText(file);
            Assert.True(
                ForbiddenReferences.Any(reference => text.Contains(reference, StringComparison.Ordinal)),
                $"{relative} no longer references Vulkan, so its allowlist entry ({reason}) should be removed.");
        }
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "SharpEmu.slnx")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException("The repository root (the folder holding SharpEmu.slnx) was not found.");
    }
}
