using System.Diagnostics;
using Cmux.Desktop;

internal static class GitStatusScenario
{
    internal static async Task Run(ShellLaunch source, List<string> passed, string? artifacts)
    {
        await Regression(source, passed);
        await ReaderCases(passed);
        await OutputBoundary(passed);
        await WorkerFailure(source, passed);
        await Integration(source, "pwsh", passed, artifacts);
        var fallback = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), @"WindowsPowerShell\v1.0\powershell.exe");
        Require(File.Exists(fallback), "The Windows PowerShell Git fixture is unavailable.");
        await Integration(source with { Executable = fallback }, "windows-powershell", passed, artifacts);
    }

    internal static async Task Regression(ShellLaunch source, List<string> passed, bool literalPixels = false)
    {
        var root = Directory.CreateTempSubdirectory("cmux-git-state-").FullName;
        var repository = Directory.CreateDirectory(Path.Combine(root, "repo 日本語 & literal")).FullName;
        var branch = literalPixels ? "feature/a&b" : "feature/日本語-🍀";
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
            if (literalPixels) VerifyLiteralRendering(window, new Strings().Format("GitBranch", branch));
            Pass(literalPixels ? "git-native-literal-pixel-regression" : "git-native-unborn-branch-visible", passed);
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

    internal static async Task OutputBoundary(List<string> passed)
    {
        var root = Directory.CreateTempSubdirectory("cmux-git-state-").FullName;
        try
        {
            var fake = Directory.CreateDirectory(Path.Combine(root, "private tool")).FullName;
            foreach (var file in Directory.EnumerateFiles(AppContext.BaseDirectory)
                .Where(path => Path.GetExtension(path) is ".dll" or ".json"))
                File.Copy(file, Path.Combine(fake, Path.GetFileName(file)));
            File.Copy(Environment.ProcessPath!, Path.Combine(fake, "git.exe"));
            var start = new ProcessStartInfo(Environment.ProcessPath!)
            {
                UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root,
                RedirectStandardOutput = true, RedirectStandardError = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8, StandardErrorEncoding = System.Text.Encoding.UTF8,
            };
            start.Environment["CMUX_TEST_GIT_OUTPUT"] = "1";
            start.Environment["PATH"] = fake;
            start.ArgumentList.Add("--git-read-output-fixture");
            start.ArgumentList.Add(root);
            using var process = Process.Start(start)!;
            _ = process.SafeHandle;
            var output = process.StandardOutput.ReadToEndAsync();
            var error = process.StandardError.ReadToEndAsync();
            try { await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15)); }
            catch { process.Kill(); throw; }
            var text = await output;
            var diagnostic = await error;
            Require(process.ExitCode == 0 && text.Contains("owned-reader-rejected-output", StringComparison.Ordinal),
                "The owned metadata reader did not reject oversized output safely: " + process.ExitCode + " " + diagnostic);
            Pass("git-reader-oversized-output-does-not-escape", passed);
        }
        finally { DeleteFixture(root); }
    }

    internal static async Task WorkerFailure(ShellLaunch source, List<string> passed)
    {
        var root = Directory.CreateTempSubdirectory("cmux-git-state-").FullName;
        using var window = new DesktopWindow(source with { Directory = root }, new MemoryClipboard()) { ShowInTaskbar = false };
        Process? original = null;
        string? profile = null;
        const System.Reflection.BindingFlags hidden = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var workerField = typeof(DesktopWindow).GetField("gitWorker", hidden)!;
        try
        {
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            profile = window.ProfilePath;
            original = Process.GetProcessById(((TerminalPane)window.Current!.Active!).ProcessId);
            _ = original.SafeHandle;
            await Program.Contains((TerminalPane)window.Current.Active!, "CMUX_TEST_READY>");
            var stopping = (CancellationTokenSource)typeof(DesktopWindow).GetField("gitClosing", hidden)!.GetValue(window)!;
            stopping.Cancel();
            await ((Task)workerField.GetValue(window)!).WaitAsync(TimeSpan.FromSeconds(10));
            // Reproduce the already observed escaped-reader fault at the real
            // lifecycle boundary, without constructing a malformed repository.
            workerField.SetValue(window, Task.FromException(new InvalidDataException("Synthetic metadata worker failure.")));
            var reported = false;
            try { await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30)); }
            catch (InvalidDataException) { reported = true; }
            Require(reported, "Shutdown did not report its metadata worker failure.");
            Require(original.WaitForExit(0) && !Directory.Exists(profile), "Metadata failure skipped owned shell/profile teardown.");
            Pass("git-worker-failure-still-closes-owned-resources", passed);
        }
        finally
        {
            workerField.SetValue(window, Task.CompletedTask);
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            if (original is not null) { Require(original.WaitForExit(0), "The original worker fixture shell survived cleanup."); original.Dispose(); }
            if (profile is not null) Require(!Directory.Exists(profile), "The worker fixture profile survived cleanup.");
            DeleteFixture(root);
        }
    }

    private static async Task ReaderCases(List<string> passed)
    {
        var root = Directory.CreateTempSubdirectory("cmux-git-state-").FullName;
        var repository = Directory.CreateDirectory(Path.Combine(root, "project & 日本語")).FullName;
        var child = Directory.CreateDirectory(Path.Combine(repository, "nested")).FullName;
        try
        {
            await Git(root, "init", "-b", "topic/名前&🍀", repository);
            var unborn = await GitStatus.Read(child, CancellationToken.None);
            Require(unborn == new GitStatus("topic/名前&🍀", false), "Nested directory/unborn branch resolution lost literal data.");
            Pass("git-reader-subdirectory-unborn-unicode-literal", passed);
            await Commit(repository);
            var sha = await Git(repository, "rev-parse", "HEAD");
            await Git(repository, "switch", "--detach", sha);
            Require(await GitStatus.Read(repository, CancellationToken.None) == new GitStatus(sha[..12], true), "Detached HEAD was not resolved from a commit.");
            Pass("git-reader-detached-head", passed);
            var worktree = Path.Combine(root, "linked worktree 日本語");
            await Git(repository, "worktree", "add", "-b", "linked/別作業", worktree, sha);
            Require(await GitStatus.Read(worktree, CancellationToken.None) == new GitStatus("linked/別作業", false), "Linked-worktree metadata was confused with the main checkout.");
            Pass("git-reader-linked-worktree", passed);
            var partial = Directory.CreateDirectory(Path.Combine(root, "missing-object checkout")).FullName;
            await Git(root, "init", partial);
            await Git(partial, "config", "core.repositoryformatversion", "1");
            await Git(partial, "config", "extensions.partialClone", "origin");
            await Git(partial, "config", "remote.origin.url", repository);
            await Git(partial, "config", "remote.origin.promisor", "true");
            await Git(partial, "config", "protocol.file.allow", "always");
            File.WriteAllText(Path.Combine(partial, ".git", "HEAD"), sha + "\n");
            var objects = Path.Combine(partial, ".git", "objects");
            Require(!Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories).Any(), "The missing-object fixture already contained objects.");
            var missing = await GitStatus.Read(partial, CancellationToken.None);
            Require(missing is null && !Directory.EnumerateFiles(objects, "*", SearchOption.AllDirectories).Any(),
                "Reading Git metadata fetched a missing commit from its owned local promisor.");
            Pass("git-reader-no-promisor-fetch-or-object-writes", passed);
            File.WriteAllText(Path.Combine(repository, ".git", "HEAD"), "not-a-valid-ref\n");
            Require(await GitStatus.Read(repository, CancellationToken.None) is null, "Malformed HEAD was presented as a verified branch.");
            Require(await GitStatus.Read(root, CancellationToken.None) is null
                && await GitStatus.Read(Path.Combine(root, "missing"), CancellationToken.None) is null, "Non-repository or missing directories acquired Git state.");
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            Require(await GitStatus.Read(worktree, canceled.Token) is null, "A canceled metadata request returned a branch.");
            Pass("git-reader-invalid-non-repository-and-cancellation", passed);
        }
        finally { DeleteFixture(root); }
    }

    private static async Task Integration(ShellLaunch source, string name, List<string> passed, string? artifacts)
    {
        var root = Directory.CreateTempSubdirectory("cmux-git-state-").FullName;
        var repository = Directory.CreateDirectory(Path.Combine(root, "project & 日本語")).FullName;
        var other = Directory.CreateDirectory(Path.Combine(root, "other project")).FullName;
        var history = Path.Combine(root, "history.txt").Replace("'", "''");
        var initialization = "Set-PSReadLineOption -HistorySaveStyle SaveNothing -HistorySavePath '" + history + "'; function global:prompt { 'CMUX_TEST_READY> ' }";
        var launch = source with { Directory = repository, Arguments = ShellLaunch.PowerShellArguments(initialization, noProfile: true) };
        var sessionPath = Path.Combine(root, "layout.json");
        using var window = new DesktopWindow(launch, new MemoryClipboard(), new SessionStore(sessionPath)) { ShowInTaskbar = false };
        var processes = new Dictionary<TerminalPane, Process>();
        var profiles = new HashSet<string>();
        void Track()
        {
            profiles.Add(window.ProfilePath);
            if (window.BrowserProfilePath is { } profile) profiles.Add(profile);
            foreach (var pane in window.Workspaces.SelectMany(workspace => workspace.Panes).OfType<TerminalPane>())
            {
                if (processes.ContainsKey(pane)) continue;
                var process = Process.GetProcessById(pane.ProcessId);
                _ = process.SafeHandle;
                processes.Add(pane, process);
            }
        }
        try
        {
            const string initial = "topic/名前&🍀";
            const string changed = "switch/現在&🍀";
            await Git(root, "init", "-b", initial, repository);
            await Commit(repository);
            await Git(root, "init", "-b", "other/別作業", other);
            window.Show();
            await window.Started.WaitAsync(TimeSpan.FromSeconds(30));
            Track();
            var first = (TerminalPane)window.Current!.Active!;
            await Expect(window, new(initial, false));
            first.FocusPane();
            await Program.Command(first, "Write-Output ('GIT_'+ 'READY')");
            await Program.Contains(first, "GIT_READY");
            var focusBefore = await first.Core.ExecuteScriptAsync("document.activeElement.classList.contains('xterm-helper-textarea')");
            Require(focusBefore == "true", "The owned terminal DOM focus precondition failed.");
            await Git(repository, "switch", "-c", changed);
            await Expect(window, new(changed, false));
            Require(window.Current.Active == first
                && await first.Core.ExecuteScriptAsync("document.activeElement.classList.contains('xterm-helper-textarea')") == "true",
                "Polling Git moved the selected pane or its owned DOM focus.");
            Pass("git-" + name + "-same-directory-branch-refresh-and-focus", passed);

            var sha = await Git(repository, "rev-parse", "HEAD");
            await Git(repository, "switch", "--detach", sha);
            await Expect(window, new(sha[..12], true));
            await Git(repository, "switch", changed);
            await Expect(window, new(changed, false));
            VerifyLiteralRendering(window, new Strings().Format("GitBranch", changed));
            Pass("git-" + name + "-literal-native-label-rendering", passed);
            if (artifacts is not null)
            {
                Directory.CreateDirectory(artifacts);
                var strip = window.Controls.OfType<StatusStrip>().Single();
                using var image = new Bitmap(strip.Width, strip.Height);
                strip.DrawToBitmap(image, new Rectangle(Point.Empty, image.Size));
                image.Save(Path.Combine(artifacts, "git-status-" + name + ".png"));
            }
            Pass("git-" + name + "-detached-and-branch-return", passed);

            await window.ClickAction("SplitRight");
            Track();
            var second = (TerminalPane)window.Current.Active!;
            await Change(second, other);
            await Expect(window, new("other/別作業", false));
            first.FocusPane();
            await Expect(window, new(changed, false));
            second.FocusPane();
            await Expect(window, new("other/別作業", false));
            await window.ClickAction("OpenBrowser");
            Track();
            Require(Status(window) == "", "A browser retained terminal Git metadata.");
            await window.ClickAction("ClosePane");
            first.FocusPane();
            await Expect(window, new(changed, false));
            Pass("git-" + name + "-split-and-browser-isolation", passed);

            await window.ClickAction("NewWorkspace");
            Track();
            var third = (TerminalPane)window.Current!.Active!;
            await Change(third, other);
            await Expect(window, new("other/別作業", false));
            for (var i = 0; i < 8; i++) { window.SelectWorkspace(0); window.SelectWorkspace(1); }
            window.SelectWorkspace(0);
            first.FocusPane();
            await Expect(window, new(changed, false));
            await Program.Command(first, "Write-Output ('GIT_AFTER_'+ 'SWITCH')");
            await Program.Contains(first, "GIT_AFTER_SWITCH");
            Pass("git-" + name + "-workspace-switch-and-live-shell", passed);

            var headPath = Path.Combine(repository, ".git", "HEAD");
            var head = File.ReadAllText(headPath);
            File.WriteAllText(headPath, "malformed-head\n");
            await Program.Until(() => Task.FromResult(Status(window) == ""), "Unreadable Git state retained a stale branch label.");
            File.WriteAllText(headPath, head);
            await Expect(window, new(changed, false));
            await Change(first, root);
            await Program.Until(() => Task.FromResult(Status(window) == ""), "Leaving the repository retained its branch.");
            await Change(first, repository);
            await Expect(window, new(changed, false));
            Pass("git-" + name + "-invalid-head-and-non-repository-clear", passed);

            await window.ClickAction("SaveSession");
            var saved = File.ReadAllText(sessionPath);
            Require(!saved.Contains(changed, StringComparison.Ordinal) && !saved.Contains("\"Git", StringComparison.Ordinal), "Git display expanded persisted state.");
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.FormClosed += (_, _) => closed.TrySetResult();
            window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(30));
            Require(processes.Values.All(process => process.WaitForExit(0)), "An original Git fixture shell survived normal close.");
            Pass("git-" + name + "-display-only-storage-and-normal-close", passed);
        }
        finally
        {
            await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(30));
            foreach (var process in processes.Values) { Require(process.WaitForExit(0), "An original Git fixture process survived cleanup."); process.Dispose(); }
            foreach (var profile in profiles) Require(!Directory.Exists(profile), "A Git fixture profile survived cleanup.");
            DeleteFixture(root);
        }
    }

    private static async Task Change(TerminalPane pane, string directory)
    {
        await Program.Command(pane, "Set-Location -LiteralPath '" + directory.Replace("'", "''") + "'");
        await Program.Until(async () => await pane.CaptureWorkingDirectory() == directory, "The native Git fixture did not receive its real CWD.");
    }

    private static Task Expect(DesktopWindow window, GitStatus state)
    {
        var expected = new Strings().Format(state.Detached ? "GitDetached" : "GitBranch", state.Value);
        return Program.Until(() => Task.FromResult(Status(window) == expected), "The selected pane did not display Git state: " + expected);
    }

    private static Task<string> Commit(string directory) => Git(directory, "-c", "user.name=Synthetic Cmux", "-c", "user.email=cmux-test@example.invalid", "-c", "commit.gpgSign=false", "commit", "--allow-empty", "-m", "Synthetic fixture");

    private static void VerifyLiteralRendering(DesktopWindow window, string expected)
    {
        var strip = window.Controls.OfType<StatusStrip>().Single();
        var label = strip.Items["GitBranch"]!;
        var original = label.Text;
        Bitmap Capture()
        {
            strip.PerformLayout();
            var bitmap = new Bitmap(strip.Width, strip.Height);
            strip.DrawToBitmap(bitmap, new Rectangle(Point.Empty, bitmap.Size));
            return bitmap;
        }
        using var actual = Capture();
        try
        {
            // A verified escaped native control is the literal-pixel oracle.
            // RenderItemText is raised after drawing and cannot prove pixels.
            label.Text = expected.Replace("&", "&&", StringComparison.Ordinal);
            using var literal = Capture();
            for (var y = 0; y < actual.Height; y++)
                for (var x = 0; x < actual.Width; x++)
                    Require(actual.GetPixel(x, y) == literal.GetPixel(x, y), "The actual native Git pixels do not match literal ampersands.");
        }
        finally { label.Text = original; }
        Console.WriteLine("git-label-pixels: literal control matched " + expected);
    }

    private static string Status(DesktopWindow window) =>
        window.Controls.OfType<StatusStrip>().Single().Items["GitBranch"]?.ToolTipText ?? "";

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
