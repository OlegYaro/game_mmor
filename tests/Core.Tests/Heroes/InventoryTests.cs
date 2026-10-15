using Core.Content;
using Core.Determinism;
using Core.Heroes;
using Core.Tests.Sim;

namespace Core.Tests.Heroes;

/// <summary>The hero inventory (FR-HERO-04): stacks by item stack size, overflow, remainders, removal, equality.</summary>
public sealed class InventoryTests
{
    private static readonly DefId _wood = new(1);
    private static readonly DefId _stone = new(2);

    [Fact]
    [Trait("Req", "FR-HERO-04")]
    public void CreateFollowsTheBalance()
    {
        HeroBalanceDef balance = SimTestData.Real.Hero;

        Inventory inventory = Inventory.Create(balance);

        Assert.Equal(30, balance.BackpackStacks);
        Assert.Equal(balance.BackpackStacks, inventory.Backpack.Count);
        Assert.Equal(9, inventory.Equipment.Count);
        Assert.All(inventory.Backpack, s => Assert.True(s.IsEmpty));
        Assert.Equal(0, inventory.UsedStacks);
    }

    [Fact]
    [Trait("Req", "FR-HERO-04")]
    public void AddTopsUpStacksFirstThenFillsEmptySlotsInOrder()
    {
        Inventory inventory = Inventory.Create(4, 0)
            .Add(_wood, 70, 100, out int first)
            .Add(_stone, 10, 50, out int second)
            .Add(_wood, 50, 100, out int third);

        Assert.Equal((70, 10, 50), (first, second, third));
        Assert.Equal([new ItemSlot(_wood, 100), new ItemSlot(_stone, 10), new ItemSlot(_wood, 20), default], inventory.Backpack);
        Assert.Equal(120, inventory.Count(_wood));
        Assert.Equal(10, inventory.Count(_stone));
        Assert.Equal(3, inventory.UsedStacks);
    }

    [Fact]
    [Trait("Req", "FR-HERO-04")]
    public void OverflowAddsWhatFitsAndReportsTheRemainder()
    {
        Inventory inventory = Inventory.Create(2, 0).Add(_stone, 30, 50, out _);

        Assert.Equal(70, inventory.SpaceFor(_stone, 50));
        Assert.Equal(50, inventory.SpaceFor(_wood, 50));
        Assert.True(inventory.CanAdd(_stone, 70, 50));
        Assert.False(inventory.CanAdd(_stone, 71, 50));

        Inventory full = inventory.Add(_stone, 100, 50, out int added);

        Assert.Equal(70, added);
        Assert.Equal([new ItemSlot(_stone, 50), new ItemSlot(_stone, 50)], full.Backpack);
        Assert.Equal(0, full.SpaceFor(_stone, 50));
        Assert.Equal(0, full.SpaceFor(_wood, 100));
        Assert.Equal(full, full.Add(_wood, 5, 100, out int none));
        Assert.Equal(0, none);
    }

    [Fact]
    [Trait("Req", "FR-HERO-04")]
    public void RemoveTakesFromTheLastStackBackwards()
    {
        Inventory inventory = Inventory.Create(3, 0).Add(_wood, 250, 100, out _);

        Inventory less = inventory.Remove(_wood, 60, out int removed);
        Assert.Equal(60, removed);
        Assert.Equal([new ItemSlot(_wood, 100), new ItemSlot(_wood, 90), default], less.Backpack);

        Inventory empty = less.Remove(_wood, 1000, out int rest);
        Assert.Equal(190, rest);
        Assert.Equal(0, empty.UsedStacks);
        Assert.Equal(empty, empty.Remove(_stone, 5, out int nothing));
        Assert.Equal(0, nothing);
    }

    [Fact]
    [Trait("Req", "FR-HERO-04")]
    public void EqualityAndHashAreByContentAndInvalidInputThrows()
    {
        Inventory a = Inventory.Create(3, 2).Add(_wood, 5, 100, out _);
        Inventory b = new([new ItemSlot(_wood, 5), default, default], [default, default]);

        Assert.Equal(a, b);
        Assert.True(a == b);
        Assert.False(a != b);
        Assert.True(a.Equals((object)b));
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, Inventory.Create(3, 2));
        Assert.Equal(new StateHasher().Hash(a), new StateHasher().Hash(b));
        Assert.Equal(0, default(Inventory).Count(_wood));
        Assert.Equal(0, default(Inventory).SpaceFor(_wood, 10));

        Assert.Throws<ArgumentException>(() => a.Add(default, 1, 10, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.Add(_wood, -1, 10, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.Add(_wood, 1, 0, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.Remove(_wood, -1, out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => a.CanAdd(_wood, -1, 10));
        Assert.Throws<ArgumentOutOfRangeException>(() => Inventory.Create(-1, 0));
    }
}
