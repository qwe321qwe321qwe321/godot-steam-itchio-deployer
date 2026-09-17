#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using Godot;
using SteamItchIoDeployerCore;

namespace GodotSteamItchIoDeployer;

public static class DeployConfigStore
{
    public const string DefaultBuildConfigPath = "res://deploy/BuildDeployConfig.tres";
    public const string DefaultSteamConfigPath = "res://deploy/SteamDeployConfig.tres";
    public const string DefaultItchConfigPath = "res://deploy/ItchIoDeployConfig.tres";
    public const string LegacySettingsPath = "res://deploy_config.cfg";
    public const string LocalSettingsPath = "res://.deployer/local_settings.cfg";
    public const string CredentialsPath = "res://.deployer/credentials.cfg";

    public static BuildDeployConfig LoadOrCreateBuildConfig()
    {
        string selectedPath = LoadSelectedBuildConfigPath();
        BuildDeployConfig? buildConfig = ResourceLoader.Exists(selectedPath)
            ? ResourceLoader.Load<BuildDeployConfig>(selectedPath, cacheMode: ResourceLoader.CacheMode.Replace)
            : null;
        if (buildConfig is not null)
        {
            EnsureNestedConfigs(buildConfig);
            return buildConfig;
        }

        DeploySettings legacy = LoadLegacySettings();
        var steamConfig = new SteamDeployConfig();
        var itchConfig = new ItchIoDeployConfig();
        buildConfig = new BuildDeployConfig
        {
            SteamConfig = steamConfig,
            ItchIoConfig = itchConfig,
        };
        ApplySettings(legacy, buildConfig);

        SaveResource(steamConfig, DefaultSteamConfigPath);
        SaveResource(itchConfig, DefaultItchConfigPath);
        SaveResource(buildConfig, DefaultBuildConfigPath);
        SaveSelectedBuildConfigPath(DefaultBuildConfigPath);
        return buildConfig;
    }

    public static DeploySettings ToSettings(BuildDeployConfig buildConfig)
    {
        EnsureNestedConfigs(buildConfig);
        SteamDeployConfig steam = buildConfig.SteamConfig!;
        ItchIoDeployConfig itch = buildConfig.ItchIoConfig!;
        return new DeploySettings
        {
            Targets = buildConfig.Targets,
            ExportPreset = buildConfig.ExportPreset,
            BuildWithDebug = buildConfig.BuildWithDebug,
            ExtraOutputFiles = buildConfig.ExtraOutputFiles?.ToArray() ?? Array.Empty<string>(),
            SteamCmdPath = PreferProjectRelativePath(steam.SteamCmdPath),
            SteamAppId = steam.AppId,
            SteamDepotId = steam.DepotId,
            SteamBuildDescription = steam.BuildDescription,
            SteamSetLive = steam.SetLive,
            SteamBranch = steam.Branch,
            SteamIgnoreFiles = steam.IgnoreFiles,
            ButlerPath = PreferProjectRelativePath(itch.ButlerPath),
            ItchTarget = itch.Target,
            ItchChannel = itch.Channel,
            ItchUserVersion = itch.UserVersion,
            ItchIfChanged = itch.IfChanged,
            ItchIgnoreFiles = itch.IgnoreFiles,
        };
    }

    public static Error SaveSettings(DeploySettings settings, BuildDeployConfig buildConfig)
    {
        EnsureNestedConfigs(buildConfig);
        ApplySettings(settings, buildConfig);

        Error steamError = SaveResource(buildConfig.SteamConfig!, GetPlatformFallbackPath(buildConfig, "SteamDeployConfig.tres"));
        if (steamError != Error.Ok) return steamError;
        Error itchError = SaveResource(buildConfig.ItchIoConfig!, GetPlatformFallbackPath(buildConfig, "ItchIoDeployConfig.tres"));
        if (itchError != Error.Ok) return itchError;
        Error buildError = SaveResource(buildConfig, DefaultBuildConfigPath);
        if (buildError != Error.Ok) return buildError;

        SaveSelectedBuildConfigPath(buildConfig.ResourcePath);
        return Error.Ok;
    }

    public static void SaveSelectedBuildConfigPath(string path)
    {
        EnsureResourceDirectory(LocalSettingsPath);
        var localConfig = new ConfigFile();
        localConfig.Load(LocalSettingsPath);
        localConfig.SetValue("resources", "selected_build_config", path);
        localConfig.Save(LocalSettingsPath);
    }

