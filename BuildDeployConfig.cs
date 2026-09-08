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

    [Export]
    public string ExportPreset { get; set; } = string.Empty;

    [Export]
    public string ExportOutputPath { get; set; } = "build/windows/game.exe";

    [Export]
    public bool BuildWithDebug { get; set; }

    // Steam can reject a depot upload submitted too soon after the previous one; batch runs wait
    // this long after an upload before starting the next config's upload.
    [Export]
    public int UploadCooldownSeconds { get; set; } = 120;

    [Export]
    public SteamDeployConfig? SteamConfig { get; set; }

    [Export]
    public ItchIoDeployConfig? ItchIoConfig { get; set; }
}
#endif
