using System.Text;
using System.Text.Json.Nodes;
using Core.Buildings;
using Core.Entities;
using Core.Heroes;
using Core.Save;
using Core.Sim;
using Core.Tests.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;

namespace Core.Tests.Save;

/// <summary>Saves with buildings in every state, a storage stock and building orders in the queue (FR-SAVE-02/04, FR-BUILD-02, 09, 13).</summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class SaveBuildingTests
{
    // One settlement (house under construction) for all the damaged-save cases, as an indented save.
    private static readonly Lazy<byte[]> _constructing = new(() =>
        SaveGame.ToBytes(BuildSettlement(BuildingState.Constructing), new SaveWriteOptions(Indented: true)));

    private static SaveLoadResult Load(byte[] bytes) => SaveGame.Read(bytes, SimTestData.Real);

    /// <summary>
    /// A settlement: a campfire, a storage holding 25 wood, and a house that is constructing, active, upgrading or
    /// moving; plus a pending move order for the storage.
    /// </summary>
    internal static Simulation BuildSettlement(BuildingState house)
    {
        Simulation sim = NewGame();
        Build(sim, "campfire");
        EntityId storage = Build(sim, "storage");
        sim.Step();
        Assert.True(sim.Content.TryGetId("wood", out Core.Content.DefId wood));
        ref Inventory stock = ref sim.World!.Inventories.Get(storage);
        stock = stock.Add(wood, 25, 100, out _);
        GiveCost(sim, "house", 1);
        (int x, int y) = FindSpot(sim, "house");
        EntityId entity = Place(sim, "house", x, y, give: false);
        if (house != BuildingState.Constructing)
        {
            StepUntilActive(sim, entity);
        }

        if (house == BuildingState.Upgrading)
        {
            GiveCost(sim, "house", 2);
            Assert.Equal(BuildResult.Ok, Order(sim, new UpgradeBuildingCommand(entity)).Result);
        }
        else if (house == BuildingState.Moving)
        {
            (int mx, int my) = FindSpot(sim, "house", rotation: 1, minDistance: 4, near: (x, y), moving: entity);
            Assert.Equal(BuildResult.Ok, Order(sim, new MoveBuildingCommand(entity, mx, my, 1)).Result);
        }

        sim.Step(5);
        Assert.Equal(house, BuildingOf(sim, entity).State);
        Building placed = BuildingOf(sim, storage);
        sim.Enqueue(new MoveBuildingCommand(storage, placed.X, placed.Y + 1));
        return sim;
    }

    [Theory]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-BUILD-02")]
    [Trait("Req", "FR-BUILD-09")]
    [Trait("Req", "FR-BUILD-13")]
    [InlineData(BuildingState.Constructing)]
    [InlineData(BuildingState.Active)]
    [InlineData(BuildingState.Upgrading)]
    [InlineData(BuildingState.Moving)]
    public void SaveLoadSaveIsByteIdenticalAndContinuesLikeTheUninterruptedRun(BuildingState state)
    {
        Simulation sim = BuildSettlement(state);
        byte[] first = SaveGame.ToBytes(sim);

        SaveLoadResult loaded = Load(first);

        Assert.True(loaded.Succeeded, loaded.Error?.Message);
        Simulation restored = loaded.Simulation!;
        Assert.Equal(first, SaveGame.ToBytes(restored));
        Assert.Null(restored.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
        Assert.Equal(sim.World!.Buildings.GetEntries(), restored.World!.Buildings.GetEntries());
        Assert.Equal(sim.World.Inventories.GetEntries(), restored.World.Inventories.GetEntries());
        Assert.Equal(sim.World.Tiles.CaptureState().Occupancy.Count, restored.World.Tiles.CaptureState().Occupancy.Count);
        Assert.Equal(sim.World.Tiles.OccupiedTiles, restored.World.Tiles.OccupiedTiles);
        Assert.Empty(restored.World.BuildingEvents); // presentation only, not saved

        for (int i = 0; i < 150; i++)
        {
            sim.Step();
            restored.Step();
            Assert.Null(restored.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
        }

        Assert.All(restored.World.Buildings.Values.ToArray(), b => Assert.Equal(BuildingState.Active, b.State));
        Assert.Equal(SaveGame.ToBytes(sim), SaveGame.ToBytes(restored));
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    [Trait("Req", "FR-BUILD-02")]
    public void BodyHoldsTheBuildingsByNameAndTheStockAsAnInventory()
    {
        Simulation sim = BuildSettlement(BuildingState.Moving);
        JsonObject body = Body(sim);

        JsonArray buildings = body["components"]!["building"]!.AsArray();
        Assert.Equal(["campfire", "storage", "house"], buildings.Select(b => (string?)b!["building"]));
        JsonObject house = buildings[2]!.AsObject();
        Assert.Equal(
            ["index", "generation", "building", "level", "state", "ticksLeft", "x", "y", "rotation", "targetX", "targetY", "targetRotation", "settlement"],
            house.Select(p => p.Key));
        Assert.Equal("moving", (string?)house["state"]);
        Assert.Equal(1, (int)house["targetRotation"]!);
        Assert.Equal(1, (int)house["settlement"]!);
        Assert.Equal("active", (string?)buildings[0]!["state"]);
        JsonObject stock = body["components"]!["inventory"]!.AsArray().Single(i => (int)i!["index"]! == (int)buildings[1]!["index"]!)!.AsObject();
        Assert.Equal(20, (int)stock["backpackSize"]!);
        Assert.Equal(0, (int)stock["equipmentSize"]!);
        Assert.Equal("wood", (string?)stock["backpack"]![0]!["item"]);
        Assert.Equal("move_building", (string?)body["commands"]!["pending"]![0]!["command"]!["kind"]);
    }

    [Theory]
    [Trait("Req", "FR-SAVE-02")]
    [Trait("Req", "FR-BUILD-02")]
    [InlineData("unknown type")]
    [InlineData("an item as type")]
    [InlineData("unknown state")]
    [InlineData("level 0")]
    [InlineData("level above the last")]
    [InlineData("constructing above level 1")]
    [InlineData("active with ticks left")]
    [InlineData("ticks above the total")]
    [InlineData("footprint not occupied")]
    [InlineData("off the map")]
    [InlineData("bad rotation")]
    [InlineData("other settlement")]
    [InlineData("target while active")]
    [InlineData("stock of the wrong size")]
    [InlineData("stock missing")]
    [InlineData("dead owner")]
    public void InvalidBuildingsAreRejected(string damage)
    {
        byte[] bytes = Edit(_constructing.Value, body =>
        {
            JsonArray buildings = body["components"]!["building"]!.AsArray();
            JsonObject campfire = buildings[0]!.AsObject();
            JsonObject storage = buildings[1]!.AsObject();
            JsonObject house = buildings[2]!.AsObject();
            JsonObject stock = body["components"]!["inventory"]!.AsArray().Single(i => (int)i!["index"]! == (int)storage["index"]!)!.AsObject();
            switch (damage)
            {
                case "unknown type": house["building"] = "castle"; break;
                case "an item as type": house["building"] = "wood"; break;
                case "unknown state": house["state"] = "burning"; break;
                case "level 0": campfire["level"] = 0; break;
                case "level above the last": campfire["level"] = 2; break;
                case "constructing above level 1": house["level"] = 2; break;
                case "active with ticks left": campfire["ticksLeft"] = 3; break;
                case "ticks above the total": house["ticksLeft"] = 1000; break;
                case "footprint not occupied": house["x"] = (int)house["x"]! + 20; break;
                case "off the map": house["y"] = 63; break;
                case "bad rotation": campfire["rotation"] = 1; break;
                case "other settlement": house["settlement"] = 2; break;
                case "target while active": campfire["targetX"] = 3; break;
                case "stock of the wrong size": stock["backpackSize"] = 21; break;
                case "stock missing": body["components"]!["inventory"]!.AsArray().Remove(stock); break;
                case "dead owner": house["index"] = 40; break;
                default: throw new ArgumentOutOfRangeException(nameof(damage));
            }
        });

        SaveLoadResult result = Load(bytes);

        Assert.False(result.Succeeded, damage);
        Assert.Equal(SaveErrorKind.InvalidState, result.Error!.Kind);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "NFR-04")]
    public void PendingBuildingOrdersAreHashedWithTheirPayload()
    {
        var house = new EntityId(3, 1);
        ISimCommand[] orders =
        [
            new PlaceBuildingCommand("house", 4, 5, 1),
            new CancelBuildingCommand(house),
            new UpgradeBuildingCommand(house),
            new MoveBuildingCommand(house, 6, 7, 1),
            new DemolishBuildingCommand(house),
        ];
        ISimCommand[] changed =
        [
            new PlaceBuildingCommand("house", 4, 5, 0),
            new CancelBuildingCommand(house with { Generation = 2 }),
            new UpgradeBuildingCommand(house with { Index = 4 }),
            new MoveBuildingCommand(house, 6, 8, 1),
            new DemolishBuildingCommand(house with { Generation = 2 }),
        ];

        for (int i = 0; i < orders.Length; i++)
        {
            Assert.Equal(CommandsHash(orders[i]), CommandsHash(CommandDto.FromCommand(orders[i]).ToCommand()));
            Assert.NotEqual(CommandsHash(orders[i]), CommandsHash(changed[i]));
        }
    }

    private static ulong CommandsHash(ISimCommand command)
    {
        var sim = new Simulation(1, SimTestData.Real);
        sim.Enqueue(command);
        return sim.ComputeHashes().TryGetHash(Simulation.CommandsHashName, out ulong hash) ? hash : 0;
    }

    private static JsonObject Body(Simulation sim)
    {
        byte[] indented = SaveGame.ToBytes(sim, new SaveWriteOptions(Indented: true));
        int newline = Array.IndexOf(indented, (byte)'\n');
        return JsonNode.Parse(Encoding.UTF8.GetString(indented, newline + 1, indented.Length - newline - 1))!.AsObject();
    }

    private static byte[] Edit(byte[] indented, Action<JsonObject> edit)
    {
        int newline = Array.IndexOf(indented, (byte)'\n');
        JsonObject body = JsonNode.Parse(Encoding.UTF8.GetString(indented, newline + 1, indented.Length - newline - 1))!.AsObject();
        edit(body);
        return [.. indented.AsSpan(0, newline + 1), .. Encoding.UTF8.GetBytes(body.ToJsonString())];
    }
}
