using Core.Content;
using Core.Economy;
using Core.Entities;
using Core.Heroes;
using Core.Jobs;
using Core.Residents;
using Core.Sim;
using Game.Gameplay;
using Game.Sim;
using Game.Ui;
using Godot;
using CoreSimulation = Core.Sim.Simulation;

namespace Game.Smoke;

/// <summary>
/// Residents and storage windows part of the smoke test (FR-UI-04, FR-NPC-05, FR-NPC-06, FR-ECO-01): builds the windows
/// inside the game scene, spawns residents through the command path, checks the rows, that a priority button and the
/// profession drop-down send commands that Core applies, that a storage filter switch changes the filter, the keys and Esc.
/// Returns null when everything passed, otherwise the reason.
/// </summary>
public static class WindowsUiSmoke
{
    private const int MapSize = 64;
    private const string Folder = "user://smoke_windows_ui";
    private const int BuildTicks = 130;
    private const int StorageTicks = 400;
    private const int Residents = 3;

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
                return "windows ui: host did not initialize";
            }

            string? startError = host.StartNew(new NewGameOptions(seed, MapSize, Difficulty.Normal));
            if (startError is not null)
            {
                return "windows ui: new game did not start " + startError;
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
        var residents = root.GetNode<ResidentsWindow>("Ui/ResidentsWindow");
        var storage = root.GetNode<StorageWindow>("Ui/StorageWindow");
        var buttons = root.GetNode<WindowButtons>("Ui/WindowButtons");
        var bar = root.GetNode<ResourceBar>("Ui/ResourceBar");
        CoreSimulation sim = host.Simulation;

        if (residents.IsOpen || storage.IsOpen || residents.MouseFilter != Control.MouseFilterEnum.Stop || storage.MouseFilter != Control.MouseFilterEnum.Stop
            || buttons.MouseFilter != Control.MouseFilterEnum.Stop)
        {
            return "windows ui: windows must start closed and stop the mouse";
        }

        if (buttons.GetGlobalRect().Intersects(bar.GetGlobalRect()))
        {
            return "windows ui: the window buttons overlap the resource bar";
        }

        // A settlement: center, a house and a storage.
        BuildingUiSmoke.Give(host, "wood", 5);
        if (BuildingUiSmoke.Place(host, "campfire") == EntityId.None)
        {
            return "windows ui: no place for the campfire";
        }

        sim.Step(BuildTicks);
        BuildingUiSmoke.Give(host, "wood", 200);
        BuildingUiSmoke.Give(host, "stone", 100);
        EntityId house = BuildingUiSmoke.Place(host, "house");
        EntityId store = BuildingUiSmoke.Place(host, "storage");
        if (house == EntityId.None || store == EntityId.None)
        {
            return "windows ui: no place for the house or the storage";
        }

        sim.Step(StorageTicks);
        for (int i = 0; i < Residents; i++)
        {
            host.Send(new SpawnResidentCommand());
        }

        sim.Step(2);
        if (sim.World!.Residents.Count != Residents)
        {
            return $"windows ui: expected {Residents} residents but there are {sim.World.Residents.Count}";
        }

        return CheckResidents(host, residents, buttons) ?? CheckStorage(host, root, residents, storage, buttons, store);
    }

    private static string? CheckResidents(SimulationHost host, ResidentsWindow residents, WindowButtons buttons)
    {
        CoreSimulation sim = host.Simulation;
        buttons.ResidentsButton.EmitSignal(BaseButton.SignalName.Pressed);
        if (!residents.IsOpen || residents.ShownRows != Residents)
        {
            return $"windows ui: the residents window shows {residents.ShownRows} rows (open {residents.IsOpen})";
        }

        EntityId first = sim.World!.Residents.Entities[0];
        ResidentsWindow.Row? row = residents.RowOf(first);
        if (row is null || row.NameText.Length == 0 || row.StateText.Length == 0 || row.HomeText.Length == 0 || row.Profession.Text.Length == 0
            || row.Priorities.Length != JobKinds.Count || residents.SummaryText.Length == 0)
        {
            return "windows ui: a resident row or the summary is empty";
        }

        int chop = (int)JobKind.Chop - 1;
        int before = sim.World.Residents.Get(first).Priorities.Get(JobKind.Chop);
        row.Priorities[chop].EmitSignal(BaseButton.SignalName.Pressed);
        sim.Step(2);
        int expected = (before + 1) % (WorkPriorities.Max + 1);
        int after = sim.World.Residents.Get(first).Priorities.Get(JobKind.Chop);
        residents.Refresh();
        if (after != expected || row.Priorities[chop].Text != (expected == 0 ? "-" : expected.ToString(System.Globalization.CultureInfo.InvariantCulture)))
        {
            return $"windows ui: the priority went {before} -> {after} (expected {expected}), button '{row.Priorities[chop].Text}'";
        }

        // The profession drop-down: an ordinary profession other than the current one.
        List<ProfessionDef> ordinary = sim.Content.Professions.Where(p => p.Kind == "ordinary").ToList();
        DefId current = sim.World.Residents.Get(first).Profession;
        int pick = ordinary.FindIndex(p => sim.Content.TryGetId(p.Id, out DefId id) && id != current);
        row.Profession.GetPopup().EmitSignal(PopupMenu.SignalName.IdPressed, (long)pick);
        sim.Step(2);
        residents.Refresh();
        DefId chosen = sim.World.Residents.Get(first).Profession;
        if (!sim.Content.TryGetId(ordinary[pick].Id, out DefId expectedProfession) || chosen != expectedProfession)
        {
            return "windows ui: the profession drop-down did not change the profession";
        }

        residents.Close();
        residents.Focus(first);
        if (!residents.IsOpen || residents.RowOf(first) is null)
        {
            return "windows ui: Focus did not open the window on the resident";
        }

        residents.Close();
        residents.Open();
        var escape = new InputEventKey { Pressed = true, Keycode = Key.Escape };
        residents._Input(escape);
        if (residents.IsOpen)
        {
            return "windows ui: Esc did not close the residents window";
        }

        var key = new InputEventKey { Pressed = true, PhysicalKeycode = Key.J };
        residents._UnhandledInput(key);
        if (!residents.IsOpen)
        {
            return "windows ui: J did not open the residents window";
        }

        residents._UnhandledInput(key);
        GD.Print($"Smoke residents window: {Residents} rows, priority {before} -> {after}, profession changed OK");
        return residents.IsOpen ? "windows ui: J did not close the residents window" : null;
    }

    private static string? CheckStorage(SimulationHost host, GameRoot root, ResidentsWindow residents, StorageWindow storage, WindowButtons buttons, EntityId store)
    {
        CoreSimulation sim = host.Simulation;
        residents.Open();
        buttons.StorageButton.EmitSignal(BaseButton.SignalName.Pressed);
        if (!storage.IsOpen || residents.IsOpen)
        {
            return "windows ui: opening the storage window must close the residents window";
        }

        if (storage.ButtonOf(store) is null || storage.ShownStorages < 1 || storage.Selected == EntityId.None || storage.FoodText.Length == 0)
        {
            return $"windows ui: the storage list is wrong ({storage.ShownStorages} rows, selected {storage.Selected})";
        }

        storage.Select(store);
        int foodBit = ItemCategories.BitOf(ItemCategories.Food);
        Button food = storage.CategoryButtons[ItemCategories.All.ToList().IndexOf(ItemCategories.Food)];
        if (!food.ButtonPressed || Storages.FilterOf(sim.World!, store) != ItemCategories.AllMask)
        {
            return "windows ui: a new storage must accept every category";
        }

        food.EmitSignal(BaseButton.SignalName.Pressed);
        sim.Step(2);
        storage.Refresh();
        if ((Storages.FilterOf(sim.World!, store) & foodBit) != 0 || food.ButtonPressed)
        {
            return "windows ui: the food switch did not remove food from the filter";
        }

        food.EmitSignal(BaseButton.SignalName.Pressed);
        sim.Step(2);
        if (Storages.FilterOf(sim.World!, store) != ItemCategories.AllMask)
        {
            return "windows ui: the second press did not restore the filter";
        }

        int before = BackpackCount(sim, "wood");
        storage.DepositButton.EmitSignal(BaseButton.SignalName.Pressed);
        sim.Step(2);
        int after = BackpackCount(sim, "wood");
        storage.Close();
        GD.Print($"Smoke storage window: {storage.ShownStorages} storages, filter toggled, backpack wood {before} -> {after} OK");
        return after < before ? null : "windows ui: the deposit button did not empty the backpack";
    }

    private static int BackpackCount(CoreSimulation sim, string item)
    {
        GameWorld world = sim.World!;
        return sim.Content.TryGetId(item, out DefId id) && world.Inventories.TryGet(world.HeroEntity, out Inventory backpack) ? backpack.Count(id) : 0;
    }
}
