#if TOOLS
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace GodotSteamItchIoDeployer;

[Tool]
public partial class SteamItchIoDeployerPlugin : EditorPlugin
{
    private const string LogPrefix = "[GodotSteamItchIoDeployer]";

    private readonly ConcurrentQueue<string> _pendingLogs = new();
    private readonly ConcurrentQueue<Action> _pendingUiActions = new();
    private EditorDock? _dock;
    private OptionButton? _preset;
    private LineEdit? _exportOutput;
    private CheckBox? _steamEnabled;
    private LineEdit? _steamCmd;
    private LineEdit? _steamAppId;
    private LineEdit? _steamDepotId;
    private LineEdit? _steamDescription;
    private CheckBox? _steamSetLive;
    private LineEdit? _steamBranch;
    private LineEdit? _steamIgnore;
    private LineEdit? _steamUsername;
    private LineEdit? _steamPassword;
    private LineEdit? _steamGuard;
    private VBoxContainer? _steamGuardPanel;
    private Label? _steamGuardMessage;
    private TaskCompletionSource<string?>? _steamGuardCompletion;
    private Button? _steamLoginTestButton;
    private Button? _steamDownloadButton;
    private CheckBox? _itchEnabled;
    private LineEdit? _butler;
    private LineEdit? _itchTarget;
    private LineEdit? _itchChannel;
    private LineEdit? _itchVersion;
    private CheckBox? _itchIfChanged;
    private LineEdit? _itchIgnore;
    private LineEdit? _butlerApiKey;
    private Button? _butlerDownloadButton;
    private Button? _buildButton;
    private Button? _uploadButton;
    private Button? _buildUploadButton;
    private RichTextLabel? _log;
    private int _busy;
    private bool _quitAfterToolInstall;
    private string _presetFileStamp = string.Empty;

