#if TOOLS
#nullable enable
using Godot;
using SteamItchIoDeployerCore;

namespace GodotSteamItchIoDeployer;

[Tool, GlobalClass]
public partial class BuildDeployConfig : Resource
{
    [Export(PropertyHint.Flags, "Steam,itch.io")]
    public DeployTargets Targets { get; set; } = DeployTargets.Steam;

    // The export output location is owned by the selected preset's export_path in
    // export_presets.cfg (read via ExportPresetReader), so it is not duplicated here.
    [Export]
    public string ExportPreset { get; set; } = string.Empty;

    [Export]
    public bool BuildWithDebug { get; set; }

    // Files copied into the export output directory after a successful export.
    [Export]
    public Godot.Collections.Array<string> ExtraOutputFiles { get; set; } = new();

    [Export]
    public SteamDeployConfig? SteamConfig { get; set; }

    [Export]
    public ItchIoDeployConfig? ItchIoConfig { get; set; }

    // [Export(PropertyHint.TypeString, "4/13:")] does not survive on a typed C# array — Godot
    // regenerates the element hint from the element type and hands the Inspector "4/0:", i.e.
    // plain text fields. Rewriting the hint here is what actually gives each entry a res:// file
    // picker: element type String (4) with PropertyHint.File (13).
    public override void _ValidateProperty(Godot.Collections.Dictionary property)
    {
        if (property["name"].AsString() == PropertyName.ExtraOutputFiles)
        {
            property["hint"] = (int)PropertyHint.TypeString;
            property["hint_string"] = $"{(int)Variant.Type.String:D}/{(int)PropertyHint.File:D}:";
        }
    }
}
#endif
