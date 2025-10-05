using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Godot;
using Timer = Godot.Timer;

namespace NavigationSwarmTest;

public partial class TestNavigation : Node3D
{
    [Export]
    private Node3D _agentsRoot;

    [Export]
    private Node3D _player;
    
    [Export]
    private NavigationSettings _settings;

    [Export]
    private bool _physicsEnabled = true;
    
    [Export]
    private bool _processEnabled = true;
    
    [Export]
    private bool _navigationEnabled = true;
    
    [Export]
    private bool _enableRenderThreads = true;
    
    private Agent[] _agents;
    private int[][] _agentBatchIndices;
    private float _movementDelta;
    private int _currentBatch = 0;
    private float[] _multiMeshRenderBufferPreviousFrame;
    private float[] _multiMeshRenderBufferNextFrame;
    private float[] _multiMeshTaskBuffer;
    private Transform3D[] _transformsForMultiMeshBufferTask;
    private Task _updateMultiMeshBufferTask;
    private Task[] _getPathTasks;
    private ThreadWorker[] _pathThreadWorkers;
    private ThreadWorker _multiMeshWorker;
    private ThreadWorker _distanceUpdatesWorker;

    private int _agentCount;
    private int _agentsPerRow;
    private float _minDistanceFromCenter;
    private float _speed;
    private int _maxBatchCount;
    private double _updatePathInterval;
    private float _agentRadius;
    private int _agentMaxNeighbours;
    private float _agentMaxNeighbourDistance;
    private float _agentTimeHorizonAgents;
    private float _agentMaxSpeed;
    private float _agentVelocitySmoothing;
    private float _agentVelocityDeadZone;
    private bool _agentAvoidanceEnabled;
    private float _agentAvoidanceRatio;
    private int _maxSkippedUpdateIntervals;
    private int _agentPathAdjustmentBreakLimit;
    private double _targetDistanceUpdateInterval;
    private float _maxPathDistanceSquared;
    private float _agentTargetMaxDistanceSquared;
    
    
    public int AgentCountOverride { get; set; } = -1;
    
    private struct Agent()
    {
        public Rid NavRid = default;
        public Transform3D Transform3D = default;
        public Vector3[] Path = [];
        public int CurrentPathIndex = 0;
        public Vector3 PreviousVelocity = Vector3.Zero;
        public bool AvoidanceEnabled = false;
        public Mesh Mesh = null;
        public Material Material = null;
        public Vector3 MeshSize = default;
        public Vector3 MeshHalfSize = default;
        public float MinDistanceForHigherUpdateInterval = 100f;
        public float DistanceToTargetSquared = -1;
        public float DistanceToPlayerSquared = -1;
        public Rid MultiMeshRid = default;
        public Rid MultiMeshInstanceRid = default;
    }
    
    public override void _Ready()
    {
        base._Ready();
        
        _agentCount = _settings.AgentCount;
        _agentsPerRow = _settings.AgentsPerRow;
        _minDistanceFromCenter = _settings.MinDistanceFromCenter;
        _speed = _settings.Speed;
        _maxBatchCount = _settings.MaxBatchCount;
        _updatePathInterval = _settings.UpdatePathInterval;
        _agentRadius = _settings.AgentRadius;
        _agentMaxNeighbours = _settings.AgentMaxNeighbours;
        _agentMaxNeighbourDistance = _settings.AgentMaxNeighbourDistance;
        _agentTimeHorizonAgents = _settings.AgentTimeHorizonAgents;
        _agentMaxSpeed = _settings.AgentMaxSpeed;
        _agentVelocitySmoothing = _settings.AgentVelocitySmoothing;
        _agentVelocityDeadZone = _settings.AgentVelocityDeadZone;
        _agentAvoidanceEnabled = _settings.AgentAvoidanceEnabled;
        _agentAvoidanceRatio = _settings.AgentAvoidanceRatio;
        _maxSkippedUpdateIntervals = _settings.MaxSkippedUpdateIntervals;
        _agentPathAdjustmentBreakLimit = _settings.AgentPathAdjustmentBreakLimit;
        _targetDistanceUpdateInterval = _settings.TargetDistanceUpdateInterval;
        _maxPathDistanceSquared = _settings.MaxPathDistanceSquared;
        _agentTargetMaxDistanceSquared = _settings.AgentTargetMaxDistanceSquared;
        
        
        SetPhysicsProcess(false);
        SetProcess(false);
        
        TreeExiting += OnTreeExiting;
        var distanceTimer = GetNode<Timer>("%ProcessDistancesTimer");
        distanceTimer.WaitTime = _targetDistanceUpdateInterval;
        distanceTimer.Timeout += OnTargetDistanceTimerTimeout;
            
        SpawnAgentsAsync();
    }

