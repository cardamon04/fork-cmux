namespace Cmux.Desktop;

internal sealed record ShellLaunch(string Executable, string[] Arguments, string Directory)
{
    internal static ShellLaunch Default()
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        var executable = paths.Select(p => Path.Combine(p, "pwsh.exe")).FirstOrDefault(File.Exists)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        return new(Path.GetFullPath(executable), PowerShellArguments(), Environment.CurrentDirectory);
    }

    internal static string[] PowerShellArguments(string? initialization = null, bool noProfile = false)
    {
        using var resource = typeof(ShellLaunch).Assembly.GetManifestResourceStream("Cmux.Desktop.PowerShellIntegration.ps1")!;
        using var reader = new StreamReader(resource);
        var script = (initialization is null ? "" : initialization + "\n") + reader.ReadToEnd();
        var encoded = Convert.ToBase64String(System.Text.Encoding.Unicode.GetBytes(script));
        return noProfile ? ["-NoLogo", "-NoProfile", "-NoExit", "-EncodedCommand", encoded]
            : ["-NoLogo", "-NoExit", "-EncodedCommand", encoded];
    }
}
