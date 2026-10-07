// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

namespace SharpEmu.Libs.Gpu.Vulkan;

/// <summary>
/// Vulkan loader configuration for RenderDoc captures. Implicit layers (overlays, capture
/// tools, vendor shims) are disabled while RenderDoc's own layer is allowed through, because
/// an implicit layer between the emulator and the driver changes what a capture records.
///
/// This is a loader concern, so it lives with the Vulkan backend: a macOS process has no
/// Vulkan loader and therefore nothing to configure.
/// </summary>
internal static class VulkanLoaderEnvironment
{
    private static readonly string[] Names =
        ["VK_LOADER_DEBUG", "VK_LOADER_LAYERS_DISABLE", "VK_LOADER_LAYERS_ALLOW"];

    /// <summary>Returns the variables it set, or an empty list when the opt-in is absent.</summary>
    public static IReadOnlyList<string> Apply()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable("SHARPEMU_VK_DISABLE_IMPLICITS"),
                "1",
                StringComparison.Ordinal))
        {
            return [];
        }

        Environment.SetEnvironmentVariable("VK_LOADER_DEBUG", "error,warn,layer");
        Environment.SetEnvironmentVariable("VK_LOADER_LAYERS_DISABLE", "~implicit~");
        Environment.SetEnvironmentVariable("VK_LOADER_LAYERS_ALLOW", "*RENDERDOC*");
        return Names;
    }
}
