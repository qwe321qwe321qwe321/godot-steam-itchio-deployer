#if TOOLS
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Godot;

namespace GodotSteamItchIoDeployer;

[Tool]
public partial class SteamItchIoDeployerPlugin : EditorPlugin
{
    private const string LogPrefix = "[GodotSteamItchIoDeployer]";
    private static readonly Regex AnsiControlSequence = new(
        "\\x1B\\[([0-?]*)([ -/]*)([@-~])",
        RegexOptions.Compiled);

    private readonly ConcurrentQueue<string> _pendingLogs = new();
    private readonly ConcurrentQueue<Action> _pendingUiActions = new();
    private EditorDock? _dock;
    private OptionButton? _preset;
    private LineEdit? _exportOutput;
    private CheckBox? _buildWithDebug;
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
    private Button? _buildFoldoutButton;
    private Button? _steamFoldoutButton;
    private CheckBox? _itchEnabled;
    private LineEdit? _butler;
    private LineEdit? _itchTarget;
    private LineEdit? _itchChannel;
    private LineEdit? _itchVersion;
    private CheckBox? _itchIfChanged;
    private LineEdit? _itchIgnore;
    private LineEdit? _butlerApiKey;
    private Button? _butlerDownloadButton;
    private Button? _itchFoldoutButton;
    private Button? _saveSettingsButton;
    private EditorResourcePicker? _buildConfigPicker;
    private EditorResourcePicker? _steamConfigPicker;
    private EditorResourcePicker? _itchConfigPicker;
    private Button? _buildButton;
    private Button? _uploadButton;
    private Button? _buildUploadButton;
    private RichTextLabel? _log;
    private int _busy;
    private bool _quitAfterToolInstall;
    private bool _guardUiProbePending;
    private string _presetFileStamp = string.Empty;
    private DeploySettings? _savedSettings;
    private BuildDeployConfig? _buildConfig;
    private bool _resourceAssignmentsDirty;

