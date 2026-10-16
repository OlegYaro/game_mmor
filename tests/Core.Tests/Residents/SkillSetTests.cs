using Core.Content;
using Core.Residents;

namespace Core.Tests.Residents;

/// <summary>The packed twelve skills of a resident (GDD E6, FR-NPC-02).</summary>
public sealed class SkillSetTests
{
    [Fact]
    [Trait("Req", "FR-NPC-02")]
    public void LevelsArePackedFiveBitsEachInSkillOrder()
    {
        int[] levels = [0, 1, 2, 3, 4, 5, 20, 19, 18, 17, 16, 15];

        SkillSet set = SkillSet.From(levels);

        Assert.Equal(levels, set.ToArray());
        Assert.Equal(20, set.Get(6));
        Assert.Equal(1UL << 5, SkillSet.From([0, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]).Bits);
        Assert.True(set.IsValid());
        Assert.Equal(SkillSet.Count, SkillNames.All.Count);
        Assert.All(SkillNames.All, name => Assert.Matches(SkillNames.Pattern, name));
        Assert.Equal(SkillNames.All.Count, SkillNames.All.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    [Trait("Req", "FR-NPC-02")]
    public void OutOfRangeLevelsAndBitsAreRejected()
    {
        Assert.Throws<ArgumentException>(() => SkillSet.From([1, 2, 3]));
        Assert.Throws<ArgumentException>(() => SkillSet.From([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 21]));
        Assert.Throws<ArgumentException>(() => SkillSet.From([0, 0, 0, 0, 0, 0, 0, 0, 0, 0, -1, 0]));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SkillSet(0).Get(12));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SkillSet(0).Get(-1));
        Assert.False(new SkillSet(21).IsValid());          // skill 0 at 21
        Assert.False(new SkillSet(1UL << 60).IsValid());   // an unused high bit
        Assert.True(new SkillSet(0).IsValid());
    }
}
