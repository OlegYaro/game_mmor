using System.Text;
using System.Text.Json.Nodes;
using Core.Content;
using Core.Entities;
using Core.Residents;
using Core.Save;
using Core.Sim;
using Core.Tests.Sim;
using static Core.Tests.Residents.ResidentTestWorld;

namespace Core.Tests.Save;

/// <summary>
/// Saves with residents (format v4, FR-SAVE-02/04, FR-NPC-01, FR-NPC-02): the body holds them by name and profession,
/// a newcomer saved mid-walk keeps walking after a load exactly like the uninterrupted game, and damaged residents or a
/// damaged reached era are rejected.
/// </summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class SaveResidentTests
{
    // Two full houses (one resident a blacksmith, one a newcomer walking in) and a homeless resident, as an indented save.
    private static readonly Lazy<byte[]> _settled = new(() => SaveGame.ToBytes(BuildSettlement(), new SaveWriteOptions(Indented: true)));

    /// <summary>
    /// A settlement on the certain-arrival content: a campfire, two houses, three residents spawned at the center (the
    /// second a blacksmith), on the first morning a newcomer that takes the last place, and then a homeless resident;
    /// saved a few ticks into the newcomer's walk.
    /// </summary>
    internal static Simulation BuildSettlement()
    {
        Simulation sim = NewResidentGame(Certain);
        Settle(sim, houses: 2);
        Spawn(sim);
        Spawn(sim, "blacksmith");
        Spawn(sim);
        StepTo(sim, ArrivalTick(sim, 0) + 5);
        EntityId newcomer = Newest(sim);
        EntityId homeless = Spawn(sim);
        Assert.Equal(ResidentState.Arriving, ResidentOf(sim, newcomer).State);
        Assert.Equal(EntityId.None, ResidentOf(sim, homeless).Home);
        return sim;
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-NPC-01")]
    [Trait("Req", "NFR-04")]
    public void SaveLoadSaveIsByteIdenticalAndANewcomerKeepsWalkingLikeTheUninterruptedRun()
    {
        Simulation sim = BuildSettlement();
        byte[] bytes = SaveGame.ToBytes(sim);

        SaveLoadResult loaded = SaveGame.Read(bytes, Certain);

        Assert.True(loaded.Succeeded, loaded.Error?.Message);
        Simulation copy = loaded.Simulation!;
        Assert.Equal(bytes, SaveGame.ToBytes(copy));
        Assert.Null(copy.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
        Assert.Equal(sim.World!.Residents.GetEntries(), copy.World!.Residents.GetEntries());
        Assert.Empty(copy.World.ResidentEvents); // presentation only

        // Both run on to the next morning's newcomer and its walk: every tick's hashes stay equal.
        for (long tick = sim.Tick; tick < ArrivalTick(sim, 1) + 200; tick++)
        {
            sim.Step();
            copy.Step();
            Assert.Null(copy.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
        }

        Assert.Equal(5, sim.World.Residents.Count);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-NPC-02")]
    public void BodyHoldsResidentsByNameAndProfessionAndTheReachedEra()
    {
        Simulation sim = BuildSettlement();
        JsonObject body = Body(_settled.Value);
        JsonArray residents = body["components"]!["resident"]!.AsArray();

        Assert.Equal("village", (string?)body["settlement"]!["reachedEra"]); // five residents
        Assert.Equal(5, residents.Count);
        Assert.Equal(
            ["index", "generation", "name", "profession", "skills", "homeIndex", "homeGeneration", "state", "job"],
            residents[0]!.AsObject().Select(p => p.Key));
        Assert.Equal(["carrier", "blacksmith", "farmer", "stonemason", "carrier"], residents.Select(r => (string?)r!["profession"]));
        Assert.Equal(["idle", "idle", "idle", "arriving", "idle"], residents.Select(r => (string?)r!["state"]));
        Assert.Equal((0, 0), ((int)residents[4]!["homeIndex"]!, (int)residents[4]!["homeGeneration"]!));
        ReadOnlySpan<EntityId> owners = sim.World!.Residents.Entities;
        for (int i = 0; i < owners.Length; i++)
        {
            Resident resident = sim.World.Residents.Values[i];
            JsonObject saved = residents[i]!.AsObject();
            Assert.Equal(ResidentRules.NameOf(sim.Content, resident), (string?)saved["name"]);
            Assert.Equal(resident.Skills.ToArray(), saved["skills"]!.AsArray().Select(s => (int)s!));
            Assert.Equal((resident.Home.Index, resident.Home.Generation), ((int)saved["homeIndex"]!, (int)saved["homeGeneration"]!));
            Assert.Equal(0, (int)saved["job"]!);
        }
    }

    [Theory]
    [Trait("Req", "FR-SAVE-04")]
    [InlineData("unknown name")]
    [InlineData("unknown profession")]
    [InlineData("an item as profession")]
    [InlineData("eleven skills")]
    [InlineData("skill above 20")]
    [InlineData("unknown state")]
    [InlineData("home is not a house")]
    [InlineData("home is gone")]
    [InlineData("a job without a job board")]
    [InlineData("too many in one house")]
    [InlineData("no mover")]
    [InlineData("dead owner")]
    [InlineData("unknown era")]
    [InlineData("an item as era")]
    public void InvalidResidentsAreRejected(string damage)
    {
        byte[] bytes = Edit(_settled.Value, body =>
        {
            JsonArray residents = body["components"]!["resident"]!.AsArray();
            JsonObject first = residents[0]!.AsObject();
            JsonObject neighbour = residents.First(r => (int)r!["homeIndex"]! != (int)first["homeIndex"]! && (int)r["homeGeneration"]! != 0)!.AsObject();
            JsonObject campfire = body["components"]!["building"]!.AsArray()[0]!.AsObject();
            switch (damage)
            {
                case "unknown name": first["name"] = "Zzyzx"; break;
                case "unknown profession": first["profession"] = "astronaut"; break;
                case "an item as profession": first["profession"] = "wood"; break;
                case "eleven skills": first["skills"]!.AsArray().RemoveAt(0); break;
                case "skill above 20": first["skills"]![3] = 21; break;
                case "unknown state": first["state"] = "dancing"; break;
                case "home is not a house": first["homeIndex"] = campfire["index"]!.DeepClone(); first["homeGeneration"] = campfire["generation"]!.DeepClone(); break;
                case "home is gone": first["homeIndex"] = 50; first["homeGeneration"] = 1; break;
                case "a job without a job board": first["job"] = 3; break;
                case "too many in one house": neighbour["homeIndex"] = first["homeIndex"]!.DeepClone(); neighbour["homeGeneration"] = first["homeGeneration"]!.DeepClone(); break;
                case "no mover":
                    JsonArray movers = body["components"]!["mover"]!.AsArray();
                    movers.Remove(movers.Single(m => (int)m!["index"]! == (int)first["index"]!));
                    break;
                case "dead owner": first["index"] = 60; break;
                case "unknown era": body["settlement"]!["reachedEra"] = "space_age"; break;
                case "an item as era": body["settlement"]!["reachedEra"] = "wood"; break;
                default: throw new ArgumentOutOfRangeException(nameof(damage));
            }
        });

        SaveLoadResult result = SaveGame.Read(bytes, Certain);

        Assert.False(result.Succeeded, damage);
        Assert.Equal(SaveErrorKind.InvalidState, result.Error!.Kind);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    public void ASaveWithoutAWorldHasNoResidentsAndNoEra()
    {
        byte[] core = SaveGame.ToBytes(new Simulation(5, SimTestData.Real), new SaveWriteOptions(Indented: true));
        Assert.Equal(string.Empty, (string?)Body(core)["settlement"]!["reachedEra"]);
        Assert.True(SaveGame.Read(core, SimTestData.Real).Succeeded);

        byte[] withEra = Edit(core, body => body["settlement"]!["reachedEra"] = "camp");
        byte[] withResident = Edit(core, body => body["components"]!["resident"]!.AsArray().Add(Body(_settled.Value)["components"]!["resident"]![0]!.DeepClone()));

        Assert.Equal(SaveErrorKind.InvalidState, SaveGame.Read(withEra, SimTestData.Real).Error!.Kind);
        Assert.Equal(SaveErrorKind.InvalidState, SaveGame.Read(withResident, SimTestData.Real).Error!.Kind);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-02")]
    public void MigrationV3ToV4AddsAnEmptyResidentStoreAndTheFirstEraOnlyForGamesWithAWorld()
    {
        var migration = new SaveMigrationV3ToV4();
        JsonObject game = Migrated(64);
        JsonObject core = Migrated(0);

        Assert.Equal(3, migration.FromVersion);
        Assert.Equal("camp", (string?)game["settlement"]!["reachedEra"]);
        Assert.Equal(string.Empty, (string?)core["settlement"]!["reachedEra"]);
        Assert.Empty(game["components"]!["resident"]!.AsArray());
        Assert.Throws<InvalidOperationException>(() => migration.Migrate(new JsonObject(), new JsonObject { ["game"] = new JsonObject() }));
        Assert.Throws<InvalidOperationException>(() => migration.Migrate(new JsonObject(), game));

        static JsonObject Migrated(int mapSize)
        {
            var body = new JsonObject { ["components"] = new JsonObject(), ["game"] = new JsonObject { ["mapSize"] = mapSize } };
            new SaveMigrationV3ToV4().Migrate(new JsonObject(), body);
            return body;
        }
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "NFR-04")]
    public void PendingSpawnOrdersAreHashedWithTheirProfession()
    {
        Assert.NotEqual(CommandsHash(new SpawnResidentCommand()), CommandsHash(new SpawnResidentCommand("farmer")));
        Assert.Equal(CommandsHash(new SpawnResidentCommand("farmer")), CommandsHash(CommandDto.FromCommand(new SpawnResidentCommand("farmer")).ToCommand()));
    }

    private static ulong CommandsHash(ISimCommand command)
    {
        var sim = new Simulation(1, SimTestData.Real);
        sim.Enqueue(command);
        return sim.ComputeHashes().TryGetHash(Simulation.CommandsHashName, out ulong hash) ? hash : 0;
    }

    private static JsonObject Body(byte[] indented)
    {
        int newline = Array.IndexOf(indented, (byte)'\n');
        return JsonNode.Parse(Encoding.UTF8.GetString(indented, newline + 1, indented.Length - newline - 1))!.AsObject();
    }

    private static byte[] Edit(byte[] indented, Action<JsonObject> edit)
    {
        int newline = Array.IndexOf(indented, (byte)'\n');
        JsonObject body = Body(indented);
        edit(body);
        return [.. indented.AsSpan(0, newline + 1), .. Encoding.UTF8.GetBytes(body.ToJsonString())];
    }
}
