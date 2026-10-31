using System.Text.Json;
using Cmux.Desktop;

internal static class SessionStoreScenario
{
    internal static void Run(List<string> passed)
    {
        var directory = Directory.CreateTempSubdirectory("cmux-session-store-").FullName;
        var path = Path.Combine(directory, "owned", "session.json");
        var sample = new SessionSnapshot(1, 4, 0, [new("Workspace 4", 0, new("terminal", Directory: directory))]);
        void Pass(string name) { passed.Add(name); Console.WriteLine("passed: " + name); }
        void Require(bool value, string message) { if (!value) throw new InvalidOperationException(message); }
        void Rejected(string json)
        {
            File.WriteAllText(path, json);
            using (var store = new SessionStore(path))
            {
                try { store.Open(); throw new Exception("Unsafe session was accepted."); }
                catch (Exception error) when (error is InvalidDataException or JsonException) { }
                Require(!store.CanSave, "Rejected session remained writable.");
            }
            Require(File.ReadAllText(path) == json, "Rejected data was overwritten.");
        }
        try
        {
            using (var store = new SessionStore(path))
            {
                Require(store.Open() is null && store.CanSave, "Fresh session was not writable.");
                store.Save(sample);
                using (var contender = new SessionStore(path))
                {
                    try { contender.Open(); throw new Exception("A second writer acquired the lease."); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                    Require(!contender.CanSave, "A competing writer can save.");
                }
                Pass("session-single-writer-lease");
                var before = File.ReadAllBytes(path);
                using (var block = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                {
                    try { store.Save(sample with { WorkspaceSequence = 5 }); throw new Exception("Replacement ignored the file lock."); }
                    catch (Exception error) when (error is IOException or UnauthorizedAccessException) { }
                }
                Require(before.SequenceEqual(File.ReadAllBytes(path)), "Failed replacement damaged the last save.");
                Require(!Directory.EnumerateFiles(Path.GetDirectoryName(path)!, "*.tmp").Any(), "Owned temp file leaked.");
                store.Save(sample with { WorkspaceSequence = 5 });
                Pass("session-atomic-replacement-preserves-last-save");
            }
            using (var reopened = new SessionStore(path))
                Require(reopened.Open()?.WorkspaceSequence == 5, "Committed session did not survive reopening.");
            Pass("session-versioned-file-roundtrip");
            Rejected("{broken");
            Rejected(JsonSerializer.Serialize(sample with { Version = 2 }));
            Rejected(JsonSerializer.Serialize(sample).Replace("\"Version\":1", "\"Version\":2,\"Version\":1"));
            Rejected(JsonSerializer.Serialize(sample).Replace("\"Version\":1", "\"Version\":1,\"Command\":\"DO_NOT_EXECUTE\""));
            Pass("session-invalid-and-future-data-protected");
            foreach (var node in new[]
            {
                new LayoutSnapshot("terminal", Directory: @"\\server\share"),
                new LayoutSnapshot("browser", Url: "file:///C:/private.txt"),
                new LayoutSnapshot("browser", Url: "https://name:secret@example.invalid/"),
                new LayoutSnapshot("split", Orientation: "vertical", Ratio: .5, First: new("browser", Url: "about:blank")),
            }) Rejected(JsonSerializer.Serialize(sample with { Workspaces = [new("Unsafe", 0, node)] }));
            Pass("session-invalid-layout-and-native-boundaries");
            Rejected(new string(' ', SessionSnapshot.MaxBytes + 1));
            var deep = new LayoutSnapshot("terminal", Directory: directory);
            for (var i = 0; i < 18; i++) deep = new("split", Orientation: "vertical", Ratio: .5, First: deep, Second: new("browser", Url: "about:blank"));
            Rejected(JsonSerializer.Serialize(sample with { Workspaces = [new("Deep", 0, deep)] }));
            Rejected(JsonSerializer.Serialize(sample with { Workspaces = Enumerable.Repeat(sample.Workspaces[0], 65).ToArray() }));
            Pass("session-bounded-bytes-tree-and-resource-count");
            File.Delete(path);
            using (var empty = new SessionStore(path))
            {
                empty.Open();
                empty.Save(new(1, 5, -1, []));
            }
            using (var reopened = new SessionStore(path)) Require(reopened.Open()?.Workspaces.Length == 0, "Empty layout did not roundtrip.");
            Pass("session-empty-layout-roundtrip");
        }
        finally { Directory.Delete(directory, recursive: true); }
    }
}
