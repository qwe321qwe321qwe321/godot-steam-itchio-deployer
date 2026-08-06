#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using Godot;

namespace GodotSteamItchIoDeployer;

public static class ExportPresetReader
{
    public static IReadOnlyList<string> ReadPresetNames()
    {
        var names = new List<string>();
        var config = new ConfigFile();
        if (config.Load("res://export_presets.cfg") != Error.Ok)
        {
            return names;
        }

        foreach (string section in config.GetSections())
        {
            if (!section.StartsWith("preset.", StringComparison.Ordinal) || section.EndsWith(".options", StringComparison.Ordinal))
            {
                continue;
            }

            string name = config.GetValue(section, "name", string.Empty).AsString();
            if (!string.IsNullOrWhiteSpace(name))
            {
                names.Add(name);
            }
        }

        return names;
    }
}
#endif
