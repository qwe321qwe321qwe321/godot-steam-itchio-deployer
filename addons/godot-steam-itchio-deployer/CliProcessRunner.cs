#if TOOLS
#nullable enable
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Text;
using System.Threading.Tasks;

namespace GodotSteamItchIoDeployer;

public sealed record CliProcessResult(int ExitCode, string CombinedOutput)
{
    public bool Succeeded => ExitCode == 0;
}

public static class CliProcessRunner
{
    public static async Task<CliProcessResult> RunAsync(
        string executablePath,
        IReadOnlyList<string> arguments,
        string workingDirectory,
        IReadOnlyDictionary<string, string>? environment,
        Action<string> onOutput)
    {
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

        var combined = new StringBuilder();
        Task stdout = PumpAsync(process.StandardOutput, combined, onOutput);
        Task stderr = PumpAsync(process.StandardError, combined, onOutput);
        await Task.WhenAll(process.WaitForExitAsync(), stdout, stderr).ConfigureAwait(false);
        return new CliProcessResult(process.ExitCode, combined.ToString());
    }

    private static async Task PumpAsync(System.IO.StreamReader reader, StringBuilder combined, Action<string> onOutput)
    {
        while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
        {
            lock (combined)
            {
                combined.AppendLine(line);
            }

            onOutput(line);
        }
    }
}
#endif
