using Core.Content;
using Core.Determinism;
using Core.Entities;

namespace Core.Jobs;

/// <summary>
/// What a reservation holds (ADR 0009: cell, thing, work slot; plus the room in a storage). Values are hashed; save names
/// are <see cref="ReservationManager.LayerNames"/>. Append only.
/// </summary>
public enum ReservationLayer : byte
{
    /// <summary>A resource node tile (the cell): one gatherer at a time.</summary>
    Tile = 0,

    /// <summary>A work slot of a building (a field): as many holders as the level's slots.</summary>
    WorkSlot = 1,

    /// <summary>Items waiting in a building's output (the thing): an amount, at most what the output holds.</summary>
    Output = 2,

    /// <summary>Room in a storage for a load on its way: an amount of one item.</summary>
    Storage = 3,
}

/// <summary>A reservable target: a tile index (<see cref="ReservationLayer.Tile"/>) or a building entity.</summary>
/// <param name="Layer">What is reserved.</param>
/// <param name="Index">The tile index, or the building's entity index.</param>
/// <param name="Generation">The building's entity generation; 0 for a tile.</param>
public readonly record struct ReservationTarget(ReservationLayer Layer, int Index, int Generation)
{
    /// <summary>The node on tile <paramref name="tile"/>.</summary>
    public static ReservationTarget OfTile(int tile) => new(ReservationLayer.Tile, tile, 0);

    /// <summary>A building target of <paramref name="layer"/>.</summary>
    public static ReservationTarget OfBuilding(ReservationLayer layer, EntityId building) => new(layer, building.Index, building.Generation);

    /// <summary>The building of a building target.</summary>
    public EntityId Building => new(Index, Generation);

    /// <summary>Key order: layer, index, generation.</summary>
    public static int Compare(ReservationTarget a, ReservationTarget b)
    {
        int c = a.Layer.CompareTo(b.Layer);
        if (c == 0)
        {
            c = a.Index.CompareTo(b.Index);
        }

        return c != 0 ? c : a.Generation.CompareTo(b.Generation);
    }

    /// <summary>Writes the layer (one byte), the index and the generation.</summary>
    public void WriteState(StateWriter writer)
    {
        writer.WriteByte((byte)Layer);
        writer.WriteInt32(Index);
        writer.WriteInt32(Generation);
    }

    /// <inheritdoc />
    public override string ToString()
    {
        string layer = (int)Layer < ReservationManager.LayerNames.Count
            ? ReservationManager.LayerNames[(int)Layer]
            : string.Create(System.Globalization.CultureInfo.InvariantCulture, $"layer {(int)Layer}");
        return Layer == ReservationLayer.Tile ? string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{layer} {Index}") : $"{layer} {Building}";
    }
}

/// <summary>One reservation: <paramref name="Holder"/> holds <paramref name="Amount"/> of <paramref name="Target"/>.</summary>
/// <param name="Target">What is reserved.</param>
/// <param name="Holder">The resident holding it.</param>
/// <param name="Item">The item of an <see cref="ReservationLayer.Output"/> or <see cref="ReservationLayer.Storage"/> reservation; default otherwise.</param>
/// <param name="Amount">Items (output, storage room) or 1 (a tile, a work slot).</param>
public readonly record struct Reservation(ReservationTarget Target, EntityId Holder, DefId Item, int Amount);

/// <summary>A target a resident failed to reach: it does not take it again before <paramref name="UntilTick"/>.</summary>
/// <param name="Holder">The resident.</param>
/// <param name="Target">The target it failed on.</param>
/// <param name="UntilTick">First tick at which the target is allowed again.</param>
public readonly record struct JobBan(EntityId Holder, ReservationTarget Target, long UntilTick);

/// <summary>A target to reserve, with the most that may be reserved of it in total (this request included).</summary>
/// <param name="Target">What to reserve.</param>
/// <param name="Item">The item (output, storage room); default otherwise.</param>
/// <param name="Amount">How much.</param>
/// <param name="Limit">Upper bound of the sum of all reservations of the target after this one.</param>
public readonly record struct ReservationRequest(ReservationTarget Target, DefId Item, int Amount, int Limit);

/// <summary>Saved reservations and bans (<see cref="ReservationManager"/>), both in key order.</summary>
/// <param name="Reservations">Reservations by (target, holder).</param>
/// <param name="Bans">Bans by (holder, target).</param>
public sealed record ReservationsState(IReadOnlyList<Reservation> Reservations, IReadOnlyList<JobBan> Bans)
{
    /// <summary>No reservations, no bans.</summary>
    public static ReservationsState Empty { get; } = new([], []);
}

