// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later
using System.Text.Json;
namespace SharpEmu.Application;

/// <summary>Owned metadata only; image files are loaded by the selected frontend.</summary>
public sealed record CatalogGame(string Name, string? TitleId, string? Version, string Path,
    long SizeBytes, string? CoverPath, string? BackgroundPath);

public static class GameCatalog
{
    public static List<CatalogGame> ScanFolders(IReadOnlyList<string> folders, IReadOnlySet<string> excludedPaths)
    {
        var games = new List<CatalogGame>();
        var seen = new HashSet<string>(GameLibraryPath.Comparer);
        var enumeration = new EnumerationOptions
        {
            IgnoreInaccessible = true,
            RecurseSubdirectories = true,
            MaxRecursionDepth = 8,
        };

        foreach (var folder in folders)
        {
            if (!Directory.Exists(folder))
            {
                continue;
            }

            try
            {
                foreach (var file in Directory.EnumerateFiles(folder, "eboot.bin", enumeration))
                {
                    var fullPath = Path.GetFullPath(file);
                    if (!seen.Add(fullPath) || excludedPaths.Contains(fullPath))
                    {
                        continue;
                    }

                    long size = 0;
                    try
                    {
                        size = new FileInfo(fullPath).Length;
                    }
                    catch (IOException exception)
                    {
                        Console.Error.WriteLine(
                            $"[GUI][WARN] Could not inspect executable '{fullPath}': {exception.Message}");
                    }

                    var (title, titleId, version) = TryReadParamJson(fullPath);
                    games.Add(new CatalogGame(
                        title ?? GameNameFor(fullPath), titleId, version, fullPath, size,
                        FindCoverFor(fullPath), FindBackgroundFor(fullPath)));
                }
            }
            catch (Exception exception)
            {
                Console.Error.WriteLine(
                    $"[GUI][WARN] Could not scan game folder '{folder}': {exception.Message}");
            }
        }

        games.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase));
        return games;
    }

    /// <summary>
    /// Reads the game title, title id and content version from
    /// sce_sys/param.json next to the executable, when present.
    /// </summary>
    public static (string? Title, string? TitleId, string? Version) TryReadParamJson(string ebootPath)
    {
        try
        {
            var directory = Path.GetDirectoryName(ebootPath);
            if (directory is null)
            {
                return (null, null, null);
            }

            var paramPath = Path.Combine(directory, "sce_sys", "param.json");
            if (!File.Exists(paramPath))
            {
                return (null, null, null);
            }

            // ReadAllText handles a UTF-8 BOM, which JsonDocument rejects in
            // raw bytes.
            using var document = JsonDocument.Parse(File.ReadAllText(paramPath));
            var root = document.RootElement;

            string? titleId = null;
            if (root.TryGetProperty("titleId", out var idElement) && idElement.ValueKind == JsonValueKind.String)
            {
                titleId = idElement.GetString();
            }

            // contentVersion carries the installed app version
            // ("01.000.000"); masterVersion is the fallback on older dumps.
            string? version = null;
            if (root.TryGetProperty("contentVersion", out var versionElement) &&
                versionElement.ValueKind == JsonValueKind.String)
            {
                version = versionElement.GetString();
            }
            else if (root.TryGetProperty("masterVersion", out var masterElement) &&
                     masterElement.ValueKind == JsonValueKind.String)
            {
                version = masterElement.GetString();
            }

            string? title = null;
            if (root.TryGetProperty("localizedParameters", out var localized) &&
                localized.ValueKind == JsonValueKind.Object)
            {
                if (localized.TryGetProperty("defaultLanguage", out var language) &&
                    language.ValueKind == JsonValueKind.String &&
                    localized.TryGetProperty(language.GetString()!, out var defaultBlock) &&
                    defaultBlock.ValueKind == JsonValueKind.Object &&
                    defaultBlock.TryGetProperty("titleName", out var titleName) &&
                    titleName.ValueKind == JsonValueKind.String)
                {
                    title = titleName.GetString();
                }
                else
                {
                    foreach (var property in localized.EnumerateObject())
                    {
                        if (property.Value.ValueKind == JsonValueKind.Object &&
                            property.Value.TryGetProperty("titleName", out var anyTitleName) &&
                            anyTitleName.ValueKind == JsonValueKind.String)
                        {
                            title = anyTitleName.GetString();
                            break;
                        }
                    }
                }
            }

            return (
                string.IsNullOrWhiteSpace(title) ? null : title,
                string.IsNullOrWhiteSpace(titleId) ? null : titleId,
                string.IsNullOrWhiteSpace(version) ? null : version.Trim());
        }
        catch (Exception)
        {
            return (null, null, null);
        }
    }

    /// <summary>
    /// Finds the cover art shipped with the game: sce_sys/icon0.png next to
    /// the executable (falling back to pic0.png).
    /// </summary>
    public static string? FindCoverFor(string ebootPath)
    {
        var directory = Path.GetDirectoryName(ebootPath);
        if (directory is null)
        {
            return null;
        }

        var sceSys = Path.Combine(directory, "sce_sys");
        foreach (var candidate in new[] { "icon0.png", "pic0.png" })
        {
            var coverPath = Path.Combine(sceSys, candidate);
            if (File.Exists(coverPath))
            {
                return coverPath;
            }
        }

        return null;
    }

    /// <summary>
    /// Finds the key art shipped with the game (sce_sys/pic0.png, falling
    /// back to pic1.png), used as the window backdrop when selected.
    /// </summary>
    public static string? FindBackgroundFor(string ebootPath)
    {
        var directory = Path.GetDirectoryName(ebootPath);
        if (directory is null)
        {
            return null;
        }

        var sceSys = Path.Combine(directory, "sce_sys");
        foreach (var candidate in new[] { "pic0.png", "pic1.png" })
        {
            var backgroundPath = Path.Combine(sceSys, candidate);
            if (File.Exists(backgroundPath))
            {
                return backgroundPath;
            }
        }

        return null;
    }

    public static string GameNameFor(string ebootPath)
    {
        var directory = Path.GetDirectoryName(ebootPath);
        var name = directory is not null ? Path.GetFileName(directory) : null;
        return string.IsNullOrEmpty(name) ? Path.GetFileName(ebootPath) : name;
    }

}
