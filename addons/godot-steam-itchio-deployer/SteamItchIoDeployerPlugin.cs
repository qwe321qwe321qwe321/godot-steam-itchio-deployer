#if TOOLS
#nullable enable
using Godot;

namespace GodotSteamItchIoDeployer;

[Tool]
public partial class SteamItchIoDeployerPlugin : EditorPlugin
{
    private const string LogPrefix = "[GodotSteamItchIoDeployer]";

    private EditorDock? _dock;
    private Button? _probeButton;

    public override void _EnterTree()
    {
        _dock = new EditorDock
        {
            Name = "SteamItchIoDeployerDock",
            Title = "Deployer",
            LayoutKey = "godot_steam_itchio_deployer",
            DefaultSlot = EditorDock.DockSlot.Bottom,
            AvailableLayouts = EditorDock.DockLayout.Horizontal | EditorDock.DockLayout.Floating,
            Global = true,
        };

        var panel = new VBoxContainer
        {
            Name = "SteamItchIoDeployerPanel",
            CustomMinimumSize = new Vector2(0, 180),
        };
        panel.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _dock.AddChild(panel);

        panel.AddChild(new Label
        {
            Text = "Godot Steam / itch.io Deployer",
        });

        panel.AddChild(new Label
        {
            Text = "C# EditorPlugin probe is active. Deployment controls will be added here.",
        });

        _probeButton = new Button
        {
            Text = "Run Plugin Probe",
            TooltipText = "Verifies that C# UI events execute inside the Godot editor.",
        };
        _probeButton.Pressed += OnProbeButtonPressed;
        panel.AddChild(_probeButton);

        AddDock(_dock);
        GD.Print($"{LogPrefix} PLUGIN_LOADED");

        if (HasProbeArgument())
        {
            OnProbeButtonPressed();
        }
    }

    public override void _ExitTree()
    {
        if (_probeButton is not null)
        {
            _probeButton.Pressed -= OnProbeButtonPressed;
        }

        if (_dock is not null && IsInstanceValid(_dock))
        {
            RemoveDock(_dock);
            _dock.QueueFree();
        }

        _probeButton = null;
        _dock = null;
    }

    private static bool HasProbeArgument()
    {
        foreach (string argument in OS.GetCmdlineUserArgs())
        {
            if (argument == "--deployer-probe")
            {
                return true;
            }
        }

        return false;
    }

    private static void OnProbeButtonPressed()
    {
        GD.Print($"{LogPrefix} PROBE_BUTTON_PRESSED");
    }
}
#endif
