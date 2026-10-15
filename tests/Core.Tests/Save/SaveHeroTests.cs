using System.Text;
using System.Text.Json.Nodes;
using Core.Heroes;
using Core.Save;
using Core.Sim;
using Core.Tests.Sim;

namespace Core.Tests.Save;

/// <summary>Saves with the hero, its path and inventory, and tile changes: format v3 (FR-SAVE-02/04, FR-HERO-01, FR-WORLD-05).</summary>
public sealed class SaveHeroTests
{
    private static SaveLoadResult Load(byte[] bytes) => SaveGame.Read(bytes, SimTestData.Real);

    private static byte[] Edit(Simulation sim, Action<JsonObject> edit)
    {
        byte[] indented = SaveGame.ToBytes(sim, new SaveWriteOptions(Indented: true));
        int newline = Array.IndexOf(indented, (byte)'\n');
        JsonObject body = JsonNode.Parse(Encoding.UTF8.GetString(indented, newline + 1, indented.Length - newline - 1))!.AsObject();
        edit(body);
        return [.. indented.AsSpan(0, newline + 1), .. Encoding.UTF8.GetBytes(body.ToJsonString())];
    }

    [Theory]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-HERO-01")]
    [Trait("Req", "FR-WORLD-05")]
    [InlineData(1)]   // walking (sprint) towards the node, mid-step
    [InlineData(25)]  // walking or arrived
    [InlineData(140)] // gathering, the node already lowered
    public void SaveLoadSaveIsByteIdenticalAndContinuesLikeTheUninterruptedRun(int ticks)
    {
        Simulation sim = SaveTestData.BuildHeroGame(ticks);
        byte[] first = SaveGame.ToBytes(sim);

        SaveLoadResult loaded = Load(first);

        Assert.True(loaded.Succeeded, loaded.Error?.Message);
        Simulation restored = loaded.Simulation!;
        Assert.Equal(first, SaveGame.ToBytes(restored));
        Assert.Null(restored.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
        Assert.Equal(sim.World!.Movers.GetEntries(), restored.World!.Movers.GetEntries());
        Assert.Equal(sim.World.Inventories.GetEntries(), restored.World.Inventories.GetEntries());
        Assert.Equal(sim.World.Heroes.GetEntries(), restored.World.Heroes.GetEntries());
        Assert.Equal(sim.World.Tiles.OccupiedTiles, restored.World.Tiles.OccupiedTiles);
        Assert.Equal(sim.World.Tiles.ChangedResourceTiles, restored.World.Tiles.ChangedResourceTiles);

        for (int i = 0; i < 300; i++)
        {
            sim.Step();
            restored.Step();
            Assert.Null(restored.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
        }

        Assert.True(restored.World.Tiles.ChangedResourceTiles > 0);
        Assert.Equal(SaveGame.ToBytes(sim), SaveGame.ToBytes(restored));
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    [Trait("Req", "FR-HERO-01")]
    public void BodyHoldsThePathTheInventoryAndTheTileChanges()
    {
        Simulation sim = SaveTestData.BuildHeroGame(140);
        JsonObject body = JsonNode.Parse(Encoding.UTF8.GetString(SaveGame.ToBytes(sim, new SaveWriteOptions(Indented: true))
            .AsSpan()[(Array.IndexOf(SaveGame.ToBytes(sim, new SaveWriteOptions(Indented: true)), (byte)'\n') + 1)..]))!.AsObject();

        JsonObject hero = body["components"]!["hero"]![0]!.AsObject();
        JsonObject inventory = body["components"]!["inventory"]![0]!.AsObject();
        Assert.Equal("gathering", (string?)hero["activity"]);
        Assert.Equal("accepted", (string?)hero["lastOrder"]);
        Assert.Equal(30, (int)inventory["backpackSize"]!);
        Assert.Equal(9, (int)inventory["equipmentSize"]!);
        Assert.Contains(inventory["backpack"]!.AsArray(), s => (string?)s!["item"] == "stone" && (int)s["slot"]! == 0 && (int)s["count"]! == 50);
        Assert.Contains(inventory["backpack"]!.AsArray(), s => (string?)s!["item"] == "wood");
        Assert.Empty(inventory["equipment"]!.AsArray());
        JsonObject resources = body["tiles"]!["resources"]![0]!.AsObject();
        Assert.Equal(16, resources["mask"]!.AsArray().Count);
        Assert.Single(resources["amounts"]!.AsArray());
        Assert.Single(body["tiles"]!["occupancy"]!.AsArray());
        Assert.Equal(1, (int)body["tiles"]!["occupancy"]![0]!["ownerIndices"]![0]!);
    }

    [Theory]
    [Trait("Req", "FR-SAVE-02")]
    [InlineData("unknown item")]
    [InlineData("overfull stack")]
    [InlineData("slot out of range")]
    [InlineData("slot order")]
    [InlineData("hero activity")]
    [InlineData("hero stop")]
    [InlineData("hero dead")]
    [InlineData("hero gathering off map")]
    [InlineData("inventory dead")]
    [InlineData("mover jump")]
    [InlineData("mover step index")]
    [InlineData("mover idle with goal")]
    [InlineData("tiles mask")]
    [InlineData("tiles owner dead")]
    [InlineData("tiles owners mismatch")]
    public void InvalidHeroOrTilePartsAreInvalidState(string damage)
    {
        Simulation sim = SaveTestData.BuildHeroGame(1);
        byte[] bytes = Edit(sim, body =>
        {
            JsonObject hero = body["components"]!["hero"]![0]!.AsObject();
            JsonObject inventory = body["components"]!["inventory"]![0]!.AsObject();
            JsonObject mover = body["components"]!["mover"]![0]!.AsObject();
            JsonArray backpack = inventory["backpack"]!.AsArray();
            JsonObject occupancy = body["tiles"]!["occupancy"]![0]!.AsObject();
            switch (damage)
            {
                case "unknown item": backpack[0]!["item"] = "unobtainium"; break;
                case "overfull stack": backpack[0]!["count"] = 51; break;
                case "slot out of range": backpack[0]!["slot"] = 30; break;
                case "slot order": backpack.Add(new JsonObject { ["slot"] = 0, ["item"] = "wood", ["count"] = 1 }); break;
                case "hero activity": hero["activity"] = "dancing"; break;
                case "hero stop": hero["lastStop"] = "bored"; break;
                case "hero dead": hero["generation"] = 5; break;
                case "hero gathering off map": hero["targetX"] = 64; break;
                case "inventory dead": inventory["index"] = 9; break;
                case "mover jump": mover["path"]!.AsArray().Add(0); break;
                case "mover step index": mover["stepIndex"] = mover["path"]!.AsArray().Count; break;
                case "mover idle with goal": mover["path"] = new JsonArray(); mover["stepIndex"] = 0; mover["stepProgress"] = 0; break;
                case "tiles mask": occupancy["mask"]!.AsArray().RemoveAt(0); break;
                case "tiles owner dead": occupancy["ownerGenerations"]![0] = 3; break;
                case "tiles owners mismatch": occupancy["ownerGenerations"]!.AsArray().Add(1); break;
            }
        });

        SaveLoadResult result = Load(bytes);

        Assert.False(result.Succeeded, damage);
        Assert.Equal(SaveErrorKind.InvalidState, result.Error!.Kind);
    }

    [Theory]
    [Trait("Req", "FR-SAVE-02")]
    [InlineData("hero")]
    [InlineData("tiles")]
    public void CoreSaveCannotCarryHeroOrTiles(string damage)
    {
        byte[] bytes = Edit(SaveTestData.BuildBusy(), body =>
        {
            if (damage == "hero")
            {
                body["components"]!["mover"] = new JsonArray(new JsonObject
                {
                    ["index"] = 0, ["generation"] = 1, ["goal"] = -1, ["goalRange"] = 0, ["path"] = new JsonArray(),
                    ["stepIndex"] = 0, ["stepProgress"] = 0, ["sprint"] = false,
                });
            }
            else
            {
                body["tiles"]!["resources"] = new JsonArray(new JsonObject { ["x"] = 0, ["y"] = 0, ["mask"] = new JsonArray(), ["amounts"] = new JsonArray() });
            }
        });

        Assert.Equal(SaveErrorKind.InvalidState, Load(bytes).Error!.Kind);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void MigrationRejectsAV2BodyThatAlreadyHasV3Members()
    {
        byte[] indented = SaveGame.ToBytes(SaveTestData.BuildHeroGame(1), new SaveWriteOptions(Indented: true));
        int newline = Array.IndexOf(indented, (byte)'\n');
        JsonObject header = JsonNode.Parse(indented.AsSpan(0, newline))!.AsObject();
        header["formatVersion"] = 2;
        byte[] bytes = [.. Encoding.UTF8.GetBytes(header.ToJsonString()), .. indented.AsSpan(newline)];

        SaveLoadResult result = Load(bytes);

        Assert.Equal(SaveErrorKind.CorruptBody, result.Error!.Kind);
        Assert.Contains("v3 members", result.Error.Message, StringComparison.Ordinal);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-HERO-01")]
    public void AWorldStateWithoutHeroComponentsPromotesTheHeroRevealer()
    {
        Simulation sim = SaveTestData.BuildHeroGame(30);
        SimulationState state = sim.CaptureState();
        WorldState legacy = state.World! with { Heroes = [], Inventories = [], Movers = [] };

        Simulation restored = Simulation.Restore(SimTestData.Real, state with { World = legacy });

        Core.Entities.EntityId hero = restored.World!.HeroEntity;
        Assert.Equal(sim.World!.HeroEntity, hero);
        Assert.Equal(Hero.FromBalance(SimTestData.Real.Hero), restored.World.Heroes.Get(hero));
        Assert.Equal(Inventory.Create(SimTestData.Real.Hero), restored.World.Inventories.Get(hero));
        Assert.Equal(Mover.Idle, restored.World.Movers.Get(hero));
    }
}
