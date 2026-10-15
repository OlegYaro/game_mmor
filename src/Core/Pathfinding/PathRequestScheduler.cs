using System.Runtime.InteropServices;
using Core.Entities;

namespace Core.Pathfinding;

/// <summary>Receives the results of <see cref="PathRequestScheduler.ProcessTick"/>.</summary>
public interface IPathResultSink
{
    /// <summary>
    /// Called once per processed request, in processing order. <paramref name="path"/> is valid only during the call
    /// (it points into the queue's buffer): copy what you keep.
    /// </summary>
    void OnPathResult(in PathRequest request, in PathResult result, ReadOnlySpan<int> path);
}

/// <summary>What one <see cref="PathRequestScheduler.ProcessTick"/> did.</summary>
/// <param name="Processed">Requests answered.</param>
/// <param name="NodesUsed">Nodes spent (each answered request counts at least one).</param>
/// <param name="Pending">Requests left for later ticks (budget spent or replan interval not over).</param>
public readonly record struct PathSchedulerTickStats(int Processed, int NodesUsed, int Pending);

/// <summary>
/// Path requests spread over ticks with a node budget (ADR 0003). Deterministic: requests run in the order
/// (higher priority, lower entity id, earlier request), each walker has at most one pending request (a new one
/// replaces it), and a walker's ordinary replans are at least <see cref="PathfinderConfig.ReplanIntervalTicks"/>
/// apart unless the request is <see cref="PathRequest.Forced"/>.
/// </summary>
/// <remarks>
/// The queue is runtime state of the movement system: lookups by entity use a dictionary but nothing iterates it, so
/// hash order never matters. Saving it (or draining it before a save) is up to the system that owns it.
/// </remarks>
public sealed class PathRequestScheduler
{
    private readonly Pathfinder _pathfinder;
    private readonly PathfinderConfig _config;
    private readonly List<Pending> _pending = [];
    private readonly Dictionary<EntityId, long> _lastSearchTick = [];
    private readonly List<int> _path = [];
    private long _nextSequence;
    private bool _processing;

    /// <summary>Creates a queue over <paramref name="pathfinder"/>.</summary>
    /// <exception cref="ArgumentOutOfRangeException">A config value is out of range.</exception>
    public PathRequestScheduler(Pathfinder pathfinder, PathfinderConfig config)
    {
        ArgumentNullException.ThrowIfNull(pathfinder);
        ArgumentNullException.ThrowIfNull(config);
        config.Validate();
        _pathfinder = pathfinder;
        _config = config;
    }

    /// <summary>Requests waiting to run.</summary>
    public int PendingCount => _pending.Count;

    /// <summary>
    /// Adds <paramref name="request"/> at <paramref name="tick"/>, replacing a pending request of the same entity.
    /// Returns the request's sequence number (the last ordering key).
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">A tile is outside the map.</exception>
    /// <exception cref="InvalidOperationException">Called from a result sink during <see cref="ProcessTick"/>.</exception>
    public long Enqueue(in PathRequest request, long tick)
    {
        ThrowIfProcessing();
        if (!_pathfinder.Grid.ContainsIndex(request.Start) || !_pathfinder.Grid.ContainsIndex(request.Goal))
        {
            throw new ArgumentOutOfRangeException(nameof(request), request, "Request tile outside the map.");
        }

        long readyTick = tick;
        if (!request.Forced && _lastSearchTick.TryGetValue(request.Entity, out long last))
        {
            readyTick = Math.Max(tick, last + _config.ReplanIntervalTicks);
        }

        long sequence = _nextSequence++;
        var entry = new Pending(request, sequence, readyTick);
        int existing = IndexOf(request.Entity);
        if (existing >= 0)
        {
            _pending[existing] = entry;
        }
        else
        {
            _pending.Add(entry);
        }

        return sequence;
    }

    /// <summary>Drops the pending request of <paramref name="entity"/>; false when it had none.</summary>
    /// <exception cref="InvalidOperationException">Called from a result sink during <see cref="ProcessTick"/>.</exception>
    public bool Cancel(EntityId entity)
    {
        ThrowIfProcessing();
        int index = IndexOf(entity);
        if (index < 0)
        {
            return false;
        }

        _pending.RemoveAt(index);
        return true;
    }

    /// <summary>Forgets <paramref name="entity"/> entirely (it was destroyed): its pending request and its last search tick.</summary>
    /// <exception cref="InvalidOperationException">Called from a result sink during <see cref="ProcessTick"/>.</exception>
    public void Forget(EntityId entity)
    {
        Cancel(entity);
        _lastSearchTick.Remove(entity);
    }

    /// <summary>
    /// Answers ready requests in order until the tick's node budget is spent; the rest wait. Each answer goes to
    /// <paramref name="sink"/> right away; the sink must not enqueue or cancel (do it on a later tick).
    /// </summary>
    /// <exception cref="InvalidOperationException">Called from a result sink during another <see cref="ProcessTick"/>.</exception>
    public PathSchedulerTickStats ProcessTick(long tick, IPathResultSink sink)
    {
        ArgumentNullException.ThrowIfNull(sink);
        ThrowIfProcessing();
        _processing = true;
        try
        {
            return ProcessSorted(tick, sink);
        }
        finally
        {
            _processing = false;
        }
    }

    private PathSchedulerTickStats ProcessSorted(long tick, IPathResultSink sink)
    {
        _pending.Sort(PendingOrder.Instance);
        int used = 0;
        int processed = 0;
        int kept = 0;
        for (int i = 0; i < _pending.Count; i++)
        {
            Pending entry = _pending[i];
            if (entry.ReadyTick > tick || used >= _config.NodeBudgetPerTick)
            {
                _pending[kept++] = entry;
                continue;
            }

            PathRequest request = entry.Request;
            PathResult result = _pathfinder.Find(request.Start, request.Goal, request.Kind, _path, _config.MaxNodesPerRequest);
            used += Math.Max(1, result.ExpandedNodes);
            processed++;
            _lastSearchTick[request.Entity] = tick;
            sink.OnPathResult(request, result, CollectionsMarshal.AsSpan(_path));
        }

        _pending.RemoveRange(kept, _pending.Count - kept);
        return new PathSchedulerTickStats(processed, used, _pending.Count);
    }

    private void ThrowIfProcessing()
    {
        if (_processing)
        {
            throw new InvalidOperationException("The path queue cannot change while it is processing a tick.");
        }
    }

    private int IndexOf(EntityId entity)
    {
        for (int i = 0; i < _pending.Count; i++)
        {
            if (_pending[i].Request.Entity == entity)
            {
                return i;
            }
        }

        return -1;
    }

    private readonly record struct Pending(PathRequest Request, long Sequence, long ReadyTick);

    private sealed class PendingOrder : IComparer<Pending>
    {
        public static readonly PendingOrder Instance = new();

        public int Compare(Pending a, Pending b)
        {
            int order = b.Request.Priority.CompareTo(a.Request.Priority);
            if (order == 0)
            {
                order = a.Request.Entity.Index.CompareTo(b.Request.Entity.Index);
            }

            if (order == 0)
            {
                order = a.Request.Entity.Generation.CompareTo(b.Request.Entity.Generation);
            }

            return order != 0 ? order : a.Sequence.CompareTo(b.Sequence);
        }
    }
}
