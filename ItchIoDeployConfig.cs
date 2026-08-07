#if TOOLS
#nullable enable
using Godot;

namespace GodotSteamItchIoDeployer;

[Tool, GlobalClass]
public partial class ItchIoDeployConfig : Resource
{
    [Export]
    public string ButlerPath { get; set; } = string.Empty;

    [Export]
    public string Target { get; set; } = string.Empty;

    [Export]
    public string Channel { get; set; } = "windows";

    [Export]
    public string UserVersion { get; set; } = string.Empty;

    [Export]
    public bool IfChanged { get; set; } = true;

    [Export]
    public string IgnoreFiles { get; set; } = "*.pdb";
}
#endif
