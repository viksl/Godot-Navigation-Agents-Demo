using Godot;

namespace NavigationSwarmTest;

public partial class NavigationSettings : Resource
{
    [Export]
    public int AgentCount { get; set; } = 120;

    [Export]
    public int AgentsPerRow { get; set; } = 100;

    [Export]
    public float MinDistanceFromCenter { get; set; } = 10;

    [Export]
    public float Speed { get; set; } = 25;

    [Export]
    public PackedScene AgentScene { get; set; }

    [Export]
    public int MaxBatchCount
    {
        get => _maxBatchCount;
        set =>  _maxBatchCount = value <= 0 ? 1 : value;  
    }

    [Export]
    public double UpdatePathInterval { get; set; } = 1f;

    [Export]
    public float MaxPathDistance
    {
        get => _maxPathDistance;
        set
        {
            _maxPathDistance = value;
            MaxPathDistanceSquared = value * value;
        }
    }

    [Export]
    public float AgentRadius { get; set; } = 1;

    [Export]
    public int AgentMaxNeighbours { get; set; } = 10;

    [Export]
    public float AgentMaxNeighbourDistance { get; set; } = 50f;

    [Export]
    public float AgentTimeHorizonAgents { get; set; } = 1f;

    [Export]
    public float AgentMaxSpeed { get; set; } = 5;
    
    [Export]
    public float AgentVelocitySmoothing { get; set; } = 0.1f;

    [Export]
    public float AgentVelocityDeadZone { get; set; } = 0.2f;

    [Export]
    public float AgentTargetMaxDistance
    {
        get => _AgentTargetMaxDistance;
        set
        {
            _AgentTargetMaxDistance = value;
            AgentTargetMaxDistanceSquared = value * value;
        }
    }

    [Export]
    public bool AgentAvoidanceEnabled { get; set; } = true;

    [Export]
    public float AgentAvoidanceRatio
    {
        get => _agentAvoidanceRation;
        set => _agentAvoidanceRation = Mathf.Clamp(value, 0, 1);
    }

    [Export]
    public int MaxSkippedUpdateIntervals
    {
        get => _maxSkippedUpdateIntervals;
        set => _maxSkippedUpdateIntervals = value <= 0 ? 1 : value;
    }

    [Export]
    public int AgentPathAdjustmentBreakLimit { get; set; } = 5;

    [Export]
    public double TargetDistanceUpdateInterval { get; set; } = 0.5;

    public float MaxPathDistanceSquared { get; set; }
    public float AgentTargetMaxDistanceSquared { get; set; }
    
    private int _maxSkippedUpdateIntervals = 5;
    private float _agentAvoidanceRation = 1;
    private int _maxBatchCount = 4;
    private float _maxPathDistance = 0.5f;
    private float _AgentTargetMaxDistance = 3f;
}