    public override void _EnterTree()
    {
        _buildConfig = DeployConfigStore.LoadOrCreateBuildConfig();
        DeploySettings settings = DeployConfigStore.ToSettings(_buildConfig);
        DeployCredentials credentials = DeployConfigStore.LoadCredentials();
        _savedSettings = CloneSettings(settings);

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

        VBoxContainer buildContent = AddFoldoutSection(root, "Build", out _buildFoldoutButton);
        var resourceGrid = CreateGrid(buildContent);
        _buildConfigPicker = AddResourceRow<BuildDeployConfig>(resourceGrid, "Build / Deploy Config", _buildConfig);
        _steamConfigPicker = AddResourceRow<SteamDeployConfig>(resourceGrid, "Steam Config", _buildConfig.SteamConfig!);
        _itchConfigPicker = AddResourceRow<ItchIoDeployConfig>(resourceGrid, "itch.io Config", _buildConfig.ItchIoConfig!);
        _buildConfigPicker.ResourceChanged += OnBuildConfigChanged;
        _steamConfigPicker.ResourceChanged += OnSteamConfigChanged;
        _itchConfigPicker.ResourceChanged += OnItchConfigChanged;

        var buildGrid = CreateGrid(buildContent);
        _preset = new OptionButton { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        PopulatePresets(_preset, settings.ExportPreset);
        _presetFileStamp = GetPresetFileStamp();
        AddRow(buildGrid, "Export Preset", _preset);
        _exportOutput = AddLineRow(buildGrid, "Export Output File", settings.ExportOutputPath, "Example: build/windows/MyGame.exe");
        _buildWithDebug = AddCheckRow(buildGrid, "Build With Debug", settings.BuildWithDebug);

        VBoxContainer steamContent = AddFoldoutSection(root, "Steam", out _steamFoldoutButton);
        _steamEnabled = new CheckBox { Text = "Upload to Steam", ButtonPressed = settings.Targets.HasFlag(DeployTargets.Steam) };
        steamContent.AddChild(_steamEnabled);
        var steamGrid = CreateGrid(steamContent);
        _steamCmd = AddToolPathRow(
            steamGrid,
            "SteamCMD",
            settings.SteamCmdPath,
            "Full path to steamcmd.exe",
            () => StartToolInstall(DeployToolKind.SteamCmd),
            out _steamDownloadButton);
        _steamAppId = AddLineRow(steamGrid, "App ID", settings.SteamAppId);
        _steamDepotId = AddLineRow(steamGrid, "Depot ID", settings.SteamDepotId);
        _steamDescription = AddLineRow(
            steamGrid,
            "Build Description",
            settings.SteamBuildDescription,
            "Supports {Date}, {DateTime}, and {GitSHA}");
        _steamSetLive = AddCheckRow(steamGrid, "Set Live", settings.SteamSetLive);
        _steamBranch = AddLineRow(steamGrid, "Branch", settings.SteamBranch);
        _steamIgnore = AddLineRow(steamGrid, "Ignore Files", settings.SteamIgnoreFiles, "Comma-separated patterns");
        _steamUsername = AddLineRow(steamGrid, "Username", credentials.SteamUsername);
        _steamPassword = AddLineRow(steamGrid, "Password", credentials.SteamPassword, secret: true);
        Button saveSteamCredentials = new() { Text = "Save Encrypted Credentials" };
        saveSteamCredentials.Pressed += SaveCredentialsPressed;
        AddRow(steamGrid, string.Empty, saveSteamCredentials);
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
        steamContent.AddChild(_steamGuardPanel);

        VBoxContainer itchContent = AddFoldoutSection(root, "itch.io", out _itchFoldoutButton);
        _itchEnabled = new CheckBox { Text = "Upload to itch.io", ButtonPressed = settings.Targets.HasFlag(DeployTargets.ItchIo) };
        itchContent.AddChild(_itchEnabled);
        var itchGrid = CreateGrid(itchContent);
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
        Button saveItchCredentials = new() { Text = "Save Encrypted Credentials" };
        saveItchCredentials.Pressed += SaveCredentialsPressed;
        AddRow(itchGrid, string.Empty, saveItchCredentials);

        var persistenceButtons = new HBoxContainer();
        root.AddChild(persistenceButtons);
        _saveSettingsButton = new Button { Text = "Save Settings" };
        _saveSettingsButton.Pressed += SaveSettingsPressed;
        persistenceButtons.AddChild(_saveSettingsButton);

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
            _guardUiProbePending = true;
            _ = RequestSteamGuardCodeAsync("probe");
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

        if (_guardUiProbePending)
        {
            _guardUiProbePending = false;
            GD.Print($"{LogPrefix} STEAM_GUARD_VISIBLE_AFTER_REQUEST={_steamGuardPanel?.Visible}");
            CancelSteamGuardCode();
            GD.Print($"{LogPrefix} STEAM_GUARD_VISIBLE_AFTER_CANCEL={_steamGuardPanel?.Visible}");
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
        Error saveError = SaveSettingsToResources(settings);
        if (saveError != Error.Ok)
        {
            Interlocked.Exchange(ref _busy, 0);
            AppendLog($"Could not save settings: {saveError}");
            return;
        }

        _savedSettings = CloneSettings(settings);

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
                    field.Text = DeployConfigStore.PreferProjectRelativePath(executablePath);
                }

                DeploySettings updatedSettings = ReadSettingsFromUi();
                Error error = SaveSettingsToResources(updatedSettings);
                if (error == Error.Ok)
                {
                    _savedSettings = CloneSettings(updatedSettings);
                }
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
                string exportMode = settings.BuildWithDebug ? "--export-debug" : "--export-release";
                var arguments = new[] { "--headless", "--path", projectPath, exportMode, settings.ExportPreset, outputPath };
                _pendingLogs.Enqueue($"Exporting preset '{settings.ExportPreset}' ({(settings.BuildWithDebug ? "debug" : "release")}) to {outputPath}");
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
                CliProcessRunner.IsSteamGuardRequired,
                GetSteamConsoleLogPath(executable)).ConfigureAwait(false);
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

    private static string GetSteamConsoleLogPath(string steamCmdExecutable)
    {
        string? steamCmdDirectory = Path.GetDirectoryName(steamCmdExecutable);
        return string.IsNullOrWhiteSpace(steamCmdDirectory)
            ? string.Empty
            : Path.Combine(steamCmdDirectory, "logs", "console_log.txt");
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

            if (_steamFoldoutButton is not null)
            {
                _steamFoldoutButton.ButtonPressed = true;
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
        DeploySettings settings = ReadSettingsFromUi();
        Error error = SaveSettingsToResources(settings);
        if (error == Error.Ok)
        {
            if (_steamCmd is not null) _steamCmd.Text = settings.SteamCmdPath;
            if (_butler is not null) _butler.Text = settings.ButlerPath;
            _savedSettings = CloneSettings(settings);
        }
        AppendLog(error == Error.Ok ? $"Settings saved to {_buildConfig?.ResourcePath}." : $"Could not save settings: {error}");
    }

    private Error SaveSettingsToResources(DeploySettings settings)
    {
        if (_buildConfig is null)
        {
            return Error.InvalidParameter;
        }

        Error error = DeployConfigStore.SaveSettings(settings, _buildConfig);
        if (error == Error.Ok)
        {
            _resourceAssignmentsDirty = false;
            if (_buildConfigPicker is not null) _buildConfigPicker.EditedResource = _buildConfig;
            if (_steamConfigPicker is not null) _steamConfigPicker.EditedResource = _buildConfig.SteamConfig;
            if (_itchConfigPicker is not null) _itchConfigPicker.EditedResource = _buildConfig.ItchIoConfig;
        }
        return error;
    }

    private void OnBuildConfigChanged(Resource resource)
    {
        if (resource is not BuildDeployConfig selected) return;
        DeployConfigStore.EnsureNestedConfigs(selected);
        _buildConfig = selected;
        if (_steamConfigPicker is not null) _steamConfigPicker.EditedResource = selected.SteamConfig;
        if (_itchConfigPicker is not null) _itchConfigPicker.EditedResource = selected.ItchIoConfig;
        DeploySettings settings = DeployConfigStore.ToSettings(selected);
        ApplySettingsToUi(settings);
        _savedSettings = CloneSettings(settings);
        _resourceAssignmentsDirty = false;
        if (!string.IsNullOrWhiteSpace(selected.ResourcePath))
        {
            DeployConfigStore.SaveSelectedBuildConfigPath(selected.ResourcePath);
        }
        AppendLog($"Loaded build/deploy config: {selected.ResourcePath}");
    }

    private void OnSteamConfigChanged(Resource resource)
    {
        if (_buildConfig is null || resource is not SteamDeployConfig selected) return;
        _buildConfig.SteamConfig = selected;
        ApplySettingsToUi(DeployConfigStore.ToSettings(_buildConfig));
        _resourceAssignmentsDirty = true;
        AppendLog($"Selected Steam config: {selected.ResourcePath}");
    }

    private void OnItchConfigChanged(Resource resource)
    {
        if (_buildConfig is null || resource is not ItchIoDeployConfig selected) return;
        _buildConfig.ItchIoConfig = selected;
        ApplySettingsToUi(DeployConfigStore.ToSettings(_buildConfig));
        _resourceAssignmentsDirty = true;
        AppendLog($"Selected itch.io config: {selected.ResourcePath}");
    }

    private void ApplySettingsToUi(DeploySettings settings)
    {
        if (_preset is not null) PopulatePresets(_preset, settings.ExportPreset);
        if (_exportOutput is not null) _exportOutput.Text = settings.ExportOutputPath;
        if (_buildWithDebug is not null) _buildWithDebug.ButtonPressed = settings.BuildWithDebug;
        if (_steamEnabled is not null) _steamEnabled.ButtonPressed = settings.Targets.HasFlag(DeployTargets.Steam);
        if (_steamCmd is not null) _steamCmd.Text = settings.SteamCmdPath;
        if (_steamAppId is not null) _steamAppId.Text = settings.SteamAppId;
        if (_steamDepotId is not null) _steamDepotId.Text = settings.SteamDepotId;
        if (_steamDescription is not null) _steamDescription.Text = settings.SteamBuildDescription;
        if (_steamSetLive is not null) _steamSetLive.ButtonPressed = settings.SteamSetLive;
        if (_steamBranch is not null) _steamBranch.Text = settings.SteamBranch;
        if (_steamIgnore is not null) _steamIgnore.Text = settings.SteamIgnoreFiles;
        if (_itchEnabled is not null) _itchEnabled.ButtonPressed = settings.Targets.HasFlag(DeployTargets.ItchIo);
        if (_butler is not null) _butler.Text = settings.ButlerPath;
        if (_itchTarget is not null) _itchTarget.Text = settings.ItchTarget;
        if (_itchChannel is not null) _itchChannel.Text = settings.ItchChannel;
        if (_itchVersion is not null) _itchVersion.Text = settings.ItchUserVersion;
        if (_itchIfChanged is not null) _itchIfChanged.ButtonPressed = settings.ItchIfChanged;
        if (_itchIgnore is not null) _itchIgnore.Text = settings.ItchIgnoreFiles;
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
            BuildWithDebug = _buildWithDebug?.ButtonPressed == true,
            SteamCmdPath = DeployConfigStore.PreferProjectRelativePath(_steamCmd?.Text ?? string.Empty),
            SteamAppId = _steamAppId?.Text.Trim() ?? string.Empty,
            SteamDepotId = _steamDepotId?.Text.Trim() ?? string.Empty,
            SteamBuildDescription = _steamDescription?.Text ?? string.Empty,
            SteamSetLive = _steamSetLive?.ButtonPressed == true,
            SteamBranch = _steamBranch?.Text.Trim() ?? string.Empty,
            SteamIgnoreFiles = _steamIgnore?.Text ?? string.Empty,
            ButlerPath = DeployConfigStore.PreferProjectRelativePath(_butler?.Text ?? string.Empty),
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

    private static DeploySettings CloneSettings(DeploySettings source) => new()
    {
        Targets = source.Targets,
        ExportPreset = source.ExportPreset,
        ExportOutputPath = source.ExportOutputPath,
        BuildWithDebug = source.BuildWithDebug,
        SteamCmdPath = source.SteamCmdPath,
        SteamAppId = source.SteamAppId,
        SteamDepotId = source.SteamDepotId,
        SteamBuildDescription = source.SteamBuildDescription,
        SteamSetLive = source.SteamSetLive,
        SteamBranch = source.SteamBranch,
        SteamIgnoreFiles = source.SteamIgnoreFiles,
        ButlerPath = source.ButlerPath,
        ItchTarget = source.ItchTarget,
        ItchChannel = source.ItchChannel,
        ItchUserVersion = source.ItchUserVersion,
        ItchIfChanged = source.ItchIfChanged,
        ItchIgnoreFiles = source.ItchIgnoreFiles,
    };

    private static bool SettingsEqual(DeploySettings left, DeploySettings right) =>
        left.Targets == right.Targets &&
        left.BuildWithDebug == right.BuildWithDebug &&
        left.SteamSetLive == right.SteamSetLive &&
        left.ItchIfChanged == right.ItchIfChanged &&
        string.Equals(left.ExportPreset, right.ExportPreset, StringComparison.Ordinal) &&
        string.Equals(left.ExportOutputPath, right.ExportOutputPath, StringComparison.Ordinal) &&
        string.Equals(left.SteamCmdPath, right.SteamCmdPath, StringComparison.Ordinal) &&
        string.Equals(left.SteamAppId, right.SteamAppId, StringComparison.Ordinal) &&
        string.Equals(left.SteamDepotId, right.SteamDepotId, StringComparison.Ordinal) &&
        string.Equals(left.SteamBuildDescription, right.SteamBuildDescription, StringComparison.Ordinal) &&
        string.Equals(left.SteamBranch, right.SteamBranch, StringComparison.Ordinal) &&
        string.Equals(left.SteamIgnoreFiles, right.SteamIgnoreFiles, StringComparison.Ordinal) &&
        string.Equals(left.ButlerPath, right.ButlerPath, StringComparison.Ordinal) &&
        string.Equals(left.ItchTarget, right.ItchTarget, StringComparison.Ordinal) &&
        string.Equals(left.ItchChannel, right.ItchChannel, StringComparison.Ordinal) &&
        string.Equals(left.ItchUserVersion, right.ItchUserVersion, StringComparison.Ordinal) &&
        string.Equals(left.ItchIgnoreFiles, right.ItchIgnoreFiles, StringComparison.Ordinal);

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
        bool settingsDirty = _resourceAssignmentsDirty || _savedSettings is null || !SettingsEqual(ReadSettingsFromUi(), _savedSettings);
        if (_buildButton is not null) _buildButton.Disabled = busy || !hasPreset;
        if (_uploadButton is not null) _uploadButton.Disabled = busy;
        if (_buildUploadButton is not null) _buildUploadButton.Disabled = busy || !hasPreset;
        if (_saveSettingsButton is not null)
        {
            _saveSettingsButton.Disabled = busy;
            _saveSettingsButton.Text = settingsDirty ? "Save Settings *" : "Save Settings";
            _saveSettingsButton.SelfModulate = settingsDirty ? Color.FromHtml("#ffd866") : Colors.White;
            _saveSettingsButton.TooltipText = settingsDirty
                ? "Settings have unsaved changes."
                : "All settings are saved.";
        }
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
        if (_log is null)
        {
            return;
        }

        _log.AddText($"[{DateTime.Now:HH:mm:ss}] ");
        AppendAnsiText(_log, message);
        _log.Newline();
    }

    private static void AppendAnsiText(RichTextLabel label, string text)
    {
        bool bold = false;
        bool italics = false;
        bool underline = false;
        Color? color = null;
        int offset = 0;

        foreach (Match match in AnsiControlSequence.Matches(text))
        {
            AppendStyledText(label, text[offset..match.Index], bold, italics, underline, color);
            offset = match.Index + match.Length;
            if (match.Groups[3].Value != "m")
            {
                continue;
            }

            string parameters = match.Groups[1].Value;
            string[] codes = string.IsNullOrEmpty(parameters) ? new[] { "0" } : parameters.Split(';');
            foreach (string codeText in codes)
            {
                if (!int.TryParse(codeText, out int code))
                {
                    continue;
                }

                switch (code)
                {
                    case 0:
                        bold = false;
                        italics = false;
                        underline = false;
                        color = null;
                        break;
                    case 1: bold = true; break;
                    case 3: italics = true; break;
                    case 4: underline = true; break;
                    case 22: bold = false; break;
                    case 23: italics = false; break;
                    case 24: underline = false; break;
                    case 39: color = null; break;
                    case >= 30 and <= 37:
                    case >= 90 and <= 97:
                        color = GetAnsiColor(code);
                        break;
                }
            }
        }

        AppendStyledText(label, text[offset..], bold, italics, underline, color);
    }

    private static void AppendStyledText(
        RichTextLabel label,
        string text,
        bool bold,
        bool italics,
        bool underline,
        Color? color)
    {
        if (string.IsNullOrEmpty(text))
        {
            return;
        }

        int pushedStyles = 0;
        if (color is Color textColor)
        {
            label.PushColor(textColor);
            pushedStyles++;
        }

        if (bold && italics)
        {
            label.PushBoldItalics();
            pushedStyles++;
        }
        else if (bold)
        {
            label.PushBold();
            pushedStyles++;
        }
        else if (italics)
        {
            label.PushItalics();
            pushedStyles++;
        }

        if (underline)
        {
            label.PushUnderline();
            pushedStyles++;
        }

        label.AddText(text);
        while (pushedStyles-- > 0)
        {
            label.Pop();
        }
    }

    private static Color GetAnsiColor(int code) => code switch
    {
        30 => Color.FromHtml("#000000"),
        31 => Color.FromHtml("#cd3131"),
        32 => Color.FromHtml("#0dbc79"),
        33 => Color.FromHtml("#e5e510"),
        34 => Color.FromHtml("#2472c8"),
        35 => Color.FromHtml("#bc3fbc"),
        36 => Color.FromHtml("#11a8cd"),
        37 => Color.FromHtml("#e5e5e5"),
        90 => Color.FromHtml("#808080"),
        91 => Color.FromHtml("#f14c4c"),
        92 => Color.FromHtml("#23d18b"),
        93 => Color.FromHtml("#f5f543"),
        94 => Color.FromHtml("#3b8eea"),
        95 => Color.FromHtml("#d670d6"),
        96 => Color.FromHtml("#29b8db"),
        97 => Color.FromHtml("#ffffff"),
        _ => Colors.White,
    };

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
            string path = DeployConfigStore.ResolveProjectPath(configuredPath);
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

    private static VBoxContainer AddFoldoutSection(Control parent, string title, out Button foldoutButton)
    {
        parent.AddChild(new HSeparator());
        var content = new VBoxContainer
        {
            Visible = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        foldoutButton = new Button
        {
            Text = $"▼ {title}",
            ToggleMode = true,
            ButtonPressed = true,
            Alignment = HorizontalAlignment.Left,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        Button capturedButton = foldoutButton;
        capturedButton.Toggled += expanded =>
        {
            content.Visible = expanded;
            capturedButton.Text = $"{(expanded ? "▼" : "▶")} {title}";
        };
        parent.AddChild(capturedButton);
        parent.AddChild(content);
        return content;
    }

    private static void AddRow(GridContainer grid, string label, Control control)
    {
        grid.AddChild(new Label { Text = label });
        grid.AddChild(control);
    }

    private static EditorResourcePicker AddResourceRow<T>(GridContainer grid, string label, T resource)
        where T : Resource
    {
        var picker = new EditorResourcePicker
        {
            BaseType = typeof(T).Name,
            EditedResource = resource,
            Editable = true,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        AddRow(grid, label, picker);
        return picker;
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
        GD.Print($"{LogPrefix} STEAM_PATH_IS_RELATIVE={!Path.IsPathRooted(_steamCmd?.Text ?? string.Empty)}");
        GD.Print($"{LogPrefix} BUTLER_PATH_IS_RELATIVE={!Path.IsPathRooted(_butler?.Text ?? string.Empty)}");
        GD.Print($"{LogPrefix} STEAM_GUARD_VISIBLE={_steamGuardPanel?.Visible}");
        GD.Print($"{LogPrefix} STEAM_LOGIN_TEST_BUTTON_PRESENT={_steamLoginTestButton is not null}");
        GD.Print($"{LogPrefix} BUILD_CONFIG_EXPANDED={_buildFoldoutButton?.ButtonPressed}");
        GD.Print($"{LogPrefix} STEAM_CONFIG_EXPANDED={_steamFoldoutButton?.ButtonPressed}");
        GD.Print($"{LogPrefix} ITCH_CONFIG_EXPANDED={_itchFoldoutButton?.ButtonPressed}");
        GD.Print($"{LogPrefix} SAVE_SETTINGS_DIRTY={_saveSettingsButton?.Text.EndsWith("*", StringComparison.Ordinal)}");
        if (_exportOutput is not null)
        {
            string originalOutput = _exportOutput.Text;
            _exportOutput.Text += ".probe-unsaved";
            UpdateButtonState();
            GD.Print($"{LogPrefix} SAVE_SETTINGS_DIRTY_AFTER_EDIT={_saveSettingsButton?.Text.EndsWith("*", StringComparison.Ordinal)}");
            _exportOutput.Text = originalOutput;
            UpdateButtonState();
            GD.Print($"{LogPrefix} SAVE_SETTINGS_DIRTY_AFTER_REVERT={_saveSettingsButton?.Text.EndsWith("*", StringComparison.Ordinal)}");
        }
        GD.Print($"{LogPrefix} EXPORT_PRESET_COUNT={_preset?.ItemCount}");
        GD.Print($"{LogPrefix} BUILD_WITH_DEBUG={_buildWithDebug?.ButtonPressed}");
        GD.Print($"{LogPrefix} RESOURCE_CONFIG_MODE={_buildConfig is not null}");
        GD.Print($"{LogPrefix} BUILD_CONFIG_RESOURCE_PATH={_buildConfig?.ResourcePath}");
        GD.Print($"{LogPrefix} STEAM_CONFIG_RESOURCE_PATH={_buildConfig?.SteamConfig?.ResourcePath}");
        GD.Print($"{LogPrefix} ITCH_CONFIG_RESOURCE_PATH={_buildConfig?.ItchIoConfig?.ResourcePath}");
        GD.Print($"{LogPrefix} RESOURCE_CONFIG_REFS_PRESENT={_buildConfig?.SteamConfig is not null && _buildConfig?.ItchIoConfig is not null}");
        string resolvedGitSha = VdfGenerator.ResolveGitSha();
        GD.Print($"{LogPrefix} GIT_SHA_RESOLVED={resolvedGitSha != "NO_SHA"}");
        GD.Print($"{LogPrefix} GIT_SHA_LENGTH={resolvedGitSha.Length}");
        GD.Print($"{LogPrefix} GUARD_PATTERN_MATCHES={CliProcessRunner.IsSteamGuardRequired("FAILED login with result code RequireTwoFactorCode")}");

        string missingPath = Path.Combine(
            ProjectSettings.GlobalizePath("res://.deployer/tools"),
            "probe-definitely-missing");
        GD.Print($"{LogPrefix} MISSING_STEAMCMD_RESOLVES={TryResolveExecutablePath(missingPath, DeployToolKind.SteamCmd, out _)}");
        GD.Print($"{LogPrefix} MISSING_BUTLER_RESOLVES={TryResolveExecutablePath(missingPath, DeployToolKind.Butler, out _)}");
    }
}
#endif
