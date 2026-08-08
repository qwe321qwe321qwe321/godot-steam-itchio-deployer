#if TOOLS
#nullable enable
using System;
using System.IO;
using System.Text;
using Godot;
using SteamItchIoDeployerCore;

namespace GodotSteamItchIoDeployer;

/// <summary>
/// Writes SteamCMD's app_build/depot_build VDF script files to disk. The VDF text itself is
/// rendered by <see cref="VdfContentBuilder"/> in the shared core; this type only owns the
/// Godot-specific file layout (res://.deployer/steam-vdf) and macro/git-SHA plumbing.
/// </summary>
public static class VdfGenerator
{
    public static string Generate(DeploySettings settings, string contentRoot)
    {
        string outputDirectory = ProjectSettings.GlobalizePath("res://.deployer/steam-vdf");
        Directory.CreateDirectory(outputDirectory);

        var options = new SteamVdfOptions
        {
            AppId = settings.SteamAppId,
            DepotId = settings.SteamDepotId,
            SetLiveEnabled = settings.SteamSetLive,
            Branch = settings.SteamBranch,
            IgnoreFiles = settings.SteamIgnoreFiles,
        };

        string depotPath = Path.Combine(outputDirectory, $"depot_build_{settings.SteamDepotId}.vdf");
        string appPath = Path.Combine(outputDirectory, $"app_build_{settings.SteamAppId}.vdf");

        File.WriteAllText(depotPath, VdfContentBuilder.BuildDepotVdfContent(options), new UTF8Encoding(false));

        string description = ResolveMacros(settings.SteamBuildDescription);
        File.WriteAllText(
            appPath,
            VdfContentBuilder.BuildAppVdfContent(
                options,
                Path.GetFullPath(contentRoot),
                Path.GetFullPath(outputDirectory),
                description,
                Path.GetFullPath(depotPath)),
            new UTF8Encoding(false));

        return appPath;
    }

    public static string ResolveMacros(string value)
    {
        DateTime now = DateTime.Now;
        return MacroResolver.Resolve(
            value,
            version: ResolveProjectVersion(),
            dateText: now.ToString("yyyy-MM-dd"),
            dateTimeText: now.ToString("yyyy-MM-dd HH:mm:ss"),
            gitSha: ResolveGitSha());
    }

    /// <summary>
    /// Reads the project's "application/config/version" ProjectSetting (Project Settings >
    /// Application > Config > Version) for the shared <c>{Version}</c> macro. This macro was
    /// previously unsupported here (only Unity's ResolveMacros substituted it); sharing
    /// MacroResolver with Unity added it for free.
    /// </summary>
    private static string ResolveProjectVersion()
    {
        Variant version = ProjectSettings.GetSetting("application/config/version", "");
        return version.AsString();
    }

    public static string ResolveGitSha() => GitShaResolver.Resolve(ProjectSettings.GlobalizePath("res://"));

    public static string[] SplitPatterns(string value) => VdfContentBuilder.SplitIgnorePatterns(value);
}
#endif
