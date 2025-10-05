using Godot;

namespace NavigationSwarmTest;

public partial class Ui : CanvasLayer
{
    [Export]
    private Node _agents;
    
    public override void _Process(double delta)
    {
        base._Process(delta);
        
        GetNode<Label>("%Fps").Text = Engine.GetFramesPerSecond() + " FPS";
        GetNode<Label>("%AgentCount").Text = _agents.GetChildCount().ToString();
    }
}