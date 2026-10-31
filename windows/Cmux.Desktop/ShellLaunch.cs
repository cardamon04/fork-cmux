namespace Cmux.Desktop;

internal sealed record ShellLaunch(string Executable, string[] Arguments, string Directory)
{
    internal static ShellLaunch Default()
    {
        var paths = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator);
        var executable = paths.Select(p => Path.Combine(p, "pwsh.exe")).FirstOrDefault(File.Exists)
            ?? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        return new(Path.GetFullPath(executable), ["-NoLogo"], Environment.CurrentDirectory);
    }
}
