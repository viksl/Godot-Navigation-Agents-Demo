using System;
using Godot;

namespace NavigationSwarmTest;

public partial class Main : Node3D
{
    [Export]
    private int AgentCount { get; set; } = -1;

    [Export]
    private NavigationSettings _settings;
    
    private bool _confirmPressed = false;
    private bool _obstaclesEnabled = false;
    private DateTime _startTime;
    
    public override void _Ready()
    {
        base._Ready();
        
        GetNode<Button>("%Confirm").Pressed += OnConfirmPressed;
        GetNode<Button>("%ConfirmObstacles").Pressed += OnConfirmObstaclesPressed;
        GetNode<Button>("%ConfirmObstacles").MouseEntered += () => DisplayHintOnMouseEntered("%ConfirmObstacles");
        GetNode<Button>("%ConfirmObstacles").MouseExited += () => DisplayHintOnMouseExited("%ConfirmObstacles");
        GetNode<Button>("%Vsync").Pressed += OnVsyncPressed;
        GetNode<Button>("%Fullscreen").Pressed += OnFullscreenPressed;
        GetNode<CheckBox>("%FullscreenExclusive").Pressed += OnFullscreenExclusivePressed;
        GetNode<CheckButton>("%VisibilityToggle").Pressed += OnVisibilityToggleToggled;
        GetNode<Button>("%Gpu").Pressed += OnGpuPressed;
        GetNode<Button>("%Gpu").FocusExited += OnGpuFocusExited;
        GetNode<Button>("%Credits").Pressed += OnCreditsPressed;
        GetNode<Button>("%Credits").FocusExited += OnCreditsFocusExited;
        GetNode<RichTextLabel>("%CreditsText").MetaClicked += OnCreditsTextMetaClicked;
        GetNode<Button>("%Instructions").Pressed += OnInstructionsPressed;
        GetNode<Button>("%Instructions").FocusExited += OnInstructionsFocusExited;
        GetNode<Button>("%Capsule").Pressed += OnCapsulePressed;
        GetNode<Button>("%Capsule").FocusExited += OnCapsuleFocusExited;
        GetNode<Button>("%Faq").Pressed += OnFaqPressed;
        GetNode<Button>("%Faq").FocusExited += OnFaqFocusExited;
        GetNode<Button>("%Exit").Pressed += OnExitPressed;
        GetNode<Control>("%ResetButtonsArea").GuiInput += OnResetButtonsAreaGuiInput;
        GetNode<HSlider>("%AvoidanceRatio").DragEnded += OnAvoidanceRatio;
        GetNode<HSlider>("%AvoidanceRatio").ValueChanged += OnAvoidanceRatioValueChanged;
        GetNode<HSlider>("%AvoidanceRatio").MouseEntered += () => DisplayHintOnMouseEntered("%AvoidanceRatio");
        GetNode<HSlider>("%AvoidanceRatio").MouseExited += () => DisplayHintOnMouseExited("%AvoidanceRatio");
        GetNode<SpinBox>("%MaxBatches").ValueChanged += OnMaxBatchesChanged;
        GetNode<SpinBox>("%MaxBatches").MouseEntered += () => DisplayHintOnMouseEntered("%MaxBatches");
        GetNode<SpinBox>("%MaxBatches").MouseExited += () => DisplayHintOnMouseExited("%MaxBatches");
        GetNode<SpinBox>("%UpdateInterval").ValueChanged += OnUpdateIntervalChanged;
        GetNode<SpinBox>("%UpdateInterval").MouseEntered += () => DisplayHintOnMouseEntered("%UpdateInterval");
        GetNode<SpinBox>("%UpdateInterval").MouseExited += () => DisplayHintOnMouseExited("%UpdateInterval");
        GetNode<Timer>("TimeUpdate").Timeout += OnTimeUpdateTimeout;
        
        // Grab the default values from the ui
        OnAvoidanceRatio(true);
        GetNode<SpinBox>("%MaxBatches").Value = _settings.MaxBatchCount;
        GetNode<SpinBox>("%UpdateInterval").Value = _settings.UpdatePathInterval;
    }

