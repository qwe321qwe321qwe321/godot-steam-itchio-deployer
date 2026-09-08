#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using SteamItchIoDeployerCore;

namespace GodotSteamItchIoDeployer;

public sealed record CliProcessResult(int ExitCode, string CombinedOutput, bool TerminatedByOutputPattern = false)
{
    public bool Succeeded => ExitCode == 0;
}

public static class CliProcessRunner
{
    private static readonly string[] GodotExportBuildFailureMarkers =
    {
        "Export .NET Project: Failed",
        "Export .NET Project: Error",
    };

    public static bool IsSteamGuardRequired(string output) => CliOutputClassifier.IsSteamGuardRequired(output);

    public static bool IsGodotExportBuildFailure(string output)
    {
        if (string.IsNullOrEmpty(output))
            return false;

        foreach (string marker in GodotExportBuildFailureMarkers)
        {
            if (output.Contains(marker, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }

    public static async Task<CliProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        Action<string> onOutput,
        Func<string, bool>? terminateWhen = null,
        string? watchedOutputFile = null,
        CancellationToken cancellationToken = default)
    {
        long watchedFileOffset = GetFileLength(watchedOutputFile);
        var startInfo = new ProcessStartInfo
        {
            FileName = executablePath,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };

        foreach (string argument in arguments)
        {
            startInfo.ArgumentList.Add(argument);
        }

        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        if (!process.Start())
        {
            throw new InvalidOperationException($"Could not start process: {executablePath}");
        }

        int terminatedByPattern = 0;
        void TerminateProcess()
        {
            if (Interlocked.Exchange(ref terminatedByPattern, 1) != 0)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                }
            }
            catch (InvalidOperationException)
            {
                // The process exited between the output match and the kill request.
            }
        }

        using CancellationTokenRegistration cancelRegistration = cancellationToken.Register(TerminateProcess);

        var combined = new StringBuilder();
        using var watcherCancellation = new CancellationTokenSource();
        Task stdout = PumpAsync(process.StandardOutput, combined, onOutput, terminateWhen, TerminateProcess);
        Task stderr = PumpAsync(process.StandardError, combined, onOutput, terminateWhen, TerminateProcess);
        Task watchedOutput = WatchOutputFileAsync(
            watchedOutputFile,
            watchedFileOffset,
            terminateWhen,
            TerminateProcess,
            watcherCancellation.Token);
        await Task.WhenAll(process.WaitForExitAsync(), stdout, stderr).ConfigureAwait(false);
        watcherCancellation.Cancel();
        await watchedOutput.ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        return new CliProcessResult(process.ExitCode, combined.ToString(), Volatile.Read(ref terminatedByPattern) != 0);
    }

    private static async Task WatchOutputFileAsync(
        string? path,
        long offset,
        Func<string, bool>? terminateWhen,
        Action terminateProcess,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(path) || terminateWhen is null)
        {
            return;
        }

        var recentOutput = new StringBuilder();
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (File.Exists(path))
                {
                    var info = new FileInfo(path);
                    if (info.Length < offset)
                    {
                        offset = 0;
                        recentOutput.Clear();
                    }

                    if (info.Length > offset)
                    {
                        await using var stream = new FileStream(
                            path,
                            FileMode.Open,
                            FileAccess.Read,
                            FileShare.ReadWrite | FileShare.Delete);
                        stream.Seek(offset, SeekOrigin.Begin);
                        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
                        string appended = await reader.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
                        offset = stream.Position;
                        recentOutput.Append(appended);
                        if (recentOutput.Length > 8192)
                        {
                            recentOutput.Remove(0, recentOutput.Length - 8192);
                        }

                        if (terminateWhen(recentOutput.ToString()))
                        {
                            terminateProcess();
                            return;
                        }
                    }
                }

                await Task.Delay(100, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // Normal shutdown after the child process exits.
        }
        catch (IOException)
        {
            // SteamCMD may briefly rotate or lock its log. stdout/stderr monitoring remains active.
        }
    }

    private static long GetFileLength(string? path)
    {
        try
        {
            return !string.IsNullOrWhiteSpace(path) && File.Exists(path) ? new FileInfo(path).Length : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static async Task PumpAsync(
        System.IO.StreamReader reader,
        StringBuilder combined,
        Action<string> onOutput,
        Func<string, bool>? terminateWhen,
        Action terminateProcess)
    {
        var line = new StringBuilder();
        var buffer = new char[1];
        while (await reader.ReadAsync(buffer.AsMemory(0, 1)).ConfigureAwait(false) > 0)
        {
            char character = buffer[0];
            if (character is '\r' or '\n')
            {
                PublishLine(line, combined, onOutput);
                continue;
            }

            line.Append(character);
            if (terminateWhen?.Invoke(line.ToString()) == true)
            {
                PublishLine(line, combined, onOutput);
                terminateProcess();
                return;
            }
        }

        PublishLine(line, combined, onOutput);
    }

    private static void PublishLine(StringBuilder line, StringBuilder combined, Action<string> onOutput)
    {
        if (line.Length == 0)
        {
            return;
        }

        string text = line.ToString();
        line.Clear();
        lock (combined)
        {
            combined.AppendLine(text);
        }

        onOutput(text);
    }
}
#endif
