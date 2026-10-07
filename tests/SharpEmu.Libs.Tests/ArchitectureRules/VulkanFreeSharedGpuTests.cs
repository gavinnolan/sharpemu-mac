// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Xml.Linq;
using Xunit;

namespace SharpEmu.Libs.Tests.ArchitectureRules;

/// <summary>
/// The macOS build is Metal/AppKit/GameController/CoreAudio only and carries no Vulkan, MoltenVK
/// or SDL code at all (ADR 0002). Now that the backends live in their own assemblies, that is
/// primarily a <em>reference</em> rule, so this fixture checks the project graph: it walks the
/// transitive ProjectReference/PackageReference closure of every project a macOS artifact
/// contains and fails on any edge into the Vulkan or SDL packages. A publish-closure guard in
/// build/platforms/macos/macos.targets enforces the same thing on the shipped artifact; this
/// test fails first, on a developer machine, with the offending edge named.
///
/// The source scan is kept alongside it because a reference is not the only way a dependency
/// creeps back: a <c>using Silk.NET.Vulkan</c> added to a shared file would be caught here
/// before someone "fixes" it by adding the package reference the project test forbids.
/// </summary>
public sealed class VulkanFreeSharedGpuTests
{
    // Every project in a macOS artifact's closure that must stay free of Vulkan and SDL. The
    // Metal backend and the macOS platform package are in the list for the same reason as the
    // shared engine: they are what replaced those dependencies.
    private static readonly string[] MacOsClosureProjects =
    [
        "src/SharpEmu.Libs/SharpEmu.Libs.csproj",
        "src/SharpEmu.HLE/SharpEmu.HLE.csproj",
        "src/SharpEmu.Core/SharpEmu.Core.csproj",
        "src/graphics/SharpEmu.Gpu.Metal/SharpEmu.Gpu.Metal.csproj",
        "src/platforms/macos/SharpEmu.Platform.MacOS/SharpEmu.Platform.MacOS.csproj",
        "src/platforms/macos/SharpEmu.Host.MacOS/SharpEmu.Host.MacOS.csproj",
    ];

    // Dependencies that must not appear anywhere in those closures.
    private static readonly string[] ForbiddenDependencies =
    [
        "Silk.NET.Vulkan",
        "Silk.NET.Vulkan.Extensions.EXT",
        "Silk.NET.Vulkan.Extensions.KHR",
        "Silk.NET.Core",
        "ppy.SDL3-CS",
        "SharpEmu.ShaderCompiler.Vulkan",
        "SharpEmu.Gpu.Vulkan",
        "SharpEmu.Adapters.Sdl",
    ];

    // Edges that are still there, each with the reason and who removes it. Keep this list
    // short: an entry is a dependency the macOS artifact still carries. Empty since the host
    // split moved SdlHostAudio into SharpEmu.Adapters.Sdl and dropped ppy.SDL3-CS from
    // SharpEmu.HLE -- the matching exemption in build/platforms/macos/macos.targets went with
    // it. Keep the mechanism (and the unused-entry assertion below), not a standing exception.
    private static readonly Dictionary<string, string> AllowedEdges = new(StringComparer.Ordinal);

    // Directories whose sources must not name a Vulkan or SDL API.
    private static readonly string[] ScannedProjectDirectories =
    [
        "src/SharpEmu.Libs",
        "src/SharpEmu.HLE",
        "src/SharpEmu.Core",
        "src/graphics/SharpEmu.Gpu.Metal",
        "src/platforms/macos/SharpEmu.Platform.MacOS",
        "src/platforms/macos/SharpEmu.Host.MacOS",
    ];

    // No exceptions: HLE/Host/Sdl is gone (the host split moved SdlHostAudio into
    // SharpEmu.Adapters.Sdl, which is referenced only for win-*/linux-* RIDs).
    private static readonly string[] AllowedSourcePrefixes = [];

    private static readonly string[] ForbiddenSourceReferences =
    [
        "Silk.NET",
        "SharpEmu.ShaderCompiler.Vulkan",
        "using SDL;",
        "SDL.SDL3",
    ];

