#if TOOLS
#nullable enable

using System;
using System.Collections.Generic;
using System.IO;

namespace GodotSteamItchIoDeployer;

// Copies BuildDeployConfig.ExtraOutputFiles into the exported build. Entries are resolved and
// checked before the export starts (a missing file should not cost a full export), then copied
// into the staging directory so they are promoted to the output directory together with the
// export artifacts instead of being written into a live output directory afterwards.
internal static class ExtraOutputFileCopier
{
    internal readonly record struct ExtraOutputFile(string ConfiguredPath, string SourcePath, string FileName);

    public static List<ExtraOutputFile> Resolve(IReadOnlyList<string> configuredPaths)
    {
        var resolved = new List<ExtraOutputFile>();
        var seenFileNames = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (string configuredPath in configuredPaths)
        {
            if (string.IsNullOrWhiteSpace(configuredPath))
            {
                continue;
            }

            string sourcePath;
            try
            {
                sourcePath = DeployConfigStore.ResolveProjectPath(configuredPath);
            }
            catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
            {
                throw new InvalidOperationException($"Extra output file path is not a valid path: {configuredPath}");
            }

            if (Directory.Exists(sourcePath))
            {
                throw new InvalidOperationException($"Extra output file is a directory, not a file: {configuredPath}");
            }

            if (!File.Exists(sourcePath))
            {
                throw new InvalidOperationException($"Extra output file does not exist: {configuredPath}");
            }

            string fileName = Path.GetFileName(sourcePath);
            if (seenFileNames.TryGetValue(fileName, out string? firstConfiguredPath))
            {
                throw new InvalidOperationException(
                    $"Extra output files '{firstConfiguredPath}' and '{configuredPath}' would both be copied to '{fileName}' in the output directory.");
            }

            seenFileNames[fileName] = configuredPath;
            resolved.Add(new ExtraOutputFile(configuredPath, sourcePath, fileName));
        }

        return resolved;
    }

    // Returns one log line per copied file. Files land flat in the output directory root, and an
    // entry whose name collides with an exported artifact replaces it — the export just finished,
    // so reporting the overwrite beats discarding a good build over a name clash.
    public static List<string> CopyInto(IReadOnlyList<ExtraOutputFile> files, string destinationDirectory)
    {
        var logLines = new List<string>();
        if (files.Count == 0)
        {
            return logLines;
        }

        Directory.CreateDirectory(destinationDirectory);
        foreach (ExtraOutputFile file in files)
        {
            string destinationPath = Path.Combine(destinationDirectory, file.FileName);
            bool replacedExportedFile = File.Exists(destinationPath);
            File.Copy(file.SourcePath, destinationPath, overwrite: true);
            logLines.Add(replacedExportedFile
                ? $"WARNING: Extra output file {file.ConfiguredPath} replaced the exported '{file.FileName}'."
                : $"Copied extra output file {file.ConfiguredPath} -> {file.FileName}");
        }

        return logLines;
    }
}
#endif
