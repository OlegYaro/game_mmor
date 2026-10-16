using Core.Buildings;
using Core.Sim;
using Game.Sim;
using Godot;

namespace Game.Ui;

/// <summary>
/// Turns refused building orders (<see cref="GameWorld.BuildingEvents"/> with a result other than Ok) into toasts with the
/// localized reason (<c>ui.build.result.{name}</c>). Every event is shown once: the node remembers the last sequence it saw
/// and starts from the current one after the simulation was replaced. When several orders were refused in one frame the
/// toast shows the latest (the toast has one line).
/// </summary>
public partial class BuildingMessages : Node
{
    /// <summary>Key shown when a demolition or cancel could not return everything because the storages were full.</summary>
    public const string ItemsLostKey = "ui.build.result.items_lost";

    private SimulationHost? _host;
    private Simulation? _seen;
    private long _lastSequence;

    /// <summary>Connects the node to the host; refusals are shown with <see cref="SimulationHost.ShowToast"/>.</summary>
    public void Bind(SimulationHost host)
    {
        _host = host;
        _seen = null;
    }

    public override void _Process(double delta) => Poll();

    /// <summary>Looks for new building events and shows a toast for the latest refusal (called every frame).</summary>
    public void Poll()
    {
        if (_host is not { IsRunning: true } || _host.Simulation.World is not { } world)
        {
            return;
        }

        Simulation sim = _host.Simulation;
        if (!ReferenceEquals(sim, _seen))
        {
            _seen = sim;
            _lastSequence = world.LastBuildingEventSequence;
            return;
        }

        if (world.LastBuildingEventSequence == _lastSequence)
        {
            return;
        }

        string? key = null;
        foreach (BuildingEvent entry in world.BuildingEvents)
        {
            if (entry.Sequence <= _lastSequence)
            {
                continue;
            }

            if (entry.Result is not (BuildResult.Ok or BuildResult.None))
            {
                key = BuildingText.ResultKey(entry.Result);
            }
            else if (entry.Lost.Count > 0)
            {
                key = ItemsLostKey;
            }
        }

        _lastSequence = world.LastBuildingEventSequence;
        if (key is not null)
        {
            _host.ShowToast(key);
        }
    }
}
