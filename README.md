# Godot Steam / itch.io Deployer

Godot 4.7 .NET editor plugin for building once and deploying the same export to Steam and itch.io.

The project is currently at the feasibility-probe stage. The plugin adds a **Deployer** bottom panel to the Godot editor and exposes a button whose event handler can be exercised from the editor or by the automated probe.

## Requirements

- Godot 4.7.1 .NET / Mono
- .NET 8 SDK

## Verify the editor plugin

```powershell
dotnet build
& 'C:\Users\PeDev\AppData\Roaming\godotenv\godot\versions\godot_dotnet_4_7_1_stable\Godot_v4.7.1-stable_mono_win64\Godot_v4.7.1-stable_mono_win64_console.exe' `
  --path . --editor --headless --quit-after 5 -- --deployer-probe
```

A successful run prints both markers:

```text
[GodotSteamItchIoDeployer] PLUGIN_LOADED
[GodotSteamItchIoDeployer] PROBE_BUTTON_PRESSED
```

The `--deployer-probe` argument calls the same handler wired to the visible button; it does not introduce a separate test-only implementation.

On Windows, use the `_console.exe` binary for automated verification when stdout needs to be captured. The `godotenv` `bin\godot.exe` symlink points to the GUI executable, which runs the probe but does not forward its output to this PowerShell session.
