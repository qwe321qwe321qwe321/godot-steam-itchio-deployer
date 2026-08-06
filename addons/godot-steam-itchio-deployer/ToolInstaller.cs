#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Net.Http;
using System.Runtime.InteropServices;
using System.Threading.Tasks;
using Godot;

namespace GodotSteamItchIoDeployer;

public enum DeployToolKind
{
    SteamCmd,
    Butler,
}

public static class ToolInstaller
{
    private const string SteamCmdWindowsUrl = "https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip";

    public static async Task<string> InstallAsync(DeployToolKind tool, Action<string> onProgress)
    {
        (string url, string executableName, string folderName) = GetPackage(tool);
        string toolsRoot = ProjectSettings.GlobalizePath("res://.deployer/tools");
        string installDirectory = Path.Combine(toolsRoot, folderName);
        string stagingDirectory = Path.Combine(toolsRoot, $".{folderName}-staging-{Guid.NewGuid():N}");
        string archivePath = Path.Combine(toolsRoot, $".{folderName}-download-{Guid.NewGuid():N}.zip");
        Directory.CreateDirectory(toolsRoot);

        try
        {
            onProgress($"Downloading {tool} from official source...");
            await DownloadAsync(url, archivePath, onProgress).ConfigureAwait(false);

            Directory.CreateDirectory(stagingDirectory);
            ZipFile.ExtractToDirectory(archivePath, stagingDirectory, overwriteFiles: true);
            string stagedExecutable = FindFile(stagingDirectory, executableName)
                ?? throw new InvalidDataException($"Downloaded archive does not contain {executableName}.");

            Directory.CreateDirectory(installDirectory);
            CopyDirectory(stagingDirectory, installDirectory);
            string executablePath = FindFile(installDirectory, executableName)
                ?? throw new InvalidDataException($"Installed files do not contain {executableName}.");

            if (!OperatingSystem.IsWindows())
            {
                File.SetUnixFileMode(
                    executablePath,
                    UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute |
                    UnixFileMode.GroupRead | UnixFileMode.GroupExecute |
                    UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
            }

            if (tool == DeployToolKind.SteamCmd)
            {
                await InitializeSteamCmdAsync(executablePath, installDirectory, onProgress).ConfigureAwait(false);
            }
            else
            {
                await VerifyButlerAsync(executablePath, installDirectory, onProgress).ConfigureAwait(false);
            }

            onProgress($"{tool} installed: {executablePath}");
            return executablePath;
        }
        finally
        {
            TryDeleteFile(archivePath);
            TryDeleteDirectory(stagingDirectory);
        }
    }

    private static (string Url, string ExecutableName, string FolderName) GetPackage(DeployToolKind tool)
    {
        if (tool == DeployToolKind.SteamCmd)
        {
            if (!OperatingSystem.IsWindows())
            {
                throw new PlatformNotSupportedException("Automatic SteamCMD installation is currently supported on Windows only.");
            }

            return (SteamCmdWindowsUrl, "steamcmd.exe", "steamcmd");
        }

        string os = OperatingSystem.IsWindows() ? "windows"
            : OperatingSystem.IsMacOS() ? "darwin"
            : OperatingSystem.IsLinux() ? "linux"
            : throw new PlatformNotSupportedException("Automatic butler installation is not supported on this operating system.");
        string architecture = RuntimeInformation.OSArchitecture switch
        {
            Architecture.X64 => "amd64",
            Architecture.Arm64 => "arm64",
            _ => throw new PlatformNotSupportedException($"Automatic butler installation is not supported on {RuntimeInformation.OSArchitecture}."),
        };
        string url = $"https://broth.itch.zone/butler/{os}-{architecture}/LATEST/archive/default";
        string executable = OperatingSystem.IsWindows() ? "butler.exe" : "butler";
        return (url, executable, "butler");
    }

    private static async Task DownloadAsync(string url, string destinationPath, Action<string> onProgress)
    {
        using var client = new System.Net.Http.HttpClient { Timeout = TimeSpan.FromMinutes(10) };
        client.DefaultRequestHeaders.UserAgent.ParseAdd("godot-steam-itchio-deployer/0.1");
        using HttpResponseMessage response = await client.GetAsync(url, HttpCompletionOption.ResponseHeadersRead).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        long? totalBytes = response.Content.Headers.ContentLength;
        await using Stream input = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
        await using var output = new FileStream(destinationPath, FileMode.CreateNew, System.IO.FileAccess.Write, FileShare.None, 81920, useAsync: true);
        var buffer = new byte[81920];
        long downloaded = 0;
        int lastReportedBucket = -1;
        while (true)
        {
            int read = await input.ReadAsync(buffer).ConfigureAwait(false);
            if (read == 0) break;
            await output.WriteAsync(buffer.AsMemory(0, read)).ConfigureAwait(false);
            downloaded += read;

            if (totalBytes is > 0)
            {
                int bucket = (int)(downloaded * 10 / totalBytes.Value);
                if (bucket != lastReportedBucket)
                {
                    lastReportedBucket = bucket;
                    onProgress($"Download progress: {Math.Min(bucket * 10, 100)}% ({downloaded:N0}/{totalBytes.Value:N0} bytes)");
                }
            }
        }

        onProgress($"Download completed: {downloaded:N0} bytes");
    }

    private static async Task InitializeSteamCmdAsync(string executablePath, string workingDirectory, Action<string> onProgress)
    {
        onProgress("Initializing SteamCMD and applying official self-updates...");
        for (int attempt = 1; attempt <= 3; attempt++)
        {
            CliProcessResult result = await CliProcessRunner.RunAsync(
                executablePath,
                new[] { "+quit" },
                workingDirectory,
                null,
                onProgress).ConfigureAwait(false);
            if (result.Succeeded)
            {
                onProgress("SteamCMD initialization completed.");
                return;
            }

            onProgress($"SteamCMD bootstrap attempt {attempt} exited with code {result.ExitCode}; retrying after updater handoff.");
            await Task.Delay(1000).ConfigureAwait(false);
        }

        throw new InvalidOperationException("SteamCMD did not finish initialization after 3 attempts.");
    }

    private static async Task VerifyButlerAsync(string executablePath, string workingDirectory, Action<string> onProgress)
    {
        CliProcessResult result = await CliProcessRunner.RunAsync(
            executablePath,
            new[] { "-V" },
            workingDirectory,
            null,
            onProgress).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"butler version check failed with exit code {result.ExitCode}.");
        }
    }

    private static string? FindFile(string root, string fileName)
    {
        foreach (string path in Directory.EnumerateFiles(root, fileName, SearchOption.AllDirectories))
        {
            return path;
        }

        return null;
    }

    private static void CopyDirectory(string source, string destination)
    {
        foreach (string directory in Directory.EnumerateDirectories(source, "*", SearchOption.AllDirectories))
        {
            Directory.CreateDirectory(Path.Combine(destination, Path.GetRelativePath(source, directory)));
        }

        foreach (string file in Directory.EnumerateFiles(source, "*", SearchOption.AllDirectories))
        {
            string destinationFile = Path.Combine(destination, Path.GetRelativePath(source, file));
            Directory.CreateDirectory(Path.GetDirectoryName(destinationFile)!);
            File.Copy(file, destinationFile, overwrite: true);
        }
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Temporary cleanup must not hide the original download/install result.
        }
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
        catch
        {
            // Temporary cleanup must not hide the original download/install result.
        }
    }
}
#endif
