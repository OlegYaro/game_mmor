using Core.Content;
using Core.Jobs;
using Core.Tests.Sim;
using Core.WorldGen;

namespace Core.Tests.Jobs;

/// <summary>Job types and work priorities (FR-NPC-06): names match the data, priorities pack into one word, professions give the defaults.</summary>
public sealed class JobKindTests
{
    [Fact]
    [Trait("Req", "FR-NPC-06")]
    public void JobTypeNamesAreTheDataNamesAndParseBack()
    {
        Assert.Equal(JobKinds.Count + 1, JobKinds.Names.Count);
        for (int k = 1; k <= JobKinds.Count; k++)
        {
            Assert.True(JobTypes.IsValid(JobKinds.Names[k]));
            Assert.True(JobKinds.TryParse(JobKinds.Names[k], out JobKind kind));
            Assert.Equal((JobKind)k, kind);
            Assert.Equal(JobKinds.Names[k], JobKinds.NameOf(kind));
        }

        Assert.False(JobKinds.TryParse("none", out _));
        Assert.False(JobKinds.TryParse(null, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => JobKinds.NameOf((JobKind)40));
        Assert.Equal(JobKinds.Count, JobKinds.TierOrder.Distinct().Count());
        Assert.Equal(Enum.GetValues<JobPhase>().Length, JobKinds.PhaseNames.Count);
    }

    [Fact]
    [Trait("Req", "FR-NPC-06")]
    public void GatheringJobsMatchTheirNodesAndTheSkillComesFromTheProfessionThatListsTheJob()
    {
        Assert.Equal(JobKind.Chop, JobKinds.GatherJobFor(ResourceKind.Tree));
        Assert.Equal(JobKind.Quarry, JobKinds.GatherJobFor(ResourceKind.Stone));
        Assert.Equal(JobKind.Forage, JobKinds.GatherJobFor(ResourceKind.BerryBush));
        Assert.Equal(JobKind.None, JobKinds.GatherJobFor(ResourceKind.None));
        foreach (JobKind kind in new[] { JobKind.Chop, JobKind.Quarry, JobKind.Forage })
        {
            Assert.Equal(kind, JobKinds.GatherJobFor(JobKinds.ResourceOf(kind)));
            Assert.True(JobKinds.IsGathering(kind));
        }

        Assert.False(JobKinds.IsGathering(JobKind.Farm));
        Assert.Equal(ResourceKind.None, JobKinds.ResourceOf(JobKind.Haul));

        ContentCatalog content = SimTestData.Real;
        List<string> skills = [.. SkillNames.All];
        Assert.Equal(skills.IndexOf("construction"), JobKinds.SkillOf(content, JobKind.Haul));
        Assert.Equal(skills.IndexOf("mining"), JobKinds.SkillOf(content, JobKind.Quarry));
        Assert.Equal(skills.IndexOf("farming"), JobKinds.SkillOf(content, JobKind.Farm));
    }

    [Fact]
    [Trait("Req", "FR-NPC-06")]
    public void PrioritiesPackThreeBitsPerJobTypeAndProfessionsGiveOneToTheirJobs()
    {
        WorkPriorities priorities = default(WorkPriorities).With(JobKind.Farm, 4).With(JobKind.Haul, 2).With(JobKind.Haul, 3);

        Assert.Equal(3, priorities.Get(JobKind.Haul));
        Assert.Equal(4, priorities.Get(JobKind.Farm));
        Assert.Equal(0, priorities.Get(JobKind.Chop));
        Assert.True(priorities.IsValid());
        Assert.False(new WorkPriorities(5).IsValid()); // haul = 5
        Assert.False(new WorkPriorities(1 << 18).IsValid()); // a bit past the last job type
        Assert.False(new WorkPriorities(-1).IsValid());
        Assert.Throws<ArgumentOutOfRangeException>(() => priorities.With(JobKind.Chop, 5));
        Assert.Throws<ArgumentOutOfRangeException>(() => priorities.With(JobKind.None, 1));
        Assert.Throws<ArgumentOutOfRangeException>(() => priorities.Get(JobKind.None));

        Assert.True(SimTestData.Real.TryGet("farmer", out ProfessionDef farmer));
        WorkPriorities defaults = WorkPriorities.ForProfession(farmer);
        Assert.Equal(1, defaults.Get(JobKind.Farm));
        Assert.Equal(1, defaults.Get(JobKind.Forage));
        Assert.Equal(0, defaults.Get(JobKind.Haul));
    }

    [Fact]
    [Trait("Req", "FR-ECO-01")]
    public void ItemCategoriesMapToFilterBitsAndUncategorizedItemsPassEveryFilter()
    {
        Assert.Equal(0b1111, ItemCategories.AllMask);
        Assert.Equal(1, ItemCategories.BitOf("resource"));
        Assert.Equal(8, ItemCategories.BitOf(ItemCategories.Currency));
        Assert.Equal(0, ItemCategories.BitOf(null));
        Assert.Equal(0, ItemCategories.BitOf("gems"));

        var filter = new Core.Economy.StorageFilter(ItemCategories.BitOf(ItemCategories.Food));
        Assert.True(filter.Allows(new ItemDef("bread", 10, 1, Category: "food", FoodValue: 1)));
        Assert.False(filter.Allows(new ItemDef("plank", 10, 1, Category: "material")));
        Assert.True(filter.Allows(new ItemDef("trinket", 10, 1)));
    }
}
