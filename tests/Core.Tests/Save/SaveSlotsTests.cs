using Core.Content;
using Core.Save;
using Core.Sim;
using Core.Tests.Sim;

namespace Core.Tests.Save;

/// <summary>Save slots, autosave rotation and the daily autosave trigger (FR-SAVE-01).</summary>
[Trait("Req", "FR-SAVE-01")]
public sealed class SaveSlotsTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "slots_" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }

    private static ContentCatalog Content => SimTestData.Real;

    private SaveSlots Slots() => new(_dir, Content.Saves, Content.Time);

    private static Simulation Game(ulong seed = 7, Difficulty difficulty = Difficulty.Hard) =>
        Simulation.CreateNewGame(Content, new NewGameOptions(seed, 64, difficulty));

    [Fact]
    public void BalanceComesFromData()
    {
        Assert.Equal(5, Content.Saves.ManualSlots);
        Assert.Equal(3, Content.Saves.AutosavesKept);
    }

    [Fact]
    public void SlotIdsParseAndFormat()
    {
        Assert.True(SaveSlotId.TryParse("slot_3", out SaveSlotId manual));
        Assert.Equal(new SaveSlotId(SaveSlotKind.Manual, 3), manual);
        Assert.Equal("slot_3.sav", manual.FileName);
        Assert.True(SaveSlotId.TryParse("autosave_1", out SaveSlotId auto));
        Assert.Equal(SaveSlotKind.Auto, auto.Kind);
        foreach (string bad in new[] { "", "slot_", "slot_0", "slot_x", "slot_-1", "slot_1.sav", "save_1", "autosave", "slot_99999" })
        {
            Assert.False(SaveSlotId.TryParse(bad, out _), bad);
        }

        Assert.False(SaveSlotId.TryParse(null, out _));
    }

    [Fact]
    public void ManualSaveListsHeaderInfo()
    {
        SaveSlots slots = Slots();
        Simulation game = Game();
        game.Step(9000 * 2 + 5);
        Assert.True(slots.SaveManual(game, new SaveSlotId(SaveSlotKind.Manual, 2)).Succeeded);

        SaveSlotEntry entry = Assert.Single(slots.List());
        Assert.Equal(new SaveSlotId(SaveSlotKind.Manual, 2), entry.Id);
        Assert.True(entry.IsReadable);
        Assert.Equal(7UL, entry.Info!.Seed);
        Assert.Equal(game.Tick, entry.Info.Tick);
        Assert.Equal(2, entry.Info.Day);
        Assert.Equal(64, entry.Info.MapSize);
        Assert.Equal(Difficulty.Hard, entry.Info.Difficulty);
    }

    [Fact]
    public void SlotOutsideTheConfiguredRangeIsRejected()
    {
        SaveSlots slots = Slots();
        SaveSlotResult tooHigh = slots.SaveManual(Game(), new SaveSlotId(SaveSlotKind.Manual, Content.Saves.ManualSlots + 1));
        Assert.False(tooHigh.Succeeded);
        Assert.False(slots.SaveManual(Game(), new SaveSlotId(SaveSlotKind.Auto, 1)).Succeeded);
        Assert.False(slots.Delete(new SaveSlotId(SaveSlotKind.Manual, 0)).Succeeded);
        Assert.Empty(slots.List());
    }

    [Fact]
    public void AutosaveKeepsOnlyTheLastThreeNewestFirst()
    {
        SaveSlots slots = Slots();
        Simulation game = Game();
        for (int day = 1; day <= 5; day++)
        {
            game.Step(9000);
            Assert.True(slots.Autosave(game).Succeeded);
        }

        IReadOnlyList<SaveSlotEntry> list = slots.List();
        Assert.Equal(3, list.Count);
        Assert.Equal([5L, 4L, 3L], list.Select(e => e.Info!.Day));
        Assert.Equal(["autosave_1", "autosave_2", "autosave_3"], list.Select(e => e.Id.Name));
        Assert.Equal(3, Directory.GetFiles(_dir).Length); // no .tmp left over
    }

    [Fact]
    public void AutosaveDoesNotTouchManualSlots()
    {
        SaveSlots slots = Slots();
        Simulation game = Game();
        slots.SaveManual(game, new SaveSlotId(SaveSlotKind.Manual, 1));
        for (int i = 0; i < 4; i++)
        {
            game.Step(10);
            slots.Autosave(game);
        }

        Assert.Equal(4, slots.List().Count);
        Assert.Single(slots.List(), e => e.Id.Kind == SaveSlotKind.Manual);
    }

    [Fact]
    public void LoadRestoresTheSameState()
    {
        SaveSlots slots = Slots();
        Simulation game = Game();
        game.Step(120);
        slots.Autosave(game);

        SaveLoadResult loaded = slots.Load(new SaveSlotId(SaveSlotKind.Auto, 1), Content);
        Assert.True(loaded.Succeeded);
        Assert.Equal(game.ComputeHashes().Combined, loaded.Simulation!.ComputeHashes().Combined);
    }

    [Fact]
    public void DeleteRemovesTheFileAndMissingIsFine()
    {
        SaveSlots slots = Slots();
        var id = new SaveSlotId(SaveSlotKind.Manual, 1);
        slots.SaveManual(Game(), id);
        Assert.True(slots.Delete(id).Succeeded);
        Assert.Empty(slots.List());
        Assert.True(slots.Delete(id).Succeeded);
    }

    [Fact]
    public void CorruptAndTruncatedFilesAreListedAsUnreadable()
    {
        SaveSlots slots = Slots();
        slots.SaveManual(Game(), new SaveSlotId(SaveSlotKind.Manual, 1));
        byte[] good = File.ReadAllBytes(slots.PathOf(new SaveSlotId(SaveSlotKind.Manual, 1)));
        File.WriteAllBytes(slots.PathOf(new SaveSlotId(SaveSlotKind.Manual, 2)), "garbage with no header"u8.ToArray());
        File.WriteAllBytes(slots.PathOf(new SaveSlotId(SaveSlotKind.Manual, 3)), good[..(good.Length / 2)]);
        File.WriteAllBytes(slots.PathOf(new SaveSlotId(SaveSlotKind.Manual, 4)), []);
        File.WriteAllBytes(slots.PathOf(new SaveSlotId(SaveSlotKind.Auto, 1)), "{\"formatVersion\":99,\"codec\":\"json\",\"seed\":1}\n{}"u8.ToArray());
        File.WriteAllText(Path.Combine(_dir, "notes.txt"), "ignored");
        File.WriteAllBytes(slots.PathOf(new SaveSlotId(SaveSlotKind.Manual, 9)), good); // outside the configured range

        IReadOnlyList<SaveSlotEntry> list = slots.List();
        Assert.Equal(5, list.Count);
        Assert.Single(list, e => e.IsReadable);
        Assert.All(list.Where(e => !e.IsReadable), e => Assert.False(string.IsNullOrEmpty(e.Error)));
        Assert.Equal(new SaveSlotId(SaveSlotKind.Manual, 1), SaveSlots.FindNewest(list)!.Id);
    }

    [Fact]
    public void FindNewestUsesFileTimeThenTick()
    {
        var info = new SaveInfo(1, 100, 0, 64, Difficulty.Normal, 2);
        var older = new SaveSlotEntry(new SaveSlotId(SaveSlotKind.Manual, 1), "a", new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero), info, null);
        var newer = new SaveSlotEntry(new SaveSlotId(SaveSlotKind.Manual, 2), "b", new DateTimeOffset(2026, 1, 2, 0, 0, 0, TimeSpan.Zero), info with { Tick = 5 }, null);
        var broken = new SaveSlotEntry(new SaveSlotId(SaveSlotKind.Auto, 1), "c", new DateTimeOffset(2027, 1, 1, 0, 0, 0, TimeSpan.Zero), null, "bad");
        Assert.Equal(newer.Id, SaveSlots.FindNewest([older, newer, broken])!.Id);

        var sameTimeHigherTick = older with { Id = new SaveSlotId(SaveSlotKind.Manual, 3), Info = info with { Tick = 900 } };
        Assert.Equal(sameTimeHigherTick.Id, SaveSlots.FindNewest([older, sameTimeHigherTick])!.Id);
        Assert.Null(SaveSlots.FindNewest([broken]));
        Assert.Null(SaveSlots.FindNewest([]));
    }

    [Fact]
    public void V1SaveWithoutWorldReportsZeroMapSize()
    {
        byte[] v1 = File.ReadAllBytes(Path.Combine(TestPathsRoot(), "Golden", "save_v1.sav"));
        Assert.True(SaveSlots.TryReadInfo(v1, 9000, out SaveInfo? info, out _));
        Assert.Equal(0, info!.MapSize);
        Assert.Equal(1, info.FormatVersion);
        Assert.Equal(Difficulty.Normal, info.Difficulty);
    }

    [Fact]
    public void WriteFailureIsAResultNotAnException()
    {
        File.WriteAllText(_dir, "a file where the folder should be");
        SaveSlots slots = Slots();
        Assert.False(slots.Autosave(Game()).Succeeded);
        Assert.False(slots.SaveManual(Game(), new SaveSlotId(SaveSlotKind.Manual, 1)).Succeeded);
        File.Delete(_dir);
    }

    [Fact]
    public void AutosavePolicyFiresOncePerNewDay()
    {
        var policy = new AutosavePolicy(lastAutosaveDay: 0);
        Assert.False(policy.Poll(0));
        Assert.True(policy.Poll(1));
        Assert.False(policy.Poll(1));
        Assert.True(policy.Poll(3)); // a skipped day still gives one save
        Assert.False(policy.Poll(2));
        policy.Reset(10);
        Assert.False(policy.Poll(10));
        Assert.True(policy.Poll(11));
        Assert.Equal(11, policy.LastAutosaveDay);
    }

    private static string TestPathsRoot() => Path.Combine(Core.Tests.Content.TestPaths.Root, "tests", "Core.Tests");
}
