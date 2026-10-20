# wslc-desktop

[中文](README.md) | **English**

A native Windows desktop management panel for WSL containers ([`wslc`](https://learn.microsoft.com/windows/wsl/)).
WinUI 3 + .NET 10, shipped as an **unpackaged, self-contained single file**: the target machine needs
neither the .NET runtime nor the Windows App SDK runtime.

## Features

| Module | What it does |
| --- | --- |
| Containers | List with live CPU / memory, start / stop / restart / force-kill / delete (all behind a confirmation), batch operations, one-click port open, detail flyout (inspect / **log snapshot + live follow + save to file** / **cancellable command execution** — output streams live and Stop signals the process inside the container) |
| Images | A **pull dialog** (registry / repository / tag as separate fields; the registry list is the built-in accelerators plus Docker Hub **plus any sources you added yourself**, 10 common-image shortcuts, one-click **tag listing** sorted "latest first → versions descending", and automatic download-size measurement for the selected tag), **private-registry authentication** (configure credentials once in Settings; they are applied automatically per registry when listing tags or pulling), **rename / re-tag**, local table, pull progress (cancellable), delete, create a container from an image in one click |
| Networks / Volumes / Sessions | List, create and remove (built-in networks are protected; consequences are spelled out before deletion) |
| WSL distros | `wsl -l -v` + registry Lxss: state / version / default / install location / VHDX size, start / terminate / set default / open terminal / unregister |
| Add a distro | Import a local package (`.tar` / `.tar.gz` / `.tar.xz` / `.vhdx` / `.wsl` — the install method is picked from the extension) or install **online** from the Microsoft catalogue (`wsl --list --online`); includes **pre-flight checks** (duplicate name / target directory / extension) and a **live equivalent-command preview**, with the install streamed and cancellable |
| Distro details | Compact the disk (reclaim freed blocks), toggle the sparse disk, resize the disk, **move the install location**, **export to a tar archive** (shows bytes written, cancellable), edit `/etc/wsl.conf` (auto-backed up before saving), set the default user, open the install location |
| Create container | Full form + **live equivalent-command preview** (shares the exact same command-assembly logic as the real run), can prefill ports and the startup command from an image inspect; supports **GPU passthrough (`--gpus all`)**, hostname / domain name, memory and CPU limits (memory units are normalised to upper case) |
| Settings | Panel preferences, **registry management** (one list covering built-in and custom sources: a radio marks the default, a "show" checkbox decides whether it appears in the dropdown, editing sets the note and credential, deleting removes custom sources — with search and scrolling), and `settings.yaml` in **two modes: form ↔ raw YAML** (line-based replacement to preserve comments, timestamped backup before every save) |
| Event stream | A long-lived `wslc events` subscription drives instant refresh (reconnect + resume with de-duplication); polling is the fallback |
| Desktop integration | Tray resident, close-to-tray, single instance, shortcuts `F5` / `Ctrl+R` / `Ctrl+N` / `Ctrl+1–8`, Mica and instant light / dark switching |
| About | Environment self-check (engine / kernel / missing components) + **one-click install / repair of engine components** (with progress and a post-install re-check) + **diagnostics bundle export** (versions / engine / sessions / API-channel state + redacted preferences + log tail) + update check and **in-place update** against GitHub Releases (download → sha256 verify → swap → auto restart, previous build kept as `.old`) |

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
- **Log following now lives inside the panel** (snapshot + live follow + save to a text file). Interactive
  programs (`vi` / `top`, anything that needs a TTY) are still handed off to Windows Terminal (`wt`), falling
  back to a console window when unavailable — the official API exposes byte streams but no PTY, and a
  self-built terminal is out of scope here
- **In-place update** is a manual flow — nothing is downloaded or replaced until you confirm, and there is no
  silent background auto-update. Each update is a full ~96 MB download (GitHub provides no binary deltas)
- In-place update requires a **writable** program directory: when the app sits in `C:\Program Files\` or
  another protected location it detects this up front and points you at the download page instead, rather
  than downloading 96 MB before failing
- Update packages are unsigned; integrity relies on the **sha256 digest** of the GitHub release asset. Because
  the app downloads the file itself (no browser Mark-of-the-Web), SmartScreen does not block the swap
- **Pull progress comes from the CLI by default** (indeterminate bar). The official C# API channel only yields
  real byte counts when it can attach to an engine session. Measured constraint: the official API treats a
  session name as a **machine-wide unique key owned by the process that created it** — the CLI keeps its
  containers in `wslc-cli-<user>`, owned by `wslc.exe`, so the panel can only perform a **read-only attach**
  and falls back to the CLI otherwise. Settings → "Official API channel" shows the **actual state**
  (available / read-only attach / reason unavailable): better to tell you "this is CLI right now" than to
  pretend there is API acceleration
- **Private-registry credentials are configured once in Settings → Private registry credentials** and then
  applied automatically per registry: passwords are encrypted with **Windows DPAPI (current user)** before
  landing in the local `prefs.json` — plaintext is never written to disk or to logs, and the blob is
  undecryptable on another machine or under another Windows account. A **Test** button exchanges a token on
  the spot so you learn about a wrong password immediately instead of after a failed pull. Public images need
  no configuration at all.
- **Settings → Registries is one list, and built-in sources are rows just like yours** — same fields
  (note, credential status, enabled). There is exactly one way to operate it: the **radio** on the left picks
  the default, the **"show" checkbox** decides whether it appears in the dropdown, **Edit** changes the note
  and credential, and **Delete** appears only for custom sources. A search box at the top filters by host,
  note or username. Private passwords are encrypted with Windows DPAPI before being stored; saving a
  credential exchanges a token right away, so a wrong password is reported immediately.
- Tag lists are fetched straight from the registry (neither the official API nor `wslc` exposes them).
  The default registry is **`docker.1panel.live`** (measured: both tags and manifests are anonymous, and
  it works **without a proxy**). **Docker Hub (`registry-1.docker.io`) is selectable too** — public images
  only need an anonymous token to list tags, but it requires outbound connectivity, so it commonly times out
  on a direct connection in China; a proxy works. Other sites (`docker.1ms.run` / `docker.m.daocloud.io`)
  need a token from their own domain — you can still pull by typing a tag manually, and the UI says so up
  front instead of letting you wait for a request that is bound to fail. **The registry you type is never
  renamed**: `docker.io` stays visible and is used as the pull reference; only the tag query talks to the
  real API endpoint `registry-1.docker.io` under the hood

## License

[Apache-2.0](LICENSE).