    [Fact]
    public void NoProjectInTheMacOsClosureReferencesVulkanOrSdl()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();

        foreach (var entry in MacOsClosureProjects)
        {
            var project = Resolve(root, entry);
            Assert.True(File.Exists(project), $"The project is missing: {entry}.");

            foreach (var edge in ForbiddenEdges(project))
            {
                if (!AllowedEdges.ContainsKey(edge))
                {
                    offenders.Add($"{entry}: {edge}");
                }
            }
        }

        Assert.Empty(offenders);
    }

    // An allowlisted edge that no longer exists would silently widen the rule, so every entry
    // must still be a real dependency of the project it names.
    [Fact]
    public void EveryAllowedEdgeStillExists()
    {
        var root = FindRepositoryRoot();
        var present = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in MacOsClosureProjects)
        {
            foreach (var edge in ForbiddenEdges(Resolve(root, entry)))
            {
                present.Add(edge);
            }
        }

        foreach (var (edge, reason) in AllowedEdges)
        {
            Assert.True(
                present.Contains(edge),
                $"The allowed edge '{edge}' ({reason}) is gone, so its entry should be removed " +
                "here and in build/platforms/macos/macos.targets.");
        }
    }

    [Fact]
    public void NoSourceInTheMacOsClosureNamesVulkanOrSdl()
    {
        var root = FindRepositoryRoot();
        var offenders = new List<string>();

        foreach (var relativeDirectory in ScannedProjectDirectories)
        {
            var directory = Resolve(root, relativeDirectory);
            Assert.True(Directory.Exists(directory), $"The scanned folder is missing: {relativeDirectory}.");

            foreach (var file in Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories))
            {
                var relative = Path.GetRelativePath(root, file).Replace(Path.DirectorySeparatorChar, '/');
                if (AllowedSourcePrefixes.Any(prefix => relative.StartsWith(prefix, StringComparison.Ordinal)))
                {
                    continue;
                }

                var text = File.ReadAllText(file);
                foreach (var reference in ForbiddenSourceReferences)
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

    // Walks the project's transitive ProjectReference closure and returns every
    // "<owner> -> <dependency>" edge that lands on a forbidden package or project. Edges rather
    // than endpoints, so an exception names the project that actually pulls the dependency in
    // instead of exempting every project that transitively reaches it.
    private static IEnumerable<string> ForbiddenEdges(string projectPath)
    {
        var edges = new SortedSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var queue = new Queue<string>();
        queue.Enqueue(Path.GetFullPath(projectPath));

        while (queue.Count > 0)
        {
            var current = queue.Dequeue();
            if (!visited.Add(current) || !File.Exists(current))
            {
                continue;
            }

            var owner = Path.GetFileNameWithoutExtension(current);
            var document = XDocument.Load(current);

            foreach (var package in Attributes(document, "PackageReference"))
            {
                if (ForbiddenDependencies.Contains(package, StringComparer.Ordinal))
                {
                    edges.Add($"{owner} -> {package}");
                }
            }

            foreach (var reference in Attributes(document, "ProjectReference"))
            {
                var referenced = Path.GetFullPath(Path.Combine(
                    Path.GetDirectoryName(current)!,
                    reference.Replace('\\', Path.DirectorySeparatorChar)));
                var name = Path.GetFileNameWithoutExtension(referenced);
                if (ForbiddenDependencies.Contains(name, StringComparer.Ordinal))
                {
                    edges.Add($"{owner} -> {name}");
                    continue;
                }

                queue.Enqueue(referenced);
            }
        }

        return edges;
    }

    // Element names in these csproj files carry no namespace, and an analyzer-only reference
    // (ReferenceOutputAssembly=false) is still a build input worth following for its own refs.
    private static IEnumerable<string> Attributes(XDocument document, string elementName) =>
        document.Descendants(elementName)
            .Select(element => (string?)element.Attribute("Include"))
            .Where(value => !string.IsNullOrEmpty(value))
            .Select(value => value!);

    private static string Resolve(string root, string relative) =>
        Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar));

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
