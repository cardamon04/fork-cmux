# cmux Windows desktop

This application runs interactive PowerShell terminals in a workspace sidebar with resizable horizontal and vertical splits. Select a pane to send input, use **Copy** with a terminal selection, or use **Paste** to send clipboard text to the selected pane. Closing a pane terminates its owned ConPTY process; closing the last pane removes that workspace. Closing the window ends all application-owned sessions.

The application is an early implementation of F-01/F-02, not a complete Windows port. Browser panels, workspace persistence, WSL/SSH integration, notifications, CLI/API, Ghostty settings and image protocols, configurable shortcuts, cloud/iPhone integration, installation and updates remain incomplete. IME composition and a full working day with real agents are not yet verified. There is no limit of ten panes; ten is the initial concurrent terminal test workload.

## Run

Prerequisites: Windows, .NET SDK 10, Node.js 24, and the Microsoft Edge WebView2 Runtime. The app uses `pwsh.exe` from PATH if present and otherwise Windows PowerShell. It starts in the launch directory and loads the normal shell profile. It does not install a runtime, change Windows settings or require elevation.

```powershell
npm ci --ignore-scripts --prefix windows/Cmux.Desktop
dotnet restore windows/Cmux.Desktop --locked-mode
dotnet run --project windows/Cmux.Desktop --no-restore
```

The renderer packages and WebView2 SDK are pinned with integrity lockfiles. No terminal assets come from a CDN at runtime. Assets are served only to `https://cmux-terminal.invalid`; external and frame navigation, popups, downloads and browser permissions are blocked in the terminal controller. Terminal output passes as data to xterm.js and never enters an HTML or JavaScript template. Clipboard access occurs through the native toolbar actions.

Temporary WebView2 profiles belong to this app instance and are removed after controllers and the browser process close. Existing browser profiles are not imported. Ambient profile-folder overrides and machine-policy redirects are refused. Workspaces and terminal scrollback are not persisted between launches yet.

## Test

```powershell
dotnet restore windows/Cmux.Desktop.Tests --locked-mode
dotnet build windows/Cmux.Desktop.Tests --no-restore --nologo -warnaserror
dotnet run --project windows/Cmux.Desktop.Tests --no-build -- desktop-results
dotnet run --project Prototypes/WindowsTerminal -- --self-test (Get-Command pwsh).Source
```

The desktop tests show their own application window and exercise toolbar actions, trusted Chromium keyboard/mouse input, selection and Copy, Ctrl+C, real interactive PowerShell/PSReadLine, Unicode, SGR rendering, resize propagation, pane/workspace isolation, teardown during output and ten live terminals. They use a temporary working directory and prompt, disable PSReadLine history persistence and point its history path to that directory, use `-NoProfile`, and inject an in-memory clipboard. They do not read or overwrite the user's clipboard. `terminal.png` captures only the application-owned renderer; it is not a screenshot of other desktop windows or proof of IME behavior.

The focused `Cmux.Desktop.Tests.exe --packaging-and-locales` check verifies regional/script fallback, localized profile refusal, supported catalogs and the actual distributed WebView2 notice. It is also part of the complete desktop suite.

The UI catalog contains English, Japanese, German, French, Arabic, Spanish, Traditional/Simplified Chinese and Korean. The current Windows UI language selects a supported catalog, with English fallback. The terminal content follows the shell and font behavior.

## Boundaries and follow-up

`Cmux.Terminal` owns the ConPTY transport previously exercised by `Prototypes/WindowsTerminal`; both the application and those existing tests reference the same library. `TerminalConnection` serializes transport operations. `TerminalPane` owns its controller and the bounded output queue, acknowledging xterm parser completion before reading more output. `DesktopWindow` owns workspace/pane actions and native controls. Toolbar actions share these same methods with the integration tests.

WinForms is a thin Windows host here, and xterm.js is an initial VT renderer. This choice allows an interactive application using the verified transport and WebView2 stack. It does not establish Ghostty feature equivalence. Compare Ghostty settings, Unicode widths, image protocols, mouse modes, IME, accessibility and long-running workloads before choosing the final rendering strategy. Candidate provenance and differences remain in [the architecture comparison](../docs/windows-architecture-comparison.md).

The xterm renderer and addons retain their MIT license files in `Assets`. The WebView2 SDK's Microsoft BSD notice is copied from the restored package to `Microsoft.Web.WebView2.LICENSE.txt` in build and publish output. See [xterm.js](https://github.com/xtermjs/xterm.js) and the official [security](https://xtermjs.org/docs/guides/security/) and [flow control](https://xtermjs.org/docs/guides/flowcontrol/) guidance. WebView2 hosting follows the [Microsoft origin and message security guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security).