    public override void _EnterTree()
    {
        DeploySettings settings = DeployConfigStore.LoadSettings();
        DeployCredentials credentials = DeployConfigStore.LoadCredentials();

        _dock = new EditorDock
        {
            Name = "SteamItchIoDeployerDock",
            Title = "Deployer",
            LayoutKey = "godot_steam_itchio_deployer",
            DefaultSlot = EditorDock.DockSlot.Bottom,
            AvailableLayouts = EditorDock.DockLayout.Horizontal | EditorDock.DockLayout.Floating,
            Global = true,
        };

        var scroll = new ScrollContainer { Name = "DeployerScroll" };
        scroll.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _dock.AddChild(scroll);

        var root = new VBoxContainer
        {
            Name = "DeployerContent",
            CustomMinimumSize = new Vector2(760, 640),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        scroll.AddChild(root);

        root.AddChild(new Label { Text = "Godot Steam / itch.io Deployer" });
        root.AddChild(new Label { Text = "Build once, then upload the exported directory to the selected services." });

        AddSection(root, "Build");
        var buildGrid = CreateGrid(root);
        _preset = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        PopulatePresets(_preset, settings.ExportPreset);
        _presetFileStamp = GetPresetFileStamp();
        AddRow(buildGrid, "Export Preset", _preset);
        _exportOutput = AddLineRow(buildGrid, "Export Output File", settings.ExportOutputPath, "Example: build/windows/MyGame.exe");

        AddSection(root, "Steam");
        _steamEnabled = new CheckBox { Text = "Upload to Steam", ButtonPressed = settings.Targets.HasFlag(DeployTargets.Steam) };
        root.AddChild(_steamEnabled);
        var steamGrid = CreateGrid(root);
        _steamCmd = AddToolPathRow(
            steamGrid,
            "SteamCMD",
            settings.SteamCmdPath,
            "Full path to steamcmd.exe",
            () => StartToolInstall(DeployToolKind.SteamCmd),
            out _steamDownloadButton);
        _steamAppId = AddLineRow(steamGrid, "App ID", settings.SteamAppId);
        _steamDepotId = AddLineRow(steamGrid, "Depot ID", settings.SteamDepotId);
        _steamDescription = AddLineRow(steamGrid, "Build Description", settings.SteamBuildDescription, "Supports {Date} and {DateTime}");
        _steamSetLive = AddCheckRow(steamGrid, "Set Live", settings.SteamSetLive);
        _steamBranch = AddLineRow(steamGrid, "Branch", settings.SteamBranch);
        _steamIgnore = AddLineRow(steamGrid, "Ignore Files", settings.SteamIgnoreFiles, "Comma-separated patterns");
        _steamUsername = AddLineRow(steamGrid, "Username", credentials.SteamUsername);
        _steamPassword = AddLineRow(steamGrid, "Password", credentials.SteamPassword, secret: true);
        _steamLoginTestButton = new Button { Text = "Test Steam Login" };
        _steamLoginTestButton.Pressed += StartSteamLoginTest;
        AddRow(steamGrid, "Authentication", _steamLoginTestButton);

        _steamGuardPanel = new VBoxContainer { Visible = false };
        _steamGuardMessage = new Label
        {
            Text = "SteamCMD requires a Steam Guard code.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        };
        _steamGuardPanel.AddChild(_steamGuardMessage);
        var steamGuardRow = new HBoxContainer();
        _steamGuardPanel.AddChild(steamGuardRow);
        _steamGuard = new LineEdit
        {
            PlaceholderText = "Steam Guard Code",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        steamGuardRow.AddChild(_steamGuard);
        Button submitSteamGuard = new() { Text = "Submit Code" };
        submitSteamGuard.Pressed += SubmitSteamGuardCode;
        steamGuardRow.AddChild(submitSteamGuard);
        Button cancelSteamGuard = new() { Text = "Cancel" };
        cancelSteamGuard.Pressed += CancelSteamGuardCode;
        steamGuardRow.AddChild(cancelSteamGuard);
        root.AddChild(_steamGuardPanel);

        AddSection(root, "itch.io");
        _itchEnabled = new CheckBox { Text = "Upload to itch.io", ButtonPressed = settings.Targets.HasFlag(DeployTargets.ItchIo) };
        root.AddChild(_itchEnabled);
        var itchGrid = CreateGrid(root);
        _butler = AddToolPathRow(
            itchGrid,
            "Butler",
            settings.ButlerPath,
            "Full path to butler executable",
            () => StartToolInstall(DeployToolKind.Butler),
            out _butlerDownloadButton);
        _itchTarget = AddLineRow(itchGrid, "Target", settings.ItchTarget, "username/game");
        _itchChannel = AddLineRow(itchGrid, "Channel", settings.ItchChannel, "Example: windows");
        _itchVersion = AddLineRow(itchGrid, "User Version", settings.ItchUserVersion, "Optional");
        _itchIfChanged = AddCheckRow(itchGrid, "If Changed", settings.ItchIfChanged);
        _itchIgnore = AddLineRow(itchGrid, "Ignore Files", settings.ItchIgnoreFiles, "Comma-separated patterns");
        _butlerApiKey = AddLineRow(itchGrid, "API Key", credentials.ButlerApiKey, secret: true);

        var persistenceButtons = new HBoxContainer();
        root.AddChild(persistenceButtons);
        var saveSettings = new Button { Text = "Save Settings" };
        saveSettings.Pressed += SaveSettingsPressed;
        persistenceButtons.AddChild(saveSettings);
        var saveCredentials = new Button { Text = "Save Encrypted Credentials" };
        saveCredentials.Pressed += SaveCredentialsPressed;
        persistenceButtons.AddChild(saveCredentials);

        var workflowButtons = new HBoxContainer();
        root.AddChild(workflowButtons);
        _buildButton = AddButton(workflowButtons, "Build", () => StartWorkflow(build: true, upload: false));
        _uploadButton = AddButton(workflowButtons, "Upload", () => StartWorkflow(build: false, upload: true));
        _buildUploadButton = AddButton(workflowButtons, "Build & Upload", () => StartWorkflow(build: true, upload: true));

        _log = new RichTextLabel
        {
            CustomMinimumSize = new Vector2(0, 220),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            ScrollFollowing = true,
            SelectionEnabled = true,
        };
        root.AddChild(_log);

        AddDock(_dock);
        SetProcess(true);
        AppendLog("PLUGIN_LOADED");
        if (_preset.ItemCount == 0)
        {
            AppendLog("No export_presets.cfg preset found. Create one in Project > Export before building.");
        }

        if (HasProbeArgument())
        {
            OnProbeButtonPressed();
        }

        if (HasArgument("--deployer-install-steamcmd"))
        {
            _quitAfterToolInstall = true;
            StartToolInstall(DeployToolKind.SteamCmd);
        }
        else if (HasArgument("--deployer-install-butler"))
        {
            _quitAfterToolInstall = true;
            StartToolInstall(DeployToolKind.Butler);
        }

        UpdateButtonState();
    }

    public override void _Process(double delta)
    {
        while (_pendingLogs.TryDequeue(out string? message))
        {
            AppendLog(message);
        }

        while (_pendingUiActions.TryDequeue(out Action? action))
        {
            action();
        }

        RefreshPresetsIfChanged();
        UpdateButtonState();
    }

    public override void _ExitTree()
    {
        SetProcess(false);
        if (_dock is not null && IsInstanceValid(_dock))
        {
            RemoveDock(_dock);
            _dock.QueueFree();
        }

        _dock = null;
        _log = null;
        _steamGuardCompletion?.TrySetResult(null);
        _steamGuardCompletion = null;
    }

    private void StartWorkflow(bool build, bool upload)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            AppendLog("Another deployment operation is already running.");
            return;
        }

        DeploySettings settings = ReadSettingsFromUi();
        DeployCredentials credentials = ReadCredentialsFromUi();
        Error saveError = DeployConfigStore.SaveSettings(settings);
        if (saveError != Error.Ok)
        {
            Interlocked.Exchange(ref _busy, 0);
            AppendLog($"Could not save settings: {saveError}");
            return;
        }

        AppendLog($"Starting {(build && upload ? "Build & Upload" : build ? "Build" : "Upload")}...");
        _ = RunWorkflowAsync(settings, credentials, build, upload);
    }

    private void StartToolInstall(DeployToolKind tool)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            AppendLog("Another deployment operation is already running.");
            return;
        }

