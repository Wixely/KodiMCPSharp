using KodiMCPSharp.Services;
using ModelContextProtocol;

namespace KodiMCPSharp.Tests;

public sealed class HandleStoreTests
{
    [Fact]
    public void Handle_IsOpaqueAndResolvesOnlyWithinInstance()
    {
        var store = new InMemoryHandleStore(15, 10, TimeProvider.System);
        const string target = "plugin://synthetic.addon/private?token=value";

        var handle = store.Create("room-a", target, "files", "folder", HandleAction.Browse);

        Assert.StartsWith("h_", handle, StringComparison.Ordinal);
        Assert.DoesNotContain("plugin", handle, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(target, store.Resolve(handle, "room-a", HandleAction.Browse).Target);
        Assert.Throws<McpException>(() => store.Resolve(handle, "room-b", HandleAction.Browse));
    }

    [Fact]
    public void Handle_EnforcesAllowedAction()
    {
        var store = new InMemoryHandleStore(15, 10, TimeProvider.System);
        var handle = store.Create("room", "synthetic-target", "video", "item", HandleAction.Play);

        Assert.Throws<McpException>(() => store.Resolve(handle, "room", HandleAction.Browse));
    }

    [Fact]
    public void Handle_Expires()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryHandleStore(1, 10, time);
        var handle = store.Create("room", "synthetic-target", "video", "folder", HandleAction.Browse);

        time.Advance(TimeSpan.FromMinutes(2));

        Assert.Throws<McpException>(() => store.Resolve(handle, "room", HandleAction.Browse));
    }

    [Fact]
    public void HandleStore_EvictsOldestAtCapacity()
    {
        var time = new ManualTimeProvider(new DateTimeOffset(2026, 8, 30, 0, 0, 0, TimeSpan.Zero));
        var store = new InMemoryHandleStore(15, 2, time);
        var oldest = store.Create("room", "first", "video", "folder", HandleAction.Browse);
        time.Advance(TimeSpan.FromSeconds(1));
        _ = store.Create("room", "second", "video", "folder", HandleAction.Browse);
        _ = store.Create("room", "third", "video", "folder", HandleAction.Browse);

        Assert.Equal(2, store.Count);
        Assert.Throws<McpException>(() => store.Resolve(oldest, "room", HandleAction.Browse));
    }

    private sealed class ManualTimeProvider(DateTimeOffset now) : TimeProvider
    {
        private DateTimeOffset _now = now;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan duration) => _now += duration;
    }
}
