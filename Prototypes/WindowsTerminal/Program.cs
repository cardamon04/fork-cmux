using System.Diagnostics;
using System.Text;
using System.Text.Json;
using WindowsTerminal;

Console.OutputEncoding = new UTF8Encoding(false);
if (args.Length > 0 && args[0] == "--child")
{
    Console.InputEncoding = new UTF8Encoding(false);
    Console.WriteLine("@@READY@@");
    while (Console.ReadLine() is { } line)
    {
        if (line == "size") Console.WriteLine($"@@SIZE:{Console.WindowWidth}:{Console.WindowHeight}@@");
        else if (line.StartsWith("echo:")) Console.WriteLine("@@ECHO:" + Encoding.UTF8.GetString(Convert.FromBase64String(line[5..])) + "@@");
        else if (line.StartsWith("text:")) Console.WriteLine("@@TEXT:" + line[5..] + "@@");
        else if (line.StartsWith("burst:"))
        {
            Console.Write(new string('x', int.Parse(line[6..])));
            Console.WriteLine("@@BURST@@");
        }
        else if (line == "cwd") Console.WriteLine("@@CWD:" + Directory.GetCurrentDirectory() + "@@");
        else if (line.StartsWith("exit:")) return int.Parse(line[5..]);
        else if (line == "args") Console.WriteLine("@@ARGS:" + JsonSerializer.Serialize(args.Skip(1)) + "@@");
    }
    return 0;
}

if (args.Length == 0 || args[0] != "--self-test")
{
    Console.Error.WriteLine("dotnet run --project Prototypes/WindowsTerminal -- --self-test [absolute-pwsh-path]");
    return 2;
}
if (!OperatingSystem.IsWindowsVersionAtLeast(10, 0, 17763))
{
    Console.Error.WriteLine("ConPTY integration tests require Windows build 17763 or later.");
    return 2;
}

var tests = new List<(string Name, Func<Task> Run)>
{
    ("unicode-input-and-nonzero-exit", UnicodeAndExit),
    ("resize-reaches-client", Resize),
    ("cwd-and-argv-with-spaces", WorkingDirectoryAndArguments),
    ("drain-large-output-during-close", LargeOutputAndClose),
    ("consumer-failure-still-drains", ConsumerFailure),
    ("dispose-terminates-attached-process", CloseRunningProcess),
    ("spawn-failure-and-invalid-size", FailureCleanup),
    ("ten-concurrent-terminals", TenTerminals),
};
if (args.Length > 1)
    tests.Add(("powershell-utf8-and-exit", () => PowerShell(args[1])));
if (args.Length > 2)
    tests = tests.Where(test => test.Name == args[2]).ToList();
if (tests.Count == 0)
{
    Console.Error.WriteLine("No test matched the requested name.");
    return 2;
}

var outcomes = new List<object>();
var failed = false;
foreach (var test in tests)
{
    var watch = Stopwatch.StartNew();
    try
    {
        await test.Run();
        outcomes.Add(new { test = test.Name, status = "passed", milliseconds = watch.ElapsedMilliseconds });
        Console.Error.WriteLine("passed: " + test.Name);
    }
    catch (Exception error)
    {
        failed = true;
        outcomes.Add(new { test = test.Name, status = "failed", milliseconds = watch.ElapsedMilliseconds, error = error.ToString() });
        Console.Error.WriteLine("failed: " + test.Name);
    }
}
Console.WriteLine(JsonSerializer.Serialize(new
{
    os = System.Runtime.InteropServices.RuntimeInformation.OSDescription,
    architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    runtime = Environment.Version.ToString(),
    powershell = args.Length > 1 ? "tested" : "not-run",
    outcomes,
}, new JsonSerializerOptions { WriteIndented = true }));
return failed ? 1 : 0;

static ConPtySession Start(Transcript output, string? directory = null, IReadOnlyList<string>? extra = null)
{
    // Use the apphost to start an independent console client of the same build.
    var executable = Path.Combine(AppContext.BaseDirectory, "WindowsTerminal.exe");
    return ConPtySession.Start(executable, new[] { "--child" }.Concat(extra ?? []).ToArray(),
        directory ?? Directory.GetCurrentDirectory(), 100, 30, output.Append);
}

static async Task UnicodeAndExit()
{
    var output = new Transcript();
    using var session = Start(output);
    await output.WaitFor("@@READY@@");
    var text = "日本語の入力🙂 + \\ path";
    // Verify characters read by the application, beyond the console input echo.
    session.Write("text:" + text + "\r");
    await output.WaitFor("@@TEXT:" + text + "@@");
    session.Write("echo:" + Convert.ToBase64String(Encoding.UTF8.GetBytes(text)) + "\r");
    await output.WaitFor("@@ECHO:" + text + "@@");
    session.Write("exit:7\r");
    if (session.WaitForExit(TimeSpan.FromSeconds(10)) != 7) throw new Exception("The child exit code was lost.");
}

static async Task Resize()
{
    var output = new Transcript();
    using var session = Start(output);
    await output.WaitFor("@@READY@@");
    session.Write("size\r");
    await output.WaitFor("@@SIZE:100:30@@");
    session.Resize(132, 43);
    session.Write("size\r");
    await output.WaitFor("@@SIZE:132:43@@");
}

