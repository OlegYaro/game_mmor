using Core.Buildings;
using Core.Entities;
using Core.Jobs;
using Core.Pathfinding;
using Core.Save;
using Core.Sim;
using Core.Tiles;
using Core.WorldGen;
using static Core.Tests.Jobs.JobTestWorld;

namespace Core.Tests.Jobs;

/// <summary>
/// The planting job (FR-WORLD-04, FR-NPC-06, FR-NPC-07): a lumberjack with nothing to chop plants a sapling on a free
/// tile next to a stump inside the territory, the tile is reserved for it, without stumps nothing is planted, a tile
/// taken on the way ends the job, the lumberjack chops trees that grew from saplings, and a save in the middle of the
/// planting continues identically.
/// </summary>
[Collection(Core.Tests.Buildings.BuildingTestWorld.Collection)]
public sealed class PlantJobTests
{
    // A stump: the tree nearest to the hero (next to the settlement) is felled straight in the tile layer.
    private static (int X, int Y) Fell(Simulation sim)
    {
        (int x, int y) = Heroes.HeroTestWorld.NearestNode(sim, ResourceKind.Tree);
        Assert.True(sim.World!.Tiles.TakeResource(x, y, int.MaxValue) > 0);
        return (x, y);
    }

    // A lumberjack that only plants (chopping would come first otherwise: chop ranks above plant in a tier).
    private static EntityId Planter(Simulation sim)
    {
        EntityId lumberjack = Spawn(sim, "lumberjack");
        StepUntil(sim, () => JobOf(sim, lumberjack).Kind == JobKind.Chop);
        sim.Enqueue(new SetWorkPriorityCommand(lumberjack, "chop", 0));
        sim.Step();
        return lumberjack;
    }