/// <summary>
/// The reservations of the job board (FR-NPC-07, ADR 0009) and the residents' banned targets. A job starts only when all
/// its targets could be reserved at once (<see cref="TryReserveAll"/>); its reservations end together, at the single
/// release point <see cref="ReleaseAll"/> called when the job ends (done or cancelled). Part of the state: saved and hashed
/// (subsystem <see cref="Sim.Simulation.JobsHashName"/>), kept sorted by key so the order is canonical.
/// </summary>
public sealed class ReservationManager : IStateHashable
{
    private readonly List<Reservation> _reservations = [];
    private readonly List<JobBan> _bans = [];

    /// <summary>Stable save names of <see cref="ReservationLayer"/>, indexed by value. Append only.</summary>
    public static IReadOnlyList<string> LayerNames { get; } = ["tile", "work_slot", "output", "storage"];

    /// <summary>All reservations, by target then holder.</summary>
    public IReadOnlyList<Reservation> Reservations => _reservations;

    /// <summary>All bans, by holder then target.</summary>
    public IReadOnlyList<JobBan> Bans => _bans;

    /// <summary>Sum of the amounts reserved of <paramref name="target"/>.</summary>
    public int AmountReserved(ReservationTarget target)
    {
        int total = 0;
        for (int i = First(target); i < _reservations.Count && _reservations[i].Target == target; i++)
        {
            total += _reservations[i].Amount;
        }

        return total;
    }

    /// <summary>Number of residents holding <paramref name="target"/>.</summary>
    public int HolderCount(ReservationTarget target)
    {
        int count = 0;
        for (int i = First(target); i < _reservations.Count && _reservations[i].Target == target; i++)
        {
            count++;
        }

        return count;
    }

    /// <summary>True when someone holds <paramref name="target"/>.</summary>
    public bool IsReserved(ReservationTarget target)
    {
        int i = First(target);
        return i < _reservations.Count && _reservations[i].Target == target;
    }

    /// <summary>The reservations of the storage <paramref name="storage"/> (incoming loads), in holder order.</summary>
    public IEnumerable<Reservation> IncomingTo(EntityId storage)
    {
        ReservationTarget target = ReservationTarget.OfBuilding(ReservationLayer.Storage, storage);
        for (int i = First(target); i < _reservations.Count && _reservations[i].Target == target; i++)
        {
            yield return _reservations[i];
        }
    }

    /// <summary>The reservations <paramref name="holder"/> holds, in target order.</summary>
    public IEnumerable<Reservation> HeldBy(EntityId holder)
    {
        foreach (Reservation reservation in _reservations)
        {
            if (reservation.Holder == holder)
            {
                yield return reservation;
            }
        }
    }

    /// <summary>
    /// Reserves every request for <paramref name="holder"/>, or none: false (and no change) when a request has a
    /// non-positive amount, the holder already holds its target, two requests name the same target, or the target's
    /// reserved total would exceed the request's limit.
    /// </summary>
    public bool TryReserveAll(EntityId holder, ReadOnlySpan<ReservationRequest> requests)
    {
        for (int i = 0; i < requests.Length; i++)
        {
            ReservationRequest r = requests[i];
            if (r.Amount <= 0 || Find(r.Target, holder) >= 0 || AmountReserved(r.Target) + r.Amount > r.Limit)
            {
                return false;
            }

            for (int j = 0; j < i; j++)
            {
                if (requests[j].Target == r.Target)
                {
                    return false;
                }
            }
        }

        foreach (ReservationRequest r in requests)
        {
            var reservation = new Reservation(r.Target, holder, r.Item, r.Amount);
            _reservations.Insert(~Search(r.Target, holder), reservation);
        }

        return true;
    }

    /// <summary>The single release point: drops every reservation of <paramref name="holder"/> (its job ended or was cancelled).</summary>
    /// <returns>How many were released.</returns>
    public int ReleaseAll(EntityId holder) => _reservations.RemoveAll(r => r.Holder == holder);

    /// <summary>Bans <paramref name="target"/> for <paramref name="holder"/> until <paramref name="untilTick"/> (replacing an older ban of it).</summary>
    public void Ban(EntityId holder, ReservationTarget target, long untilTick)
    {
        var ban = new JobBan(holder, target, untilTick);
        int slot = SearchBan(holder, target);
        if (slot >= 0)
        {
            _bans[slot] = ban;
        }
        else
        {
            _bans.Insert(~slot, ban);
        }
    }

    /// <summary>True when <paramref name="holder"/> may not take <paramref name="target"/> at <paramref name="tick"/>.</summary>
    public bool IsBanned(EntityId holder, ReservationTarget target, long tick)
    {
        if (_bans.Count == 0)
        {
            return false;
        }

        int slot = SearchBan(holder, target);
        return slot >= 0 && _bans[slot].UntilTick > tick;
    }

