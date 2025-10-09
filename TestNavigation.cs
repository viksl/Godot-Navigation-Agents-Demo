using System;
using System.Buffers;
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
    private Task[] _pathTasks;
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
    private float _maxDistanceToPathPointSquared;
    private float _agentMaxDistanceToTargetSquared;
    private float _targetSafeDistanceFromPathEndSquared = 4f;
    private int _agentPathQueriesSkippedMax = 5;
    
    private readonly ArrayPool<Vector3> _agentPositions = ArrayPool<Vector3>.Shared;
    private readonly ArrayPool<Vector3> _agentPathLastPoints = ArrayPool<Vector3>.Shared;
    private readonly ArrayPool<Vector3[]> _agentPaths = ArrayPool<Vector3[]>.Shared;
    private readonly ArrayPool<bool> _isTargetReachable = ArrayPool<bool>.Shared;
    private readonly ArrayPool<float> _distances = ArrayPool<float>.Shared;
    
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
        public bool IsTargetReachable = true;
        public Rid MultiMeshRid = default;
        public Rid MultiMeshInstanceRid = default;
        public Node3D Target = null;
        public float SlowDownToTargetMaxDistanceSquared = 20f;
        public float MaxDistanceToTargetSquared = 3f;
        public int PathQueriesSkipped = 0;
        public int PathQueriesSkippedMax = 5;
    }
    
    public override void _Ready()
    {
        base._Ready();
        
        // TODO: Store some settings from _settings in private variables, for example max batches can be changed from
        //      ui, which is a problem, either not all agents get updated or OutOfRange error with value higher than
        //      the size of the batches array. On the other hand some settings such as the not yet implemented
        //      rendering distance will be useful to change on the go so keep the settings resource and use it too.
        
        // TODO: Compare performance vs the previous version, is it the same or not? This one seems to me it's slower
        //      but I'm not sure. and why would this be slower, it's still the same code just neatly stashed into an array.
        
        // TODO: Not all agents need to query for a path, for example agents near each other can follow the same path.
        //      This could reduce the number of queries by a lot. But right now the rendering is the bottleneck,
        //      or to be precise the CPU part of rendering, not even disabling depth draw mode and enabling no depth test
        //      on the material makes any difference.
        //      Maybe agents farther away could be rendered less often? That would not helped with agents stacked though.
        //      Need to test:
        //      Disable frustum culling - this can be done through the RenderingServer's instance_set_ignore_culling.
        //      Disabling with an extra AABB margin stabilizes teh frames at around 90 in debug editor mode for 10k
        //      agents with no avoidance when they overlap. It's not much but it's something.
        //      Test multimesh and particles I guess.
        
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
        _maxDistanceToPathPointSquared = _settings.MaxDistanceToPathPointSquared;
        _agentMaxDistanceToTargetSquared = _settings.AgentMaxDistanceToTargetSquared;
        _agentPathQueriesSkippedMax = _settings.AgentPathQueriesSkippedMax;
        
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
        // TODO: Maybe set custom aabb for the MultiMesh too?
        
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
                Target = _player,
                PathQueriesSkippedMax = _agentPathQueriesSkippedMax,
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
        
        _pathTasks = new Task[_maxBatchCount];
        _pathTasks.AsSpan().Clear();
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
        
        if (_pathTasks is null)
        {
            return;
        }

        if (_pathTasks[currentBatch] is not null)
        {
            return;
        }
        
        var navMapRid = GetWorld3D().GetNavigationMap();
        
        var newAgentPaths = _agentPaths.Rent(_agentBatchIndices[currentBatch].Length);
        var oldAgentPaths = _agentPaths.Rent(_agentBatchIndices[currentBatch].Length);
        var positions = _agentPositions.Rent(_agentBatchIndices[currentBatch].Length);
        var targetPositions = _agentPositions.Rent(_agentBatchIndices[currentBatch].Length);
        var isTargetReachable = _isTargetReachable.Rent(_agentBatchIndices[currentBatch].Length);
        
        ReadOnlySpan<Agent> agents = _agents.AsSpan();
        
        var agentIndex = 0;
        foreach (var agentBatchIndex in _agentBatchIndices[currentBatch].AsSpan())
        {
            ref readonly var agent = ref agents[agentBatchIndex];
            positions[agentIndex] = agent.Transform3D.Origin;
            targetPositions[agentIndex] = agent.Target.GlobalPosition;
            isTargetReachable[agentIndex] = agent.IsTargetReachable;
            
            oldAgentPaths[agentIndex] = new Vector3[agent.Path.Length];
            for (var i = 0; i < agent.Path.Length; i++)
            {
                var vector3 = agent.Path[i];
                oldAgentPaths[agentIndex][i] = vector3;
            }

            agentIndex++;
        }

        var pathTask = _pathThreadWorkers[currentBatch].EnqueueWork(
            () =>
            {
                var ns = NavigationServer3D.Singleton;
                var indices = _agentBatchIndices[currentBatch].AsSpan();
                var i = 0;
                var agents = _agents.AsSpan();
                
                foreach (var agentIndex in indices)
                {
                    var targetPosition = targetPositions[i];

                    ref var skippedQueries = ref agents[agentIndex].PathQueriesSkipped;
                    ref var maxQueries = ref agents[agentIndex].PathQueriesSkippedMax;
                    if (skippedQueries < maxQueries)
                    {
                        // Keep the path if the target hasn't moved far enough.
                        var oldPath = oldAgentPaths[i];
                        if (oldPath.Length > 0 && isTargetReachable[i])
                        {
                            newAgentPaths[i] = oldPath;
                            skippedQueries++;
                            i++;
                            continue;
                        }
                    }
                    else
                    {
                        skippedQueries = 0;
                    }
                    
                    // TODO: Both will eventually need to have Y set to 0 to avoid floating above the ground and such?
                    newAgentPaths[i] = ns.MapGetPath(navMapRid, positions[i], targetPosition, true);
                    i++;
                }
            }
        );
        
        _pathTasks[currentBatch] = pathTask;

        await pathTask;

        _agentPaths.Return(oldAgentPaths);
        _agentPositions.Return(positions);
        _agentPositions.Return(targetPositions);
        _isTargetReachable.Return(isTargetReachable);
        
        Callable.From(() =>
        {
            ApplyPathsOnMainThread(currentBatch, newAgentPaths);
        }).CallDeferred();
    }

    private void ApplyPathsOnMainThread(int batchIndex, Vector3[][] newAgentPaths)
    {
        _pathTasks[batchIndex] = null;
        var indices = _agentBatchIndices[batchIndex].AsSpan();
        var pathBreakLimit = _agentPathAdjustmentBreakLimit;
        
        for (var i = 0; i < indices.Length; i++)
        {
            var index = indices[i];
            ref var agent = ref _agents[index];
            var newPath = newAgentPaths[i];

            if (newPath == null || newPath.Length == 0)
            {
                continue;
            }

            var startIndex = 0;
            // Check the path for any points that are behind the agent, if so, adjust the path until a limit is hit.
            // if the limit is hit, the path is probably really going backwards, so let's follow it.
            if (agent.Path is {Length: > 0} && agent.CurrentPathIndex < agent.Path.Length)
            {
                var agentPosition = agent.Transform3D.Origin;
                var pathPointPosition = agent.Path[agent.CurrentPathIndex];
                var currentDirection = agentPosition.DirectionTo(pathPointPosition);
                var breakCounter = 0;
                for (int j = 0; j < newPath.Length; j++)
                {
                    // var newPathPointPosition = agent.Path[j];
                    var newPathPointPosition = newPath[j];
                    var direction = agentPosition.DirectionTo(newPathPointPosition);
                    var dot = currentDirection.Dot(direction);
                    // Some positions might be behind the agent, in that case we need to skip them.
                    // This is a bit of a hack/rough solution, but it works.
                    // But on maps with obstacles, the more agents used, the more batches need to be set
                    // otherwise the agents will start moving really weird from side to side. This is because
                    // it takes too long to find paths while the agent is moving and the gap becomes too large
                    // which makes the dot product below no longer work well.
                    if (dot < 0)
                    {
                        newPath[j] = agentPosition;
                        startIndex++;
                        breakCounter++;
                        // Limit how many path checks happen to make sure that if there's a path actually going
                        // backwards that we don't skip it.
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
        
        _agentPaths.Return(newAgentPaths);
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
        var maxPathDistanceSquared = _maxDistanceToPathPointSquared;
        
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
            var distanceToTargetSquared = agent.DistanceToTargetSquared;
            
            var speedMultiplier = 1f;
            if (distanceToTargetSquared < agent.MaxDistanceToTargetSquared)
            {
                ns.AgentSetVelocity(agent.NavRid, Vector3.Zero);
                continue;
            }
            
            var slowDownDistanceSquared = agent.SlowDownToTargetMaxDistanceSquared;
            if (distanceToTargetSquared < slowDownDistanceSquared)
            {
                speedMultiplier = Math.Max(0.2f, distanceToTargetSquared / slowDownDistanceSquared);
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
        // 12 here is the number of floats per transform, 4 for each of the basis vectors and 3 for the origin.
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
        var agentPositions = _agentPositions.Rent(_agents.Length);
        var targetPositions = _agentPositions.Rent(_agents.Length);
        var agentPathLastPoints = _agentPathLastPoints.Rent(_agents.Length);
        var isTargetReachable = _isTargetReachable.Rent(_agents.Length);
        var pathEmptyCondition = Vector3.Right * float.MaxValue;
        
        for (var i = 0; i < agents.Length; i++)
        {
            ref readonly var agent = ref agents[i];
            agentPositions[i] = agent.Transform3D.Origin;
            targetPositions[i] = agent.Target.GlobalPosition;
            var path = agent.Path;
            if (path.Length > 0)
            {
                agentPathLastPoints[i] = path[^1];
            }
            else
            {
                agentPathLastPoints[i] = pathEmptyCondition;
            }
        }
        
        var distancesToPlayerSquared = _distances.Rent(_agents.Length);
        var distancesToTargetSquared = _distances.Rent(_agents.Length);
        
        var distanceUpdatesTask = _distanceUpdatesWorker.EnqueueWork(
            () =>
            {
                for (var agentIndex = 0; agentIndex < agentPositions.Length; agentIndex++)
                {
                    ref var agentPosition = ref agentPositions[agentIndex];
                    ref var target = ref targetPositions[agentIndex];
                    
                    var agentTargetDx = target.X - agentPosition.X;
                    var agentTargetDz = target.Z - agentPosition.Z;
                    var agentTargetDxSq = agentTargetDx * agentTargetDx;
                    var agentTargetDzSq = agentTargetDz * agentTargetDz;
                    var agentTargetDistancesSquared = agentTargetDxSq + agentTargetDzSq;
                    distancesToTargetSquared[agentIndex] = agentTargetDistancesSquared;
                        
                    var agentPlayerDx = playerPosition.X - agentPosition.X;
                    var agentPlayerDz = playerPosition.Z - agentPosition.Z;
                    var agentPlayerDxSq = agentPlayerDx * agentPlayerDx;
                    var agentPlayerDzSq = agentPlayerDz * agentPlayerDz;
                    distancesToPlayerSquared[agentIndex] = agentPlayerDxSq + agentPlayerDzSq;
                    
                    var targetPosition = targetPositions[agentIndex];
                    
                    var targetMaxOffset = agentTargetDistancesSquared > 250f
                    ? _targetSafeDistanceFromPathEndSquared * 10f // just some value, it can be changed, no particular reason why 30
                    : _targetSafeDistanceFromPathEndSquared * 5f; // same as above, but careful, too low will stop the agents 
                    
                    // Keep the path if the target hasn't moved far enough.
                    var lastPathPoint = agentPathLastPoints[agentIndex];
                    if (lastPathPoint != pathEmptyCondition && lastPathPoint.DistanceSquaredTo(targetPosition) < targetMaxOffset)
                    {
                        isTargetReachable[agentIndex] = true;
                    }
                    else
                    {
                        isTargetReachable[agentIndex] = false;
                    }
                }
            }
        );
        
        await distanceUpdatesTask;

        _agentPositions.Return(agentPositions);
        _agentPositions.Return(targetPositions);
        _agentPathLastPoints.Return(agentPathLastPoints);

        Callable.From(() =>
        {
            ApplyAgentDistances(distancesToPlayerSquared, distancesToTargetSquared, isTargetReachable);
        }).CallDeferred();
    }

    private void ApplyAgentDistances(float[] distancesToPlayer, float[] distancesToTarget, bool[] isTargetReachable)
    {
        var agents = _agents.AsSpan();
        for (var index = 0; index < agents.Length; index++)
        {
            ref var agent = ref agents[index];
            agent.DistanceToPlayerSquared = distancesToPlayer[index];
            agent.DistanceToTargetSquared = distancesToTarget[index];
            agent.IsTargetReachable = isTargetReachable[index];
        }
        
        _distances.Return(distancesToPlayer);
        _distances.Return(distancesToTarget);
        _isTargetReachable.Return(isTargetReachable);
    }
}