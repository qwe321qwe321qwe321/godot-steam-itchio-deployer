#if TOOLS
#nullable enable
using System;
using System.IO;
using System.Text;
using Godot;

namespace GodotSteamItchIoDeployer;

public static class VdfGenerator
{
    public static string Generate(DeploySettings settings, string contentRoot)
    {
        string outputDirectory = ProjectSettings.GlobalizePath("user://godot-steam-itchio-deployer/steam-vdf");
        Directory.CreateDirectory(outputDirectory);

        string depotPath = Path.Combine(outputDirectory, $"depot_build_{settings.SteamDepotId}.vdf");
        string appPath = Path.Combine(outputDirectory, $"app_build_{settings.SteamAppId}.vdf");
        File.WriteAllText(depotPath, BuildDepotVdf(settings), new UTF8Encoding(false));
        File.WriteAllText(appPath, BuildAppVdf(settings, contentRoot, depotPath), new UTF8Encoding(false));
        return appPath;
    }

    private static string BuildDepotVdf(DeploySettings settings)
    {
        var text = new StringBuilder();
        text.AppendLine("\"DepotBuild\"");
        text.AppendLine("{");
        text.AppendLine($"\t\"DepotID\"\t\"{EscapeValue(settings.SteamDepotId)}\"");
        text.AppendLine("\t\"FileMapping\"");
        text.AppendLine("\t{");
        text.AppendLine("\t\t\"LocalPath\"\t\"*\"");
        text.AppendLine("\t\t\"DepotPath\"\t\".\"");
        text.AppendLine("\t\t\"Recursive\"\t\"1\"");
        text.AppendLine("\t}");
        foreach (string pattern in SplitPatterns(settings.SteamIgnoreFiles))
        {
            text.AppendLine($"\t\"FileExclusion\"\t\"{EscapeValue(pattern)}\"");
        }

        text.AppendLine("}");
        return text.ToString();
    }

    private static string BuildAppVdf(DeploySettings settings, string contentRoot, string depotPath)
    {
        string description = ResolveMacros(settings.SteamBuildDescription);
        string branch = settings.SteamSetLive ? settings.SteamBranch : string.Empty;
        var text = new StringBuilder();
        text.AppendLine("\"AppBuild\"");
        text.AppendLine("{");
        text.AppendLine($"\t\"AppID\"\t\"{EscapeValue(settings.SteamAppId)}\"");
        text.AppendLine($"\t\"Desc\"\t\"{EscapeValue(description)}\"");
        text.AppendLine("\t\"Preview\"\t\"0\"");
        text.AppendLine($"\t\"ContentRoot\"\t\"{EscapePath(contentRoot)}\"");
        text.AppendLine($"\t\"BuildOutput\"\t\"{EscapePath(Path.GetDirectoryName(depotPath) ?? contentRoot)}\"");
        text.AppendLine($"\t\"SetLive\"\t\"{EscapeValue(branch)}\"");
        text.AppendLine("\t\"Depots\"");
        text.AppendLine("\t{");
        text.AppendLine($"\t\t\"{EscapeValue(settings.SteamDepotId)}\"\t\"{EscapePath(depotPath)}\"");
        text.AppendLine("\t}");
        text.AppendLine("}");
        return text.ToString();
    }

    public static string ResolveMacros(string value)
    {
        DateTime now = DateTime.Now;
        return value
            .Replace("{DateTime}", now.ToString("yyyy-MM-dd HH:mm:ss"), StringComparison.Ordinal)
            .Replace("{Date}", now.ToString("yyyy-MM-dd"), StringComparison.Ordinal);
    }

    public static string[] SplitPatterns(string value)
    {
        return value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }

    private static string EscapeValue(string value) => value.Replace("\"", "\\\"", StringComparison.Ordinal);

    private static string EscapePath(string value)
    {
        string absolute = Path.GetFullPath(value);
        return OperatingSystem.IsWindows()
            ? absolute.Replace("/", "\\", StringComparison.Ordinal).Replace("\\", "\\\\", StringComparison.Ordinal)
            : absolute.Replace("\\", "/", StringComparison.Ordinal);
    }
}
#endif
