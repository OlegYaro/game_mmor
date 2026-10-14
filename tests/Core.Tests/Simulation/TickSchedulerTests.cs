using Core.Sim;

namespace Core.Tests.Sim;

public sealed class TickSchedulerTests
{
    private const int Tps = 10;

    [Fact]
    [Trait("Req", "FR-TIME-02")]
    [Trait("Req", "NFR-02")]
    public void OneTickPerTenthOfASecondAtNormalSpeed()
    {
        var scheduler = new TickScheduler(Tps);

        Assert.Equal(0, scheduler.AdvanceMilliseconds(50));
        Assert.Equal(500, scheduler.AlphaPermille);
        Assert.Equal(1, scheduler.AdvanceMilliseconds(50));
        Assert.Equal(0, scheduler.AlphaPermille);
        Assert.Equal(1, scheduler.AdvanceMilliseconds(125));
        Assert.Equal(250, scheduler.AlphaPermille);
    }

    [Fact]
    [Trait("Req", "FR-TIME-02")]
    public void AccumulatorRemainderCarriesOverBetweenFrames()
    {
        var scheduler = new TickScheduler(Tps);
        int ticks = 0;
        for (int frame = 0; frame < 30; frame++)
        {
            ticks += scheduler.AdvanceMilliseconds(33);
        }

        Assert.Equal(9, ticks); // 990 ms
        Assert.Equal(900, scheduler.AlphaPermille);
        Assert.Equal(1, scheduler.AdvanceMilliseconds(10));
    }

    [Theory]
    [InlineData(16_667, 6_000, 1000)] // 60 fps for 100.002 s
    [InlineData(6_944, 14_400, 999)] // 144 fps for 99.99 s
    [InlineData(33_333, 3_000, 999)] // 30 fps for 99.999 s
    [Trait("Req", "FR-TIME-02")]
    [Trait("Req", "NFR-02")]
    public void NoDriftOverLongRuns(long frameMicroseconds, int frames, int expectedTicks)
    {
        var scheduler = new TickScheduler(Tps);
        long ticks = 0;
        for (int frame = 0; frame < frames; frame++)
        {
            ticks += scheduler.Advance(frameMicroseconds);
        }

        Assert.Equal(expectedTicks, ticks);
        Assert.Equal(0, scheduler.DroppedTicks);
    }

    [Theory]
    [InlineData(GameSpeed.X1, 1)]
    [InlineData(GameSpeed.X2, 2)]
    [InlineData(GameSpeed.X4, 4)]
    [Trait("Req", "FR-TIME-02")]
    public void SpeedMultipliesTicks(GameSpeed speed, int ticksPer100Ms)
    {
        var scheduler = new TickScheduler(Tps);

        Assert.True(scheduler.TrySetSpeed(speed));
        Assert.Equal(speed, scheduler.Speed);
        Assert.Equal(ticksPer100Ms, scheduler.AdvanceMilliseconds(100));
    }

    [Fact]
    [Trait("Req", "FR-TIME-02")]
    [Trait("Req", "NFR-02")]
    public void AtMostFourTicksPerFrameAndExcessIsDroppedKeepingTheFraction()
    {
        var scheduler = new TickScheduler(Tps);
        Assert.Equal(TickScheduler.DefaultMaxTicksPerFrame, scheduler.MaxTicksPerFrame);

        Assert.Equal(4, scheduler.AdvanceMilliseconds(1050)); // 10.5 ticks due
        Assert.Equal(6, scheduler.DroppedTicks);
        Assert.Equal(500, scheduler.AlphaPermille);

        // The backlog is gone: the next normal frame is not inflated.
        Assert.Equal(1, scheduler.AdvanceMilliseconds(50));
        Assert.Equal(0, scheduler.AlphaPermille);
    }

    [Fact]
    [Trait("Req", "NFR-02")]
    public void FourTicksPerFrameAtFourTimesSpeedAndTenFps()
    {
        var scheduler = new TickScheduler(Tps);
        scheduler.TrySetSpeed(GameSpeed.X4);

        for (int frame = 0; frame < 10; frame++)
        {
            Assert.Equal(4, scheduler.AdvanceMilliseconds(100));
        }

        Assert.Equal(0, scheduler.DroppedTicks);
    }

    [Fact]
    [Trait("Req", "FR-TIME-02")]
    public void PauseFreezesTheAccumulator()
    {
        var scheduler = new TickScheduler(Tps);
        scheduler.AdvanceMilliseconds(70);

        scheduler.TogglePause();
        Assert.True(scheduler.IsPaused);
        Assert.Equal(0, scheduler.AdvanceMilliseconds(5000));
        Assert.Equal(700, scheduler.AlphaPermille);
        Assert.Equal(0, scheduler.DroppedTicks);

        scheduler.TogglePause();
        Assert.False(scheduler.IsPaused);
        Assert.Equal(1, scheduler.AdvanceMilliseconds(30));
    }

    [Fact]
    [Trait("Req", "FR-TIME-02")]
    public void SpeedSurvivesPause()
    {
        var scheduler = new TickScheduler(Tps);
        scheduler.TrySetSpeed(GameSpeed.X2);
        scheduler.IsPaused = true;
        scheduler.IsPaused = false;

        Assert.Equal(GameSpeed.X2, scheduler.Speed);
        Assert.Equal(2, scheduler.AdvanceMilliseconds(100));
    }

    [Fact]
    [Trait("Req", "FR-TIME-02")]
    public void TwentyTimesSpeedNeedsTheDevFlag()
    {
        var scheduler = new TickScheduler(Tps);

        Assert.False(scheduler.IsSpeedAllowed(GameSpeed.X20));
        Assert.False(scheduler.TrySetSpeed(GameSpeed.X20));
        Assert.Equal(GameSpeed.X1, scheduler.Speed);
        Assert.False(scheduler.TrySetSpeed((GameSpeed)3));

        scheduler.DevSpeedsEnabled = true;
        Assert.True(scheduler.TrySetSpeed(GameSpeed.X20));
        Assert.Equal(2, scheduler.AdvanceMilliseconds(10)); // 200 ticks/s

        scheduler.DevSpeedsEnabled = false;
        Assert.Equal(GameSpeed.X4, scheduler.Speed);

        var dev = new TickScheduler(Tps, devSpeedsEnabled: true);
        Assert.True(dev.TrySetSpeed(GameSpeed.X20));
    }

    [Fact]
    [Trait("Req", "FR-TIME-02")]
    public void SpeedChangeKeepsThePartialTick()
    {
        var scheduler = new TickScheduler(Tps);
        scheduler.AdvanceMilliseconds(50); // half a tick
        scheduler.TrySetSpeed(GameSpeed.X2);

        Assert.Equal(1, scheduler.AdvanceMilliseconds(25)); // + half a tick at x2
        Assert.Equal(0, scheduler.AlphaPermille);
    }

    [Fact]
    [Trait("Req", "NFR-02")]
    public void HugeOrInvalidElapsedTimeIsHandled()
    {
        var scheduler = new TickScheduler(Tps, devSpeedsEnabled: true);
        scheduler.TrySetSpeed(GameSpeed.X20);

        Assert.Equal(4, scheduler.Advance(long.MaxValue));
        Assert.Equal(4, scheduler.AdvanceMilliseconds(long.MaxValue));
        Assert.InRange(scheduler.AlphaPermille, 0, 999);
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.Advance(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => scheduler.AdvanceMilliseconds(-1));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TickScheduler(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new TickScheduler(Tps, maxTicksPerFrame: 0));

        scheduler.ResetAccumulator();
        Assert.Equal(0, scheduler.AlphaPermille);
    }
}