    public override void _PhysicsProcess(double delta)
    {
        base._PhysicsProcess(delta);
        
        MoveAgents((float)delta);
        
        if (_enableRenderThreads)
        {
            if (_updateMultiMeshBufferTask is not null && _updateMultiMeshBufferTask.IsCompleted)
            {
                if (_updateMultiMeshBufferTask.Status == TaskStatus.RanToCompletion)
                {
                    (_multiMeshRenderBufferNextFrame, _multiMeshTaskBuffer, _multiMeshRenderBufferPreviousFrame) = (_multiMeshTaskBuffer, _multiMeshRenderBufferPreviousFrame, _multiMeshRenderBufferNextFrame);
                }
                else
                {
                    GD.PrintErr($"Build task failed: {_updateMultiMeshBufferTask.Exception}");
                }
                _updateMultiMeshBufferTask = null;
            }
            
            if (_updateMultiMeshBufferTask == null && _transformsForMultiMeshBufferTask is not null)
            {
                UpdateMultiMeshAgentTransforms();
                _updateMultiMeshBufferTask = _multiMeshWorker.EnqueueWork(UpdateMultiMeshBuffer);
            }
            
            var rs = RenderingServer.Singleton;
            rs.MultimeshSetBufferInterpolated(_agents[0].MultiMeshRid, _multiMeshRenderBufferNextFrame, _multiMeshRenderBufferPreviousFrame);
        }
    }

