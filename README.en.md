# wslc-desktop

[中文](README.md) | **English**

A native Windows desktop management panel for WSL containers ([`wslc`](https://learn.microsoft.com/windows/wsl/)).
WinUI 3 + .NET 10, shipped as an **unpackaged, self-contained single file**: the target machine needs
neither the .NET runtime nor the Windows App SDK runtime.

## Features

| Module | What it does |
| --- | --- |
| Containers | List with live CPU / memory, start / stop / restart / force-kill / delete (all behind a confirmation), batch operations, one-click port open, detail flyout (inspect / log snapshot / one-shot exec) |
| Images | Local table, pull (live progress, cancellable), delete, create a container from an image in one click |
| Networks / Volumes / Sessions | List, create and remove (built-in networks are protected; consequences are spelled out before deletion) |
| WSL distros | `wsl -l -v` + registry Lxss: state / version / default / install location / VHDX size, start / terminate / set default / open terminal / unregister |
| Create container | Full form + **live equivalent-command preview** (shares the exact same command-assembly logic as the real run), can prefill ports and the startup command from an image inspect |
| Settings | Panel preferences + `settings.yaml` in **two modes: form ↔ raw YAML** (line-based replacement to preserve comments, timestamped backup before every save) |
| Event stream | A long-lived `wslc events` subscription drives instant refresh (reconnect + resume with de-duplication); polling is the fallback |
| Desktop integration | Tray resident, close-to-tray, single instance, shortcuts `F5` / `Ctrl+R` / `Ctrl+N` / `Ctrl+1–8`, Mica and instant light / dark switching |
| About | Environment self-check (engine / kernel / missing components) + update check against GitHub Releases |

## Requirements

- Windows 10 1809+ / Windows 11
- WSL container engine **3.0.1 or later** (`wsl --update`). When the version is too low or components are
  missing, the panel shows an actionable upgrade hint
- Building requires the **.NET 10 SDK**; **running a released build requires no runtime at all** (self-contained)

## Build and run

Run the scripts in the repository root (double-click, or from a command line):

```bat
启动.bat            :: launch the existing build
启动.bat build      :: build first, then launch
跑测试.bat           :: full test suite (including Live machine tests)
跑测试.bat unit      :: skip the Live tests, faster
```

Or manually:

```bat
dotnet build WslcDesktop.slnx -t:Rebuild
dotnet test  tests\WslcDesktop.Core.Tests\WslcDesktop.Core.Tests.csproj
```

> `启动.bat` / `跑测试.bat` **must stay GBK(ANSI) encoded with CRLF line endings**: cmd parses batch files
> using code page 936, and saving them as UTF-8 turns the Chinese text into garbage bytes — it can even
> split comment lines so the following statements get executed as commands. `.gitattributes` pins these
> two files to CRLF, but the encoding still has to be selected explicitly in your editor.

> **Do not use `dotnet run`**: this is an unpackaged WinUI 3 app, and the Run hook from
> `Microsoft.Windows.SDK.BuildTools.WinApp` treats it as a packaged app, failing with
> `Failed to launch application: Developer Mode is not enabled` unless developer mode is enabled.
> Build first, then run the exe directly.

## Layout

```
src/WslcDesktop.Core/           service layer: wslc CLI channel, models, parsers (pure functions + fixture tests)
src/WslcDesktop.App/            WinUI 3 UI: 10 pages, detail flyout, tray, single instance
tests/WslcDesktop.Core.Tests/   xUnit: parsing / command building / event stream / preferences / update check + Live tests
```

## Naming convention

The application name is always the repository name, **`wslc-desktop`** — there is no second spelling:

| Where | Value |
| --- | --- |
| Repository / Release / released exe | `wslc-desktop`, `wslc-desktop.exe` |
| Window title / tray tooltip / About page / app properties | `wslc-desktop` |
| Assembly name (`AssemblyName`) | `wslc-desktop` |
| C# namespaces | `WslcDesktop` (App), `WslcDesktop.Core` (Core / Tests) |
| Projects and folders | `WslcDesktop.App` / `WslcDesktop.Core` / `WslcDesktop.Core.Tests` |
| Data directory | `%LOCALAPPDATA%\WslcDesktop\` (`prefs.json`, `error.log`) |

> The root namespace is deliberately `WslcDesktop` rather than `WslcDesktop.App`: the App page's class is
> `WslcDesktop.App`, so a namespace whose last segment is also `App` would produce `WslcDesktop.App.App`
> and make namespace / type resolution ambiguous. The lower-case hyphenated `wslc-desktop` is used for
> **external identity only** (file names, titles, user agent) and never inside C# identifiers.

## Test baseline

- Unit tests + `Live*` machine tests must all be green (`Live*` skips itself when the engine is unavailable,
  so it never turns the suite red)
- Rendering / layout / real interaction must be verified on a real machine: CI cannot catch these

## Known limitations

- The official `wslc compose` has not shipped yet, so no orchestration layer is built in
- Interactive terminals and `logs -f` following are handed off to Windows Terminal (`wt`), falling back to a
  console window when unavailable
- The update check only compares versions and points you at the download; it never downloads or replaces the
  running exe (non-MSIX self-update would have to deal with the locked exe, permissions and signing —
  more risk than value)
- Pull progress: the engine currently returns only a status keyword (no byte counts), so the bar is
  indeterminate with status text; it will switch to a percentage automatically once byte counts are exposed

## License

[Apache-2.0](LICENSE).
