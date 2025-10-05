using System.Diagnostics;
using Godot;

namespace NavigationSwarmTest;

public partial class CameraPivot : Node3D
{
    [Export]
    private Node3D _target;

    private int _currentCameraIndex = 0;
    
    
    public override void _UnhandledInput(InputEvent @event)
    {
        base._UnhandledInput(@event);

        if (@event.IsActionPressed("SwitchCamera"))
        {
            var cameraPivot = GetNode("%CameraPivot");
            var lastCameraIndex = cameraPivot.GetChildCount();
            _currentCameraIndex = (_currentCameraIndex + 1) % lastCameraIndex;
            ((Camera3D)cameraPivot.GetChild(_currentCameraIndex)).Current = true;
        }
    }
    
    public override void _Process(double delta)
    {
        base._Process(delta);
        
        GlobalPosition = _target.GetGlobalTransformInterpolated().Origin;
    }
}