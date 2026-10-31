namespace Cmux.Desktop;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new DesktopWindow(ShellLaunch.Default(), session: new SessionStore(SessionStore.DefaultPath)));
    }
}
