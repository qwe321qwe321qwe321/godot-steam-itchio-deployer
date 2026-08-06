#if TOOLS
#nullable enable
using System;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using Godot;

namespace GodotSteamItchIoDeployer;

public static class DeployConfigStore
{
    public const string SettingsPath = "res://deploy_config.cfg";
    public const string LocalSettingsPath = "res://.deployer/local_settings.cfg";
    public const string CredentialsPath = "res://.deployer/credentials.cfg";

    public static DeploySettings LoadSettings()
    {
        var settings = new DeploySettings();
        var config = new ConfigFile();
        if (config.Load(SettingsPath) != Error.Ok)
        {
            return settings;
        }

        settings.Targets = (DeployTargets)config.GetValue("build", "targets", (long)settings.Targets).AsInt64();
        settings.ExportPreset = ReadString(config, "build", "export_preset", settings.ExportPreset);
        settings.ExportOutputPath = ReadString(config, "build", "export_output_path", settings.ExportOutputPath);

        string legacySteamCmdPath = ReadString(config, "steam", "steamcmd_path", string.Empty);
        settings.SteamAppId = ReadString(config, "steam", "app_id", settings.SteamAppId);
        settings.SteamDepotId = ReadString(config, "steam", "depot_id", settings.SteamDepotId);
        settings.SteamBuildDescription = ReadString(config, "steam", "build_description", settings.SteamBuildDescription);
        settings.SteamSetLive = config.GetValue("steam", "set_live", settings.SteamSetLive).AsBool();
        settings.SteamBranch = ReadString(config, "steam", "branch", settings.SteamBranch);
        settings.SteamIgnoreFiles = ReadString(config, "steam", "ignore_files", settings.SteamIgnoreFiles);

        string legacyButlerPath = ReadString(config, "itch", "butler_path", string.Empty);
        settings.ItchTarget = ReadString(config, "itch", "target", settings.ItchTarget);
        settings.ItchChannel = ReadString(config, "itch", "channel", settings.ItchChannel);
        settings.ItchUserVersion = ReadString(config, "itch", "user_version", settings.ItchUserVersion);
        settings.ItchIfChanged = config.GetValue("itch", "if_changed", settings.ItchIfChanged).AsBool();
        settings.ItchIgnoreFiles = ReadString(config, "itch", "ignore_files", settings.ItchIgnoreFiles);

        var localConfig = new ConfigFile();
        if (localConfig.Load(LocalSettingsPath) == Error.Ok)
        {
            settings.SteamCmdPath = ReadString(localConfig, "tools", "steamcmd_path", string.Empty);
            settings.ButlerPath = ReadString(localConfig, "tools", "butler_path", string.Empty);
        }
        else if (!string.IsNullOrWhiteSpace(legacySteamCmdPath) || !string.IsNullOrWhiteSpace(legacyButlerPath))
        {
            settings.SteamCmdPath = legacySteamCmdPath;
            settings.ButlerPath = legacyButlerPath;
            SaveSettings(settings);
        }

        return settings;
    }

    public static Error SaveSettings(DeploySettings settings)
    {
        var config = new ConfigFile();
        config.SetValue("build", "targets", (long)settings.Targets);
        config.SetValue("build", "export_preset", settings.ExportPreset);
        config.SetValue("build", "export_output_path", settings.ExportOutputPath);

        config.SetValue("steam", "app_id", settings.SteamAppId);
        config.SetValue("steam", "depot_id", settings.SteamDepotId);
        config.SetValue("steam", "build_description", settings.SteamBuildDescription);
        config.SetValue("steam", "set_live", settings.SteamSetLive);
        config.SetValue("steam", "branch", settings.SteamBranch);
        config.SetValue("steam", "ignore_files", settings.SteamIgnoreFiles);

        config.SetValue("itch", "target", settings.ItchTarget);
        config.SetValue("itch", "channel", settings.ItchChannel);
        config.SetValue("itch", "user_version", settings.ItchUserVersion);
        config.SetValue("itch", "if_changed", settings.ItchIfChanged);
        config.SetValue("itch", "ignore_files", settings.ItchIgnoreFiles);
        Error sharedError = config.Save(SettingsPath);
        if (sharedError != Error.Ok)
        {
            return sharedError;
        }

        string localAbsolutePath = ProjectSettings.GlobalizePath(LocalSettingsPath);
        string? localDirectory = Path.GetDirectoryName(localAbsolutePath);
        if (!string.IsNullOrWhiteSpace(localDirectory))
        {
            Directory.CreateDirectory(localDirectory);
        }

        var localConfig = new ConfigFile();
        localConfig.SetValue("tools", "steamcmd_path", settings.SteamCmdPath);
        localConfig.SetValue("tools", "butler_path", settings.ButlerPath);
        return localConfig.Save(LocalSettingsPath);
    }

    public static DeployCredentials LoadCredentials()
    {
        var credentials = new DeployCredentials();
        var config = new ConfigFile();
        if (config.LoadEncryptedPass(CredentialsPath, DeriveMachinePassword()) != Error.Ok)
        {
            return credentials;
        }

        credentials.SteamUsername = ReadString(config, "steam", "username", string.Empty);
        credentials.SteamPassword = ReadString(config, "steam", "password", string.Empty);
        credentials.ButlerApiKey = ReadString(config, "itch", "api_key", string.Empty);
        return credentials;
    }

    public static Error SaveCredentials(DeployCredentials credentials)
    {
        string absolutePath = ProjectSettings.GlobalizePath(CredentialsPath);
        string? directory = Path.GetDirectoryName(absolutePath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var config = new ConfigFile();
        config.SetValue("steam", "username", credentials.SteamUsername);
        config.SetValue("steam", "password", credentials.SteamPassword);
        config.SetValue("itch", "api_key", credentials.ButlerApiKey);
        return config.SaveEncryptedPass(CredentialsPath, DeriveMachinePassword());
    }

    private static string ReadString(ConfigFile config, string section, string key, string fallback)
    {
        return config.GetValue(section, key, fallback).AsString();
    }

    private static string DeriveMachinePassword()
    {
        string material = $"{OS.GetUniqueId()}|{ProjectSettings.GlobalizePath("res://")}|GodotSteamItchIoDeployer_v1";
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)));
    }
}
#endif
