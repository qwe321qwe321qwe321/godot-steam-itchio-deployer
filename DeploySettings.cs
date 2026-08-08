#if TOOLS
#nullable enable
using SteamItchIoDeployerCore;

namespace GodotSteamItchIoDeployer;

public sealed class DeploySettings
{
    public DeployTargets Targets { get; set; } = DeployTargets.Steam;
    public string ExportPreset { get; set; } = string.Empty;
    public string ExportOutputPath { get; set; } = "build/windows/game.exe";
    public bool BuildWithDebug { get; set; }

    public string SteamCmdPath { get; set; } = string.Empty;
    public string SteamAppId { get; set; } = string.Empty;
    public string SteamDepotId { get; set; } = string.Empty;
    public string SteamBuildDescription { get; set; } = "Godot build {DateTime} - {GitSHA}";
    public bool SteamSetLive { get; set; }
    public string SteamBranch { get; set; } = "default";
    public string SteamIgnoreFiles { get; set; } = "*.pdb";

    public string ButlerPath { get; set; } = string.Empty;
    public string ItchTarget { get; set; } = string.Empty;
    public string ItchChannel { get; set; } = "windows";
    public string ItchUserVersion { get; set; } = string.Empty;
    public bool ItchIfChanged { get; set; } = true;
    public string ItchIgnoreFiles { get; set; } = "*.pdb";
}

public sealed class DeployCredentials
{
    public string SteamUsername { get; set; } = string.Empty;
    public string SteamPassword { get; set; } = string.Empty;
    public string SteamGuardCode { get; set; } = string.Empty;
    public string ButlerApiKey { get; set; } = string.Empty;
}
#endif
