#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using Godot;

namespace GodotSteamItchIoDeployer;

public static class ExportPresetReader
{
    private static string _cachedStamp = string.Empty;
    private static List<string> _cachedNames = new();
    private static Dictionary<string, string> _cachedExportPaths = new(StringComparer.Ordinal);
    private static Dictionary<string, string[]> _cachedFeatures = new(StringComparer.Ordinal);

    public static IReadOnlyList<string> ReadPresetNames()
    {
        EnsureCacheLoaded();
        return _cachedNames;
    }

    // Returns the preset's export_path from export_presets.cfg: null when the preset name is
    // unknown, an empty string when the preset exists but has no export path configured.
    public static string? ReadExportPath(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
        {
            return null;
        }

        EnsureCacheLoaded();
        return _cachedExportPaths.TryGetValue(presetName.Trim(), out string? exportPath) ? exportPath : null;
    }

    public static IReadOnlyList<string> ReadPresetFeatures(string presetName)
    {
        if (string.IsNullOrWhiteSpace(presetName))
        {
            return Array.Empty<string>();
        }

        EnsureCacheLoaded();
        return _cachedFeatures.TryGetValue(presetName.Trim(), out string[]? features)
            ? features
            : Array.Empty<string>();
    }

    // The batch tab polls output-directory readiness every frame, and the dock re-reads this on
    // every settings pass — cache the parsed file behind an mtime+size stamp like the preset
    // dropdown refresh so polling never re-parses an unchanged file.
    private static void EnsureCacheLoaded()
    {
        string stamp = GetFileStamp();
        if (stamp == _cachedStamp)
        {
            return;
        }

        var names = new List<string>();
        var exportPaths = new Dictionary<string, string>(StringComparer.Ordinal);
        var features = new Dictionary<string, string[]>(StringComparer.Ordinal);
        var config = new ConfigFile();
        if (config.Load("res://export_presets.cfg") == Error.Ok)
        {
            foreach (string section in config.GetSections())
            {
                if (!section.StartsWith("preset.", StringComparison.Ordinal) || section.EndsWith(".options", StringComparison.Ordinal))
                {
                    continue;
                }

                string name = config.GetValue(section, "name", string.Empty).AsString();
                if (string.IsNullOrWhiteSpace(name))
                {
                    continue;
                }

                names.Add(name);
                exportPaths[name] = config.GetValue(section, "export_path", string.Empty).AsString();
                features[name] = config.GetValue(section, "custom_features", string.Empty).AsString()
                    .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            }
        }

        _cachedNames = names;
        _cachedExportPaths = exportPaths;
        _cachedFeatures = features;
        _cachedStamp = stamp;
    }

    private static string GetFileStamp()
    {
        try
        {
            string path = ProjectSettings.GlobalizePath("res://export_presets.cfg");
            var info = new FileInfo(path);
            return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "missing";
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "invalid";
        }
    }
}
#endif
