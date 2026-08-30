#if TOOLS
#nullable enable

using System;
using System.IO;
using System.Linq;
using System.Text;

namespace GodotSteamItchIoDeployer;

internal static class ExportArtifactValidator
{
    private const string NoSha = "NO_SHA";
    private static readonly TimeSpan NoShaTimestampGrace = TimeSpan.FromSeconds(5);

    public static string CreateStagingDirectory(string projectPath, string outputDirectory)
    {
        string fullProjectPath = NormalizeDirectory(projectPath);
        string fullOutputDirectory = NormalizeDirectory(outputDirectory);
        string? outputParent = Directory.GetParent(fullOutputDirectory)?.FullName;

        if (string.IsNullOrWhiteSpace(outputParent) ||
            string.Equals(fullOutputDirectory, Path.GetPathRoot(fullOutputDirectory), StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Export output directory must not be a filesystem root.");
        }

        if (string.Equals(fullOutputDirectory, fullProjectPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException("Export output directory must not be the project root.");
        }

        string stagingRoot = Path.Combine(Path.GetTempPath(), "godot-steam-itchio-deployer-exports");
        Directory.CreateDirectory(stagingRoot);
        string stagingDirectory = Path.Combine(
            stagingRoot,
            $"{Path.GetFileName(fullOutputDirectory)}-{Guid.NewGuid():N}");
        Directory.CreateDirectory(stagingDirectory);
        return stagingDirectory;
    }

    public static void ValidateExportOutput(string outputPath)
    {
        if (File.Exists(outputPath))
        {
            if (new FileInfo(outputPath).Length == 0)
                throw new InvalidOperationException($"Godot export produced an empty output file: {outputPath}");
            return;
        }

        if (Directory.Exists(outputPath) && Directory.EnumerateFileSystemEntries(outputPath).Any())
            return;

        throw new InvalidOperationException($"Godot export did not produce the configured output: {outputPath}");
    }

    public static string ValidateManagedAssembly(
        string projectPath,
        bool buildWithDebug,
        string expectedSha,
        DateTime exportStartedUtc)
    {
        string configuration = buildWithDebug ? "ExportDebug" : "ExportRelease";
        string assemblyRoot = Path.Combine(projectPath, ".godot", "mono", "temp", "bin", configuration);
        if (!Directory.Exists(assemblyRoot))
        {
            throw new InvalidOperationException($"Godot export did not produce the {configuration} managed output directory.");
        }

        string[] candidates = Directory.GetFiles(assemblyRoot, "*.dll", SearchOption.AllDirectories);
        if (candidates.Length == 0)
        {
            throw new InvalidOperationException($"Godot export did not produce a managed DLL under {assemblyRoot}.");
        }

        if (!string.Equals(expectedSha, NoSha, StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(expectedSha))
        {
            string? matchingAssembly = candidates.FirstOrDefault(path => ContainsText(path, expectedSha));
            if (matchingAssembly is not null)
                return matchingAssembly;

            throw new InvalidOperationException(
                $"Godot export produced no managed DLL containing the current Git SHA {expectedSha}. " +
                $"Candidates: {string.Join(", ", candidates.Select(path => Path.GetFileName(path)))}");
        }

        string expectedAssemblyName = $"{typeof(ExportArtifactValidator).Assembly.GetName().Name}.dll";
        string? mainAssembly = candidates.FirstOrDefault(path =>
            string.Equals(Path.GetFileName(path), expectedAssemblyName, StringComparison.OrdinalIgnoreCase));
        if (mainAssembly is null)
        {
            throw new InvalidOperationException(
                $"Godot export did not produce the project managed DLL '{expectedAssemblyName}'.");
        }

        if (new FileInfo(mainAssembly).LastWriteTimeUtc < exportStartedUtc - NoShaTimestampGrace)
        {
            throw new InvalidOperationException(
                $"Godot export left a stale managed DLL because Git SHA validation was unavailable: {mainAssembly}");
        }

        return mainAssembly;
    }

    public static void ValidatePackagedManagedAssemblies(string stagingDirectory, string expectedSha)
    {
        if (string.Equals(expectedSha, NoSha, StringComparison.OrdinalIgnoreCase) || string.IsNullOrWhiteSpace(expectedSha))
            return;

        string expectedAssemblyName = $"{typeof(ExportArtifactValidator).Assembly.GetName().Name}.dll";
        string[] candidates = Directory.GetFiles(stagingDirectory, expectedAssemblyName, SearchOption.AllDirectories);
        if (candidates.Length == 0)
        {
            // Production presets may embed build outputs in the PCK instead of copying the DLL
            // beside the executable. The source publish output is validated separately above.
            return;
        }

        if (candidates.Any(path => ContainsText(path, expectedSha)))
            return;

        throw new InvalidOperationException(
            $"The exported package contains '{expectedAssemblyName}', but none of its copies contains the current Git SHA {expectedSha}.");
    }

    public static string? PromoteStagedBuild(string stagingDirectory, string outputDirectory)
    {
        string fullStagingDirectory = NormalizeDirectory(stagingDirectory);
        string fullOutputDirectory = NormalizeDirectory(outputDirectory);
        if (!Directory.Exists(fullStagingDirectory))
            throw new DirectoryNotFoundException($"Staging directory does not exist: {fullStagingDirectory}");

        string outputParent = Directory.GetParent(fullOutputDirectory)?.FullName
            ?? throw new InvalidOperationException("Export output directory has no parent directory.");
        Directory.CreateDirectory(outputParent);

        string? backupDirectory = null;
        bool promoted = false;
        try
        {
            if (File.Exists(fullOutputDirectory))
                throw new IOException($"Export output path is a file, not a directory: {fullOutputDirectory}");

            if (Directory.Exists(fullOutputDirectory))
            {
                backupDirectory = $"{fullOutputDirectory}.previous-{Guid.NewGuid():N}";
                Directory.Move(fullOutputDirectory, backupDirectory);
            }

            try
            {
                Directory.Move(fullStagingDirectory, fullOutputDirectory);
                promoted = true;
            }
            catch (IOException)
            {
                if (Directory.Exists(fullOutputDirectory))
                    throw;

                // CopyDirectory can fail after creating a partial destination. Mark the destination
                // as ours before copying so the catch block removes it and restores the previous build.
                promoted = true;
                CopyDirectory(fullStagingDirectory, fullOutputDirectory);
                Directory.Delete(fullStagingDirectory, recursive: true);
            }

            if (backupDirectory is not null)
            {
                try
                {
                    Directory.Delete(backupDirectory, recursive: true);
                    backupDirectory = null;
                }
                catch
                {
                    // The new build is valid. Keep the previous output as a recoverable backup and
                    // let the caller report it instead of turning a successful build into a failure.
                }
            }

            return backupDirectory;
        }
        catch
        {
            if (promoted && Directory.Exists(fullOutputDirectory))
                Directory.Delete(fullOutputDirectory, recursive: true);

            if (backupDirectory is not null && Directory.Exists(backupDirectory) && !Directory.Exists(fullOutputDirectory))
                Directory.Move(backupDirectory, fullOutputDirectory);

            throw;
        }
    }

    public static void CleanupStagingDirectory(string stagingDirectory)
    {
        if (Directory.Exists(stagingDirectory))
            Directory.Delete(stagingDirectory, recursive: true);
    }

    private static bool ContainsText(string path, string value)
    {
        byte[] bytes = File.ReadAllBytes(path);
        return Encoding.UTF8.GetString(bytes).Contains(value, StringComparison.Ordinal) ||
               Encoding.Unicode.GetString(bytes).Contains(value, StringComparison.Ordinal);
    }

    private static void CopyDirectory(string sourceDirectory, string destinationDirectory)
    {
        Directory.CreateDirectory(destinationDirectory);
        foreach (string file in Directory.GetFiles(sourceDirectory))
        {
            File.Copy(file, Path.Combine(destinationDirectory, Path.GetFileName(file)), overwrite: true);
        }

        foreach (string directory in Directory.GetDirectories(sourceDirectory))
        {
            CopyDirectory(directory, Path.Combine(destinationDirectory, Path.GetFileName(directory)));
        }
    }

    private static string NormalizeDirectory(string path)
    {
        string fullPath = Path.GetFullPath(path);
        string root = Path.GetPathRoot(fullPath) ?? string.Empty;
        return fullPath.Length > root.Length
            ? fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            : fullPath;
    }
}
#endif