    // Enumerates every BuildDeployConfig saved under res://deploy/ (recursively) so the dock can
    // offer a plain dropdown instead of the resource picker's quick-load flow. Non-matching
    // .tres files (Steam/itch.io sub-configs) are filtered out by loading and checking the type.
    public static List<string> ListBuildConfigPaths()
    {
        var paths = new List<string>();
        string root = Path.Combine(GetProjectRoot(), "deploy");
        if (!Directory.Exists(root)) return paths;
        string projectRoot = GetProjectRoot();
        foreach (string file in Directory.EnumerateFiles(root, "*.tres", SearchOption.AllDirectories))
        {
            string resourcePath = $"res://{Path.GetRelativePath(projectRoot, file).Replace('\\', '/')}";
            if (ResourceLoader.Exists(resourcePath) && ResourceLoader.Load(resourcePath) is BuildDeployConfig)
            {
                paths.Add(resourcePath);
            }
        }

        paths.Sort(StringComparer.OrdinalIgnoreCase);
        return paths;
    }

    // Cheap directory fingerprint (name + mtime + size of every .tres) polled every frame by the
    // dock to rebuild the config dropdown when files are added, renamed, or edited externally.
    public static string GetDeployDirectoryStamp()
    {
        try
        {
            string root = Path.Combine(GetProjectRoot(), "deploy");
            if (!Directory.Exists(root)) return "missing";
            var builder = new StringBuilder();
            foreach (string file in Directory.EnumerateFiles(root, "*.tres", SearchOption.AllDirectories)
                         .OrderBy(path => path, StringComparer.OrdinalIgnoreCase))
            {
                var info = new FileInfo(file);
                builder.Append(info.Name)
                    .Append(':')
                    .Append(info.Exists ? info.LastWriteTimeUtc.Ticks : -1)
                    .Append(':')
                    .Append(info.Exists ? info.Length : -1)
                    .Append('|');
            }

            return builder.Length == 0 ? "empty" : builder.ToString();
        }
        catch (Exception exception) when (exception is ArgumentException
                                              or NotSupportedException
                                              or PathTooLongException
                                              or IOException
                                              or UnauthorizedAccessException)
        {
            return "invalid";
        }
    }

    // Batch slots persist as an ordered list of res:// paths (empty string = unassigned slot),
    // one entry per row shown in the Batch Build & Upload tab.
    public static List<string> LoadBatchConfigPaths()
    {
        var localConfig = new ConfigFile();
        if (localConfig.Load(LocalSettingsPath) != Error.Ok) return new List<string>();
        string[] paths = localConfig.GetValue("batch", "config_paths", Array.Empty<string>()).AsStringArray();
        return new List<string>(paths);
    }

    public static void SaveBatchConfigPaths(IEnumerable<string> paths)
    {
        EnsureResourceDirectory(LocalSettingsPath);
        var localConfig = new ConfigFile();
        localConfig.Load(LocalSettingsPath);
        var pathArray = new List<string>();
        foreach (string path in paths) pathArray.Add(path);
        localConfig.SetValue("batch", "config_paths", pathArray.ToArray());
        localConfig.Save(LocalSettingsPath);
    }

    // The upload cooldown is a batch-run-wide knob (Steam can reject a depot upload submitted
    // too soon after the previous one on the same App ID), so it lives next to the batch slot
    // list in local_settings.cfg instead of on each BuildDeployConfig.
    public const int DefaultBatchUploadCooldownSeconds = 120;

    public static int LoadBatchUploadCooldownSeconds()
    {
        var localConfig = new ConfigFile();
        if (localConfig.Load(LocalSettingsPath) != Error.Ok) return DefaultBatchUploadCooldownSeconds;
        int value = localConfig.GetValue("batch", "upload_cooldown_seconds", DefaultBatchUploadCooldownSeconds).AsInt32();
        return Math.Clamp(value, 0, 3600);
    }

    public static void SaveBatchUploadCooldownSeconds(int seconds)
    {
        EnsureResourceDirectory(LocalSettingsPath);
        var localConfig = new ConfigFile();
        localConfig.Load(LocalSettingsPath);
        localConfig.SetValue("batch", "upload_cooldown_seconds", Math.Clamp(seconds, 0, 3600));
        localConfig.Save(LocalSettingsPath);
    }

