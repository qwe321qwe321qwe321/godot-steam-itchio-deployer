#if TOOLS
#nullable enable
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Godot;
using SteamItchIoDeployerCore;

namespace GodotSteamItchIoDeployer;

[Tool]
public partial class SteamItchIoDeployerPlugin : EditorPlugin
{
    private const string LogPrefix = "[GodotSteamItchIoDeployer]";
    private static readonly Regex AnsiControlSequence = new(
        "\\x1B\\[([0-?]*)([ -/]*)([@-~])",
        RegexOptions.Compiled);
    private static readonly Regex PlaytestPresetName = new("playtest", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex ProductionPresetName = new("production|release", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Godot performs managed export through a child process, so derive the deployment flavor
    // from the selected preset name and pass it through the environment-backed BuildFlavor
    // MSBuild property for consuming C# projects.
    private static string ResolveBuildFlavor(string exportPreset)
    {
        if (PlaytestPresetName.IsMatch(exportPreset))
            return "Playtest";
        if (ProductionPresetName.IsMatch(exportPreset))
            return "Production";
        return "Dev";
    }

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
    private VBoxContainer? _steamContent;
    private CheckBox? _itchEnabled;
    private LineEdit? _butler;
    private LineEdit? _itchTarget;
    private LineEdit? _itchChannel;
    private LineEdit? _itchVersion;
    private CheckBox? _itchIfChanged;
    private LineEdit? _itchIgnore;
    private LineEdit? _butlerApiKey;
    private Button? _butlerDownloadButton;
    private Button? _consoleFoldoutButton;
    private VBoxContainer? _consoleContent;
    private HBoxContainer? _settingsColumns;
    private Button? _saveSettingsButton;
    private EditorResourcePicker? _buildConfigPicker;
    private EditorResourcePicker? _steamConfigPicker;
    private EditorResourcePicker? _itchConfigPicker;
    private Button? _buildButton;
    private Button? _uploadButton;
    private Button? _buildUploadButton;
    private Button? _cancelButton;
    private Button? _batchCancelButton;
    private RichTextLabel? _log;
    private int _busy;
    private CancellationTokenSource? _operationCts;
    private bool _quitAfterToolInstall;
    private bool _guardUiProbePending;
    private string _presetFileStamp = string.Empty;
    private DeploySettings? _savedSettings;
    private BuildDeployConfig? _buildConfig;
    private bool _resourceAssignmentsDirty;

    // Main tab toolbar (Deploy vs. Batch Build & Upload).
    private enum MainTab { Deploy, Batch }
    private MainTab _mainTab = MainTab.Deploy;
    private VBoxContainer? _deployTabContent;
    private VBoxContainer? _batchTabContent;

    // Batch build/upload state. Slots may be temporarily unassigned (null) while the user is
    // still picking a config, mirroring the reference Unity implementation's batch list.
    private readonly List<BuildDeployConfig?> _batchConfigs = new();
    private VBoxContainer? _batchListContainer;
    private Button? _batchAddButton;
    private Button? _batchBuildOnlyButton;
    private Button? _batchUploadOnlyButton;
    private Button? _batchBuildUploadButton;
    private Label? _batchUploadOnlyInfoLabel;
    private Label? _batchGeneralHintLabel;
    private Label? _batchProgressLabel;
    private ProgressBar? _batchProgressBar;
    private bool _isBatchMode;
    private int _batchCurrentIndex;
    private int _batchCount;
    private string _batchStatusText = string.Empty;

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

        var root = new VBoxContainer
        {
            Name = "DeployerContent",
            CustomMinimumSize = new Vector2(760, 640),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        root.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _dock.AddChild(root);

        root.AddChild(new Label { Text = "Godot Steam / itch.io Deployer" });
        root.AddChild(new Label { Text = "Build once, then upload the exported directory to the selected services." });

        var tabBar = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        root.AddChild(tabBar);
        var mainTabGroup = new ButtonGroup();
        Button deployTabButton = new()
        {
            Text = "Deploy",
            ToggleMode = true,
            ButtonPressed = true,
            ButtonGroup = mainTabGroup,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        Button batchTabButton = new()
        {
            Text = "Batch Build & Upload",
            ToggleMode = true,
            ButtonGroup = mainTabGroup,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        deployTabButton.Toggled += pressed => { if (pressed) SetMainTab(MainTab.Deploy); };
        batchTabButton.Toggled += pressed => { if (pressed) SetMainTab(MainTab.Batch); };
        tabBar.AddChild(deployTabButton);
        tabBar.AddChild(batchTabButton);

        _deployTabContent = new VBoxContainer
        {
            Name = "DeployTabContent",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        root.AddChild(_deployTabContent);

        var buildConfigGrid = CreateGrid(_deployTabContent);
        _buildConfigPicker = AddResourceRow<BuildDeployConfig>(buildConfigGrid, "Build / Deploy Config", _buildConfig);

        var actionButtons = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        _deployTabContent.AddChild(actionButtons);
        _saveSettingsButton = new Button { Text = "Save Settings" };
        _saveSettingsButton.Pressed += SaveSettingsPressed;
        actionButtons.AddChild(_saveSettingsButton);
        _buildButton = AddButton(actionButtons, "Build", () => StartWorkflow(build: true, upload: false));
        _uploadButton = AddButton(actionButtons, "Upload", () => StartWorkflow(build: false, upload: true));
        _buildUploadButton = AddButton(actionButtons, "Build & Upload", () => StartWorkflow(build: true, upload: true));
        _cancelButton = new Button { Text = "Cancel", Disabled = true };
        _cancelButton.Pressed += CancelCurrentOperation;
        actionButtons.AddChild(_cancelButton);

        var scroll = new ScrollContainer
        {
            Name = "DeployerSettingsScroll",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        _deployTabContent.AddChild(scroll);
        _settingsColumns = new HBoxContainer
        {
            Name = "DeployerSettingsColumns",
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        scroll.AddChild(_settingsColumns);

        VBoxContainer buildColumn = AddSettingsColumn(_settingsColumns, "BuildColumn");
        VBoxContainer steamColumn = AddSettingsColumn(_settingsColumns, "SteamColumn");
        VBoxContainer itchColumn = AddSettingsColumn(_settingsColumns, "ItchColumn");

        VBoxContainer buildContent = AddStaticSection(buildColumn, "Build");
        var resourceGrid = CreateGrid(buildContent);
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

        _steamContent = AddStaticSection(steamColumn, "Steam");
        _steamEnabled = new CheckBox { Text = "Upload to Steam", ButtonPressed = settings.Targets.HasFlag(DeployTargets.Steam) };
        _steamContent.AddChild(_steamEnabled);
        var steamGrid = CreateGrid(_steamContent);
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
        _steamContent.AddChild(_steamGuardPanel);

        VBoxContainer itchContent = AddStaticSection(itchColumn, "itch.io");
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

        _batchTabContent = new VBoxContainer
        {
            Name = "BatchTabContent",
            Visible = false,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        root.AddChild(_batchTabContent);
        BuildBatchTabUi(_batchTabContent);

        _consoleContent = AddFoldoutSection(root, "Console Result", out _consoleFoldoutButton, expanded: false);
        _log = new RichTextLabel
        {
            CustomMinimumSize = new Vector2(0, 220),
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
            ScrollFollowing = true,
            SelectionEnabled = true,
        };
        _consoleContent.AddChild(_log);

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
            GD.Print($"{LogPrefix} STEAM_SECTION_VISIBLE_AFTER_GUARD={_steamContent?.Visible == true}");
            CancelSteamGuardCode();
            GD.Print($"{LogPrefix} STEAM_GUARD_VISIBLE_AFTER_CANCEL={_steamGuardPanel?.Visible}");
        }

        RefreshPresetsIfChanged();
        UpdateButtonState();
    }

    public override void _ExitTree()
    {
        SetProcess(false);
        _operationCts?.Cancel();
        ClearOperationCts();
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

    private void SetMainTab(MainTab tab)
    {
        _mainTab = tab;
        if (_deployTabContent is not null) _deployTabContent.Visible = tab == MainTab.Deploy;
        if (_batchTabContent is not null) _batchTabContent.Visible = tab == MainTab.Batch;
        if (tab == MainTab.Batch)
        {
            // Rows rendered by a pre-reload plugin instance can survive a C# assembly reload
            // while _batchConfigs restarts empty, leaving stale rows beside the per-frame
            // "add at least one config" hint. Re-syncing from the store on every tab switch
            // makes that orphaned UI self-correct.
            LoadBatchConfigsFromStore();
            RebuildBatchList();
        }
    }

    private void BuildBatchTabUi(VBoxContainer parent)
    {
        parent.AddChild(new Label
        {
            Text = "Add multiple Build/Deploy configs. Each one is built and uploaded in sequence using the credentials from the Deploy tab.",
            AutowrapMode = TextServer.AutowrapMode.WordSmart,
        });

        var batchListScroll = new ScrollContainer
        {
            CustomMinimumSize = new Vector2(0, 160),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        parent.AddChild(batchListScroll);
        _batchListContainer = new VBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        batchListScroll.AddChild(_batchListContainer);

        _batchAddButton = new Button { Text = "+ Add Config" };
        _batchAddButton.Pressed += () =>
        {
            _batchConfigs.Add(null);
            SaveBatchConfigsToStore();
            RebuildBatchList();
        };
        parent.AddChild(_batchAddButton);
        parent.AddChild(new HSeparator());

        _batchProgressLabel = new Label { HorizontalAlignment = HorizontalAlignment.Center, Visible = false };
        parent.AddChild(_batchProgressLabel);
        _batchProgressBar = new ProgressBar { Visible = false, ShowPercentage = false };
        parent.AddChild(_batchProgressBar);

        var batchButtons = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
        parent.AddChild(batchButtons);
        _batchBuildOnlyButton = AddButton(batchButtons, "Batch Build Only", () => { _ = StartBatchAsync(build: true, upload: false); });
        _batchUploadOnlyButton = AddButton(batchButtons, "Batch Upload Only", () => { _ = StartBatchAsync(build: false, upload: true); });
        _batchBuildUploadButton = AddButton(batchButtons, "Batch Build & Upload", () => { _ = StartBatchAsync(build: true, upload: true); });
        _batchCancelButton = new Button { Text = "Cancel", Disabled = true };
        _batchCancelButton.Pressed += CancelCurrentOperation;
        batchButtons.AddChild(_batchCancelButton);

        _batchUploadOnlyInfoLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
        parent.AddChild(_batchUploadOnlyInfoLabel);
        _batchGeneralHintLabel = new Label { AutowrapMode = TextServer.AutowrapMode.WordSmart, Visible = false };
        parent.AddChild(_batchGeneralHintLabel);

        parent.AddChild(new HSeparator());
        parent.AddChild(new Label { Text = "Batch uses the same Steam credentials and itch.io API key set in the Deploy tab." });

        LoadBatchConfigsFromStore();
        RebuildBatchList();
    }

    private void RebuildBatchList()
    {
        if (_batchListContainer is null) return;
        foreach (Node child in _batchListContainer.GetChildren())
        {
            _batchListContainer.RemoveChild(child);
            child.QueueFree();
        }

        for (int i = 0; i < _batchConfigs.Count; i++)
        {
            int index = i;
            var row = new HBoxContainer { SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            row.AddChild(new Label { Text = $"{index + 1}.", CustomMinimumSize = new Vector2(24, 0) });

            var picker = new EditorResourcePicker
            {
                BaseType = nameof(BuildDeployConfig),
                EditedResource = _batchConfigs[index],
                Editable = true,
                SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            };
            picker.ResourceChanged += resource =>
            {
                _batchConfigs[index] = resource as BuildDeployConfig;
                SaveBatchConfigsToStore();
            };
            row.AddChild(picker);

            Button upButton = new() { Text = "↑", Disabled = index == 0 };
            upButton.Pressed += () => MoveBatchConfig(index, -1);
            row.AddChild(upButton);

            Button downButton = new() { Text = "↓", Disabled = index == _batchConfigs.Count - 1 };
            downButton.Pressed += () => MoveBatchConfig(index, 1);
            row.AddChild(downButton);

            Button removeButton = new() { Text = "✕" };
            removeButton.Pressed += () => RemoveBatchConfig(index);
            row.AddChild(removeButton);

            _batchListContainer.AddChild(row);
        }
    }

    private void MoveBatchConfig(int index, int delta)
    {
        int target = index + delta;
        if (target < 0 || target >= _batchConfigs.Count) return;
        (_batchConfigs[index], _batchConfigs[target]) = (_batchConfigs[target], _batchConfigs[index]);
        SaveBatchConfigsToStore();
        RebuildBatchList();
    }

    private void RemoveBatchConfig(int index)
    {
        if (index < 0 || index >= _batchConfigs.Count) return;
        _batchConfigs.RemoveAt(index);
        SaveBatchConfigsToStore();
        RebuildBatchList();
    }

    private void LoadBatchConfigsFromStore()
    {
        _batchConfigs.Clear();
        foreach (string path in DeployConfigStore.LoadBatchConfigPaths())
        {
            if (string.IsNullOrWhiteSpace(path))
            {
                _batchConfigs.Add(null);
                continue;
            }

            _batchConfigs.Add(ResourceLoader.Exists(path) ? ResourceLoader.Load<BuildDeployConfig>(path) : null);
        }
    }

    private void SaveBatchConfigsToStore()
    {
        var paths = new List<string>();
        foreach (BuildDeployConfig? cfg in _batchConfigs)
        {
            paths.Add(cfg is not null && !string.IsNullOrWhiteSpace(cfg.ResourcePath) ? cfg.ResourcePath : string.Empty);
        }

        DeployConfigStore.SaveBatchConfigPaths(paths);
    }

    // Non-throwing counterpart of ResolveProjectPath, safe to poll every frame for the batch
    // tab's "ready to upload" status and for the overwrite-confirmation dialog.
    private static string? TryResolveOutputDirectory(DeploySettings settings, string projectPath)
    {
        if (string.IsNullOrWhiteSpace(settings.ExportOutputPath)) return null;
        try
        {
            string outputPath = Path.GetFullPath(Path.IsPathRooted(settings.ExportOutputPath)
                ? settings.ExportOutputPath
                : Path.Combine(projectPath, settings.ExportOutputPath));
            return Path.GetDirectoryName(outputPath);
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    private void UpdateBatchUiState()
    {
        bool busy = Volatile.Read(ref _busy) != 0;
        string projectPath = ProjectSettings.GlobalizePath("res://");
        bool hasConfigs = _batchConfigs.Count > 0;
        bool allAssigned = hasConfigs && _batchConfigs.TrueForAll(c => c is not null);

        int uploadableCount = 0;
        var missingNames = new List<string>();
        int assignedCount = 0;
        if (allAssigned)
        {
            foreach (BuildDeployConfig? cfg in _batchConfigs)
            {
                if (cfg is null) continue;
                assignedCount++;
                DeploySettings itemSettings = DeployConfigStore.ToSettings(cfg);
                string? outputDirectory = TryResolveOutputDirectory(itemSettings, projectPath);
                if (outputDirectory is not null && Directory.Exists(outputDirectory))
                {
                    uploadableCount++;
                }
                else
                {
                    missingNames.Add(string.IsNullOrWhiteSpace(cfg.ResourcePath) ? "(unsaved config)" : cfg.ResourcePath.GetFile());
                }
            }
        }

        if (_batchAddButton is not null) _batchAddButton.Disabled = busy;
        if (_batchBuildOnlyButton is not null) _batchBuildOnlyButton.Disabled = busy || !allAssigned;
        if (_batchBuildUploadButton is not null) _batchBuildUploadButton.Disabled = busy || !allAssigned;
        if (_batchUploadOnlyButton is not null) _batchUploadOnlyButton.Disabled = busy || !allAssigned || uploadableCount == 0;
        if (_batchCancelButton is not null) _batchCancelButton.Disabled = !busy;

        if (_batchGeneralHintLabel is not null)
        {
            if (!hasConfigs)
            {
                _batchGeneralHintLabel.Visible = true;
                _batchGeneralHintLabel.Text = "Add at least one Build/Deploy config to run a batch.";
            }
            else if (!allAssigned)
            {
                _batchGeneralHintLabel.Visible = true;
                _batchGeneralHintLabel.Text = "All config slots must be assigned before running.";
            }
            else
            {
                _batchGeneralHintLabel.Visible = false;
            }
        }

        if (_batchUploadOnlyInfoLabel is not null)
        {
            if (!allAssigned || assignedCount == 0)
            {
                _batchUploadOnlyInfoLabel.Visible = false;
            }
            else if (missingNames.Count == assignedCount)
            {
                _batchUploadOnlyInfoLabel.Visible = true;
                _batchUploadOnlyInfoLabel.Text = "No uploadable configs — build output path does not exist for any config.";
            }
            else if (missingNames.Count > 0)
            {
                _batchUploadOnlyInfoLabel.Visible = true;
                _batchUploadOnlyInfoLabel.Text = $"Build output not found for: {string.Join(", ", missingNames)}. These will be skipped.";
            }
            else
            {
                _batchUploadOnlyInfoLabel.Visible = true;
                _batchUploadOnlyInfoLabel.Text = "All configs have existing build output and are ready to upload.";
            }
        }

        if (_batchProgressLabel is not null)
        {
            _batchProgressLabel.Visible = _isBatchMode;
            _batchProgressLabel.Text = _isBatchMode ? $"[{_batchCurrentIndex + 1}/{_batchCount}] {_batchStatusText}" : string.Empty;
        }

        if (_batchProgressBar is not null)
        {
            _batchProgressBar.Visible = _isBatchMode;
            _batchProgressBar.Value = _batchCount > 0 ? 100.0 * _batchCurrentIndex / _batchCount : 0.0;
        }
    }

    private async Task StartBatchAsync(bool build, bool upload)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            AppendLog("Another deployment operation is already running.");
            return;
        }

        try
        {
            List<BuildDeployConfig> configs = new();
            foreach (BuildDeployConfig? cfg in _batchConfigs)
            {
                if (cfg is null)
                {
                    AppendLog("All batch slots must be assigned before running.");
                    return;
                }

                configs.Add(cfg);
            }

            if (configs.Count == 0)
            {
                AppendLog("Add at least one Build/Deploy config to run a batch.");
                return;
            }

            // Everything below can suspend across the confirmation dialog's await and resume off
            // the main thread (the completion source is deliberately RunContinuationsAsynchronously),
            // so grab every UI-derived value and touch every UI widget before that await, then talk
            // to the UI only through the thread-safe _pendingLogs/_pendingUiActions queues afterward.
            ExpandConsoleResult();
            DeployCredentials credentials = ReadCredentialsFromUi();

            if (build)
            {
                bool confirmed = await ConfirmBatchOutputPathsOverwriteAsync(configs).ConfigureAwait(false);
                if (!confirmed)
                {
                    _pendingLogs.Enqueue("Batch cancelled.");
                    return;
                }
            }

            CancellationToken cancellationToken = BeginOperation();
            _isBatchMode = true;
            _batchCurrentIndex = 0;
            _batchCount = configs.Count;
            _batchStatusText = "Starting...";
            _pendingLogs.Enqueue($"Starting Batch {(build && upload ? "Build & Upload" : build ? "Build" : "Upload")}...");
            await RunBatchAsync(configs, credentials, build, upload, skipMissingOutput: !build && upload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _isBatchMode = false;
            ClearOperationCts();
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task RunBatchAsync(
        List<BuildDeployConfig> configs,
        DeployCredentials credentials,
        bool build,
        bool upload,
        bool skipMissingOutput,
        CancellationToken cancellationToken)
    {
        string projectPath = ProjectSettings.GlobalizePath("res://");
        _pendingLogs.Enqueue($"=== BATCH START: {configs.Count} config(s) ===");

        // Steam's cooldown is a per-AppID limit, not a global one — batching AppID A then AppID B
        // should not make B wait on A's timer. Keyed by SteamAppId; itch-only configs never wait.
        var lastSteamUploadCompletedUtcByAppId = new Dictionary<string, DateTime>(StringComparer.OrdinalIgnoreCase);

        for (int i = 0; i < configs.Count; i++)
        {
            _batchCurrentIndex = i;
            BuildDeployConfig cfg = configs[i];
            DeploySettings itemSettings = DeployConfigStore.ToSettings(cfg);
            string label = string.IsNullOrWhiteSpace(cfg.ResourcePath) ? $"Config {i + 1}" : cfg.ResourcePath.GetFile();

            if (cancellationToken.IsCancellationRequested)
            {
                _pendingLogs.Enqueue($"=== BATCH CANCELLED before config [{i + 1}/{configs.Count}]: {label} ===");
                return;
            }

            if (skipMissingOutput)
            {
                string? outputDirectory = TryResolveOutputDirectory(itemSettings, projectPath);
                if (outputDirectory is null || !Directory.Exists(outputDirectory))
                {
                    _pendingLogs.Enqueue($"--- Config [{i + 1}/{configs.Count}]: {label} — SKIPPED (build output not found: {outputDirectory}) ---");
                    continue;
                }
            }

            _pendingLogs.Enqueue($"--- Config [{i + 1}/{configs.Count}]: {label} ---");

            // Build and upload are two separate RunWorkflowCoreAsync calls (instead of one build+upload
            // call) specifically so the cooldown wait below can sit between them — right before this
            // config's upload starts, not before its build does.
            if (build)
            {
                _batchStatusText = $"Building {label}...";
                bool builtOk = await RunWorkflowCoreAsync(itemSettings, credentials, build: true, upload: false, cancellationToken).ConfigureAwait(false);
                if (!builtOk)
                {
                    string reason = cancellationToken.IsCancellationRequested ? "CANCELLED" : "ABORTED";
                    _pendingLogs.Enqueue($"=== BATCH {reason} at config [{i + 1}/{configs.Count}]: {label} (build) ===");
                    return;
                }
            }

            if (upload)
            {
                bool hasSteamAppId = itemSettings.Targets.HasFlag(DeployTargets.Steam) &&
                    !string.IsNullOrWhiteSpace(itemSettings.SteamAppId);
                if (hasSteamAppId && lastSteamUploadCompletedUtcByAppId.TryGetValue(itemSettings.SteamAppId, out DateTime lastCompletedUtc))
                {
                    double cooldownSeconds = Math.Max(1, cfg.UploadCooldownSeconds);
                    double elapsed = (DateTime.UtcNow - lastCompletedUtc).TotalSeconds;
                    double remaining = cooldownSeconds - elapsed;
                    if (remaining > 0)
                    {
                        int remainingSeconds = (int)Math.Ceiling(remaining);
                        _pendingLogs.Enqueue($"Waiting {remainingSeconds}s before uploading {label} to avoid Steam rate limits on App ID {itemSettings.SteamAppId}...");
                        _batchStatusText = $"Waiting {remainingSeconds}s before uploading {label}...";
                        try
                        {
                            // Log a countdown line at every whole-10-seconds mark instead of going
                            // silent for the whole wait. Timer jitter can wake the delay a hair
                            // early, so a mark we already logged counts as already passed.
                            int loggedMarkSeconds = remainingSeconds % 10 == 0 ? remainingSeconds : int.MaxValue;
                            while (true)
                            {
                                double remainingNow = cooldownSeconds - (DateTime.UtcNow - lastCompletedUtc).TotalSeconds;
                                if (remainingNow <= 0)
                                    break;

                                int markSeconds = (int)(remainingNow / 10.0) * 10;
                                if (markSeconds >= loggedMarkSeconds)
                                    markSeconds -= 10;
                                if (markSeconds < 10)
                                {
                                    await Task.Delay(TimeSpan.FromSeconds(remainingNow), cancellationToken).ConfigureAwait(false);
                                    break;
                                }

                                await Task.Delay(TimeSpan.FromSeconds(remainingNow - markSeconds), cancellationToken).ConfigureAwait(false);
                                _pendingLogs.Enqueue($"Waiting {markSeconds}s before uploading {label} to avoid Steam rate limits on App ID {itemSettings.SteamAppId}...");
                                _batchStatusText = $"Waiting {markSeconds}s before uploading {label}...";
                                loggedMarkSeconds = markSeconds;
                            }
                        }
                        catch (OperationCanceledException)
                        {
                            _pendingLogs.Enqueue($"=== BATCH CANCELLED while waiting to upload config [{i + 1}/{configs.Count}]: {label} ===");
                            return;
                        }
                    }
                }

                _batchStatusText = $"Uploading {label}...";
                bool uploadedOk = await RunWorkflowCoreAsync(itemSettings, credentials, build: false, upload: true, cancellationToken).ConfigureAwait(false);
                if (!uploadedOk)
                {
                    string reason = cancellationToken.IsCancellationRequested ? "CANCELLED" : "ABORTED";
                    _pendingLogs.Enqueue($"=== BATCH {reason} at config [{i + 1}/{configs.Count}]: {label} (upload) ===");
                    return;
                }

                if (hasSteamAppId) lastSteamUploadCompletedUtcByAppId[itemSettings.SteamAppId] = DateTime.UtcNow;
            }

            _pendingLogs.Enqueue($"=== Config [{i + 1}/{configs.Count}] complete ===");
        }

        _pendingLogs.Enqueue($"=== BATCH COMPLETE: all {configs.Count} config(s) processed ===");
    }

    private async Task<bool> ConfirmBatchOutputPathsOverwriteAsync(List<BuildDeployConfig> configs)
    {
        string projectPath = ProjectSettings.GlobalizePath("res://");
        var configsByPath = new Dictionary<string, List<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (BuildDeployConfig cfg in configs)
        {
            DeploySettings itemSettings = DeployConfigStore.ToSettings(cfg);
            string? outputDirectory = TryResolveOutputDirectory(itemSettings, projectPath);
            if (string.IsNullOrWhiteSpace(outputDirectory)) continue;

            string normalizedPath = Path.GetFullPath(outputDirectory);
            if (!configsByPath.TryGetValue(normalizedPath, out List<string>? names))
            {
                names = new List<string>();
                configsByPath[normalizedPath] = names;
            }

            names.Add(string.IsNullOrWhiteSpace(cfg.ResourcePath) ? "(unsaved config)" : cfg.ResourcePath.GetFile());
        }

        var warnings = new List<string>();
        foreach ((string path, List<string> names) in configsByPath)
        {
            bool containsFiles = Directory.Exists(path) && Directory.EnumerateFileSystemEntries(path).Any();
            bool sharedByMultipleConfigs = names.Count > 1;
            if (!containsFiles && !sharedByMultipleConfigs) continue;

            var reasons = new List<string>();
            if (containsFiles) reasons.Add("already contains files");
            if (sharedByMultipleConfigs) reasons.Add("is shared by multiple batch configs");
            warnings.Add($"{string.Join(", ", names)}\n{path}\n({string.Join("; ", reasons)})");
        }

        if (warnings.Count == 0) return true;

        string message = "The following batch build output folders may be overwritten:\n\n" +
            string.Join("\n\n", warnings) +
            "\n\nConfirm all output paths now and continue with the entire batch?";
        return await ShowConfirmationDialogAsync("Build Output Paths Need Confirmation", message).ConfigureAwait(false);
    }

    private Task<bool> ShowConfirmationDialogAsync(string title, string message)
    {
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var dialog = new ConfirmationDialog
        {
            Title = title,
            DialogText = message,
            OkButtonText = "Continue",
            CancelButtonText = "Cancel",
        };
        dialog.Confirmed += () => completion.TrySetResult(true);
        dialog.Canceled += () => completion.TrySetResult(false);
        dialog.CloseRequested += () => completion.TrySetResult(false);
        (_dock ?? (Node?)GetTree().Root)?.AddChild(dialog);
        dialog.PopupCentered(new Vector2I(520, 320));

        _ = completion.Task.ContinueWith(
            _ => _pendingUiActions.Enqueue(() =>
            {
                if (IsInstanceValid(dialog)) dialog.QueueFree();
            }),
            TaskScheduler.Default);

        return completion.Task;
    }

    private void StartWorkflow(bool build, bool upload)
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            AppendLog("Another deployment operation is already running.");
            return;
        }

        ExpandConsoleResult();

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

        CancellationToken cancellationToken = BeginOperation();
        AppendLog($"Starting {(build && upload ? "Build & Upload" : build ? "Build" : "Upload")}...");
        _ = RunWorkflowAsync(settings, credentials, build, upload, cancellationToken);
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

    private async Task RunWorkflowAsync(DeploySettings settings, DeployCredentials credentials, bool build, bool upload, CancellationToken cancellationToken)
    {
        try
        {
            await RunWorkflowCoreAsync(settings, credentials, build, upload, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            ClearOperationCts();
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private CancellationToken BeginOperation()
    {
        _operationCts?.Dispose();
        _operationCts = new CancellationTokenSource();
        return _operationCts.Token;
    }

    private void ClearOperationCts()
    {
        _operationCts?.Dispose();
        _operationCts = null;
    }

    private void CancelCurrentOperation()
    {
        if (_operationCts is { IsCancellationRequested: false } cts)
        {
            AppendLog("Cancellation requested...");
            cts.Cancel();
        }
    }

    // Shared by the single Build/Upload/Build & Upload buttons and the batch loop. Unlike
    // RunWorkflowAsync, this does not own the `_busy` flag or the console log's error framing —
    // the batch loop needs the boolean result to decide whether to abort the rest of the run.
    private async Task<bool> RunWorkflowCoreAsync(DeploySettings settings, DeployCredentials credentials, bool build, bool upload, CancellationToken cancellationToken)
    {
        string? stagingDirectory = null;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            string projectPath = ProjectSettings.GlobalizePath("res://");
            string outputPath = ResolveProjectPath(settings.ExportOutputPath, projectPath);
            string? outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrWhiteSpace(outputDirectory))
            {
                throw new InvalidOperationException("Export output must include a file name.");
            }
            string outputFileName = Path.GetFileName(outputPath);
            if (string.IsNullOrWhiteSpace(outputFileName))
            {
                throw new InvalidOperationException("Export output must include a file name.");
            }

            if (build)
            {
                if (string.IsNullOrWhiteSpace(settings.ExportPreset))
                {
                    throw new InvalidOperationException("Select a Godot export preset before building.");
                }

                stagingDirectory = ExportArtifactValidator.CreateStagingDirectory(projectPath, outputDirectory);
                string stagedOutputPath = Path.Combine(stagingDirectory, outputFileName);
                string godotExecutable = OS.GetExecutablePath();
                string exportMode = settings.BuildWithDebug ? "--export-debug" : "--export-release";
                var arguments = new[] { "--headless", "--path", projectPath, exportMode, settings.ExportPreset, stagedOutputPath };
                string buildFlavor = ResolveBuildFlavor(settings.ExportPreset);
                var buildEnvironment = new Dictionary<string, string> { ["BuildFlavor"] = buildFlavor };
                if (settings.Targets.HasFlag(DeployTargets.Steam))
                {
                    string steamAppId = NormalizeSteamAppId(settings.SteamAppId);
                    buildEnvironment["SteamAppId"] = steamAppId;
                }
                DateTime exportStartedUtc = DateTime.UtcNow;
                string appIdDescription = buildEnvironment.TryGetValue("SteamAppId", out string? configuredAppId)
                    ? $", SteamAppId={configuredAppId}"
                    : string.Empty;
                _pendingLogs.Enqueue($"Exporting preset '{settings.ExportPreset}' ({(settings.BuildWithDebug ? "debug" : "release")}, BuildFlavor={buildFlavor}{appIdDescription}) to staging output {stagedOutputPath}");
                CliProcessResult result = await CliProcessRunner.RunAsync(godotExecutable, arguments, projectPath, buildEnvironment, QueueProcessOutput, cancellationToken: cancellationToken).ConfigureAwait(false);
                if (!result.Succeeded)
                {
                    throw new InvalidOperationException($"Godot export failed with exit code {result.ExitCode}.");
                }
                if (CliProcessRunner.IsGodotExportBuildFailure(result.CombinedOutput))
                {
                    throw new InvalidOperationException("Godot reported a .NET build failure even though the export process returned exit code 0.");
                }

                ExportArtifactValidator.ValidateExportOutput(stagedOutputPath);
                string expectedSha = VdfGenerator.ResolveGitSha();
                string managedAssembly = ExportArtifactValidator.ValidateManagedAssembly(
                    projectPath,
                    settings.BuildWithDebug,
                    expectedSha,
                    exportStartedUtc);
                ExportArtifactValidator.ValidatePackagedManagedAssemblies(stagingDirectory, expectedSha);
                _pendingLogs.Enqueue($"Managed build verified: {Path.GetFileName(managedAssembly)} ({expectedSha})");

                string? previousOutputBackup = ExportArtifactValidator.PromoteStagedBuild(stagingDirectory, outputDirectory);
                stagingDirectory = null;
                if (previousOutputBackup is not null)
                {
                    _pendingLogs.Enqueue($"WARNING: Previous build was kept as a recoverable backup: {previousOutputBackup}");
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
                    await UploadSteamAsync(settings, credentials, outputDirectory, projectPath, cancellationToken).ConfigureAwait(false);
                }

                if (settings.Targets.HasFlag(DeployTargets.ItchIo))
                {
                    await UploadItchAsync(settings, credentials, outputDirectory, projectPath, cancellationToken).ConfigureAwait(false);
                }
            }

            _pendingLogs.Enqueue("Workflow completed successfully.");
            return true;
        }
        catch (OperationCanceledException)
        {
            _pendingLogs.Enqueue("Cancelled by user.");
            return false;
        }
        catch (Exception exception)
        {
            _pendingLogs.Enqueue($"ERROR: {exception.Message}");
            return false;
        }
        finally
        {
            if (stagingDirectory is not null)
            {
                try
                {
                    ExportArtifactValidator.CleanupStagingDirectory(stagingDirectory);
                }
                catch (Exception cleanupException)
                {
                    _pendingLogs.Enqueue($"WARNING: Could not clean export staging directory: {cleanupException.Message}");
                }
            }
        }
    }

    private async Task UploadSteamAsync(DeploySettings settings, DeployCredentials credentials, string contentRoot, string workingDirectory, CancellationToken cancellationToken)
    {
        string steamAppId = NormalizeSteamAppId(settings.SteamAppId);
        settings.SteamAppId = steamAppId;
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
            "Steam upload",
            cancellationToken).ConfigureAwait(false);
        if (!result.Succeeded)
        {
            throw new InvalidOperationException($"SteamCMD failed with exit code {result.ExitCode}.");
        }

        _pendingLogs.Enqueue("Steam upload completed.");
    }

    private static string NormalizeSteamAppId(string value)
    {
        if (!uint.TryParse(value?.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out uint appId)
            || appId == 0)
        {
            throw new InvalidOperationException(
                $"Steam App ID must be a positive decimal Steam AppID; received '{value}'.");
        }

        return appId.ToString(CultureInfo.InvariantCulture);
    }

    private void StartSteamLoginTest()
    {
        if (Interlocked.CompareExchange(ref _busy, 1, 0) != 0)
        {
            AppendLog("Another deployment operation is already running.");
            return;
        }

        CancellationToken cancellationToken = BeginOperation();
        _ = RunSteamLoginTestAsync(ReadSettingsFromUi(), ReadCredentialsFromUi(), cancellationToken);
    }

    private async Task RunSteamLoginTestAsync(DeploySettings settings, DeployCredentials credentials, CancellationToken cancellationToken)
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
                "Steam login test",
                cancellationToken).ConfigureAwait(false);
            _pendingLogs.Enqueue(result.Succeeded
                ? "Steam login test successful."
                : $"ERROR: Steam login test failed with exit code {result.ExitCode}.");
        }
        catch (OperationCanceledException exception)
        {
            _pendingLogs.Enqueue(string.IsNullOrWhiteSpace(exception.Message) ? "Cancelled by user." : exception.Message);
        }
        catch (Exception exception)
        {
            _pendingLogs.Enqueue($"ERROR: Steam login test failed: {exception.Message}");
        }
        finally
        {
            HideSteamGuardPanel();
            ClearOperationCts();
            Interlocked.Exchange(ref _busy, 0);
        }
    }

    private async Task<CliProcessResult> RunSteamCommandWithGuardAsync(
        string executable,
        DeployCredentials credentials,
        IReadOnlyList<string> commandArguments,
        string workingDirectory,
        string operationName,
        CancellationToken cancellationToken)
    {
        string guardCode = string.Empty;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            cancellationToken.ThrowIfCancellationRequested();
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
                GetSteamConsoleLogPath(executable),
                cancellationToken).ConfigureAwait(false);
            if (!result.TerminatedByOutputPattern)
            {
                return result;
            }

            if (attempt == 2)
            {
                throw new InvalidOperationException("Steam Guard verification failed after three attempts.");
            }

            _pendingLogs.Enqueue("Steam Guard code required.");
            // Cancelling while the Steam Guard panel is waiting for input has to unblock the
            // TaskCompletionSource explicitly — it isn't driven by any awaitable that observes the token.
            using (cancellationToken.Register(CancelSteamGuardCode))
            {
                guardCode = await RequestSteamGuardCodeAsync(operationName).ConfigureAwait(false)
                    ?? throw new OperationCanceledException("Steam Guard entry cancelled.");
            }
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

    private async Task UploadItchAsync(DeploySettings settings, DeployCredentials credentials, string contentRoot, string workingDirectory, CancellationToken cancellationToken)
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
        CliProcessResult result = await CliProcessRunner.RunAsync(executable, arguments, workingDirectory, environment, QueueProcessOutput, cancellationToken: cancellationToken).ConfigureAwait(false);
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
        if (_cancelButton is not null) _cancelButton.Disabled = !busy;
        UpdateBatchUiState();
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

    private static VBoxContainer AddSettingsColumn(HBoxContainer parent, string name)
    {
        var column = new VBoxContainer
        {
            Name = name,
            CustomMinimumSize = new Vector2(250, 0),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        parent.AddChild(column);
        return column;
    }

    private static void AddSection(Control parent, string title)
    {
        parent.AddChild(new HSeparator());
        parent.AddChild(new Label { Text = title });
    }

    private static VBoxContainer AddFoldoutSection(Control parent, string title, out Button foldoutButton, bool expanded = true)
    {
        parent.AddChild(new HSeparator());
        var content = new VBoxContainer
        {
            Visible = expanded,
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
        };
        foldoutButton = new Button
        {
            Text = $"{(expanded ? "▼" : "▶")} {title}",
            ToggleMode = true,
            ButtonPressed = expanded,
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

    private static VBoxContainer AddStaticSection(Control parent, string title)
    {
        parent.AddChild(new HSeparator());
        parent.AddChild(new Label { Text = title, HorizontalAlignment = HorizontalAlignment.Center });
        var content = new VBoxContainer
        {
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            SizeFlagsVertical = Control.SizeFlags.ExpandFill,
        };
        parent.AddChild(content);
        return content;
    }

    private static void SetFoldoutExpanded(Button? button, Control? content, string title, bool expanded)
    {
        if (button is not null)
        {
            button.SetPressedNoSignal(expanded);
            button.Text = $"{(expanded ? "▼" : "▶")} {title}";
        }
        if (content is not null) content.Visible = expanded;
    }

    private void ExpandConsoleResult() =>
        SetFoldoutExpanded(_consoleFoldoutButton, _consoleContent, "Console Result", true);

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
        GD.Print($"{LogPrefix} SETTINGS_HORIZONTAL_COLUMN_COUNT={_settingsColumns?.GetChildCount()}");
        GD.Print($"{LogPrefix} SETTINGS_COLUMNS_ALWAYS_VISIBLE={_settingsColumns?.GetChildren().All(child => child is Control { Visible: true })}");
        GD.Print($"{LogPrefix} CONSOLE_RESULT_COLLAPSED_INITIALLY={_consoleFoldoutButton?.ButtonPressed == false && _consoleContent?.Visible == false}");
        ExpandConsoleResult();
        GD.Print($"{LogPrefix} CONSOLE_RESULT_EXPANDS_FOR_WORKFLOW={_consoleFoldoutButton?.ButtonPressed == true && _consoleContent?.Visible == true}");
        SetFoldoutExpanded(_consoleFoldoutButton, _consoleContent, "Console Result", false);
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
        GD.Print($"{LogPrefix} GODOT_EXPORT_FAILURE_MATCHES={CliProcessRunner.IsGodotExportBuildFailure("ERROR: Export .NET Project: Failed to build project")}");

        string missingPath = Path.Combine(
            ProjectSettings.GlobalizePath("res://.deployer/tools"),
            "probe-definitely-missing");
        GD.Print($"{LogPrefix} MISSING_STEAMCMD_RESOLVES={TryResolveExecutablePath(missingPath, DeployToolKind.SteamCmd, out _)}");
        GD.Print($"{LogPrefix} MISSING_BUTLER_RESOLVES={TryResolveExecutablePath(missingPath, DeployToolKind.Butler, out _)}");
    }
}
#endif
