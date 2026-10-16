using Core.Buildings;
using Core.Content;
using Core.Entities;
using Core.Sim;
using Game.Gameplay;
using Game.Sim;
using Game.Ui;
using Godot;
using CoreSimulation = Core.Sim.Simulation;

namespace Game.Smoke;

/// <summary>
/// Building panels part of the smoke test (FR-BUILD-01, FR-BUILD-09, FR-UI-01): builds the panels inside the game scene,
/// checks the grey buttons and their reasons before and after materials are given through the command path, opens the
/// inspector for a built house, presses "Upgrade" and checks the order reached Core, and that a refused order becomes a toast.
/// Returns null when everything passed, otherwise the reason.
/// </summary>
public static class BuildingUiSmoke
{
    private const int MapSize = 64;
    private const string Folder = "user://smoke_building_ui";
    private const int BuildTicks = 130;

    /// <summary>Runs the checks with nodes parented under <paramref name="owner"/> (freed before returning).</summary>
    public static string? Check(Node owner, ulong seed)
    {
        string dir = ProjectSettings.GlobalizePath(Folder);
        if (System.IO.Directory.Exists(dir))
        {
            System.IO.Directory.Delete(dir, recursive: true);
        }

        var host = new SimulationHost();
        owner.AddChild(host);
        host.SetProcess(false);
        host.SetProcessUnhandledInput(false);
        GameRoot? root = null;
        try
        {
            if (!host.Initialize(dir))
            {
                return "building ui: host did not initialize";
            }

            string? startError = host.StartNew(new NewGameOptions(seed, MapSize, Difficulty.Normal));
            if (startError is not null)
            {
                return "building ui: new game did not start " + startError;
            }

            root = GD.Load<PackedScene>("res://Gameplay/GameRoot.tscn").Instantiate<GameRoot>();
            owner.AddChild(root);
            root.Bind(host);
            return Run(host, root);
        }
        finally
        {
            root?.QueueFree();
            host.QueueFree();
            if (System.IO.Directory.Exists(dir))
            {
                System.IO.Directory.Delete(dir, recursive: true);
            }
        }
    }

