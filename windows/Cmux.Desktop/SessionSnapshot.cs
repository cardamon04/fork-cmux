namespace Cmux.Desktop;

internal sealed record SessionSnapshot(int Version, int WorkspaceSequence, int SelectedWorkspace, WorkspaceSnapshot[] Workspaces)
{
    internal const int CurrentVersion = 1;
    internal const int MaxLayoutDepth = 16;
    internal const int MaxBytes = 1024 * 1024;

    internal void Validate()
    {
        if (Version != CurrentVersion || WorkspaceSequence < 0 || WorkspaceSequence == int.MaxValue
            || Workspaces is null || Workspaces.Length > 64 || WorkspaceSequence < Workspaces.Length
            || (Workspaces.Length == 0 ? SelectedWorkspace != -1 : SelectedWorkspace < 0 || SelectedWorkspace >= Workspaces.Length))
            throw new InvalidDataException();
        var total = 0;
        foreach (var workspace in Workspaces)
        {
            if (workspace is null || !WorkspaceTitle.IsValidStored(workspace.Title)) throw new InvalidDataException();
            var leaves = ValidateNode(workspace.Layout, 0, ref total);
            if (workspace.ActivePane < 0 || workspace.ActivePane >= leaves) throw new InvalidDataException();
        }
    }

    private static int ValidateNode(LayoutSnapshot? node, int depth, ref int total)
    {
        if (node is null || depth > MaxLayoutDepth) throw new InvalidDataException();
        if (node.Kind == "split")
        {
            if (node.Directory is not null || node.Url is not null || node.Orientation is not ("vertical" or "horizontal")
                || node.Ratio is not { } ratio || !double.IsFinite(ratio) || ratio < 0 || ratio > 1)
                throw new InvalidDataException();
            return ValidateNode(node.First, depth + 1, ref total) + ValidateNode(node.Second, depth + 1, ref total);
        }
        if (node.First is not null || node.Second is not null || node.Ratio is not null || node.Orientation is not null
            || ++total > 256) throw new InvalidDataException();
        if (node.Kind == "terminal")
        {
            if (node.Url is not null || !SessionStore.IsLocalPath(node.Directory)) throw new InvalidDataException();
        }
        else if (node.Kind == "browser")
        {
            if (node.Directory is not null || !IsBrowserAddress(node.Url)) throw new InvalidDataException();
        }
        else throw new InvalidDataException();
        return 1;
    }

    internal static bool IsBrowserAddress(string? address) => address == "about:blank"
        || address is { Length: > 0 and <= 32768 } && !address.Any(char.IsControl)
            && Uri.TryCreate(address, UriKind.Absolute, out var uri) && uri.Scheme is "http" or "https"
            && uri.Host.Length != 0 && uri.UserInfo.Length == 0;
}

internal sealed record WorkspaceSnapshot(string Title, int ActivePane, LayoutSnapshot Layout);
internal sealed record LayoutSnapshot(string Kind, string? Directory = null, string? Url = null,
    string? Orientation = null, double? Ratio = null, LayoutSnapshot? First = null, LayoutSnapshot? Second = null);