    public int GetAgentCount() => AgentCountOverride >= 0 ? AgentCountOverride : _agentCount;
    
    
    private async void SpawnAgentsAsync()
    {
        await ToSignal(GetTree().CreateTimer(0.1f), SceneTreeTimer.SignalName.Timeout);
        
        var agentNode = _settings.AgentScene.Instantiate<Node3D>();
        var mesh =  (BoxMesh)agentNode.GetNode<MeshInstance3D>("MeshInstance3D").Mesh;
        var meshSize = mesh.Size;
        var meshHalfSize = meshSize / 2f;
        var material = agentNode.GetNode<MeshInstance3D>("MeshInstance3D").MaterialOverride;
        var scenarioId = GetWorld3D().Scenario;
        const float distanceBetweenAgents = 0.5f;
        
        var ns = NavigationServer3D.Singleton;
        ns.SetActive(true);

        var rs = RenderingServer.Singleton;
        
        var agentCount = AgentCountOverride >= 0 ? AgentCountOverride : _agentCount;

        var multiMeshInstanceRid = rs.InstanceCreate();
        rs.InstanceSetScenario(multiMeshInstanceRid, scenarioId);
        rs.InstanceGeometrySetMaterialOverlay(multiMeshInstanceRid, material.GetRid());
        
        var multiMeshRid = rs.MultimeshCreate();
        rs.InstanceSetBase(multiMeshInstanceRid, multiMeshRid);
        rs.MultimeshSetMesh(multiMeshRid, mesh.GetRid());
        rs.MultimeshSetPhysicsInterpolated(multiMeshRid, true);
        rs.MultimeshSetPhysicsInterpolationQuality(multiMeshRid, RenderingServer.MultimeshPhysicsInterpolationQuality.Fast);
        
        // Create agents.
        _agents = new Agent[agentCount];
        for (var i = 0; i < agentCount; i++)
        {
            var agentNavRid = ns.AgentCreate();
            
            var agentData = new Agent
            {
                NavRid = agentNavRid,
                Mesh = mesh,
                AvoidanceEnabled = false,
                Material = material,
                MeshSize = meshSize,
                MeshHalfSize = meshHalfSize,
                MultiMeshInstanceRid = multiMeshInstanceRid,
                MultiMeshRid = multiMeshRid,
            };
            
            var navMapRid = GetWorld3D().GetNavigationMap();
            ns.AgentSetMap(agentNavRid, navMapRid);

            _agents[i] = agentData;
        }

        rs.MultimeshAllocateData(
            multiMeshRid,
            _agents.Length,
            RenderingServer.MultimeshTransformFormat.Transform3D
        );
        rs.MultimeshSetVisibleInstances(multiMeshRid, -1);
        
        // Spawn agents at position.
        var nextAgentIndex = 0;
        for (int z = 0; z < agentCount; z++)
        {
            if (nextAgentIndex >= agentCount)
            {
                break;
            }

            for (int x = 0; x < _agentsPerRow; x++)
            {
                if (nextAgentIndex >= agentCount)
                {
                    break;
                }

                ref var agent = ref _agents[nextAgentIndex];
                var globalPosition = new Vector3(
                    -_agentsPerRow / 2f + x + x * distanceBetweenAgents,
                    agent.MeshHalfSize.Y,
                    -_minDistanceFromCenter - z - z * distanceBetweenAgents
                );
                agent.Transform3D = new(Basis.Identity, globalPosition);
                ns.AgentSetPosition(agent.NavRid, agent.Transform3D.Origin);
                nextAgentIndex++;
            }
        }

        // Assign agents to batches.
        var agentBatchIndices = new List<List<int>>();
        for (var i = 0; i < _maxBatchCount; i++)
        {
            agentBatchIndices.Add([]);
        }
        
        var indices = Enumerable.Range(0, _agents.Length).ToList();
        var nextBatchIndex = 0;
        for (int i = 0; i < _agents.Length; i++)
        {
            var index = Random.Shared.Next(indices.Count);
            var agentIndex = indices[index];
            indices.RemoveAt(index);
            
            agentBatchIndices[nextBatchIndex].Add(agentIndex);
            nextBatchIndex = ++nextBatchIndex % _maxBatchCount;
        }
        
        _agentBatchIndices = new int[_maxBatchCount][];
        for (var i = 0; i < _maxBatchCount; i++)
        {
            _agentBatchIndices[i] = agentBatchIndices[i].OrderBy(ii => ii).ToArray();
        }
        
        _getPathTasks = new Task[_maxBatchCount];
        _getPathTasks.AsSpan().Clear();
        _pathThreadWorkers = new ThreadWorker[_maxBatchCount];
        Enumerable.Range(0, _maxBatchCount).ToList().ForEach(i => _pathThreadWorkers[i] = new ThreadWorker("PathThreadWorker_" + i));
        
        // Assign agents avoidance.
        if (_agentAvoidanceEnabled && _navigationEnabled)
        {
            var avoidanceAgentCount = Mathf.FloorToInt(_agentAvoidanceRatio * _agents.Length);
            var noAvoidanceAgentsIndices = Enumerable.Range(0, _agents.Length).ToList();
            for (var i = 0; i < avoidanceAgentCount; i++)
            {
                var index = Random.Shared.Next(noAvoidanceAgentsIndices.Count);
                var agentIndex = noAvoidanceAgentsIndices[index];
                ref var agent = ref _agents[agentIndex];
                noAvoidanceAgentsIndices.RemoveAt(index);
                
                agent.AvoidanceEnabled = true;
                var agentNavRid = agent.NavRid;
                ns.AgentSetAvoidanceCallback(agentNavRid,
                    Callable.From<Vector3>((safeVelocity) =>
                    {
                        ref var agent = ref _agents[agentIndex];
                        OnAgentSafeVelocityComputed(safeVelocity, ref agent);
                    }
                ));
                ns.AgentSetAvoidanceEnabled(agentNavRid, true);
                ns.AgentSetRadius(agentNavRid, _agentRadius);
                ns.AgentSetMaxNeighbors(agentNavRid, _agentMaxNeighbours);
                ns.AgentSetNeighborDistance(agentNavRid, _agentMaxNeighbourDistance);
                ns.AgentSetTimeHorizonAgents(agentNavRid, _agentTimeHorizonAgents);
                ns.AgentSetMaxSpeed(agentNavRid, _agentMaxSpeed);
                ns.AgentSetAvoidancePriority(agentNavRid, 1);
            }
        }
        
        agentNode.QueueFree();

        _multiMeshWorker = new ThreadWorker("MultiMeshWorker");
        _transformsForMultiMeshBufferTask = new Transform3D[_agents.Length];
        UpdateMultiMeshAgentTransforms();
        _multiMeshRenderBufferPreviousFrame = new float[_agents.Length * 12];
        _multiMeshRenderBufferNextFrame = new float[_agents.Length * 12];
        _multiMeshTaskBuffer = new float[_agents.Length * 12];
        UpdateMultiMeshBuffer();
        (_multiMeshRenderBufferPreviousFrame, _multiMeshTaskBuffer) = (_multiMeshTaskBuffer, _multiMeshRenderBufferPreviousFrame);

        // Start agent navigation update interval timer.
        var timer = new Timer();
        timer.Autostart = true;
        AddChild(timer);
        timer.WaitTime = _updatePathInterval;
        timer.Timeout += OnTimerTimeout;
        OnTimerTimeout();
        
        SetPhysicsProcess(_physicsEnabled);
        SetProcess(_processEnabled);
        
        _distanceUpdatesWorker = new ThreadWorker("DistanceUpdatesWorker");
        OnTargetDistanceTimerTimeout();
        GetNode<Timer>("%ProcessDistancesTimer").Start();
    }