    public static void EnsureNestedConfigs(BuildDeployConfig buildConfig)
    {
        buildConfig.SteamConfig ??= new SteamDeployConfig();
        buildConfig.ItchIoConfig ??= new ItchIoDeployConfig();
    }

    public static string ResolveProjectPath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return string.Empty;
        string path = configuredPath.Trim();
        if (path.StartsWith("res://", StringComparison.OrdinalIgnoreCase))
            return Path.GetFullPath(ProjectSettings.GlobalizePath(path));
        if (Path.IsPathRooted(path)) return Path.GetFullPath(path);
        return Path.GetFullPath(Path.Combine(GetProjectRoot(), path));
    }

    public static string PreferProjectRelativePath(string configuredPath)
    {
        if (string.IsNullOrWhiteSpace(configuredPath)) return string.Empty;
        try
        {
            string absolutePath = ResolveProjectPath(configuredPath);
            string relativePath = Path.GetRelativePath(GetProjectRoot(), absolutePath);
            bool outsideProject = relativePath == ".." ||
                                  relativePath.StartsWith($"..{Path.DirectorySeparatorChar}", StringComparison.Ordinal) ||
                                  Path.IsPathRooted(relativePath);
            return outsideProject ? absolutePath.Replace('\\', '/') : relativePath.Replace('\\', '/');
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return configuredPath.Trim();
        }
    }

    public static DeployCredentials LoadCredentials()
    {
        var credentials = new DeployCredentials();
        var config = new ConfigFile();
        if (config.LoadEncryptedPass(CredentialsPath, DeriveMachinePassword()) != Error.Ok) return credentials;
        credentials.SteamUsername = ReadString(config, "steam", "username", string.Empty);
        credentials.SteamPassword = ReadString(config, "steam", "password", string.Empty);
        credentials.ButlerApiKey = ReadString(config, "itch", "api_key", string.Empty);
        return credentials;
    }

    public static Error SaveCredentials(DeployCredentials credentials)
    {
        EnsureResourceDirectory(CredentialsPath);
        var config = new ConfigFile();
        config.SetValue("steam", "username", credentials.SteamUsername);
        config.SetValue("steam", "password", credentials.SteamPassword);
        config.SetValue("itch", "api_key", credentials.ButlerApiKey);
        return config.SaveEncryptedPass(CredentialsPath, DeriveMachinePassword());
    }

    private static DeploySettings LoadLegacySettings()
    {
        var settings = new DeploySettings();
        var config = new ConfigFile();
        if (config.Load(LegacySettingsPath) == Error.Ok)
        {
            settings.Targets = (DeployTargets)config.GetValue("build", "targets", (long)settings.Targets).AsInt64();
            settings.ExportPreset = ReadString(config, "build", "export_preset", settings.ExportPreset);
            settings.BuildWithDebug = config.GetValue("build", "with_debug", settings.BuildWithDebug).AsBool();
            settings.SteamCmdPath = ReadString(config, "steam", "steamcmd_path", settings.SteamCmdPath);
            settings.SteamAppId = ReadString(config, "steam", "app_id", settings.SteamAppId);
            settings.SteamDepotId = ReadString(config, "steam", "depot_id", settings.SteamDepotId);
            settings.SteamBuildDescription = ReadString(config, "steam", "build_description", settings.SteamBuildDescription);
            settings.SteamSetLive = config.GetValue("steam", "set_live", settings.SteamSetLive).AsBool();
            settings.SteamBranch = ReadString(config, "steam", "branch", settings.SteamBranch);
            settings.SteamIgnoreFiles = ReadString(config, "steam", "ignore_files", settings.SteamIgnoreFiles);
            settings.ButlerPath = ReadString(config, "itch", "butler_path", settings.ButlerPath);
            settings.ItchTarget = ReadString(config, "itch", "target", settings.ItchTarget);
            settings.ItchChannel = ReadString(config, "itch", "channel", settings.ItchChannel);
            settings.ItchUserVersion = ReadString(config, "itch", "user_version", settings.ItchUserVersion);
            settings.ItchIfChanged = config.GetValue("itch", "if_changed", settings.ItchIfChanged).AsBool();
            settings.ItchIgnoreFiles = ReadString(config, "itch", "ignore_files", settings.ItchIgnoreFiles);
        }

        var localConfig = new ConfigFile();
        if (localConfig.Load(LocalSettingsPath) == Error.Ok)
        {
            settings.SteamCmdPath = ReadString(localConfig, "tools", "steamcmd_path", settings.SteamCmdPath);
            settings.ButlerPath = ReadString(localConfig, "tools", "butler_path", settings.ButlerPath);
        }
        settings.SteamCmdPath = PreferProjectRelativePath(settings.SteamCmdPath);
        settings.ButlerPath = PreferProjectRelativePath(settings.ButlerPath);
        return settings;
    }

    private static void ApplySettings(DeploySettings settings, BuildDeployConfig buildConfig)
    {
        EnsureNestedConfigs(buildConfig);
        buildConfig.Targets = settings.Targets;
        buildConfig.ExportPreset = settings.ExportPreset;
        buildConfig.BuildWithDebug = settings.BuildWithDebug;
        // Mutate the existing Godot array in place: the Inspector may still be editing this very
        // instance, and replacing it would leave that editor bound to a detached array.
        Godot.Collections.Array<string> extraFiles = buildConfig.ExtraOutputFiles ?? new Godot.Collections.Array<string>();
        extraFiles.Clear();
        foreach (string extraFile in settings.ExtraOutputFiles)
        {
            extraFiles.Add(extraFile);
        }
        buildConfig.ExtraOutputFiles = extraFiles;
        SteamDeployConfig steam = buildConfig.SteamConfig!;
        steam.SteamCmdPath = PreferProjectRelativePath(settings.SteamCmdPath);
        steam.AppId = settings.SteamAppId;
        steam.DepotId = settings.SteamDepotId;
        steam.BuildDescription = settings.SteamBuildDescription;
        steam.SetLive = settings.SteamSetLive;
        steam.Branch = settings.SteamBranch;
        steam.IgnoreFiles = settings.SteamIgnoreFiles;
        ItchIoDeployConfig itch = buildConfig.ItchIoConfig!;
        itch.ButlerPath = PreferProjectRelativePath(settings.ButlerPath);
        itch.Target = settings.ItchTarget;
        itch.Channel = settings.ItchChannel;
        itch.UserVersion = settings.ItchUserVersion;
        itch.IfChanged = settings.ItchIfChanged;
        itch.IgnoreFiles = settings.ItchIgnoreFiles;
    }

    private static Error SaveResource(Resource resource, string fallbackPath)
    {
        string path = string.IsNullOrWhiteSpace(resource.ResourcePath) || resource.ResourcePath.Contains("::", StringComparison.Ordinal)
            ? fallbackPath
            : resource.ResourcePath;
        EnsureResourceDirectory(path);
        resource.TakeOverPath(path);
        return ResourceSaver.Save(resource, path);
    }

    private static string GetPlatformFallbackPath(BuildDeployConfig buildConfig, string defaultFileName)
    {
        if (string.IsNullOrWhiteSpace(buildConfig.ResourcePath))
        {
            return $"res://deploy/{defaultFileName}";
        }

        string directory = buildConfig.ResourcePath.GetBaseDir();
        string buildName = buildConfig.ResourcePath.GetFile().GetBaseName();
        string platform = defaultFileName.GetBaseName().Replace("DeployConfig", string.Empty, StringComparison.Ordinal);
        return $"{directory}/{buildName}.{platform}.tres";
    }

    private static string LoadSelectedBuildConfigPath()
    {
        var localConfig = new ConfigFile();
        if (localConfig.Load(LocalSettingsPath) != Error.Ok) return DefaultBuildConfigPath;
        string path = ReadString(localConfig, "resources", "selected_build_config", DefaultBuildConfigPath);
        return string.IsNullOrWhiteSpace(path) ? DefaultBuildConfigPath : path;
    }

    private static void EnsureResourceDirectory(string resourcePath)
    {
        string? directory = Path.GetDirectoryName(ProjectSettings.GlobalizePath(resourcePath));
        if (!string.IsNullOrWhiteSpace(directory)) Directory.CreateDirectory(directory);
    }

    private static string ReadString(ConfigFile config, string section, string key, string fallback) =>
        config.GetValue(section, key, fallback).AsString();

    private static string DeriveMachinePassword()
    {
        string material = $"{OS.GetUniqueId()}|{ProjectSettings.GlobalizePath("res://")}|GodotSteamItchIoDeployer_v1";
        return MachineKeyDerivation.Sha256Hex(material);
    }

    private static string GetProjectRoot() =>
        Path.TrimEndingDirectorySeparator(Path.GetFullPath(ProjectSettings.GlobalizePath("res://")));
}
#endif