static async Task WorkingDirectoryAndArguments()
{
    var directory = Path.Combine(Path.GetTempPath(), "cmux conpty 日本語 " + Guid.NewGuid().ToString("N"));
    Directory.CreateDirectory(directory);
    var runtimeFiles = new[] { "WindowsTerminal.exe", "WindowsTerminal.dll", "WindowsTerminal.deps.json", "WindowsTerminal.runtimeconfig.json" };
    try
    {
        foreach (var file in runtimeFiles)
            File.Copy(Path.Combine(AppContext.BaseDirectory, file), Path.Combine(directory, file));
        var arguments = new[] { "", "space value", "quoted\"value", "C:\\path with spaces\\", "日本語🙂" };
        var output = new Transcript();
        using var session = ConPtySession.Start(Path.Combine(directory, "WindowsTerminal.exe"),
            new[] { "--child" }.Concat(arguments).ToArray(), directory, 100, 30, output.Append);
        await output.WaitFor("@@READY@@");
        session.Write("cwd\rargs\r");
        await output.WaitFor("@@CWD:" + directory + "@@");
        await output.WaitFor("@@ARGS:" + JsonSerializer.Serialize(arguments) + "@@");
    }
    finally
    {
        foreach (var file in runtimeFiles) File.Delete(Path.Combine(directory, file));
        Directory.Delete(directory);
    }
}

static async Task LargeOutputAndClose()
{
    var output = new Transcript();
    var session = Start(output);
    try
    {
        await output.WaitFor("@@READY@@");
        session.Write("burst:100000\r");
        await output.WaitFor("@@BURST@@");
        // Start another burst and close while it may still be producing output.
        session.Write("burst:100000\r");
    }
    finally { session.Dispose(); }
}

static async Task ConsumerFailure()
{
    var output = new Transcript();
    var failConsumer = false;
    var session = ConPtySession.Start(Path.Combine(AppContext.BaseDirectory, "WindowsTerminal.exe"), ["--child"],
        Directory.GetCurrentDirectory(), 100, 30, text =>
        {
            if (Volatile.Read(ref failConsumer)) throw new InvalidOperationException("Intentional test consumer failure.");
            output.Append(text);
        });
    try
    {
        await output.WaitFor("@@READY@@");
        Volatile.Write(ref failConsumer, true);
        session.Write("burst:100000\rexit:0\r");
        if (session.WaitForExit(TimeSpan.FromSeconds(10)) != 0) throw new Exception("The drain did not let the child finish.");
        try { session.Dispose(); throw new Exception("The consumer failure was lost."); }
        catch (AggregateException error) when (error.InnerException is InvalidOperationException) { }
    }
    finally { session.Dispose(); }
}

static async Task CloseRunningProcess()
{
    var output = new Transcript();
    var session = Start(output);
    try
    {
        await output.WaitFor("@@READY@@");
        using var process = Process.GetProcessById(session.ProcessId);
        session.Dispose();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(10));
        session.Dispose();
        try { session.Write("ignored"); throw new Exception("A disposed terminal accepted input."); }
        catch (ObjectDisposedException) { }
    }
    finally { session.Dispose(); }
}

static Task FailureCleanup()
{
    var output = new Transcript();
    try
    {
        using var unexpected = ConPtySession.Start(Environment.ProcessPath!, [], Directory.GetCurrentDirectory(), 0, 30, output.Append);
        throw new Exception("An invalid terminal size was accepted.");
    }
    catch (ArgumentOutOfRangeException) { }
    // Existing non-executable file reaches CreateProcessW and exercises cleanup
    // after pipes, pseudoconsole and output reader have all been allocated.
    var file = Path.GetTempFileName();
    try
    {
        try
        {
            using var unexpected = ConPtySession.Start(file, [], Directory.GetCurrentDirectory(), 80, 24, output.Append);
            throw new Exception("A non-executable file was started.");
        }
        catch (System.ComponentModel.Win32Exception) { }
    }
    finally { File.Delete(file); }
    return Task.CompletedTask;
}

static async Task TenTerminals()
{
    await Task.WhenAll(Enumerable.Range(0, 10).Select(async index =>
    {
        var output = new Transcript();
        using var session = Start(output);
        await output.WaitFor("@@READY@@");
        session.Write("echo:" + Convert.ToBase64String(Encoding.UTF8.GetBytes("terminal-" + index)) + "\r");
        await output.WaitFor("@@ECHO:terminal-" + index + "@@");
        session.Write("exit:0\r");
        if (session.WaitForExit(TimeSpan.FromSeconds(10)) != 0) throw new Exception("Concurrent child failed.");
    }));
}

static async Task PowerShell(string executable)
{
    var output = new Transcript();
    // A profile-free read/eval loop verifies console input and PowerShell parsing
    // without loading the user's PSReadLine configuration. -Command - requires
    // redirected stdin and cannot be used with a real ConPTY console.
    var command = "[Console]::WriteLine('@@POWERSHELL-READY@@'); while($null -ne ($line = [Console]::ReadLine())) { Invoke-Expression $line }";
    using var session = ConPtySession.Start(executable, ["-NoLogo", "-NoProfile", "-Command", command],
        Directory.GetCurrentDirectory(), 120, 30, output.Append);
    await output.WaitFor("@@POWERSHELL-READY@@");
    session.Write("[Console]::WriteLine('@@POWERSHELL:日本語🙂@@'); exit 13\r");
    await output.WaitFor("@@POWERSHELL:日本語🙂@@");
    if (session.WaitForExit(TimeSpan.FromSeconds(10)) != 13) throw new Exception("PowerShell exit code was lost.");
}