    private void OnTimerTimeout()
    {
        _ = GetPathsAsync();
    }
    
    private async Task GetPathsAsync()
    {
        if (!_navigationEnabled)
        {
            return;
        }
        
        var currentBatch = _currentBatch;
        _currentBatch = ++_currentBatch % _maxBatchCount;
        
        if (_getPathTasks is null)
        {
            return;
        }

        if (_getPathTasks[currentBatch] is not null)
        {
            return;
        }
        
        var agentPaths = new Vector3[_agentBatchIndices[currentBatch].Length][];
        var navMapRid = GetWorld3D().GetNavigationMap();
        var playerPosition = _player.GlobalPosition;
        var positions = new Vector3[_agentBatchIndices[currentBatch].Length];
        ReadOnlySpan<Agent> agents = _agents.AsSpan();
        var ii = 0;
        foreach (var index in _agentBatchIndices[currentBatch].AsSpan())
        {
            ref readonly var agent = ref agents[index];
            positions[ii] = agent.Transform3D.Origin;
            ii++;
        }

        var pathTask = _pathThreadWorkers[currentBatch].EnqueueWork(
            () =>
            {
                var ns = NavigationServer3D.Singleton;
        
                var indices = _agentBatchIndices[currentBatch].AsSpan();
                var i = 0;
                foreach (var _ in indices)
                {
                    agentPaths[i] = ns.MapGetPath(navMapRid, positions[i], playerPosition, true);
                    i++;
                }
            }
        );
        
        _getPathTasks[currentBatch] = pathTask;

        await pathTask;

        Callable.From(() =>
        {
            ApplyPathsOnMainThread(currentBatch, agentPaths);
        }).CallDeferred();
    }

    private void ApplyPathsOnMainThread(int batchIndex, Vector3[][] agentPaths)
    {
        _getPathTasks[batchIndex] = null;
        var indices = _agentBatchIndices[batchIndex].AsSpan();
        var pathBreakLimit = _agentPathAdjustmentBreakLimit;
        
        for (var i = 0; i < indices.Length; i++)
        {
            var index = indices[i];
            ref var agent = ref _agents[index];
            var newPath = agentPaths[i];

            if (newPath == null || newPath.Length == 0)
            {
                continue;
            }

            var startIndex = 0;
            if (agent.Path is {Length: > 0} && agent.CurrentPathIndex < agent.Path.Length)
            {
                var agentPosition = agent.Transform3D.Origin;
                var pathPointPosition = agent.Path[agent.CurrentPathIndex];
                var currentDirection = agentPosition.DirectionTo(pathPointPosition);
                var breakCounter = 0;
                for (int j = 0; j < newPath.Length; j++)
                {
                    var newPathPointPosition = newPath[j];
                    var direction = agentPosition.DirectionTo(newPathPointPosition);
                    var dot = currentDirection.Dot(direction);
                    if (dot < 0)
                    {
                        newPath[j] = agentPosition;
                        startIndex++;
                        breakCounter++;
                        if (breakCounter <= pathBreakLimit)
                        {
                            continue;
                        }
                        
                        startIndex = 0;
                    }

                    break;
                }

                startIndex %= newPath.Length;
                
                if (startIndex == 0)
                {
                    newPath[startIndex] = agentPosition;
                }
                else
                {
                    newPath[startIndex - 1] = agentPosition;
                }
            }
            
            agent.Path = newPath;
            agent.CurrentPathIndex = startIndex;
        }
    }
    
