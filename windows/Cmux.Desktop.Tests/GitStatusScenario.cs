using System.Diagnostics;
using Cmux.Desktop;

internal static class GitStatusScenario
{
    internal static async Task Regression(ShellLaunch source, List<string> passed)
    {
        var root = Directory.CreateTempSubdirectory("cmux-git-state-").FullName;
        var repository = Directory.CreateDirectory(Path.Combine(root, "repo 日本語 & literal")).FullName;
        const string branch = "feature/日本語-🍀";
        using var window = new DesktopWindow(source with { Directory = repository }, new MemoryClipboard()) { ShowInTaskbar = false };
        Process? shell = null;
        string? profile = null;
        try
        {
            await Git(root, "init", "-b", branch, repository);
            Require(await Git(repository, "symbolic-ref", "--short", "HEAD") == branch, "The real Git branch fixture was not ready.");
            Console.WriteLine("fixture-ready: real Git unborn branch " + branch);
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            profile = window.ProfilePath;
            shell = Process.GetProcessById(((TerminalPane)window.Current!.Active!).ProcessId);
            _ = shell.SafeHandle;
            await Program.Contains((TerminalPane)window.Current.Active!, "CMUX_TEST_READY>");
            await Program.Until(() => Task.FromResult(Status(window).Contains(branch, StringComparison.Ordinal)),
                "The selected terminal's real Git branch was not displayed.");
            Pass("git-native-unborn-branch-visible", passed);
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            if (shell is not null)
            {
                Require(shell.WaitForExit(0), "The original Git fixture shell survived shutdown.");
                shell.Dispose();
            }
            if (profile is not null) Require(!Directory.Exists(profile), "The Git fixture profile survived shutdown.");
            DeleteFixture(root);
        }
    }

    private static string Status(DesktopWindow window) =>
        window.Controls.OfType<StatusStrip>().Single().Items["GitBranch"]?.Text ?? "";

    private static async Task<string> Git(string directory, params string[] arguments)
    {
        var start = new ProcessStartInfo("git")
        {
            WorkingDirectory = directory, UseShellExecute = false, CreateNoWindow = true,
            RedirectStandardOutput = true, RedirectStandardError = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
        };
        foreach (var key in start.Environment.Keys.Where(key => key.StartsWith("GIT_", StringComparison.OrdinalIgnoreCase)).ToArray())
            start.Environment.Remove(key);
        start.Environment["GIT_CONFIG_GLOBAL"] = "NUL";
        start.Environment["GIT_CONFIG_SYSTEM"] = "NUL";
        foreach (var argument in new[] { "-c", "init.templateDir=", "-c", "core.hooksPath=NUL" }.Concat(arguments))
            start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!;
        _ = process.SafeHandle;
        var output = process.StandardOutput.ReadToEndAsync();
        var error = process.StandardError.ReadToEndAsync();
        try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
        catch { process.Kill(); throw; }
        Require(process.ExitCode == 0, "Git fixture failed: " + await error);
        return (await output).TrimEnd('\r', '\n');
    }

    private static void DeleteFixture(string directory)
    {
        var full = Path.GetFullPath(directory);
        Require(Path.GetDirectoryName(full)!.Equals(Path.GetFullPath(Path.GetTempPath()).TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase)
            && Path.GetFileName(full).StartsWith("cmux-git-state-", StringComparison.Ordinal), "Refusing to remove a non-fixture directory.");
        foreach (var file in Directory.EnumerateFiles(full, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
        Directory.Delete(full, recursive: true);
    }

    private static void Pass(string name, List<string> passed) { passed.Add(name); Console.WriteLine("passed: " + name); }
    private static void Require(bool condition, string message) { if (!condition) throw new InvalidOperationException(message); }
}
