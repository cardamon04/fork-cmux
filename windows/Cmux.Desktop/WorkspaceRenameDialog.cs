using System.Globalization;

namespace Cmux.Desktop;

internal sealed class WorkspaceRenameDialog : Form
{
    private readonly TextBox name = new() { Name = "WorkspaceName", Dock = DockStyle.Fill, MaxLength = WorkspaceTitle.MaxCharacters + 1 };
    internal string WorkspaceName => name.Text;

    internal WorkspaceRenameDialog(Strings strings, string current)
    {
        Name = "WorkspaceRenameDialog";
        Text = strings["RenameWorkspace"];
        Font = new Font("Segoe UI", 10);
        ClientSize = new(560, 190);
        FormBorderStyle = FormBorderStyle.FixedDialog;
        MaximizeBox = MinimizeBox = ShowInTaskbar = false;
        StartPosition = FormStartPosition.CenterParent;
        if (CultureInfo.CurrentUICulture.TextInfo.IsRightToLeft)
        {
            RightToLeft = RightToLeft.Yes;
            RightToLeftLayout = true;
        }
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, Padding = new Padding(12), ColumnCount = 1, RowCount = 4 };
        foreach (var height in new[] { 26, 32, 60, 40 }) layout.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        layout.Controls.Add(new Label { Text = strings["WorkspaceName"], AutoSize = true, UseMnemonic = false }, 0, 0);
        name.AccessibleName = strings["WorkspaceName"];
        var error = new Label { Name = "Validation", Dock = DockStyle.Fill, ForeColor = Color.Firebrick, UseMnemonic = false };
        var confirm = new Button { Name = "Confirm", Text = strings["Rename"], AutoSize = true, DialogResult = DialogResult.OK };
        var cancel = new Button { Name = "Cancel", Text = strings["Cancel"], AutoSize = true, DialogResult = DialogResult.Cancel };
        var buttons = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.RightToLeft };
        buttons.Controls.Add(cancel);
        buttons.Controls.Add(confirm);
        layout.Controls.Add(name, 0, 1);
        layout.Controls.Add(error, 0, 2);
        layout.Controls.Add(buttons, 0, 3);
        Controls.Add(layout);
        AcceptButton = confirm;
        CancelButton = cancel;
        void ValidateName()
        {
            confirm.Enabled = WorkspaceTitle.IsValidRename(name.Text);
            error.Text = confirm.Enabled ? "" : strings.Format("WorkspaceNameInvalid", WorkspaceTitle.MaxCharacters);
        }
        name.TextChanged += (_, _) => ValidateName();
        name.Text = current;
        ValidateName();
        Shown += (_, _) => { name.Focus(); name.SelectAll(); };
    }
}
