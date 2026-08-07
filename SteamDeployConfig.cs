#if TOOLS
#nullable enable
using Godot;

namespace GodotSteamItchIoDeployer;

[Tool, GlobalClass]
public partial class SteamDeployConfig : Resource
{
    [Export]
    public string SteamCmdPath { get; set; } = string.Empty;

    [Export]
    public string AppId { get; set; } = string.Empty;

    [Export]
    public string DepotId { get; set; } = string.Empty;

    [Export]
    public string BuildDescription { get; set; } = "Godot build {DateTime} - {GitSHA}";

    [Export]
    public bool SetLive { get; set; }

    [Export]
    public string Branch { get; set; } = "default";

    [Export]
    public string IgnoreFiles { get; set; } = "*.pdb";
}
#endif