    public override void _Process(double delta)
    {
        base._Process(delta);
        
        GetNode<Label>("%Fps").Text = Engine.GetFramesPerSecond() + " FPS";
        var navigation = GetNodeOrNull<TestNavigation>("TestNavigation");
        int agentCount = 0;
        if (navigation is not null)
        {
            agentCount = navigation.GetAgentCount();
        }

        GetNode<Label>("%AgentCount").Text = agentCount.ToString();
    }
    
    
    private void LoadNavigationScene()
    {
        if (GetNodeOrNull<Node3D>("TestNavigation") is { } testNavigation)
        {
            testNavigation.TreeExited += LoadNavigationScene;
            testNavigation.QueueFree();
            RemoveChild(testNavigation);
            return;
        }

        var scenePath = _obstaclesEnabled ? "res://TestNavigationObstacles.tscn" : "res://TestNavigation.tscn";
        var navigationScene = GD.Load<PackedScene>(scenePath);
        var navigationTest = navigationScene.Instantiate<TestNavigation>();
        navigationTest.AgentCountOverride = AgentCount;
        AddChild(navigationTest);
        _confirmPressed = false;

        _startTime = DateTime.Now;
        GetNode<Timer>("TimeUpdate").Start();
    }
    
    private void OnConfirmPressed()
    {
        if (_confirmPressed)
        {
            return;
        }
        
        _obstaclesEnabled = false;
        _confirmPressed = true;
        AgentCount = (int)GetNode<SpinBox>("%AgentCountSet").Value;
        LoadNavigationScene();
    }
    
    private void OnConfirmObstaclesPressed()
    {
        if (_confirmPressed)
        {
            return;
        }

        _obstaclesEnabled = true;
        _confirmPressed = true;
        AgentCount = (int)GetNode<SpinBox>("%AgentCountSet").Value;
        LoadNavigationScene();
    }

    private void OnVsyncPressed()
    {
        var button = GetNode<Button>("%Vsync");
        var enabled = button.ButtonPressed;
        button.Text = $"V-Sync | {(enabled ? "Enabled" : "Disabled") }";
        DisplayServer.Singleton.WindowSetVsyncMode(enabled ? DisplayServer.VSyncMode.Enabled : DisplayServer.VSyncMode.Disabled);
    }

    private void OnFullscreenPressed()
    {
        var mode = DisplayServer.Singleton.WindowGetMode();
        mode = mode == DisplayServer.WindowMode.Windowed
            ? GetNode<CheckBox>("%FullscreenExclusive").ButtonPressed ? DisplayServer.WindowMode.ExclusiveFullscreen : DisplayServer.WindowMode.Fullscreen
            : DisplayServer.WindowMode.Windowed;
        DisplayServer.Singleton.WindowSetMode(mode);
    }

    private void OnFullscreenExclusivePressed()
    {
        var mode = DisplayServer.Singleton.WindowGetMode();

        var exclusivePressed = GetNode<CheckBox>("%FullscreenExclusive").ButtonPressed;
        switch (exclusivePressed)
        {
            case true when mode is DisplayServer.WindowMode.Windowed or DisplayServer.WindowMode.Fullscreen:
                DisplayServer.Singleton.WindowSetMode(DisplayServer.WindowMode.ExclusiveFullscreen);
                break;
            case false when mode == DisplayServer.WindowMode.ExclusiveFullscreen:
                DisplayServer.Singleton.WindowSetMode(DisplayServer.WindowMode.Fullscreen);
                break;
        }
    }
    
    private void OnVisibilityToggleToggled()
    {
        var button = GetNode<CheckButton>("%VisibilityToggle");
        var settings = GetNode<Control>("%Settings");
        if (button.ButtonPressed)
        {
            settings.Show();
        }
        else
        {
            settings.Hide();
        }
    }

    private void OnGpuPressed()
    {
        var panel = (Control)(GetNode("%Gpu").GetChildren()[0]);
        if (panel.Visible)
        {
            panel.Hide();
            return;
        }
        
        panel.Show();
    }

    private void OnGpuFocusExited()
    {
        var panel = (Control)(GetNode("%Gpu").GetChildren()[0]);
        panel.Hide();
    }
    
