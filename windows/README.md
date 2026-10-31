# cmux Windows desktop

This application runs interactive PowerShell terminals in a workspace sidebar with resizable horizontal and vertical splits. Select a pane to send input, use **Copy** with a terminal selection, or use **Paste** to send clipboard text to the selected pane. Closing a pane terminates its owned ConPTY process; closing the last pane removes that workspace. Closing the window ends all application-owned sessions.

**Open browser** creates a browser beside the selected pane, or a browser workspace when none remains. Enter an HTTP/HTTPS URL and use **Go** or Enter, **Back**, **Forward**, **Reload** and **Developer tools**. A bare website name defaults to HTTPS; localhost and loopback addresses default to HTTP. Browser panes retain their history when switching workspaces and share a browser profile within this application instance. The terminal's Copy/Paste toolbar actions are disabled while a browser is selected; browser keyboard/context-menu operations belong to the web control.

The application implements parts of F-01/F-02/F-06, not a complete Windows port. Persistent browser profiles, browser-data import, downloads, popup/new-window handling, permission grants, browser automation CLI/API, workspace persistence, WSL/SSH integration, notifications, Ghostty settings and image protocols, configurable shortcuts, cloud/iPhone integration, installation and updates remain incomplete. IME composition and a full working day with real agents are not yet verified. There is no limit of ten panes; ten is the initial concurrent terminal test workload.

## Run

Prerequisites: Windows, .NET SDK 10, Node.js 24, and the Microsoft Edge WebView2 Runtime. The app uses `pwsh.exe` from PATH if present and otherwise Windows PowerShell. It starts in the launch directory and loads the normal shell profile. It does not install a runtime, change Windows settings or require elevation.

```powershell
npm ci --ignore-scripts --prefix windows/Cmux.Desktop
dotnet restore windows/Cmux.Desktop --locked-mode
dotnet run --project windows/Cmux.Desktop --no-restore
```

The renderer packages and WebView2 SDK are pinned with integrity lockfiles. No terminal assets come from a CDN at runtime. Assets are served only to `https://cmux-terminal.invalid`; external and frame navigation, popups, downloads and browser permissions are blocked in the terminal controller. Terminal output passes as data to xterm.js and never enters an HTML or JavaScript template. Clipboard access occurs through the native toolbar actions.

Temporary WebView2 profiles belong to this app instance and are removed after controllers and the browser process close. Existing browser profiles are not imported. Ambient profile-folder overrides and machine-policy redirects are refused. Workspaces and terminal scrollback are not persisted between launches yet.

Web browsing uses a separate profile from the fixed-origin terminal renderer. Web pages have no WebView native message channel or host objects. Top-level browsing accepts HTTP/HTTPS and a blank page; file URLs and external application schemes are blocked. Popups, downloads and permission requests currently show localized notices and are declined. Browser data belongs to the temporary browser profile and is removed on application exit. The app does not open a remote debugging TCP port.

Opening a browser preserves a pane selected while startup is pending. A failed browser process shows a local recovery message; workspace switching, resizing and healthy terminals remain usable. Close the failed pane and open a browser again to recover. The app does not automatically reload or resubmit a failed page.

## Test

```powershell
dotnet restore windows/Cmux.Desktop.Tests --locked-mode
dotnet build windows/Cmux.Desktop.Tests --no-restore --nologo -warnaserror
dotnet run --project windows/Cmux.Desktop.Tests --no-build -- desktop-results
dotnet run --project Prototypes/WindowsTerminal -- --self-test (Get-Command pwsh).Source
```

The desktop tests show their own application window and exercise toolbar actions, trusted Chromium keyboard/mouse input, selection and Copy, Ctrl+C, real interactive PowerShell/PSReadLine, Unicode, SGR rendering, resize propagation, pane/workspace isolation, teardown during output and ten live terminals. They use a temporary working directory and prompt, disable PSReadLine history persistence and point its history path to that directory, use `-NoProfile`, and inject an in-memory clipboard. They do not read or overwrite the user's clipboard. `terminal.png` captures only the application-owned renderer; it is not a screenshot of other desktop windows or proof of IME behavior.

Browser tests use a private loopback HTTP fixture and the real browser toolbar, page input/clicks, history/reload, native inspector window, same-origin cookies across separate profiles, host-message denial, workspace switching, closing during an unfinished load and reopening. `--browser-only [artifact-directory]` runs this focused workflow. No public website or real account is used. `browser.png` is a renderer capture; `desktop.png`, when supported by Windows PrintWindow, captures only this test application's window. These images do not establish IME or full browser parity.

`--browser-failures` runs the startup-selection and fatal-process regressions. It deliberately crashes only a test-owned browser after checking its PID and profile differ from the terminal's, then verifies UI dispatch, healthy shell output, recreation and profile cleanup. These checks also run in the full suite.

The focused `Cmux.Desktop.Tests.exe --packaging-and-locales` check verifies regional/script fallback, localized profile refusal, supported catalogs and the actual distributed WebView2 notice. It is also part of the complete desktop suite.

`Cmux.Desktop.Tests.exe --paste-limit` verifies that a permitted 32,768-character clipboard paste still reaches the shell when bracketed paste adds its protocol envelope. It uses a private clipboard fixture and leaves the pasted marker unexecuted, then closes that owned terminal. This check is also in the full suite.

The UI catalog contains English, Japanese, German, French, Arabic, Spanish, Traditional/Simplified Chinese and Korean. The current Windows UI language selects a supported catalog, with English fallback. The terminal content follows the shell and font behavior.

## Boundaries and follow-up

`Cmux.Terminal` owns the ConPTY transport previously exercised by `Prototypes/WindowsTerminal`; both the application and those existing tests reference the same library. `TerminalConnection` serializes transport operations. `WorkspacePane` defines the shared pane lifecycle; `TerminalPane` owns the trusted renderer/output queue and `BrowserPane` owns untrusted document navigation. `WebViewProfile` owns each isolated profile/browser lifetime. `DesktopWindow` owns mixed workspace/pane actions and native controls. Toolbar actions share these same methods with the integration tests.

WinForms is a thin Windows host here, and xterm.js is an initial VT renderer. This choice allows an interactive application using the verified transport and WebView2 stack. It does not establish Ghostty feature equivalence. Compare Ghostty settings, Unicode widths, image protocols, mouse modes, IME, accessibility and long-running workloads before choosing the final rendering strategy. Candidate provenance and differences remain in [the architecture comparison](../docs/windows-architecture-comparison.md).

The xterm renderer and addons retain their MIT license files in `Assets`. The WebView2 SDK's Microsoft BSD notice is copied from the restored package to `Microsoft.Web.WebView2.LICENSE.txt` in build and publish output. See [xterm.js](https://github.com/xtermjs/xterm.js) and the official [security](https://xtermjs.org/docs/guides/security/) and [flow control](https://xtermjs.org/docs/guides/flowcontrol/) guidance. WebView2 hosting follows the [Microsoft origin and message security guidance](https://learn.microsoft.com/en-us/microsoft-edge/webview2/concepts/security).
