using System.Text;
using System.Text.Json.Nodes;
using Core.Content;
using Core.Economy;
using Core.Entities;
using Core.Jobs;
using Core.Save;
using Core.Sim;
using static Core.Tests.Buildings.BuildingTestWorld;
using static Core.Tests.Jobs.JobTestWorld;

namespace Core.Tests.Save;

/// <summary>
/// Saves with work and food (format v4, FR-SAVE-02/04, FR-NPC-06, FR-NPC-07, FR-ECO-01, FR-ECO-10): jobs, reservations,
/// bans, field outputs, storage filters and the food state are saved; a game saved in the middle of the work continues
/// exactly like the uninterrupted one; damaged entries are rejected.
/// </summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class SaveJobTests
{
    private static readonly Lazy<byte[]> _working = new(() => SaveGame.ToBytes(BuildWorking(), new SaveWriteOptions(Indented: true)));

    /// <summary>
    /// A settlement at work: storage (no money taken in), field, house; a lumberjack, two farmers, a carrier and a
    /// blacksmith; a lumberjack banned from a far tree; two days without food so far (one resident has left); run until the
    /// lumberjack carries wood to the storage while the carrier hauls the field's output.
    /// </summary>
    internal static Simulation BuildWorking()
    {
        (Simulation sim, EntityId storage) = NewSettlement();
        Build(sim, "field");
        Build(sim, "house");
        sim.Enqueue(new SetStorageFilterCommand(storage, ItemCategories.AllMask & ~ItemCategories.BitOf(ItemCategories.Currency)));
        EntityId lumberjack = Spawn(sim, "lumberjack");
        Spawn(sim, "farmer");
        Spawn(sim, "farmer");
        EntityId carrier = Spawn(sim, "carrier");
        Spawn(sim, "blacksmith");
        sim.World!.Reservations.Ban(lumberjack, ReservationTarget.OfTile(TileIndex(sim, 0, 0)), sim.Tick + 5000);
        StepUntil(sim, () => JobOf(sim, lumberjack).Phase == JobPhase.ToStorage && JobOf(sim, carrier).Kind == JobKind.Haul);
        sim.World.DaysWithoutFood = 2;
        sim.World.LastLeaveDay = 0;
        return sim;
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-NPC-07")]
    [Trait("Req", "NFR-04")]
    public void SavesInTheMiddleOfTheWorkLoadIdenticallyAndContinueLikeTheUninterruptedGame()
    {
        Simulation sim = BuildWorking();
        for (int round = 0; round < 8; round++)
        {
            byte[] bytes = SaveGame.ToBytes(sim);
            SaveLoadResult loaded = SaveGame.Read(bytes, Quiet);
            Assert.True(loaded.Succeeded, loaded.Error?.Message);
            Simulation copy = loaded.Simulation!;
            Assert.Equal(bytes, SaveGame.ToBytes(copy));
            Assert.Null(copy.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
            Assert.Empty(copy.World!.EconomyEvents); // presentation only

            for (int tick = 0; tick < 137; tick++)
            {
                sim.Step();
                copy.Step();
                Assert.Null(copy.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
            }
        }

        Assert.Contains(sim.World!.Jobs.Values.ToArray(), j => j.Kind == JobKind.Farm);
    }

    /// <summary>
    /// Review R1 of M3a: a carrier walks to a field (haul, to the target) with room reserved in storage B; B is demolished.
    /// The job ends in that tick (its reservations with it) and the carrier takes the haul again with the other storage,
    /// so a save made right after loads and continues like the uninterrupted game.
    /// </summary>
    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-NPC-07")]
    public void ACarrierWhoseStorageIsDemolishedOnTheWayToTheFieldTakesTheHaulAgainAndTheGameSaves()
    {
        (Simulation sim, EntityId first) = NewSettlement();
        EntityId second = Build(sim, "storage");
        EntityId field = Build(sim, "field");
        sim.World!.WorkOutputs.Get(field) = new WorkOutput(10);
        EntityId carrier = Spawn(sim, "carrier");
        StepUntil(sim, () => JobOf(sim, carrier) is { Kind: JobKind.Haul, Phase: JobPhase.ToTarget });
        EntityId reserved = JobOf(sim, carrier).Storage;
        EntityId other = reserved == first ? second : first;

        sim.Enqueue(new DemolishBuildingCommand(reserved));
        sim.Step();

        Assert.False(sim.World.Buildings.Has(reserved));
        Assert.DoesNotContain(sim.World.Reservations.Reservations, r => r.Target == ReservationTarget.OfBuilding(ReservationLayer.Storage, reserved));
        Assert.Equal((JobKind.Haul, JobPhase.ToTarget, field, other), (JobOf(sim, carrier).Kind, JobOf(sim, carrier).Phase, JobOf(sim, carrier).Building, JobOf(sim, carrier).Storage));

        byte[] bytes = SaveGame.ToBytes(sim);
        SaveLoadResult loaded = SaveGame.Read(bytes, Quiet);
        Assert.True(loaded.Succeeded, loaded.Error?.Message);
        Simulation copy = loaded.Simulation!;
        for (int tick = 0; tick < 300; tick++)
        {
            sim.Step();
            copy.Step();
            Assert.Null(copy.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
        }

        Assert.Equal(10, Stored(sim, other, "vegetable"));
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "FR-NPC-07")]
    public void TheBodyHoldsJobsReservationsBansOutputsFiltersAndTheFoodState()
    {
        Simulation sim = BuildWorking();
        JsonObject body = Body(_working.Value);
        JsonObject components = body["components"]!.AsObject();

        Assert.Equal(sim.World!.Residents.Count, components["job"]!.AsArray().Count);
        Assert.Equal(
            ["index", "generation", "kind", "phase", "buildingIndex", "buildingGeneration", "targetTile", "storageIndex", "storageGeneration", "goalTile",
                "workLeft", "carryItem", "carryCount", "nextThinkTick", "idleLooks"],
            components["job"]![0]!.AsObject().Select(p => p.Key));
        JsonObject lumberjack = components["job"]!.AsArray().Single(j => (string?)j!["kind"] == "chop")!.AsObject();
        Assert.Equal(("to_storage", "wood", sim.Content.Economy.HaulCapacity), ((string?)lumberjack["phase"], (string?)lumberjack["carryItem"], (int)lumberjack["carryCount"]!));
        Assert.Equal("", (string?)components["job"]!.AsArray().Single(j => (string?)j!["kind"] == "none")!["carryItem"]);
        Assert.Single(components["workOutput"]!.AsArray());
        Assert.Equal(["resource", "food", "material"], components["storageFilter"]![0]!["categories"]!.AsArray().Select(c => (string)c!));

        JsonArray reservations = body["jobs"]!["reservations"]!.AsArray();
        Assert.Equal(sim.World.Reservations.Reservations.Count, reservations.Count);
        Assert.Contains(reservations, r => (string?)r!["layer"] == "tile");
        Assert.Contains(reservations, r => (string?)r!["layer"] == "work_slot");
        Assert.Contains(reservations, r => (string?)r!["layer"] == "storage" && (string?)r["item"] == "wood");
        Assert.Equal(["layer", "index", "generation", "holderIndex", "holderGeneration", "item", "amount"], reservations[0]!.AsObject().Select(p => p.Key));
        JsonObject ban = body["jobs"]!["bans"]!.AsArray().Single()!.AsObject();
        Assert.Equal(("tile", 0), ((string?)ban["layer"], (int)ban["index"]!));
        Assert.Equal((2, 0L), ((int)body["settlement"]!["daysWithoutFood"]!, (long)body["settlement"]!["lastLeaveDay"]!));
    }

    [Theory]
    [Trait("Req", "FR-SAVE-04")]
    [InlineData("job of a building")]
    [InlineData("resident without a job")]
    [InlineData("reservation of another target")]
    [InlineData("job without its reservations")]
    [InlineData("ban of a building")]
    [InlineData("ban that has ended")]
    [InlineData("filter on a house")]
    [InlineData("filter that allows everything")]
    [InlineData("output on a house")]
    [InlineData("field without output")]
    [InlineData("output above a stack")]
    [InlineData("negative days without food")]
    [InlineData("leave day without a shortage")]
    [InlineData("leave day in the future")]
    [InlineData("load above the haul capacity")]
    [InlineData("idle but working")]
    [InlineData("idle looks with a job")]
    [InlineData("idle looks out of range")]
    [InlineData("storage that is not one")]
    [InlineData("unknown job kind")]
    [InlineData("unknown phase")]
    [InlineData("unknown layer")]
    [InlineData("unknown category")]
    [InlineData("repeated category")]
    [InlineData("unknown carried item")]
    [InlineData("priorities out of order")]
    [InlineData("priority above 4")]
    public void InconsistentWorkIsRejected(string damage)
    {
        SaveLoadResult result = SaveGame.Read(Damage(damage), Quiet);

        Assert.False(result.Succeeded, damage);
        Assert.Equal(SaveErrorKind.InvalidState, result.Error!.Kind);
    }

    [Fact]
    [Trait("Req", "FR-SAVE-04")]
    public void TheUndamagedSampleLoads()
    {
        Assert.True(SaveGame.Read(_working.Value, Quiet).Succeeded);
    }

    private static byte[] Damage(string damage) => Edit(_working.Value, body =>
    {
        JsonObject components = body["components"]!.AsObject();
        JsonArray jobs = components["job"]!.AsArray();
        JsonObject chop = jobs.Single(j => (string?)j!["kind"] == "chop")!.AsObject();
        JsonObject idle = jobs.First(j => (string?)j!["kind"] == "none")!.AsObject();
        JsonArray buildings = components["building"]!.AsArray();
        JsonObject house = buildings.Single(b => (string?)b!["building"] == "house")!.AsObject();
        JsonObject storage = buildings.Single(b => (string?)b!["building"] == "storage")!.AsObject();
        JsonArray reservations = body["jobs"]!["reservations"]!.AsArray();
        JsonObject ban = body["jobs"]!["bans"]![0]!.AsObject();
        JsonObject settlement = body["settlement"]!.AsObject();
        JsonObject lumberjack = components["resident"]!.AsArray().Single(r => (int)r!["index"]! == (int)chop["index"]!)!.AsObject();
        switch (damage)
        {
            case "job of a building":
                JsonObject copy = idle.DeepClone().AsObject();
                copy["index"] = storage["index"]!.DeepClone();
                copy["generation"] = storage["generation"]!.DeepClone();
                jobs.Add(copy);
                SortByIndex(jobs);
                break;
            case "resident without a job": jobs.Remove(idle); break;
            case "reservation of another target": reservations.Single(r => (string?)r!["layer"] == "tile")!["index"] = 5; break;
            case "job without its reservations": reservations.Clear(); break;
            case "ban of a building": ban["holderIndex"] = storage["index"]!.DeepClone(); break;
            case "ban that has ended": ban["untilTick"] = body["tick"]!.DeepClone(); break;
            case "filter on a house": components["storageFilter"]![0]!["index"] = house["index"]!.DeepClone(); break;
            case "filter that allows everything": components["storageFilter"]![0]!["categories"] = new JsonArray("resource", "food", "material", "currency"); break;
            case "output on a house": components["workOutput"]![0]!["index"] = house["index"]!.DeepClone(); break;
            case "field without output": components["workOutput"]!.AsArray().Clear(); break;
            case "output above a stack": components["workOutput"]![0]!["count"] = 101; break;
            case "negative days without food": settlement["daysWithoutFood"] = -1; break;
            case "leave day without a shortage": settlement["daysWithoutFood"] = 0; break;
            case "leave day in the future": settlement["lastLeaveDay"] = 9; break;
            case "load above the haul capacity": chop["carryCount"] = 21; break;
            case "idle but working": idle["phase"] = "working"; break;
            case "idle looks with a job": chop["idleLooks"] = 2; break;
            case "idle looks out of range": idle["idleLooks"] = ResidentJob.MaxIdleLooks + 1; break;
            case "storage that is not one": chop["storageIndex"] = house["index"]!.DeepClone(); break;
            case "unknown job kind": chop["kind"] = "dance"; break;
            case "unknown phase": chop["phase"] = "napping"; break;
            case "unknown layer": reservations[0]!["layer"] = "attic"; break;
            case "unknown category": components["storageFilter"]![0]!["categories"] = new JsonArray("gems"); break;
            case "repeated category": components["storageFilter"]![0]!["categories"] = new JsonArray("food", "food"); break;
            case "unknown carried item": chop["carryItem"] = "unobtainium"; break;
            case "priorities out of order":
                JsonArray priorities = lumberjack["priorities"]!.AsArray();
                JsonNode last = priorities[^1]!;
                priorities.RemoveAt(priorities.Count - 1);
                priorities.Insert(0, last);
                break;
            case "priority above 4": lumberjack["priorities"]![0]!["priority"] = 5; break;
            default: throw new ArgumentOutOfRangeException(nameof(damage));
        }
    });

    private static void SortByIndex(JsonArray array)
    {
        JsonNode[] sorted = [.. array.OrderBy(n => (int)n!["index"]!).Select(n => n!.DeepClone())];
        array.Clear();
        foreach (JsonNode node in sorted)
        {
            array.Add(node);
        }
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
