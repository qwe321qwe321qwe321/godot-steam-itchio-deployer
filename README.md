# Godot Steam / itch.io Deployer

Godot 4.7 .NET editor plugin that exports once and uploads the resulting directory to Steam and/or itch.io.

## Requirements

- Godot 4.7.1 .NET / Mono
- .NET 8 SDK
- A configured Godot export preset
- SteamCMD for Steam uploads
- butler for itch.io uploads

## Install in a Godot C# project

For a private/internal project, add this repository as a submodule at the standard Godot addon path:

```powershell
git submodule add https://github.com/qwe321qwe321qwe321/godot-steam-itchio-deployer.git addons/godot-steam-itchio-deployer
git submodule update --init --recursive
```

Run `dotnet build`, then enable **Godot Steam itch.io Deployer** under **Project > Project Settings > Plugins**. A **Deployer** dock appears at the bottom of the editor.

The parent repository pins an exact addon commit. Update deliberately with:

```powershell
git submodule update --remote --merge addons/godot-steam-itchio-deployer
```

Then test and commit the updated submodule pointer in the parent project. Fresh clones must use `git clone --recurse-submodules` or run the initialization command above.

For release ZIP or Godot Asset Library distribution, package this repository so its root is extracted as `addons/godot-steam-itchio-deployer/`.

## Configure

1. Create an export preset under **Project > Export**. The plugin watches `export_presets.cfg` and refreshes the preset list while the editor remains open.
2. Select the preset and set **Export Output File**, for example `build/windows/MyGame.exe`. Enable **Build With Debug** to use Godot's `--export-debug`; leave it disabled for `--export-release`.
3. Select Steam and/or itch.io and fill in the corresponding fields.
   - Steam **Build Description** supports `{Date}`, `{DateTime}`, and `{GitSHA}`. `{GitSHA}` resolves the consuming project's full `git rev-parse HEAD` commit and falls back to `NO_SHA` when Git or repository metadata is unavailable.
   - **Download & Install** next to SteamCMD appears only when the configured path cannot resolve to an existing executable. It downloads Valve's official Windows package, applies its first-run self-updates, verifies it can start, and fills the path automatically.
   - **Download & Install** next to Butler appears only when the configured path cannot resolve to an existing executable. It downloads the latest official itch.io broth package for the current OS/CPU architecture, verifies its version, and fills the path automatically.
4. Configuration follows the original Unity package's ScriptableObject layout, mapped to Godot Resources. The dock exposes Resource pickers for a top-level `BuildDeployConfig` and its referenced `SteamDeployConfig` and `ItchIoDeployConfig`. Use the pickers' menu to load/save `.tres` assets, then use **Save Settings** to persist the edited Resources. The defaults live under `res://deploy/` and are suitable for version control. A legacy `res://deploy_config.cfg` is imported only when those Resources do not exist.
   - `BuildDeployConfig.tres`: targets, export preset/output, debug build choice, and references to the two platform configs.
   - `SteamDeployConfig.tres`: SteamCMD path, app/depot IDs, build description, branch, set-live, and ignore patterns.
   - `ItchIoDeployConfig.tres`: butler path, target/channel/version, if-changed, and ignore patterns.
   Paths inside the project are stored relative to the project root (for example `.deployer/tools/steamcmd/steamcmd.exe`); external tools retain absolute paths.
5. Optionally use **Save Encrypted Credentials**. Credentials are machine- and project-bound and saved at `res://.deployer/credentials.cfg`. Steam Guard codes are never saved.

Build, Steam, and itch.io configuration panels can be collapsed independently inside their own scrolling settings area. The top-level Build / Deploy Config picker and the Save/Build/Upload action bar are pinned above that scroll area, so they remain visible while editing long platform forms. The Steam and itch.io Resource pickers are grouped at the top of the collapsible Build panel. **Console Result** is a separate collapsible panel below the settings area and expands automatically when a Build, Upload, or Build & Upload workflow starts. If Steam Guard is requested, the Steam panel expands automatically before the temporary code input appears. Each **Save Encrypted Credentials** action sits directly below that platform's secret input. **Save Settings** gains an asterisk and yellow highlight whenever the current non-secret configuration differs from the saved files; credential edits are intentionally tracked separately.

Use **Test Steam Login** after filling in the SteamCMD path, username, and password. The Steam Guard field is hidden during normal setup. If SteamCMD reports that a Guard code is required during a login test or upload, the plugin stops that attempt, reveals a temporary code prompt, and retries the same operation after submission without rebuilding. Guard detection monitors both redirected process output and SteamCMD's appended `logs/console_log.txt`, because current Windows SteamCMD builds may emit the interactive prompt only to that log.

The export output is a file path because that is what Godot's export CLI requires. Uploads use the parent directory of that file as their content root.

## Workflows

- **Build** runs the selected preset through `godot --headless --export-debug` or `--export-release`, according to **Build With Debug**.
- **Upload** uploads an existing output directory to the selected services.
- **Build & Upload** exports once, then uploads to Steam followed by itch.io.

Steam uploads generate app/depot VDF files under `res://.deployer/steam-vdf` and invoke SteamCMD. itch.io uploads invoke `butler push` with `BUTLER_API_KEY` injected only into the child process environment.

All child-process output is streamed into the shared log console. ANSI SGR formatting emitted by Godot, SteamCMD, or butler is translated to native `RichTextLabel` color, bold, italic, and underline styles; unsupported terminal control sequences are removed instead of appearing as raw codes.

Automatically downloaded tools and local-only configuration live inside the Godot project under `res://.deployer/`. The entire directory must remain in `.gitignore`. SteamCMD automatic installation currently supports Windows. Butler automatic installation supports Windows, macOS, and Linux on x64/ARM64 when itch.io publishes the corresponding package.

Official download sources:

- SteamCMD: `https://steamcdn-a.akamaihd.net/client/installer/steamcmd.zip`
- Butler broth channel: `https://broth.itch.zone/butler/<os>-<arch>/LATEST/archive/default`

## Automated EditorPlugin probe

Run this from a consuming Godot project after adding the submodule:

```powershell
dotnet build
& 'C:\Users\PeDev\AppData\Roaming\godotenv\godot\versions\godot_dotnet_4_7_1_stable\Godot_v4.7.1-stable_mono_win64\Godot_v4.7.1-stable_mono_win64_console.exe' `
  --path <consumer-project> --editor --headless --quit-after 10 -- --deployer-probe
```

A successful run prints:

```text
[GodotSteamItchIoDeployer] PLUGIN_LOADED
[GodotSteamItchIoDeployer] PROBE_BUTTON_PRESSED
```

Use the Windows `_console.exe` binary when stdout needs to be captured. The `godotenv` `bin\godot.exe` symlink points to the GUI executable and does not forward its output to this PowerShell session.