    private void MoveAgents(float delta)
    {
        var navMapRid = GetWorld3D().GetNavigationMap();
        if (NavigationServer3D.MapGetIterationId(navMapRid) == 0)
        {
            return;
        }
        
        _movementDelta = _speed * delta;
        
        var ns = NavigationServer3D.Singleton;
        var agents = _agents.AsSpan();
        var agentTargetMaxDistanceSquared = _agentTargetMaxDistanceSquared;
        var maxPathDistanceSquared = _maxPathDistanceSquared;
        
        foreach (ref var agent in agents)
        {
            ref var agentCurrentPosition = ref agent.Transform3D.Origin;
            var nextPathPosition = agent.CurrentPathIndex < agent.Path.Length
                ? agent.Path[agent.CurrentPathIndex]
                : agentCurrentPosition;
            
            var agentPathDx = agentCurrentPosition.X - nextPathPosition.X;
            var agentPathDz = agentCurrentPosition.Z - nextPathPosition.Z;
            var agentPathDistanceSquared = agentPathDx * agentPathDx + agentPathDz * agentPathDz;
            if (agent.Path.Length > 0 && agentPathDistanceSquared <= maxPathDistanceSquared)
            {
                agent.CurrentPathIndex++;
                nextPathPosition = agent.CurrentPathIndex < agent.Path.Length
                    ? agent.Path[agent.CurrentPathIndex]
                    : agentCurrentPosition;
            }

            var direction = agentCurrentPosition.DirectionTo(nextPathPosition);
            var distanceToTargetSquared = agent.DistanceToPlayerSquared;
            
            var speedMultiplier = 1f;
            if (distanceToTargetSquared < agentTargetMaxDistanceSquared)
            {
                speedMultiplier = Math.Max(0.2f, distanceToTargetSquared / agentTargetMaxDistanceSquared);
            }
            Vector3 newVelocity = direction * speedMultiplier * _movementDelta;
            if (agent.AvoidanceEnabled)
            {
                var navRid = agent.NavRid;
                ns.AgentSetVelocity(navRid, newVelocity);
            }
            else
            {
                OnAgentSafeVelocityComputed(newVelocity, ref agent);
            }
        }
    }
    
    private void OnAgentSafeVelocityComputed(Vector3 safeVelocity, ref Agent agentData)
    {
        if (!IsInstanceValid(this) || IsQueuedForDeletion())
        {
            return;
        }
        
        var agentNavRid = agentData.NavRid;
        var smoothedVelocity = agentData.PreviousVelocity.Lerp(safeVelocity, _agentVelocitySmoothing);
        agentData.PreviousVelocity = smoothedVelocity;
        if (smoothedVelocity.Length() < _agentVelocityDeadZone)
        {
            return;
        }

        ref var globalPosition = ref agentData.Transform3D.Origin;
        globalPosition = globalPosition.MoveToward(globalPosition + smoothedVelocity, _movementDelta);
        if (agentData.AvoidanceEnabled)
        {
            var ns = NavigationServer3D.Singleton;
            ns.AgentSetPosition(agentNavRid, globalPosition);
        }
    }
    
    private void OnTreeExiting()
    {
        var ns = NavigationServer3D.Singleton;
        var rs = RenderingServer.Singleton;
        var multiMeshCleared = false;
        foreach (ref var agent in _agents.AsSpan())
        {
            ns.AgentSetAvoidanceCallback(agent.NavRid, default);
            ns.FreeRid(agent.NavRid);
            
            agent.Mesh = null;
            if (!multiMeshCleared)
            {
                rs.FreeRid(agent.MultiMeshInstanceRid);
                multiMeshCleared = true;
            }
        }
        
        ns.SetActive(false);
        
        _multiMeshWorker?.Dispose();
        if (_pathThreadWorkers != null)
        {
            foreach (var worker in _pathThreadWorkers)
            {
                worker?.Dispose();
            }
        }
    }
    