    private static (int X, int Y) TileOf(Simulation sim, int index) => (index % sim.World!.Size, index / sim.World.Size);

    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    [Trait("Req", "FR-NPC-06")]
    [Trait("Req", "FR-NPC-07")]
    public void ALumberjackPlantsASaplingOnAFreeReservedTileNextToAStump()
    {
        (Simulation sim, _) = NewSettlement();
        GameWorld world = sim.World!;
        EntityId lumberjack = Planter(sim);
        sim.Step(100);
        Assert.Equal(JobKind.None, JobOf(sim, lumberjack).Kind); // no stump yet (its first tree still stands): nothing to replant

        Fell(sim);
        StepUntil(sim, () => JobOf(sim, lumberjack).Kind == JobKind.Plant);

        ResidentJob job = JobOf(sim, lumberjack);
        (int x, int y) = TileOf(sim, job.TargetTile);
        Territory territory = Construction.GetTerritory(sim)!.Value;
        Assert.Equal((JobPhase.ToTarget, EntityId.None, EntityId.None, 0), (job.Phase, job.Building, job.Storage, job.CarryCount));
        Assert.True(world.Tiles.CanPlant(x, y));
        Assert.True(territory.Contains(x, y));
        Assert.Contains(world.Reservations.HeldBy(lumberjack), r => r.Target == ReservationTarget.OfTile(job.TargetTile));
        Assert.Contains(Around(x, y, 1), t => world.Map.Terrain.Contains(t.X, t.Y) && world.Tiles.GetFlora(t.X, t.Y) == FloraState.Stump);
        Assert.DoesNotContain(Around(x, y, 1), t => world.Map.Terrain.Contains(t.X, t.Y) && world.Tiles.IsOccupied(t.X, t.Y));

        // It walks there, works the planting ticks and leaves a sapling; the reservation ends with the job.
        StepUntil(sim, () => JobOf(sim, lumberjack).Phase == JobPhase.Working);
        long started = sim.Tick;
        StepUntil(sim, () => world.Tiles.GetFlora(x, y) == FloraState.Sapling);
        Assert.InRange(sim.Tick - started, 1, sim.Content.Regrowth.PlantActionTicks);
        Assert.Equal(sim.Tick, world.Tiles.GetFloraTick(x, y));
        Assert.DoesNotContain(world.Reservations.Reservations, r => r.Target == ReservationTarget.OfTile(job.TargetTile));

        // And plants the next one elsewhere.
        StepUntil(sim, () => JobOf(sim, lumberjack).Kind == JobKind.Plant);
        Assert.NotEqual(job.TargetTile, JobOf(sim, lumberjack).TargetTile);
    }

    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    [Trait("Req", "FR-NPC-07")]
    public void TwoPlantersNeverShareATileAndATileTakenOnTheWayEndsTheJob()
    {
        (Simulation sim, _) = NewSettlement();
        GameWorld world = sim.World!;
        Fell(sim);
        EntityId first = Planter(sim);
        EntityId second = Planter(sim);
        StepUntil(sim, () => JobOf(sim, first).Kind == JobKind.Plant && JobOf(sim, second).Kind == JobKind.Plant);
        Assert.NotEqual(JobOf(sim, first).TargetTile, JobOf(sim, second).TargetTile);

        // Something occupies the first planter's tile before it gets there: the job ends, nothing is planted.
        (int x, int y) = TileOf(sim, JobOf(sim, first).TargetTile);
        EntityId marker = sim.Entities.Create();
        Assert.True(world.Tiles.TryOccupy(x, y, marker));
        sim.Step();
        Assert.False(JobOf(sim, first).Kind == JobKind.Plant && JobOf(sim, first).TargetTile == TileIndex(sim, x, y));
        Assert.DoesNotContain(world.Reservations.Reservations, r => r.Target == ReservationTarget.OfTile(TileIndex(sim, x, y)));
        Assert.Equal(FloraState.None, world.Tiles.GetFlora(x, y));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    [Trait("Req", "FR-NPC-06")]
    public void ALumberjackChopsATreeThatGrewFromASapling()
    {
        (Simulation sim, _) = NewSettlement();
        GameWorld world = sim.World!;
        (int cx, int cy) = Residents.ResidentTestWorld.CenterTile(sim);

        // A sapling on the nearest free tile around the center grows into a tree (the growth pass with one-tick days).
        (int x, int y) = Enumerable.Range(1, 6)
            .SelectMany(r => Around(cx, cy, r).Where(t => Math.Max(Math.Abs(t.X - cx), Math.Abs(t.Y - cy)) == r))
            .First(t => world.Tiles.CanPlant(t.X, t.Y) && world.Pathfinder.Grid.IsPassable(t.X, t.Y));
        Assert.True(world.Tiles.Plant(x, y));
        sim.Step(sim.Content.Regrowth.SaplingGrowDays + 1);
        world.Tiles.GrowFlora(sim.Content.Regrowth, sim.Content.WorldGen.Resources.Trees, ticksPerDay: 1);
        Assert.Equal(FloraState.Tree, world.Tiles.GetFlora(x, y));

        EntityId lumberjack = Spawn(sim, "lumberjack");
        Assert.Equal((JobKind.Chop, TileIndex(sim, x, y)), (JobOf(sim, lumberjack).Kind, JobOf(sim, lumberjack).TargetTile));
        StepUntil(sim, () => world.Tiles.GetFlora(x, y) == FloraState.Stump);
        Assert.Equal(ResourceKind.None, world.Tiles.GetEffectiveResource(x, y));
    }

    [Fact]
    [Trait("Req", "FR-WORLD-04")]
    [Trait("Req", "FR-SAVE-04")]
    [Trait("Req", "NFR-04")]
    public void ASaveInTheMiddleOfPlantingContinuesLikeTheUninterruptedGame()
    {
        (Simulation sim, _) = NewSettlement();
        Fell(sim);
        EntityId lumberjack = Planter(sim);
        Spawn(sim, "carrier");
        StepUntil(sim, () => JobOf(sim, lumberjack).Phase == JobPhase.Working && JobOf(sim, lumberjack).Kind == JobKind.Plant);

        byte[] bytes = SaveGame.ToBytes(sim);
        SaveLoadResult loaded = SaveGame.Read(bytes, Quiet);
        Assert.True(loaded.Succeeded, loaded.Error?.Message);
        Simulation copy = loaded.Simulation!;
        Assert.Equal(bytes, SaveGame.ToBytes(copy));
        Assert.Equal(JobOf(sim, lumberjack), JobOf(copy, lumberjack));
        for (int tick = 0; tick < 600; tick++)
        {
            sim.Step();
            copy.Step();
            Assert.Null(copy.ComputeHashes().FindFirstMismatch(sim.ComputeHashes()));
        }

        Assert.True(sim.World!.Tiles.FloraTiles >= 2);
        Assert.True(sim.World.Tiles.WalkedTiles > 0);
    }

    private static IEnumerable<(int X, int Y)> Around(int x, int y, int radius)
    {
        for (int dy = -radius; dy <= radius; dy++)
        {
            for (int dx = -radius; dx <= radius; dx++)
            {
                if (dx != 0 || dy != 0)
                {
                    yield return (x + dx, y + dy);
                }
            }
        }
    }
}