    private static string? Run(SimulationHost host, GameRoot root)
    {
        var bar = root.GetNode<ResourceBar>("Ui/ResourceBar");
        var menu = root.GetNode<BuildMenu>("Ui/BuildMenu");
        var inspector = root.GetNode<InspectorPanel>("Ui/InspectorPanel");
        var messages = root.GetNode<BuildingMessages>("Ui/BuildingMessages");
        CoreSimulation sim = host.Simulation;
        messages.Poll();

        if (menu.IsOpen || inspector.Visible || bar.MouseFilter != Control.MouseFilterEnum.Stop || menu.MouseFilter != Control.MouseFilterEnum.Stop
            || inspector.MouseFilter != Control.MouseFilterEnum.Stop)
        {
            return "building ui: panels must start closed and stop the mouse";
        }

        menu.ToggleVisible();
        if (!menu.IsOpen || menu.Category != Construction.CenterCategory)
        {
            return $"building ui: the menu did not open on the center tab ({menu.Category})";
        }

        Button? campfire = menu.ButtonOf("campfire");
        Button? house = menu.ButtonOf("house");
        if (campfire is null || house is null || menu.ButtonOf("storage") is null)
        {
            return "building ui: a building button is missing";
        }

        if (!campfire.Disabled || menu.ReasonOf("campfire").Length == 0 || !house.Disabled || menu.ReasonOf("house").Length == 0)
        {
            return "building ui: buttons must be grey with a reason before any materials";
        }

        string chosen = string.Empty;
        menu.BuildingChosen += id => chosen = id;
        Give(host, "wood", 5);
        menu.Refresh();
        if (campfire.Disabled || menu.ReasonOf("campfire").Length != 0 || !house.Disabled)
        {
            return "building ui: the campfire button must be active after wood was given (house still needs a center)";
        }

        campfire.EmitSignal(BaseButton.SignalName.Pressed);
        if (chosen != "campfire" || menu.IsOpen)
        {
            return $"building ui: choosing the campfire raised '{chosen}' and left the menu open={menu.IsOpen}";
        }

        EntityId fire = Place(host, "campfire");
        if (fire == EntityId.None)
        {
            return "building ui: no place for the campfire";
        }

        sim.Step(BuildTicks);
        Give(host, "wood", 20);
        EntityId home = Place(host, "house");
        if (home == EntityId.None)
        {
            return "building ui: no place for the house";
        }

        inspector.Show(home);
        if (!inspector.Visible || !inspector.CancelButton.Visible || inspector.UpgradeButton.Visible)
        {
            return "building ui: a house under construction must offer only Cancel";
        }

        sim.Step(BuildTicks);
        inspector.Refresh();
        if (!inspector.UpgradeButton.Visible || !inspector.UpgradeButton.Disabled || !inspector.MoveButton.Visible || inspector.MoveButton.Disabled
            || inspector.CancelButton.Visible || inspector.DemolishButton.Disabled)
        {
            return "building ui: a finished house without materials must have a grey Upgrade and active Move and Demolish";
        }

        Give(host, "wood", 30);
        Give(host, "stone", 10);
        bar.RefreshNow();
        inspector.Refresh();
        if (inspector.UpgradeButton.Disabled || bar.ShownText.Length == 0)
        {
            return "building ui: Upgrade must be active after the materials were given";
        }

        inspector.UpgradeButton.EmitSignal(BaseButton.SignalName.Pressed);
        sim.Step();
        if (sim.World!.Buildings.Get(home).State != BuildingState.Upgrading)
        {
            return "building ui: pressing Upgrade did not reach Core";
        }

        // The center can never be demolished: the button is grey and the order, if sent anyway, comes back as a toast.
        inspector.Show(fire);
        if (!inspector.DemolishButton.Disabled || inspector.DemolishInfoText.Length == 0)
        {
            return "building ui: Demolish of the center must be grey with a reason";
        }

        string toast = string.Empty;
        host.Toast += key => toast = key;
        host.Send(new DemolishBuildingCommand(fire));
        sim.Step();
        messages.Poll();
        if (toast != BuildingText.ResultKey(BuildResult.CenterNotRemovable))
        {
            return $"building ui: expected the center refusal toast but got '{toast}'";
        }

        inspector.Hide();
        menu.ToggleVisible();
        menu.ToggleVisible();
        menu.ToggleVisible();
        GD.Print("Smoke building ui: menu, inspector, upgrade and refusal toast OK");
        return inspector.Visible ? "building ui: Hide left the inspector open" : null;
    }

    // Sends GiveItemCommand and runs one tick.
    internal static void Give(SimulationHost host, string item, int count)
    {
        host.Send(new GiveItemCommand(item, count));
        host.Simulation.Step();
    }

    // Places the building at the valid spot nearest to the start point and returns it, or none when there is no spot.
    internal static EntityId Place(SimulationHost host, string id)
    {
        CoreSimulation sim = host.Simulation;
        var start = sim.World!.Map.Start;
        for (int ring = 2; ring < MapSize; ring++)
        {
            for (int y = start.Y - ring; y <= start.Y + ring; y++)
            {
                for (int x = start.X - ring; x <= start.X + ring; x++)
                {
                    if (Math.Max(Math.Abs(x - start.X), Math.Abs(y - start.Y)) != ring || !Construction.CheckPlacement(sim, id, x, y, 0).IsOk)
                    {
                        continue;
                    }

                    long before = sim.World.LastBuildingEventSequence;
                    host.Send(new PlaceBuildingCommand(id, x, y));
                    sim.Step();
                    BuildingEvent placed = sim.World.BuildingEvents[^1];
                    return sim.World.LastBuildingEventSequence == before + 1 && placed.Result == BuildResult.Ok ? placed.Building : EntityId.None;
                }
            }
        }

        return EntityId.None;
    }
}