    private void OnCreditsPressed()
    {
        var panel = (Control)(GetNode("%Credits").GetChildren()[0]);
        if (panel.Visible)
        {
            panel.Hide();
            return;
        }
        
        panel.Show();
    }

    private void OnCreditsFocusExited()
    {
        var panel = (Control)(GetNode("%Credits").GetChildren()[0]);
        panel.Hide();
    }
    
    private void OnCreditsTextMetaClicked(Variant meta)
    {
        var jsonString = meta.ToString();
        var steamLink = System.Text.Json.JsonDocument.Parse(jsonString).RootElement.GetProperty("steam").GetString();
        OS.ShellOpen(steamLink);
    }
    
    private void OnInstructionsPressed()
    {
        var panel = (Control)(GetNode("%Instructions").GetChildren()[0]);
        if (panel.Visible)
        {
            panel.Hide();
            return;
        }
        
        panel.Show();
    }
    
    private void OnInstructionsFocusExited()
    {
        var panel = (Control)(GetNode("%Instructions").GetChildren()[0]);
        panel.Hide();
    }
    
    private void OnCapsulePressed()
    {
        var panel = (Control)(GetNode("%Capsule").GetChildren()[0]);
        if (panel.Visible)
        {
            panel.Hide();
            return;
        }

        panel.Show();
    }
    
    private void OnCapsuleFocusExited()
    {
        var panel = (Control)(GetNode("%Capsule").GetChildren()[0]);
        panel.Hide();
    }

    private void OnResetButtonsAreaGuiInput(InputEvent @event)
    {
        if (@event is InputEventMouseButton { ButtonIndex: MouseButton.Left, Pressed: true } button && !button.IsEcho())
        {
            ((Control)(GetNode("%Gpu").GetChildren()[0])).Hide();
            ((Control)(GetNode("%Credits").GetChildren()[0])).Hide();
            ((Control)(GetNode("%Instructions").GetChildren()[0])).Hide();
            ((Control)(GetNode("%Capsule").GetChildren()[0])).Hide();
            ((Control)(GetNode("%Faq").GetChildren()[0])).Hide();
        }
    }
    
    private void OnFaqPressed()
    {
        var panel = (Control)(GetNode("%Faq").GetChildren()[0]);
        if (panel.Visible)
        {
            panel.Hide();
            return;
        }

        panel.Show();
    }
    
    private void OnFaqFocusExited()
    {
        var panel = (Control)(GetNode("%Faq").GetChildren()[0]);
        panel.Hide();
    }
    
    private void OnExitPressed()
    {
        GetTree().Quit();
    }

    private void OnAvoidanceRatio(bool valueChanged)
    {
        if (!valueChanged)
        {
            return;
        }
        
        var slider = GetNode<HSlider>("%AvoidanceRatio");

        if (slider.Value == 0)
        {
            _settings.AgentAvoidanceEnabled = false;
            _settings.AgentAvoidanceRatio = 0;
        }

        if (slider.Value > 0)
        {
            _settings.AgentAvoidanceEnabled = true;
            _settings.AgentAvoidanceRatio = (float)(slider.Value / slider.MaxValue);
        }
    }

    private void OnAvoidanceRatioValueChanged(double value)
    {
        GetNode<Label>("%AvoidanceRatioLabel").Text = (int)value * 10 + "%";
    }

    private void OnMaxBatchesChanged(double value)
    {
        _settings.MaxBatchCount = (int)GetNode<SpinBox>("%MaxBatches").Value;
    }

    private void OnUpdateIntervalChanged(double value)
    {
        _settings.UpdatePathInterval = GetNode<SpinBox>("%UpdateInterval").Value;
    }

    private void DisplayHintOnMouseEntered(string uniqueName)
    {
        ((Control)GetNode(uniqueName).GetChild(0)).Show();
    }
    
    private void DisplayHintOnMouseExited(string uniqueName)
    {
        ((Control)GetNode(uniqueName).GetChild(0)).Hide();
    }

    private void OnTimeUpdateTimeout()
    {
        var elapsed = DateTime.Now - _startTime;
        GetNode<Label>("%Time").Text = $"{elapsed.Hours:D2}:{elapsed.Minutes:D2}:{elapsed.Seconds:D2}:{elapsed.Milliseconds / 10:D2}";
    }
}