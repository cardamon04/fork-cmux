using System.Text.Json;
using Cmux.Desktop;

// Real SessionStore/DesktopWindow restoration on the isolated native CI runner.
internal static class LayoutExtentScenario
{
    internal static async Task Run(ShellLaunch launch, List<string> passed)
    {
        var errors = new List<Exception>();
        foreach (var orientation in new[] { "vertical", "horizontal" })
            foreach (var side in new[] { "first", "second" })
                try { await Restore(launch, orientation, side, passed); }
                catch (Exception error) { errors.Add(error); }
        if (errors.Count != 0) throw new AggregateException("Small-extent saved layout regressions failed.", errors);
    }

    private static async Task Restore(ShellLaunch launch, string orientation, string side, List<string> passed)
    {
        var directory = Directory.CreateTempSubdirectory("cmux-layout-extent-").FullName;
        var path = Path.Combine(directory, "session.json");
        const double desired = .37;
        LayoutSnapshot Leaf() => new("browser", Url: "about:blank");
        var inner = new LayoutSnapshot("split", Orientation: orientation, Ratio: desired, First: Leaf(), Second: Leaf());
        var root = new LayoutSnapshot("split", Orientation: orientation, Ratio: side == "first" ? 0 : 1,
            First: side == "first" ? inner : Leaf(), Second: side == "second" ? inner : Leaf());
        var saved = new SessionSnapshot(1, 1, 0, [new("Collapsed owned layout", 1, root)]);
        saved.Validate();
        using (var store = new SessionStore(path)) { store.Open(); store.Save(saved); }
        var checkpoint = File.ReadAllBytes(path);
        using var window = new DesktopWindow(launch, new MemoryClipboard(), new SessionStore(path)) { ShowInTaskbar = false };
        var ratioErrors = new List<string>();
        void RatioFailure(object? _, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (ratioErrors.Count < 8 && e.Exception is ArgumentOutOfRangeException
                && e.Exception.StackTrace?.Contains("ApplyRestoredRatios", StringComparison.Ordinal) == true)
                ratioErrors.Add(e.Exception.GetType().Name + ": " + e.Exception.Message);
        }
        var phase = "restore-startup";
        try
        {
            Console.WriteLine("LAYOUT_EXTENT_PRECONDITION " + JsonSerializer.Serialize(new
            {
                orientation, side, validSavedGraph = true, leaves = 3, depth = 2,
                outerRatio = root.Ratio, innerRatio = desired, activePane = 1, configuredBrowserPanes = 3,
            }));
            AppDomain.CurrentDomain.FirstChanceException += RatioFailure;
            try { window.Show(); await window.Started.WaitAsync(TimeSpan.FromSeconds(30)); }
            finally { AppDomain.CurrentDomain.FirstChanceException -= RatioFailure; }
            var workspace = window.Current!;
            Console.WriteLine("LAYOUT_EXTENT_STARTUP " + JsonSerializer.Serialize(new
            {
                orientation, side, window.SessionStatus, window.LastError,
                workspaces = window.Workspaces.Count(), paneKinds = workspace.Panes.Select(pane => pane.Kind.ToString()).ToArray(),
                rootControl = workspace.Controls.Count == 0 ? "empty" : workspace.Controls[0].GetType().Name,
                canSave = window.IsActionEnabled("SaveSession"), ratioErrors,
            }));
            Require(ratioErrors.Count == 0 && window.SessionStatus == new Strings()["SessionRestored"]
                && workspace.Panes.Count == 3 && workspace.Panes.All(pane => pane is BrowserPane),
                "A valid saved collapsed graph was replaced during native restoration.");
            var outer = (SplitContainer)workspace.Controls[0];
            var nested = (SplitContainer)(side == "first" ? outer.Panel1 : outer.Panel2).Controls[0];
            var panes = workspace.Panes.OfType<BrowserPane>().ToArray();
            Require(window.LastError is null && window.Workspaces.Count() == 1 && panes.Length == 3
                && workspace.Active == workspace.Panes[1], "Restore lost the valid pane graph or saved selection.");
            Pass(orientation, side, "valid-collapsed-layout-starts", passed);

            phase = "collapsed-checkpoint";
            var extent = orientation == "vertical" ? nested.ClientSize.Width : nested.ClientSize.Height;
            Console.WriteLine("LAYOUT_EXTENT_RESTORED " + JsonSerializer.Serialize(new
            {
                orientation, side, extent, nested.SplitterWidth, nested.SplitterDistance,
                pending = workspace.PendingRatios.ContainsKey(nested), ratio = workspace.SplitRatio(nested),
            }));
            Require(extent <= nested.SplitterWidth && workspace.PendingRatios.ContainsKey(nested)
                && workspace.SplitRatio(nested) == desired && checkpoint.SequenceEqual(File.ReadAllBytes(path)),
                "The unavailable extent consumed its saved ratio or damaged the saved checkpoint.");
            window.CaptureSession().Validate();
            Pass(orientation, side, "unavailable-extent-retains-saved-ratio", passed);

            var cores = panes.Select(pane => pane.Core).ToArray();
            phase = "expand";
            var available = (orientation == "vertical" ? outer.ClientSize.Width : outer.ClientSize.Height) - outer.SplitterWidth;
            outer.SplitterDistance = available / 2;
            await Program.Until(() => Task.FromResult(!workspace.PendingRatios.ContainsKey(nested)), "Growing the owned split did not project its deferred ratio.");
            Require(Math.Abs(workspace.SplitRatio(nested) - desired) < .02
                && panes.Select(pane => pane.Core).SequenceEqual(cores) && workspace.Active == workspace.Panes[1],
                "Growing the split lost the saved proportion, browser identity or selection.");
            Pass(orientation, side, "grow-applies-ratio-without-restart", passed);

            phase = "save-close-readback";
            var snapshot = window.CaptureSession(); snapshot.Validate();
            var expected = JsonSerializer.Serialize(snapshot);
            await window.ClickAction("SaveSession");
            Require(window.LastError is null && JsonSerializer.Serialize(JsonSerializer.Deserialize<SessionSnapshot>(File.ReadAllText(path))) == expected,
                "The recovered layout did not save through the unchanged format.");
            var profiles = new[] { window.ProfilePath, window.BrowserProfilePath! };
            var closed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            window.FormClosed += (_, _) => closed.TrySetResult(); window.Close();
            await closed.Task.WaitAsync(TimeSpan.FromSeconds(45));
            Require(panes.All(pane => pane.IsDisposed) && profiles.All(profile => !Directory.Exists(profile)), "The owned restoration retained a browser or profile after normal close.");
            using var reopened = new SessionStore(path);
            var readback = reopened.Open()!; readback.Validate();
            Require(readback.Version == 1 && JsonSerializer.Serialize(readback) == expected, "Normal close/store readback changed the recovered layout.");
            Pass(orientation, side, "save-normal-close-store-readback", passed);
        }
        catch (Exception error)
        {
            Console.WriteLine("LAYOUT_EXTENT_FAILURE " + JsonSerializer.Serialize(new
            {
                orientation, side, phase, error = error.Message, type = error.GetType().Name,
                checkpointUnchanged = checkpoint.SequenceEqual(File.ReadAllBytes(path)), ratioErrors,
            }));
            throw;
        }
        finally
        {
            if (!window.IsDisposed) { await window.Shutdown().WaitAsync(TimeSpan.FromSeconds(45)); window.Dispose(); }
            var full = Path.GetFullPath(directory);
            Require(Path.GetDirectoryName(full)!.Equals(Path.TrimEndingDirectorySeparator(Path.GetTempPath()), StringComparison.OrdinalIgnoreCase)
                && Path.GetFileName(full).StartsWith("cmux-layout-extent-", StringComparison.Ordinal), "Refusing to remove an unowned layout-extent fixture.");
            Directory.Delete(full, recursive: true);
        }
    }
    private static void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
    private static void Pass(string orientation, string side, string name, List<string> passed)
    {
        var value = "layout-extent-" + orientation + "-" + side + "-" + name;
        passed.Add(value); Console.WriteLine("passed: " + value);
    }
}