    private void UpdateMultiMeshBuffer()
    {
        ReadOnlySpan<Transform3D> transforms = _transformsForMultiMeshBufferTask.AsSpan();
        var nextIndex = 0;
        foreach (ref readonly var xform in transforms)
        {
            ref readonly var basis = ref xform.Basis;
            ref readonly var origin = ref xform.Origin;
            _multiMeshTaskBuffer[nextIndex] = basis.X.X;
            _multiMeshTaskBuffer[nextIndex + 1] = basis.Y.X;
            _multiMeshTaskBuffer[nextIndex + 2] = basis.Z.X;
            _multiMeshTaskBuffer[nextIndex + 3] = origin.X;
            _multiMeshTaskBuffer[nextIndex + 4] = basis.X.Y;
            _multiMeshTaskBuffer[nextIndex + 5] = basis.Y.Y;
            _multiMeshTaskBuffer[nextIndex + 6] = basis.Z.Y;
            _multiMeshTaskBuffer[nextIndex + 7] = origin.Y;
            _multiMeshTaskBuffer[nextIndex + 8] = basis.X.Z;
            _multiMeshTaskBuffer[nextIndex + 9] = basis.Y.Z;
            _multiMeshTaskBuffer[nextIndex + 10] = basis.Z.Z;
            _multiMeshTaskBuffer[nextIndex + 11] = origin.Z;
            
            nextIndex += 12;
        }
    }

    private void UpdateMultiMeshAgentTransforms()
    {
        if (_agents is null || _agents.Length == 0)
        {
            return;
        }

        ReadOnlySpan<Agent> agents = _agents.AsSpan();
        for (var i = 0; i < agents.Length; i++)
        {
            _transformsForMultiMeshBufferTask[i] = agents[i].Transform3D;
        }
    }

    private void OnTargetDistanceTimerTimeout()
    {
        _ = ProcessAgentDistances();
    }

    private async Task ProcessAgentDistances()
    {
        var playerPosition = _player.GlobalPosition;
        ReadOnlySpan<Agent> agents = _agents.AsSpan();
        var agentPositions = new Vector3[_agents.Length];
        var targetPositions = new Vector3[_agents.Length];
        for (var i = 0; i < agents.Length; i++)
        {
            ref readonly var agent = ref agents[i];
            agentPositions[i] = agent.Transform3D.Origin;
            targetPositions[i] = playerPosition;
        }

        var distancesToPlayerSquared = new float[_agents.Length];
        var distancesToTargetSquared = new float[_agents.Length];
        
        var distanceUpdatesTask = _distanceUpdatesWorker.EnqueueWork(
            () =>
            {
                for (var index = 0; index < agentPositions.Length; index++)
                {
                    ref var agentPosition = ref agentPositions[index];
                    ref var target = ref targetPositions[index];
                    
                    var agentTargetDx = target.X - agentPosition.X;
                    var agentTargetDz = target.Z - agentPosition.Z;
                    var agentTargetDxSq = agentTargetDx * agentTargetDx;
                    var agentTargetDzSq = agentTargetDz * agentTargetDz;
                    distancesToTargetSquared[index] = agentTargetDxSq + agentTargetDzSq; 
                        
                    var agentPlayerDx = playerPosition.X - agentPosition.X;
                    var agentPlayerDz = playerPosition.Z - agentPosition.Z;
                    var agentPlayerDxSq = agentPlayerDx * agentPlayerDx;
                    var agentPlayerDzSq = agentPlayerDz * agentPlayerDz;
                    distancesToPlayerSquared[index] = agentPlayerDxSq + agentPlayerDzSq;
                }
            }
        );
        
        await distanceUpdatesTask;

        Callable.From(() =>
        {
            ApplyAgentDistances(distancesToPlayerSquared, distancesToTargetSquared);
        }).CallDeferred();
    }

    private void ApplyAgentDistances(float[] distancesToPlayer, float[] distancesToTarget)
    {
        var agents = _agents.AsSpan();
        for (var index = 0; index < agents.Length; index++)
        {
            ref var agent = ref agents[index];
            agent.DistanceToPlayerSquared = distancesToPlayer[index];
            agent.DistanceToTargetSquared = distancesToTarget[index];
        }
    }
}