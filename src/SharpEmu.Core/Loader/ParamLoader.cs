// Copyright (C) 2026 SharpEmu Emulator Project
// SPDX-License-Identifier: GPL-2.0-or-later

using System.Text.Json;
using SharpEmu.Core;

namespace SharpEmu.Core.Loader;

public sealed record ParamLoader(
    string? TitleId,
    string? ContentId,
    string? ContentVersion,
    string? MasterVersion,
    string? TargetContentVersion,
    LocalizedParameters? LocalizedParameters,
    Disc? Disc
);

public sealed record LocalizedParameters(
    string? DefaultLanguage,
    Dictionary<string, LocalizedLanguage>? Languages
);

public sealed record LocalizedLanguage(string? TitleName);

public sealed record Disc(LocalizedParameters? LocalizedParameters);

public static class Ps5ParamJsonReader
{
    public static (string? Title, string? TitleId, string? Version) TryReadPs5Param(IFileSystem fs, string paramJsonPath)
    {
        if (!fs.Exists(paramJsonPath))
            return (null, null, null);

        if (!fs.TryReadAllBytes(paramJsonPath, out var data))
            return (null, null, null);

        return TryReadPs5Param(data);
    }

    public static (string? Title, string? TitleId, string? Version) TryReadPs5Param(byte[] data)
    {
        if (data == null || data.Length == 0)
            return (null, null, null);

        try
        {
            ReadOnlyMemory<byte> json = data;
            if (json.Span.StartsWith("\uFEFF"u8))
            {
                json = json[3..];
            }

            using var doc = JsonDocument.Parse(json);
            return TryReadPs5Param(doc.RootElement);
        }
        catch (JsonException)
        {
            return (null, null, null);
        }
    }

    private static (string? Title, string? TitleId, string? Version) TryReadPs5Param(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return (null, null, null);

        var titleId = GetString(root, "titleId");

        string? ver =
            GetString(root, "contentVersion")
            ?? GetString(root, "masterVersion")
            ?? GetString(root, "targetContentVersion");

        string? title = ExtractTitleName(root);

        return (title, titleId, ver);
    }

    private static string? ExtractTitleName(JsonElement root)
    {
        if ((!root.TryGetProperty("localizedParameters", out var lp) || lp.ValueKind != JsonValueKind.Object) &&
            root.TryGetProperty("disc", out var disc) && disc.ValueKind == JsonValueKind.Object)
        {
            disc.TryGetProperty("localizedParameters", out lp);
        }

        if (lp.ValueKind != JsonValueKind.Object)
            return null;

        var defLang = GetString(lp, "defaultLanguage");

        if (!string.IsNullOrEmpty(defLang))
        {
            if (lp.TryGetProperty(defLang, out var langObj) && langObj.ValueKind == JsonValueKind.Object)
            {
                var title = GetString(langObj, "titleName");
                if (!string.IsNullOrWhiteSpace(title))
                    return title;
            }
        }

        if (lp.TryGetProperty("en-US", out var en) && en.ValueKind == JsonValueKind.Object)
        {
            var title = GetString(en, "titleName");
            if (!string.IsNullOrWhiteSpace(title))
                return title;
        }

        foreach (var property in lp.EnumerateObject())
        {
            if (property.Value.ValueKind == JsonValueKind.Object)
            {
                var title = GetString(property.Value, "titleName");
                if (!string.IsNullOrWhiteSpace(title))
                    return title;
            }
        }

        return null;
    }

    // kernel.flexibleMemorySize: the title's flexible memory budget in bytes.
    public static ulong? TryReadFlexibleMemorySize(IFileSystem fs, string paramJsonPath) =>
        fs.Exists(paramJsonPath) && fs.TryReadAllBytes(paramJsonPath, out var data)
            ? TryReadFlexibleMemorySize(data)
            : null;

    public static ulong? TryReadFlexibleMemorySize(byte[] data)
    {
        if (data == null || data.Length == 0)
            return null;
        try
        {
            ReadOnlyMemory<byte> json = data;
            if (json.Span.StartsWith("﻿"u8))
                json = json[3..];
            using var doc = JsonDocument.Parse(json);
            if (doc.RootElement.ValueKind != JsonValueKind.Object ||
                !doc.RootElement.TryGetProperty("kernel", out var kernel) ||
                kernel.ValueKind != JsonValueKind.Object ||
                !kernel.TryGetProperty("flexibleMemorySize", out var size))
                return null;
            if (size.ValueKind == JsonValueKind.Number)
                return size.TryGetUInt64(out var number) ? number : null;
            if (size.ValueKind != JsonValueKind.String || size.GetString() is not { } text)
                return null;
            text = text.Trim();
            var hex = text.StartsWith("0x", StringComparison.OrdinalIgnoreCase);
            return ulong.TryParse(hex ? text[2..] : text,
                hex ? System.Globalization.NumberStyles.HexNumber : System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static string? GetString(JsonElement parent, string propertyName) =>
        parent.TryGetProperty(propertyName, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString()
            : null;
}
