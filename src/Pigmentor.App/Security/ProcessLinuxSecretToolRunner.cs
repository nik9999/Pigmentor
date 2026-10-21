using System.Diagnostics;
using System.Text;

namespace Pigmentor.App.Security;

/// <summary>
/// Production process runner executing the standard Linux `secret-tool` binary.
/// Transmits secrets strictly via standard input to prevent leakage into process lists or logs.
/// </summary>
public sealed class ProcessLinuxSecretToolRunner : ILinuxSecretToolRunner
{
    private const string ToolCommand = "secret-tool";

    public bool IsAvailable()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = ToolCommand,
                Arguments = "--version",
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };

            using var process = Process.Start(psi);
            if (process == null)
            {
                return false;
            }

            process.WaitForExit(1000);
            return process.ExitCode == 0;
        }
        catch
        {
            return false;
        }
    }

    public async Task StoreSecretAsync(string label, string service, string key, string secret, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ToolCommand,
            Arguments = $"store --label=\"{Escape(label)}\" service \"{Escape(service)}\" key \"{Escape(key)}\"",
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch secret-tool process.");
        await using (var writer = new StreamWriter(process.StandardInput.BaseStream, new UTF8Encoding(false), leaveOpen: false))
        {
            await writer.WriteAsync(secret.AsMemory(), cancellationToken).ConfigureAwait(false);
            await writer.FlushAsync(cancellationToken).ConfigureAwait(false);
        }

        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(cancellationToken).ConfigureAwait(false);
            throw new InvalidOperationException($"secret-tool store failed with exit code {process.ExitCode}: {error.Trim()}");
        }
    }

    public async Task<string?> LookupSecretAsync(string service, string key, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ToolCommand,
            Arguments = $"lookup service \"{Escape(service)}\" key \"{Escape(key)}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch secret-tool process.");
        var outputTask = process.StandardOutput.ReadToEndAsync(cancellationToken);
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);

        var output = await outputTask.ConfigureAwait(false);

        if (process.ExitCode != 0 || string.IsNullOrEmpty(output))
        {
            return null;
        }

        var trimmed = output.TrimEnd('\r', '\n');
        return string.IsNullOrEmpty(trimmed) ? null : trimmed;
    }

    public async Task ClearSecretAsync(string service, string key, CancellationToken cancellationToken)
    {
        var psi = new ProcessStartInfo
        {
            FileName = ToolCommand,
            Arguments = $"clear service \"{Escape(service)}\" key \"{Escape(key)}\"",
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        using var process = Process.Start(psi) ?? throw new InvalidOperationException("Failed to launch secret-tool process.");
        await process.WaitForExitAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string Escape(string value) => value.Replace("\"", "\\\"");
}

