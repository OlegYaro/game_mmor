using System.Text.Json;
using Core.Tests.Content;
using Core.WorldGen;

namespace Core.Tests.WorldGen;

/// <summary>Vocabulary of placed things: data names, art ids and the numeric values stored in hashes.</summary>
public sealed class WorldNamesTests
{
    [Fact]
    public void NumericValuesAreFixed()
    {
        // Part of the map hash and of future tile diffs: never renumber.
        Assert.Equal([0, 1, 2, 3, 4], Enum.GetValues<ResourceKind>().Select(k => (int)k));
        Assert.Equal([0, 1, 2, 3], Enum.GetValues<PoiKind>().Select(k => (int)k));
        Assert.Equal([0, 1, 2], Enum.GetValues<Faction>().Select(k => (int)k));
        Assert.Equal([0, 1, 2, 3], Enum.GetValues<StartRelaxation>().Select(k => (int)k));
    }

    [Fact]
    public void NamesRoundTrip()
    {
        Assert.Equal(["tree", "stone", "ore", "berry_bush"], WorldNames.Resources.Select(WorldNames.ToName));
        Assert.Equal(["chest", "ruins", "lair"], WorldNames.PoiKinds.Select(WorldNames.ToName));
        Assert.Equal(["human", "orc"], WorldNames.Factions.Select(WorldNames.ToName));
        Assert.Equal("none", WorldNames.ToName(ResourceKind.None));

        foreach (PoiKind kind in WorldNames.PoiKinds)
        {
            Assert.True(WorldNames.TryParse(WorldNames.ToName(kind), out PoiKind parsed));
            Assert.Equal(kind, parsed);
        }

        foreach (Faction faction in WorldNames.Factions)
        {
            Assert.True(WorldNames.TryParse(WorldNames.ToName(faction), out Faction parsed));
            Assert.Equal(faction, parsed);
        }

        Assert.False(WorldNames.TryParse("none", out PoiKind noKind));
        Assert.Equal(PoiKind.None, noKind);
        Assert.False(WorldNames.TryParse("portal", out PoiKind _));
        Assert.False(WorldNames.TryParse((string?)null, out Faction noFaction));
        Assert.Equal(Faction.None, noFaction);
        Assert.False(WorldNames.TryParse("elf", out Faction _));
    }

    [Fact]
    public void UnknownValuesThrow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldNames.ToName((ResourceKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldNames.ToName((PoiKind)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldNames.ToName((Faction)99));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldNames.ArtId(ResourceKind.None));
        Assert.Throws<ArgumentOutOfRangeException>(() => WorldNames.ArtId(PoiKind.None));
    }

    [Fact]
    public void OnlyOrcsAreEvil()
    {
        Assert.True(WorldNames.IsEvil(Faction.Orc));
        Assert.False(WorldNames.IsEvil(Faction.Human));
    }

    [Fact]
    public void EveryArtIdExistsInTheAtlas()
    {
        using JsonDocument atlas = JsonDocument.Parse(File.ReadAllText(Path.Combine(TestPaths.Data, "art", "atlas.json")));
        HashSet<string> objects = atlas.RootElement.GetProperty("cells").EnumerateArray()
            .Where(c => c.GetProperty("kind").GetString() == "object")
            .Select(c => c.GetProperty("id").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        List<string> ids =
        [
            .. WorldNames.Resources.Select(WorldNames.ArtId),
            .. WorldNames.PoiKinds.Select(WorldNames.ArtId),
            WorldNames.SettlementSiteArtId,
            WorldNames.StartMarkerArtId,
        ];

        Assert.Equal(["tree", "rock", "ore", "berry_bush", "poi_chest", "poi_ruins", "poi_lair", "settlement_site", "start_marker"], ids);
        Assert.All(ids, id => Assert.Contains(id, objects));
    }

    [Fact]
    public void WorldMapAccessorsCheckBounds()
    {
        WorldMap world = WorldGenTestData.InvariantWorld(2);
        int i = world.ResourceKinds.IndexOf(ResourceKind.Stone);
        (int x, int y) = (i % world.Size, i / world.Size);

        Assert.Equal(ResourceKind.Stone, world.GetResource(x, y));
        Assert.Equal(world.ResourceAmounts[i], world.GetResourceAmount(x, y));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.GetResource(-1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => world.GetResourceAmount(0, world.Size));
    }
}
