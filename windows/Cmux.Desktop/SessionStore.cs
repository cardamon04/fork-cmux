using System.Text.Json;
using System.Text.Json.Serialization;

namespace Cmux.Desktop;

// A lifetime lease makes a second app instance read-only for this session path.
// File I/O runs outside the UI thread; callers serialize load/save/dispose.
internal sealed class SessionStore(string path) : IDisposable
{
    private static readonly JsonSerializerOptions Coding = new()
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        MaxDepth = 64,
        WriteIndented = true,
    };
    private readonly string path = Path.GetFullPath(path);
    private FileStream? lease;
    internal bool CanSave { get; private set; }
    internal void ProtectExisting() => CanSave = false;
    internal static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "cmux-windows", "session.json");

    internal SessionSnapshot? Open()
    {
        if (lease is not null) throw new InvalidOperationException();
        EnsurePaths();
        lease = new FileStream(path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
        SessionSnapshot? snapshot = null;
        if (File.Exists(path))
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            var buffer = new byte[SessionSnapshot.MaxBytes + 1];
            var count = 0;
            int read;
            while (count < buffer.Length && (read = stream.Read(buffer, count, buffer.Length - count)) != 0) count += read;
            if (count > SessionSnapshot.MaxBytes) throw new InvalidDataException();
            using (var document = JsonDocument.Parse(buffer.AsMemory(0, count), new JsonDocumentOptions { MaxDepth = 64 }))
                RejectDuplicateKeys(document.RootElement);
            snapshot = JsonSerializer.Deserialize<SessionSnapshot>(buffer.AsSpan(0, count), Coding) ?? throw new InvalidDataException();
            snapshot.Validate();
        }
        CanSave = true;
        return snapshot;
    }

    private static void RejectDuplicateKeys(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in value.EnumerateObject())
            {
                if (!names.Add(property.Name)) throw new InvalidDataException();
                RejectDuplicateKeys(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array)
            foreach (var item in value.EnumerateArray()) RejectDuplicateKeys(item);
    }

    internal void Save(SessionSnapshot snapshot)
    {
        if (!CanSave || lease is null) throw new InvalidOperationException();
        snapshot.Validate();
        var bytes = JsonSerializer.SerializeToUtf8Bytes(snapshot, Coding);
        if (bytes.Length > SessionSnapshot.MaxBytes) throw new InvalidDataException();
        EnsurePaths();
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            using (var output = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                output.Write(bytes);
                output.Flush(flushToDisk: true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    private void EnsurePaths()
    {
        if (!IsLocalPath(path)) throw new IOException();
        var directory = Path.GetDirectoryName(path)!;
        CheckNoReparsePoints(directory);
        System.IO.Directory.CreateDirectory(directory);
        CheckNoReparsePoints(directory);
        foreach (var file in new[] { path, path + ".lock" }) CheckEntry(file);
    }

    internal static bool IsLocalPath(string? value) => value is { Length: >= 3 and <= 32767 }
        && char.IsAsciiLetter(value[0]) && value[1] == ':' && value[2] == '\\'
        && !value.Any(char.IsControl) && !value.AsSpan(2).Contains(':');

    private static void CheckNoReparsePoints(string path)
    {
        var full = Path.GetFullPath(path);
        if (!IsLocalPath(full)) throw new IOException();
        if (new DriveInfo(full[..3]).DriveType is DriveType.Network or DriveType.Unknown or DriveType.NoRootDirectory)
            throw new IOException();
        var current = full[..3];
        foreach (var part in full[3..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            current = Path.Combine(current, part);
            CheckEntry(current);
        }
    }

    private static void CheckEntry(string path)
    {
        try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException(); }
        catch (FileNotFoundException) { }
        catch (DirectoryNotFoundException) { }
    }

    internal static bool IsExistingLocalDirectory(string directory)
    {
        if (!IsLocalPath(directory)) return false;
        try { CheckNoReparsePoints(directory); return System.IO.Directory.Exists(directory); }
        catch (IOException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
        catch (ArgumentException) { return false; }
    }

    public void Dispose() { CanSave = false; lease?.Dispose(); lease = null; }
}
