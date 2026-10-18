// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Reflection;

namespace SharpEmu.Application;

/// <summary>
/// Every GitHub URL the GUI uses, derived from one build-time repository
/// identity.
/// </summary>
/// <remarks>
/// The slug comes from the <c>SharpEmuRepositorySlug</c> MSBuild property,
/// baked in as <c>[AssemblyMetadata("SharpEmu.RepositorySlug")]</c>. It is not
/// a compile-time constant on purpose: a fork must never point its "check for
/// updates", "latest commit" or "view source" links at upstream. Doing so
/// would offer upstream release archives as an in-place update for a build
/// that is not upstream, replacing this fork's binaries with ones that lack
/// its changes.
/// </remarks>
public static class ProjectLinks
{
    private const string MetadataKey = "SharpEmu.RepositorySlug";

    /// <summary>
    /// Used only if the metadata attribute is missing, which means the build
    /// did not go through Directory.Build.props. Defaulting to the fork rather
    /// than upstream keeps the failure mode safe.
    /// </summary>
    private const string FallbackRepositorySlug = "sharpemu/sharpemu-mac";

    private static readonly string RepositorySlug = ResolveRepositorySlug();

    public static string RepositoryUrl => $"https://github.com/{RepositorySlug}";

    public static string ReleasesUrl => $"{RepositoryUrl}/releases/latest";

    public static string LatestCommitApiUrl =>
        $"https://api.github.com/repos/{RepositorySlug}/commits/main";

    public static string LatestReleaseApiUrl =>
        $"https://api.github.com/repos/{RepositorySlug}/releases/latest";

    public static string CompareCommitsApiUrl(string fromSha, string toSha) =>
        $"https://api.github.com/repos/{RepositorySlug}/compare/{fromSha}...{toSha}";

    public static string CommitUrl(string sha) => $"{RepositoryUrl}/commit/{sha}";

    public static string DocumentUrl(string path) =>
        $"{RepositoryUrl}/blob/main/{path.TrimStart('/')}";

    private static string ResolveRepositorySlug()
    {
        foreach (var attribute in typeof(ProjectLinks).Assembly
            .GetCustomAttributes<AssemblyMetadataAttribute>())
        {
            if (attribute.Key == MetadataKey && !string.IsNullOrWhiteSpace(attribute.Value))
            {
                return attribute.Value.Trim();
            }
        }

        return FallbackRepositorySlug;
    }
}
