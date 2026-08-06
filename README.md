# Godot Steam / itch.io Deployer

Godot 4.7 .NET editor plugin that exports once and uploads the resulting directory to Steam and/or itch.io.

## Requirements

- Godot 4.7.1 .NET / Mono
- .NET 8 SDK
- A configured Godot export preset
- SteamCMD for Steam uploads
- butler for itch.io uploads

## Install in a Godot C# project

Copy this directory into the target project:

```text
addons/godot-steam-itchio-deployer/
```

Run `dotnet build`, then enable **Godot Steam itch.io Deployer** under **Project > Project Settings > Plugins**. A **Deployer** dock appears at the bottom of the editor.

## Configure

1. Create an export preset under **Project > Export**. The plugin reads preset names from `export_presets.cfg`.
2. Select the preset and set **Export Output File**, for example `build/windows/MyGame.exe`.
3. Select Steam and/or itch.io and fill in the corresponding fields.
4. Use **Save Settings** to write non-secret values to `res://deploy_config.cfg`.
5. Optionally use **Save Encrypted Credentials**. Credentials are machine- and project-bound and saved under `user://godot-steam-itchio-deployer/credentials.cfg`, outside the repository. Steam Guard codes are never saved.

The export output is a file path because that is what Godot's export CLI requires. Uploads use the parent directory of that file as their content root.

## Workflows

- **Build** runs the selected preset through `godot --headless --export-release`.
- **Upload** uploads an existing output directory to the selected services.
- **Build & Upload** exports once, then uploads to Steam followed by itch.io.

Steam uploads generate app/depot VDF files under `user://godot-steam-itchio-deployer/steam-vdf` and invoke SteamCMD. itch.io uploads invoke `butler push` with `BUTLER_API_KEY` injected only into the child process environment.

All child-process output is streamed into the shared log console.

## Automated EditorPlugin probe

```powershell
dotnet build
& 'C:\Users\PeDev\AppData\Roaming\godotenv\godot\versions\godot_dotnet_4_7_1_stable\Godot_v4.7.1-stable_mono_win64\Godot_v4.7.1-stable_mono_win64_console.exe' `
  --path . --editor --headless --quit-after 10 -- --deployer-probe
```

A successful run prints:

```text
[GodotSteamItchIoDeployer] PLUGIN_LOADED
[GodotSteamItchIoDeployer] PROBE_BUTTON_PRESSED
```

Use the Windows `_console.exe` binary when stdout needs to be captured. The `godotenv` `bin\godot.exe` symlink points to the GUI executable and does not forward its output to this PowerShell session.
