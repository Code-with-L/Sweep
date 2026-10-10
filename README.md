# Sweep — App Uninstaller

A Windows uninstaller that lists installed programs, runs their registered uninstaller,
then finds and removes what was left behind.

Written from scratch against public Windows APIs. No third-party uninstaller engine, no
cloud database, no telemetry.

![Main window](docs/screenshot-main.png)

## What it does

- **Lists installed programs** from the Windows uninstall registry keys, reading name,
  version, publisher, install date, size, scope (per-user vs all-users, 32 vs 64-bit).
- **Runs the real uninstaller**, detecting the installer technology in use (Inno Setup,
  NSIS, InstallShield, Windows Installer, Squirrel, InstallAware) and applying the
  documented silent switches where they are known. Uses the vendor's `QuietUninstallString`
  when present.
- **Always offers a leftover scan afterwards**, whatever exit code the uninstaller
  returned, and surfaces the uninstaller's own output. The program stays reachable for
  scanning after it disappears from the list.
- **Scans for leftovers** across files, folders, the registry, and the system artefacts
  that a name-based sweep cannot reach:

  | Category | Source |
  |---|---|
  | Startup entries | `HKCU`/`HKLM` `…\CurrentVersion\Run`, `RunOnce`, Startup folder |
  | Services | `HKLM\SYSTEM\CurrentControlSet\Services\*` `ImagePath`, with orphan detection |
  | Scheduled tasks | Task Scheduler inventory via `schtasks`, skipping `\Microsoft\Windows\*` |
  | Shortcuts | Start Menu and Desktop `.lnk` files |
  | Environment variables | `HKCU\Environment`, session manager `Environment` (by variable name only) |
  | Fonts | `…\CurrentVersion\Fonts` in both hives |
  | File associations | `HKCU\Software\Classes` default values |
  | Registry keys | `HKCU\Software`, `HKLM\SOFTWARE` in both WOW64 views |

- **Rates every match** as Safe, Moderate or Risky. Risky items start unchecked.
- **Lists Store/MSIX apps** in a separate module. These never appear in the uninstall
  registry keys, so the classic program list cannot show them at all.
- **Manages startup entries**: Run/RunOnce, Startup folder shortcuts, Windows services and
  scheduled tasks in one list. Disabling a service uses the Service Control Manager and
  disabling a task uses `schtasks` — both reversible. Registry edits are not used, because
  SCM caches start types and a registry edit appears to work then silently reverts.
- **Filters and sorts** by type (32/64-bit, read from the real PE header), by size, by
  drive, and by any column.
- **Context menu** actions: run, open containing folder, open registry key, open website,
  open help link, kill the program's running processes, copy details.
- **Quarantines instead of deleting** by default, so a wrong match can be undone, and
  reports loudly if the undo manifest cannot be written.
- **Creates a restore point** before each removal when System Protection is enabled, and
  tells you when it could not.
- Handles locked files by deferring deletion to the next restart, and long paths.
- **Shows every program icon**, extracting them off the UI thread. Programs that genuinely
  have no icon get a generated letter badge rather than a blank cell or an anonymous
  document glyph.

![Leftovers](docs/screenshot-leftovers.png)

## Architecture support

| Target    | Build                                          | Verified |
|-----------|------------------------------------------------|----------|
| `win-arm64` | self-contained, native ARM64 (~122 MB)        | PE machine = `ARM64`, runs natively on ARM64 |
| `win-x64`   | framework-dependent (~0.4 MB)                 | PE machine = `x64` |
| `win-x86`   | framework-dependent (~0.3 MB)                 | PE machine = `x86` |

Registry enumeration uses explicit `RegistryView` handling rather than relying on process
bitness, so 32-bit and 64-bit views are read correctly under WOW64 **and on Windows on
ARM**. x86 program directories are resolved through `SHGetKnownFolderPath`, which is the
API that behaves correctly under ARM64 emulation.

## Building

```powershell
dotnet build

# Self-contained ARM64 (no runtime prerequisite on the target machine)
dotnet publish -c Release -r win-arm64 --self-contained true -o dist\win-arm64

# Framework-dependent builds
dotnet publish -c Release -r win-x64 --self-contained false -o dist\win-x64
dotnet publish -c Release -r win-x86 --self-contained false -o dist\win-x86
```

Requires the .NET 10 SDK. The app self-elevates via UAC on startup.

## Diagnostics

The app doubles as a command-line verifier, which is how the scanners were validated
against the 100+ real uninstall strings on a test machine.

```powershell
# Enumerate programs, show tokens and the uninstall plan (plan is never executed)
Sweep.exe --selftest --app Discord

# Show every uninstall string with detected family, resolved exe and silent switches
Sweep.exe --selftest --dump-uninstallers

# Show derived search tokens for every installed program
Sweep.exe --selftest --tokens

# Enumerate Store/MSIX apps and startup entries
Sweep.exe --selftest --storeapps
Sweep.exe --selftest --autorun
```

`tools\capture.ps1` takes DPI-aware screenshots of the app or a dialog, used for UI
regression checks.

## Precision notes

Leftover detection is a heuristic, so it is tuned to favour precision over recall. A
scanner that offers to delete unrelated files is worse than no scanner, because the user
stops trusting the output.

Measures taken:

- Product **category nouns** are stripped before matching, so `VLC media player` searches
  for `vlc`, never for `media`. Without this, `Windows Media Player` and `social-media`
  were reported as VLC leftovers.
- Matching is **whole-word / camelCase** first. Compacted substring matching is only used
  for tokens of 6+ characters, because otherwise `media` matches `immediate`.
- The **user profile root is never scanned**. Developers keep repositories and scripts
  named after tools under their home directory, and matching those would offer to delete
  source code.
- **Loose files are only matched directly inside a scan root**, never deeper.
- Windows- and sandbox-owned directories (`LocalAppData\Packages`, `ProgramData\Microsoft`,
  `C:\Windows`, `Package Cache`, …) are excluded outright.
- Registry walks skip `CLSID`, `TypeLib`, `Interface`, `AppID` and `Wow6432Node`.
- Scans are bounded by time and match caps **per phase** (files and registry have
  independent budgets), and a truncated or partly-failed scan says so in the UI rather
  than being presented as a complete result.
- The registry phase always runs, even if the file phase hit its cap. An earlier version
  silently skipped it, which meant a broad match hid every registry leftover.

## Limitations

- There is no per-vendor removal recipe database. Detection is generic and name-based, so
  it finds the common cases but cannot know an application's bespoke quirks. Uninstall
  history is recorded locally in the activity log; there is no shared cloud log service.
- Registry matching is name-based, so a third-party key that happens to contain the
  product name can be reported. This is why results are reviewed rather than deleted
  blindly, and why everything is rated by confidence.
- Browser extensions / add-ons and BHOs are **not** enumerated. Reading a browser's
  extension and preference stores safely requires per-profile parsing, and a mistake there
  breaks the user's browser rather than just their leftovers.
- MSI component/orphan analysis is not done. `Win32_Product`-style queries trigger
  self-repair on every package they touch.
- Startup entries (`Run`/`RunOnce`) have no native "disabled" state. Windows only offers
  the undocumented `StartupApproved` blob, so Sweep reports that these must be removed
  rather than faking a disable.
- `PATH` contents are never edited automatically; only environment variable *names* are
  matched.
- Store app removal needs PowerShell; a locked-down machine without it cannot uninstall
  MSIX packages from here.
- Quarantined items are moved, so restoring does not restore timestamps or ACLs exactly.

## Licence

MIT.