        AppendLog($"Starting {tool} download and install...");
        _ = RunToolInstallAsync(tool);
    }

    private async Task RunToolInstallAsync(DeployToolKind tool)
    {
        try
        {
            string executablePath = await ToolInstaller.InstallAsync(tool, QueueProcessOutput).ConfigureAwait(false);
            _pendingUiActions.Enqueue(() =>
            {
                LineEdit? field = tool == DeployToolKind.SteamCmd ? _steamCmd : _butler;
                if (field is not null)
                {
                    field.Text = executablePath;
                }

                Error error = DeployConfigStore.SaveSettings(ReadSettingsFromUi());
                AppendLog(error == Error.Ok
                    ? $"{tool} path saved automatically."
                    : $"{tool} installed, but settings could not be saved: {error}");
                if (_quitAfterToolInstall)
                {
                    GetTree().Quit(error == Error.Ok ? 0 : 1);
                }
            });
        }
        catch (Exception exception)
        {
            _pendingLogs.Enqueue($"ERROR: {tool} installation failed: {exception.Message}");
            if (_quitAfterToolInstall)
            {
                _pendingUiActions.Enqueue(() => GetTree().Quit(1));
            }
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task RunWorkflowAsync(DeploySettings settings, DeployCredentials credentials, bool build, bool upload)
    {
        try
        {
            string projectPath = ProjectSettings.GlobalizePath("res://");
            string outputPath = ResolveProjectPath(settings.ExportOutputPath, projectPath);
            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new InvalidOperationException("Export output must include a file name.");
            }

            if (build)
            {
                if (string.IsNullOrWhiteSpace(settings.ExportPreset))
                {
                    throw new InvalidOperationException("Select a Godot export preset before building.");
                }

                Directory.CreateDirectory(outputDirectory);
                string godotExecutable = OS.GetExecutablePath();
                var arguments = new[] { "--headless", "--path", projectPath, "--export-release", settings.ExportPreset, outputPath };
                _pendingLogs.Enqueue($"Exporting preset '{settings.ExportPreset}' to {outputPath}");
                CliProcessResult result = await CliProcessRunner.RunAsync(godotExecutable, arguments, projectPath, null, QueueProcessOutput).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException($"Godot export failed with exit code {result.ExitCode}.");
                }

                _pendingLogs.Enqueue("Build completed.");
            }

            if (upload)
            {
                if (!Directory.Exists(outputDirectory))
                {
                    throw new DirectoryNotFoundException($"Upload directory does not exist: {outputDirectory}");
                }

                if (settings.Targets == DeployTargets.None)
                {
                    throw new InvalidOperationException("Select Steam and/or itch.io before uploading.");
                }

                if (settings.Targets.HasFlag(DeployTargets.Steam))
                {
                    await UploadSteamAsync(settings, credentials, outputDirectory, projectPath).ConfigureAwait(false);
                }

                if (settings.Targets.HasFlag(DeployTargets.ItchIo))
                {
                    await UploadItchAsync(settings, credentials, outputDirectory, projectPath).ConfigureAwait(false);
                }
            }

            _pendingLogs.Enqueue("Workflow completed successfully.");
        }
        catch (Exception exception)
        {
            _pendingLogs.Enqueue($"ERROR: {exception.Message}");
        }
        finally
        {
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task UploadSteamAsync(DeploySettings settings, DeployCredentials credentials, string contentRoot, string workingDirectory)
    {
        Require(settings.SteamAppId, "Steam App ID");
        Require(settings.SteamDepotId, "Steam Depot ID");
        Require(credentials.SteamUsername, "Steam username");
        Require(credentials.SteamPassword, "Steam password");
        string executable = ResolveExecutable(settings.SteamCmdPath, "SteamCMD", DeployToolKind.SteamCmd);
        string vdfPath = VdfGenerator.Generate(settings, contentRoot);
        _pendingLogs.Enqueue("Uploading to Steam...");
        CliProcessResult result = await RunSteamCommandWithGuardAsync(
            executable,
            credentials,
            new[] { "+run_app_build", vdfPath },
            workingDirectory,
            "Steam upload").ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"SteamCMD failed with exit code {result.ExitCode}.");
        }

        _pendingLogs.Enqueue("Steam upload completed.");
    }

    private void StartSteamLoginTest()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            AppendLog("Another deployment operation is already running.");
            return;
        }

        _ = RunSteamLoginTestAsync(ReadSettingsFromUi(), ReadCredentialsFromUi());
    }

    private async Task RunSteamLoginTestAsync(DeploySettings settings, DeployCredentials credentials)
    {
        try
        {
            Require(credentials.SteamUsername, "Steam username");
            Require(credentials.SteamPassword, "Steam password");
            string executable = ResolveExecutable(settings.SteamCmdPath, "SteamCMD", DeployToolKind.SteamCmd);
            string projectPath = ProjectSettings.GlobalizePath("res://");
            _pendingLogs.Enqueue("Testing Steam login...");
            CliProcessResult result = await RunSteamCommandWithGuardAsync(
                executable,
                credentials,
                Array.Empty<string>(),
                projectPath,
                "Steam login test").ConfigureAwait(false);
            _pendingLogs.Enqueue(result.Succeeded
                ? "Steam login test successful."
                : $"ERROR: Steam login test failed with exit code {result.ExitCode}.");
        }
        catch (OperationCanceledException exception)
        {
            _pendingLogs.Enqueue(exception.Message);
        }
        catch (Exception exception)
        {
            _pendingLogs.Enqueue($"ERROR: Steam login test failed: {exception.Message}");
        }
        finally
        {
            HideSteamGuardPanel();
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task<CliProcessResult> RunSteamCommandWithGuardAsync(
        string executable,
        DeployCredentials credentials,
        IReadOnlyList<string> commandArguments,
        string workingDirectory,
        string operationName)
    {
        string guardCode = string.Empty;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var arguments = new List<string>();
            if (!string.IsNullOrWhiteSpace(guardCode))
            {
                arguments.Add("+set_steam_guard_code");
                arguments.Add(guardCode);
            }

            arguments.Add("+login");
            arguments.Add(credentials.SteamUsername);
            arguments.Add(credentials.SteamPassword);
            arguments.AddRange(commandArguments);
            arguments.Add("+quit");

            CliProcessResult result = await CliProcessRunner.RunAsync(
                executable,
                arguments,
                workingDirectory,
                null,
                QueueProcessOutput,
                CliProcessRunner.IsSteamGuardRequired).ConfigureAwait(false);
            if (!result.TerminatedByOutputPattern)
            {
                return result;
            }

            if (attempt == 2)
            {
                throw new InvalidOperationException("Steam Guard verification failed after three attempts.");
            }

            _pendingLogs.Enqueue("Steam Guard code required.");
            guardCode = await RequestSteamGuardCodeAsync(operationName).ConfigureAwait(false)
                ?? throw new OperationCanceledException("Steam Guard entry cancelled.");
        }

        throw new InvalidOperationException("Steam Guard verification did not complete.");
    }

    private Task<string?> RequestSteamGuardCodeAsync(string operationName)
    {
        var completion = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        _pendingUiActions.Enqueue(() =>
        {
            _steamGuardCompletion?.TrySetResult(null);
            _steamGuardCompletion = completion;
            if (_steamGuardMessage is not null)
            {
                _steamGuardMessage.Text = $"SteamCMD requires a Steam Guard code to continue {operationName}.";
            }

            if (_steamGuard is not null)
            {
                _steamGuard.Text = string.Empty;
                _steamGuard.GrabFocus();
            }

            if (_steamGuardPanel is not null)
            {
                _steamGuardPanel.Visible = true;
            }
        });
        return completion.Task;
    }

    private void SubmitSteamGuardCode()
    {
        string code = _steamGuard?.Text.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(code))
        {
            return;
        }

        TaskCompletionSource<string?>? completion = _steamGuardCompletion;
        _steamGuardCompletion = null;
        if (_steamGuardPanel is not null) _steamGuardPanel.Visible = false;
        if (_steamGuard is not null) _steamGuard.Text = string.Empty;
        completion?.TrySetResult(code);
    }

    private void CancelSteamGuardCode()
    {
        TaskCompletionSource<string?>? completion = _steamGuardCompletion;
        _steamGuardCompletion = null;
        if (_steamGuardPanel is not null) _steamGuardPanel.Visible = false;
        if (_steamGuard is not null) _steamGuard.Text = string.Empty;
        completion?.TrySetResult(null);
    }

    private void HideSteamGuardPanel()
    {
        _pendingUiActions.Enqueue(() =>
        {
            if (_steamGuardPanel is not null) _steamGuardPanel.Visible = false;
            if (_steamGuard is not null) _steamGuard.Text = string.Empty;
            _steamGuardCompletion = null;
        });
    }

    private async Task UploadItchAsync(DeploySettings settings, DeployCredentials credentials, string contentRoot, string workingDirectory)
    {
        Require(settings.ItchTarget, "itch.io target");
        Require(settings.ItchChannel, "itch.io channel");
        Require(credentials.ButlerApiKey, "Butler API key");
        string executable = ResolveExecutable(settings.ButlerPath, "butler", DeployToolKind.Butler);
        var arguments = new List<string> { "push", contentRoot, $"{settings.ItchTarget}:{settings.ItchChannel}" };
        if (!string.IsNullOrWhiteSpace(settings.ItchUserVersion))
        {
            arguments.Add("--userversion");
            arguments.Add(VdfGenerator.ResolveMacros(settings.ItchUserVersion));
        }

        if (settings.ItchIfChanged)
        {
            arguments.Add("--if-changed");
        }

        foreach (string pattern in VdfGenerator.SplitPatterns(settings.ItchIgnoreFiles))
        {
            arguments.Add("--ignore");
            arguments.Add(pattern);
        }

        var environment = new Dictionary<string, string> { ["BUTLER_API_KEY"] = credentials.ButlerApiKey };
        _pendingLogs.Enqueue("Uploading to itch.io...");
        CliProcessResult result = await CliProcessRunner.RunAsync(executable, arguments, workingDirectory, environment, QueueProcessOutput).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"butler failed with exit code {result.ExitCode}.");
        }

        _pendingLogs.Enqueue("itch.io upload completed.");
    }

    private void SaveSettingsPressed()
    {
        Error error = DeployConfigStore.SaveSettings(ReadSettingsFromUi());
        AppendLog(error == Error.Ok ? $"Settings saved to {DeployConfigStore.SettingsPath}." : $"Could not save settings: {error}");
    }

    private void SaveCredentialsPressed()
    {
        Error error = DeployConfigStore.SaveCredentials(ReadCredentialsFromUi());
        AppendLog(error == Error.Ok ? $"Credentials saved encrypted at {DeployConfigStore.CredentialsPath}." : $"Could not save credentials: {error}");
    }

    private DeploySettings ReadSettingsFromUi()
    {
        DeployTargets targets = DeployTargets.None;
        if (_steamEnabled?.ButtonPressed == true) targets |= DeployTargets.Steam;
        if (_itchEnabled?.ButtonPressed == true) targets |= DeployTargets.ItchIo;
        return new DeploySettings
        {
            Targets = targets,
            ExportPreset = _preset is { ItemCount: > 0 } ? _preset.GetItemText(_preset.Selected) : string.Empty,
            ExportOutputPath = _exportOutput?.Text.Trim() ?? string.Empty,
            SteamCmdPath = _steamCmd?.Text.Trim() ?? string.Empty,
            SteamAppId = _steamAppId?.Text.Trim() ?? string.Empty,
            SteamDepotId = _steamDepotId?.Text.Trim() ?? string.Empty,
            SteamBuildDescription = _steamDescription?.Text ?? string.Empty,
            SteamSetLive = _steamSetLive?.ButtonPressed == true,
            SteamBranch = _steamBranch?.Text.Trim() ?? string.Empty,
            SteamIgnoreFiles = _steamIgnore?.Text ?? string.Empty,
            ButlerPath = _butler?.Text.Trim() ?? string.Empty,
            ItchTarget = _itchTarget?.Text.Trim() ?? string.Empty,
            ItchChannel = _itchChannel?.Text.Trim() ?? string.Empty,
            ItchUserVersion = _itchVersion?.Text ?? string.Empty,
            ItchIfChanged = _itchIfChanged?.ButtonPressed == true,
            ItchIgnoreFiles = _itchIgnore?.Text ?? string.Empty,
        };
    }

    private DeployCredentials ReadCredentialsFromUi() => new()
    {
        SteamUsername = _steamUsername?.Text.Trim() ?? string.Empty,
        SteamPassword = _steamPassword?.Text ?? string.Empty,
        SteamGuardCode = string.Empty,
        ButlerApiKey = _butlerApiKey?.Text.Trim() ?? string.Empty,
    };

    private static void PopulatePresets(OptionButton option, string selectedPreset)
    {
        option.Clear();
        IReadOnlyList<string> presets = ExportPresetReader.ReadPresetNames();
        for (int index = 0; index < presets.Count; index++)
        {
            option.AddItem(presets[index]);
            if (presets[index] == selectedPreset)
            {
                option.Select(index);
            }
        }
    }

    private void RefreshPresetsIfChanged()
    {
        if (_preset is null)
        {
            return;
        }

        string currentStamp = GetPresetFileStamp();
        if (currentStamp == _presetFileStamp)
        {
            return;
        }

        string selectedPreset = _preset.ItemCount > 0
            ? _preset.GetItemText(_preset.Selected)
            : string.Empty;
        _presetFileStamp = currentStamp;
        PopulatePresets(_preset, selectedPreset);
        AppendLog($"Export presets refreshed ({_preset.ItemCount} found).");
    }

    private static string GetPresetFileStamp()
    {
        try
        {
            string path = ProjectSettings.GlobalizePath("res://export_presets.cfg");
            var info = new FileInfo(path);
            return info.Exists ? $"{info.LastWriteTimeUtc.Ticks}:{info.Length}" : "missing";
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return "invalid";
        }
    }

    private void UpdateButtonState()
    {
        bool busy = Volatile.Read(ref _busy) != 0;
        bool hasPreset = _preset is { ItemCount: > 0 };
        if (_buildButton is not null) _buildButton.Disabled = busy || !hasPreset;
        if (_uploadButton is not null) _uploadButton.Disabled = busy;
        if (_buildUploadButton is not null) _buildUploadButton.Disabled = busy || !hasPreset;
        if (_steamLoginTestButton is not null)
        {
            bool canTestLogin = TryResolveExecutablePath(_steamCmd?.Text, DeployToolKind.SteamCmd, out _) &&
                                !string.IsNullOrWhiteSpace(_steamUsername?.Text) &&
                                !string.IsNullOrWhiteSpace(_steamPassword?.Text);
            _steamLoginTestButton.Disabled = busy || !canTestLogin;
        }
        if (_steamDownloadButton is not null)
        {
            bool missing = !TryResolveExecutablePath(_steamCmd?.Text, DeployToolKind.SteamCmd, out _);
            _steamDownloadButton.Visible = missing;
            _steamDownloadButton.Disabled = busy;
        }

        if (_butlerDownloadButton is not null)
        {
            bool missing = !TryResolveExecutablePath(_butler?.Text, DeployToolKind.Butler, out _);
            _butlerDownloadButton.Visible = missing;
            _butlerDownloadButton.Disabled = busy;
        }
    }

    private void AppendLog(string message)
    {
        GD.Print($"{LogPrefix} {message}");
        _log?.AppendText($"[{DateTime.Now:HH:mm:ss}] {message}\n");
    }

    private void QueueProcessOutput(string line) => _pendingLogs.Enqueue(line);

    private static string ResolveProjectPath(string path, string projectPath)
    {
        Require(path, "Export output file");
        return Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(projectPath, path));
    }

    private static string ResolveExecutable(string configuredPath, string displayName, DeployToolKind tool)
    {
        Require(configuredPath, displayName + " path");
        if (TryResolveExecutablePath(configuredPath, tool, out string executablePath))
        {
            return executablePath;
        }

        throw new FileNotFoundException($"{displayName} executable not found at configured path: {configuredPath}");
    }

    private static bool TryResolveExecutablePath(string? configuredPath, DeployToolKind tool, out string executablePath)
    {
        executablePath = string.Empty;
        if (string.IsNullOrWhiteSpace(configuredPath))
        {
            return false;
        }

        try
        {
            string path = Path.GetFullPath(configuredPath.Trim());
            if (File.Exists(path))
            {
                executablePath = path;
                return true;
            }

            string executableName = tool switch
            {
                DeployToolKind.SteamCmd when OperatingSystem.IsWindows() => "steamcmd.exe",
                DeployToolKind.SteamCmd => "steamcmd.sh",
                DeployToolKind.Butler when OperatingSystem.IsWindows() => "butler.exe",
                _ => "butler",
            };

            if (Directory.Exists(path))
            {
                string directoryCandidate = Path.Combine(path, executableName);
                if (File.Exists(directoryCandidate))
                {
                    executablePath = directoryCandidate;
                    return true;
                }
            }

            if (OperatingSystem.IsWindows() &&
                !path.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) &&
                File.Exists(path + ".exe"))
            {
                executablePath = path + ".exe";
                return true;
            }
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        return false;
    }

    private static void Require(string value, string field)
    {
        if (string.IsNullOrWhiteSpace(value)) throw new InvalidOperationException($"Missing required field: {field}");
    }

    private static GridContainer CreateGrid(Control parent)
    {
        var grid = new GridContainer { Columns = 2, SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        parent.AddChild(grid);
        return grid;
    }

    private static void AddSection(Control parent, string title)
    {
        parent.AddChild(new HSeparator());
        parent.AddChild(new Label { Text = title });
    }

    private static void AddRow(GridContainer grid, string label, Control control)
    {
        grid.AddChild(new Label { Text = label });
        grid.AddChild(control);
    }

    private static LineEdit AddLineRow(GridContainer grid, string label, string value, string placeholder = "", bool secret = false)
    {
        var edit = new LineEdit
        {
            Text = value,
            PlaceholderText = placeholder,
            Secret = secret,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        AddRow(grid, label, edit);
        return edit;
    }

    private static CheckBox AddCheckRow(GridContainer grid, string label, bool value)
    {
        var checkBox = new CheckBox { ButtonPressed = value };
        AddRow(grid, label, checkBox);
        return checkBox;
    }

    private static Button AddButton(Control parent, string text, Action action)
    {
        var button = new Button { Text = text };
        button.Pressed += action;
        parent.AddChild(button);
        return button;
    }

    private static bool HasArgument(string expected)
    {
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument == expected) return true;
        }

        return false;
    }

    private static bool HasProbeArgument() => HasArgument("--deployer-probe");

    private static LineEdit AddToolPathRow(
        GridContainer grid,
        string label,
        string value,
        string placeholder,
        Action downloadAction,
        out Button downloadButton)
    {
        var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        var edit = new LineEdit
        {
            Text = value,
            PlaceholderText = placeholder,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        row.AddChild(edit);
        downloadButton = new Button { Text = "Download & Install" };
        downloadButton.Pressed += downloadAction;
        row.AddChild(downloadButton);
        AddRow(grid, label, row);
        return edit;
    }

    private void OnProbeButtonPressed()
    {
        UpdateButtonState();
        GD.Print($"{LogPrefix} PROBE_BUTTON_PRESSED");
        GD.Print($"{LogPrefix} STEAM_DOWNLOAD_VISIBLE={_steamDownloadButton?.Visible}");
        GD.Print($"{LogPrefix} BUTLER_DOWNLOAD_VISIBLE={_butlerDownloadButton?.Visible}");
        GD.Print($"{LogPrefix} STEAM_GUARD_VISIBLE={_steamGuardPanel?.Visible}");
        GD.Print($"{LogPrefix} EXPORT_PRESET_COUNT={_preset?.ItemCount}");
        GD.Print($"{LogPrefix} GUARD_PATTERN_MATCHES={CliProcessRunner.IsSteamGuardRequired("FAILED login with result code RequireTwoFactorCode")}");

        string missingPath = Path.Combine(
            ProjectSettings.GlobalizePath("res://.deployer/tools"),
            "probe-definitely-missing");
        GD.Print($"{LogPrefix} MISSING_STEAMCMD_RESOLVES={TryResolveExecutablePath(missingPath, DeployToolKind.SteamCmd, out _)}");
        GD.Print($"{LogPrefix} MISSING_BUTLER_RESOLVES={TryResolveExecutablePath(missingPath, DeployToolKind.Butler, out _)}");
    }
}
#endif
