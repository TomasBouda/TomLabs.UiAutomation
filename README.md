# TomLabs.UiAutomation

[![Build](https://github.com/TomasBouda/TomLabs.UiAutomation/actions/workflows/build.yml/badge.svg)](https://github.com/TomasBouda/TomLabs.UiAutomation/actions/workflows/build.yml)
[![NuGet TomLabs.UiAutomation](https://img.shields.io/nuget/v/TomLabs.UiAutomation?label=TomLabs.UiAutomation&logo=nuget)](https://www.nuget.org/packages/TomLabs.UiAutomation)
[![NuGet tomlabs-ui](https://img.shields.io/nuget/v/TomLabs.UiAutomation.Tool?label=tomlabs-ui&logo=nuget)](https://www.nuget.org/packages/TomLabs.UiAutomation.Tool)
[![Downloads](https://img.shields.io/nuget/dt/TomLabs.UiAutomation?label=downloads)](https://www.nuget.org/packages/TomLabs.UiAutomation)
[![License: MIT](https://img.shields.io/badge/license-MIT-green.svg)](LICENSE)

Drive and screenshot an Avalonia desktop app from a script — or from an AI coding session — **without touching
the desktop**. No `SendKeys`, no cursor moves, no screen capture: the app itself answers on a loopback HTTP port,
renders its window through `RenderTargetBitmap`, dumps its visual tree and injects clicks, key gestures and text
straight into the Avalonia window through the same raw-input path the platform backends use. With `--headless`
the same app runs on Avalonia's headless platform with the Skia renderer, so nothing appears on screen at all and
the screenshots are still pixel-identical to the real window.

Two packages:

- **`TomLabs.UiAutomation`** — the in-app server and the `--headless` switch. Reference it from the app.
- **`TomLabs.UiAutomation.Tool`** — the `tomlabs-ui` dotnet tool that builds, starts, calls and stops the app.

## Wire it into the app

```csharp
// Program.cs
public static AppBuilder BuildAvaloniaApp(string[] args) =>
    AppBuilder.Configure<App>()
        .UsePlatformDetectOrHeadless(args)   // UsePlatformDetect(), or the headless platform with --headless
        .LogToTrace();

// App.axaml.cs, OnFrameworkInitializationCompleted
var automation = UiAutomationServer.StartIfEnabled(desktop, "MyApp", AppInfo.Version, message => log.Information(message));
automation?.Actions["page"] = (window, arg) => { viewModel.Page = arg; return new { page = arg }; };   // optional shortcuts
desktop.Exit += (_, _) => automation?.Dispose();
```

The server starts only when the environment variable **`MYAPP_AUTOMATION=<port>`** (the app name upper-cased) is
set, and listens on `127.0.0.1` only. Nothing changes for normal users.

## Drive it

```bash
dotnet tool install -g TomLabs.UiAutomation.Tool

tomlabs-ui start                          # build Release into ./.ui-build, start MyApp --headless with
                                          # MYAPP_AUTOMATION=47831 and MYAPP_DATA_DIR = a fresh copy of tests/ui-fixture
tomlabs-ui call "click?text=Settings"     # any endpoint; the JSON answer is printed
tomlabs-ui call "key?gesture=Ctrl%2BK"
tomlabs-ui shot out/settings.png          # PNG of the main window (--window top for the newest dialog)
tomlabs-ui tree "depth=8"                 # visual tree: Type #Name .classes [x,y wxh] "text"
tomlabs-ui stop
```

`start --visible` opens a real window (real DPI, native popups) — input still goes straight into the Avalonia
window, never through the desktop. `--data-dir`, `--fixture`, `--env NAME=VALUE`, `--project`, `--port` and
`--no-build` cover the rest; `tomlabs-ui --help` lists everything. `curl http://127.0.0.1:47831/<endpoint>` works
just as well once the app runs.

Convention the tool relies on: the app reads its data folder from `<APPNAME>_DATA_DIR` (so a fixture can replace
the live data), and `tests/ui-fixture/` in the repository holds a small synthetic data set. Add `.ui-build/` to
`.gitignore`.

## Endpoints

GET or POST, query-string arguments; JSON answers except `/tree` (text) and `/screenshot` (PNG). Errors are
`{"error": "..."}` with 400 (bad argument, nothing found) or 500.

| Endpoint | What it does |
|---|---|
| `/` | name, version, platform (`Avalonia.Headless` / `Avalonia.Win32`), theme, open windows |
| `/screenshot?window=&scale=&path=` | PNG of the window (`top` = newest dialog, an index or a title); `path` also saves it (fully qualified) |
| `/tree?window=&name=&depth=&all=1` | visual tree; `name` narrows to a named control, `all=1` includes hidden nodes |
| `/find?text=&name=&type=` | visible controls matching text (contains, exact first), `Name` or type, with bounds |
| `/click?text=\|name=\|x=&y=&button=right&count=2` | pointer press + release at the control's centre (or at x,y) |
| `/key?gesture=Ctrl+Shift+K` | key down + up on the focused element (`%2B` for `+` in a URL; spaces work too) |
| `/type?text=` | text input into the focused element |
| `/get?path=Selected.Name` | read a property path on the window's DataContext |
| `/set?path=Search&value=x` | write a DataContext property (or `name=<Control>&property=Text&value=`) |
| `/invoke?command=SaveCommand&parameter=` | execute an `ICommand` of the DataContext |
| `/do/<action>?arg=` | app-specific action registered in `Actions` |
| `/theme?variant=dark\|light\|default` | switch the theme variant |
| `/resize?width=&height=` | resize the window |
| `/wait?ms=` | let timers, animations and layout settle |
| `/quit` | shut the application down |

Input-like commands wait ~150 ms (`SettleMilliseconds`) and force a layout pass before answering, so a
screenshot taken right after them is settled. Windows other than the main one are addressed with `window=`.

## In unit tests

The server also runs inside `Avalonia.Headless.XUnit` tests: `new UiAutomationServer(UiAutomationHost.ForWindow(window), port, "app", "1.0")`
— see `tests/` for examples. It forces a headless render tick before hit testing, so clicks work without a running main loop.

## Publishing

Tags `v<version>` publish both packages to nuget.org through [Trusted Publishing](https://learn.microsoft.com/nuget/nuget-org/trusted-publishing):
no API key is stored anywhere. One-time setup: on nuget.org → Trusted Publishing, add a policy for repository owner
`TomasBouda`, repository `TomLabs.UiAutomation`, workflow file `build.yml`. Then bump `VersionPrefix` in `Directory.Build.props`, add the changelog
section and push the tag.

## Notes

- The library is built with `AvaloniaAccessUnstablePrivateApis`: raw input is public in Avalonia's implementation
  assemblies but hidden in the reference assemblies. Consumers do not need the switch, but Avalonia requires such a
  package to pin the **exact** Avalonia version (`[11.3.18]`), so the app must reference the same one; a new Avalonia
  version means a new package release.
- Popups that are separate top-levels (context menus, tooltips) are not part of the main window's screenshot.
