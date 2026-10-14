using Core.Logging;

namespace Core.Tests.Logging;

public sealed class SimLogTests
{
    [Fact]
    [Trait("Req", "NFR-08")]
    public void RingBufferKeepsTheNewestEventsOldestFirst()
    {
        var log = new SimLog(capacity: 3);
        for (int tick = 1; tick <= 5; tick++)
        {
            log.Write(tick, LogLevel.Info, "test", "event {n}", LogField.Of("n", tick));
        }

        Assert.Equal([3L, 4L, 5L], log.GetEvents().Select(e => e.Tick));
        Assert.Equal(3, log.Count);
        Assert.Equal(3, log.Capacity);
        Assert.Equal(5, log.TotalWritten);
        Assert.Equal(2, log.Overwritten);
    }

    [Fact]
    [Trait("Req", "NFR-08")]
    public void EventsBelowTheMinimumLevelAreDropped()
    {
        var log = new SimLog(minimumLevel: LogLevel.Info);
        int delivered = 0;
        using IDisposable subscription = log.Subscribe(_ => delivered++);

        Assert.Null(log.Write(1, LogLevel.Debug, "test", "hidden"));
        Assert.NotNull(log.Write(1, LogLevel.Warning, "test", "shown"));
        Assert.False(log.IsEnabled(LogLevel.Trace));
        Assert.True(log.IsEnabled(LogLevel.Error));

        log.MinimumLevel = LogLevel.Trace;
        Assert.NotNull(log.Write(2, LogLevel.Trace, "test", "now shown"));

        Assert.Equal(["shown", "now shown"], log.GetEvents().Select(e => e.Template));
        Assert.Equal(2, delivered);
    }

    [Fact]
    [Trait("Req", "NFR-08")]
    public void SubscribersReceiveEventsInOrderUntilDisposed()
    {
        var log = new SimLog();
        List<string> received = [];
        IDisposable first = log.Subscribe(e => received.Add("a:" + e.Template));
        using IDisposable second = log.Subscribe(e => received.Add("b:" + e.Template));

        log.Write(1, LogLevel.Info, "test", "one");
        first.Dispose();
        first.Dispose();
        log.Write(2, LogLevel.Info, "test", "two");

        Assert.Equal(["a:one", "b:one", "b:two"], received);
    }

    [Fact]
    [Trait("Req", "NFR-08")]
    public void HandlerMayUnsubscribeWhileEventsAreDelivered()
    {
        var log = new SimLog();
        int calls = 0;
        IDisposable? subscription = null;
        subscription = log.Subscribe(_ =>
        {
            calls++;
            subscription!.Dispose();
        });

        log.Write(1, LogLevel.Info, "test", "one");
        log.Write(2, LogLevel.Info, "test", "two");

        Assert.Equal(1, calls);
    }

    [Fact]
    [Trait("Req", "NFR-08")]
    public void TemplateRendersFieldsInInvariantFormat()
    {
        var log = new SimLog();
        LogEvent e = log.Write(42, LogLevel.Warning, "combat", "{attacker} hit for {damage} ({missing}) {",
            LogField.Of("attacker", "wolf"), LogField.Of("damage", -1234567))!;

        Assert.Equal("wolf hit for -1234567 ({missing}) {", e.Render());
        Assert.Equal("[42] Warning combat: wolf hit for -1234567 ({missing}) {", e.ToString());
        Assert.Equal(-1234567, e.Find("damage")!.Value.Number);
        Assert.True(e.Find("damage")!.Value.IsNumber);
        Assert.False(e.Find("attacker")!.Value.IsNumber);
        Assert.Null(e.Find("nope"));
        Assert.Equal(["attacker", "damage"], e.Fields.Select(f => f.Key));
    }

    [Fact]
    [Trait("Req", "NFR-08")]
    public void ClearDropsEventsButKeepsCounters()
    {
        var log = new SimLog(capacity: 2);
        log.Write(1, LogLevel.Info, "test", "a");
        log.Write(2, LogLevel.Info, "test", "b");
        log.Write(3, LogLevel.Info, "test", "c");

        log.Clear();
        log.Write(4, LogLevel.Info, "test", "d");

        Assert.Equal(["d"], log.GetEvents().Select(e => e.Template));
        Assert.Equal(4, log.TotalWritten);
    }

    [Fact]
    [Trait("Req", "NFR-08")]
    public void InvalidArgumentsAreRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimLog(capacity: 0));
        Assert.Throws<ArgumentException>(() => new SimLog().Write(1, LogLevel.Info, "", "x"));
        Assert.Throws<ArgumentException>(() => LogField.Of("", 1));
    }
}