    /// <summary>
    /// Forgets bans that ended by <paramref name="tick"/> and everything held by entities that are no longer alive
    /// (a resident removed by a command), so the state stays canonical.
    /// </summary>
    internal void Prune(long tick, EntityRegistry registry)
    {
        _bans.RemoveAll(b => b.UntilTick <= tick || !registry.IsAlive(b.Holder));
        _reservations.RemoveAll(r => !registry.IsAlive(r.Holder));
    }

    /// <summary>Snapshot for saves.</summary>
    public ReservationsState CaptureState() => new(_reservations.ToArray(), _bans.ToArray());

    /// <summary>Replaces the content with a saved state.</summary>
    /// <exception cref="ArgumentException">The entries are not in strict key order, or an amount or a tick is invalid.</exception>
    internal void Load(ReservationsState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        for (int i = 0; i < state.Reservations.Count; i++)
        {
            Reservation r = state.Reservations[i];
            if (r.Amount <= 0 || !Enum.IsDefined(r.Target.Layer) || (i > 0 && Compare(state.Reservations[i - 1], r) >= 0))
            {
                throw new ArgumentException($"Reservation {i} ({r}) is invalid or out of order.", nameof(state));
            }
        }

        for (int i = 0; i < state.Bans.Count; i++)
        {
            JobBan b = state.Bans[i];
            if (b.UntilTick < 0 || !Enum.IsDefined(b.Target.Layer)
                || (i > 0 && CompareBan(state.Bans[i - 1].Holder, state.Bans[i - 1].Target, b.Holder, b.Target) >= 0))
            {
                throw new ArgumentException($"Ban {i} ({b}) is invalid or out of order.", nameof(state));
            }
        }

        _reservations.Clear();
        _reservations.AddRange(state.Reservations);
        _bans.Clear();
        _bans.AddRange(state.Bans);
    }

    /// <summary>Writes a tag, the reservations (target, holder, item, amount) and the bans (holder, target, tick) in key order.</summary>
    public void WriteState(StateWriter writer)
    {
        ArgumentNullException.ThrowIfNull(writer);
        writer.WriteString("jobs");
        writer.WriteInt32(_reservations.Count);
        foreach (Reservation r in _reservations)
        {
            r.Target.WriteState(writer);
            r.Holder.WriteState(writer);
            writer.WriteInt32(r.Item.Value);
            writer.WriteInt32(r.Amount);
        }

        writer.WriteInt32(_bans.Count);
        foreach (JobBan b in _bans)
        {
            b.Holder.WriteState(writer);
            b.Target.WriteState(writer);
            writer.WriteInt64(b.UntilTick);
        }
    }

    private static int Compare(Reservation a, Reservation b)
    {
        int c = ReservationTarget.Compare(a.Target, b.Target);
        return c != 0 ? c : CompareEntity(a.Holder, b.Holder);
    }

    private static int CompareBan(EntityId aHolder, ReservationTarget aTarget, EntityId bHolder, ReservationTarget bTarget)
    {
        int c = CompareEntity(aHolder, bHolder);
        return c != 0 ? c : ReservationTarget.Compare(aTarget, bTarget);
    }

    private static int CompareEntity(EntityId a, EntityId b)
    {
        int c = a.Index.CompareTo(b.Index);
        return c != 0 ? c : a.Generation.CompareTo(b.Generation);
    }

    // Index of the first reservation of `target` (or where it would be).
    private int First(ReservationTarget target)
    {
        int low = 0;
        int high = _reservations.Count;
        while (low < high)
        {
            int mid = low + ((high - low) >> 1);
            if (ReservationTarget.Compare(_reservations[mid].Target, target) < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid;
            }
        }

        return low;
    }

    private int Find(ReservationTarget target, EntityId holder)
    {
        int slot = Search(target, holder);
        return slot >= 0 ? slot : -1;
    }

    // Binary search by (target, holder): the slot, or the complement of the insertion point.
    private int Search(ReservationTarget target, EntityId holder)
    {
        var key = new Reservation(target, holder, default, 0);
        int low = 0;
        int high = _reservations.Count - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            int c = Compare(_reservations[mid], key);
            if (c == 0)
            {
                return mid;
            }

            if (c < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return ~low;
    }

    private int SearchBan(EntityId holder, ReservationTarget target)
    {
        int low = 0;
        int high = _bans.Count - 1;
        while (low <= high)
        {
            int mid = low + ((high - low) >> 1);
            int c = CompareBan(_bans[mid].Holder, _bans[mid].Target, holder, target);
            if (c == 0)
            {
                return mid;
            }

            if (c < 0)
            {
                low = mid + 1;
            }
            else
            {
                high = mid - 1;
            }
        }

        return ~low;
    }
}
