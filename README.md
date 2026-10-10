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
- **Scans for leftovers** — files, folders and registry entries — that still reference the
  program, and shows them in a review list with checkboxes. Nothing is removed until you
  confirm.
- **Quarantines instead of deleting** by default, so a wrong match can be undone.
- **Creates a restore point** before each removal when System Protection is enabled.
- Handles locked files by deferring deletion to the next restart, and long paths.

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
- Scans are bounded by time and match caps and say so when results are partial.

## Limitations

- There is no per-vendor removal recipe database. Detection is generic and name-based, so
  it finds the common cases (install folders, AppData trees, registry keys, file-type
  associations) but cannot know an application's bespoke quirks.
- Registry matching is name-based, so a third-party key that happens to contain the product
  name can be reported. This is why results are reviewed rather than deleted blindly.
- Uninstallers that always show a UI regardless of switches will not become silent.
- Quarantined items are moved, so restoring does not restore timestamps or ACLs exactly.

## Licence

MIT.