using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace Cmux.Desktop;

internal sealed record GitStatus(string Value, bool Detached)
{
    private const int MaxOutputCharacters = 32768;

    // Git owns discovery and ref storage (including linked worktrees and
    // reftable). These built-ins read metadata; they do not run hooks or fetch.
    internal static async Task<GitStatus?> Read(string directory, CancellationToken cancellationToken)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(TimeSpan.FromSeconds(4));
        try
        {
            budget.Token.ThrowIfCancellationRequested();
            if (!SessionStore.IsExistingLocalDirectory(directory) || FindExecutable() is not { } executable) return null;
            var reference = await Command(executable, directory, ["symbolic-ref", "--quiet", "HEAD"], budget.Token);
            if (reference is { ExitCode: 0 } && reference.Value.Text.StartsWith("refs/heads/", StringComparison.Ordinal))
            {
                var branch = reference.Value.Text["refs/heads/".Length..];
                return branch.Length > 0 ? new(branch, false) : null;
            }
            if (reference is null || reference.Value.ExitCode is not (0 or 1)) return null;
            var commit = await Command(executable, directory, ["rev-parse", "--verify", "HEAD^{commit}"], budget.Token);
            return commit is { ExitCode: 0 } && commit.Value.Text.Length is 40 or 64
                && commit.Value.Text.All(char.IsAsciiHexDigit) ? new(commit.Value.Text[..12], true) : null;
        }
        catch (Exception error) when (error is IOException or InvalidDataException or ArgumentException or Win32Exception or OperationCanceledException)
        {
            return null;
        }
    }

    private static string? FindExecutable()
    {
        foreach (var entry in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
        {
            var directory = entry.Trim().Trim('"');
            if (!Path.IsPathFullyQualified(directory) || directory.StartsWith(@"\\", StringComparison.Ordinal)) continue;
            var executable = Path.Combine(directory, "git.exe");
            if (File.Exists(executable)) return executable;
        }
        return null;
    }

    private static async Task<(int ExitCode, string Text)?> Command(string executable, string directory, string[] arguments, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        var start = new ProcessStartInfo(executable)
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = new UTF8Encoding(false, true),
        };
        // A terminal may inherit Git's internal environment from the process
        // launching cmux. It must not redirect this pane's repository or traces.
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_TERMINAL_PROMPT"] = "0";
        start.ArgumentList.Add("--no-lazy-fetch");
        start.ArgumentList.Add("--no-optional-locks");
        foreach (var argument in arguments) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        _ = process.SafeHandle;
        try
        {
            var output = ReadBounded(process.StandardOutput, token);
            var error = process.StandardError.BaseStream.CopyToAsync(Stream.Null, token);
            await Task.WhenAll(output, error, process.WaitForExitAsync(token));
            var text = (await output).TrimEnd('\r', '\n');
            return text.Any(char.IsControl) ? null : (process.ExitCode, text);
        }
        finally
        {
            if (!process.HasExited)
            {
                process.Kill(); // Only this originally owned process handle.
                await process.WaitForExitAsync();
            }
        }
    }

    private static async Task<string> ReadBounded(StreamReader reader, CancellationToken token)
    {
        var result = new StringBuilder();
        var buffer = new char[1024];
        int count;
        while ((count = await reader.ReadAsync(buffer.AsMemory(), token)) != 0)
        {
            if (result.Length + count > MaxOutputCharacters) throw new InvalidDataException("Git metadata exceeded its output limit.");
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}
