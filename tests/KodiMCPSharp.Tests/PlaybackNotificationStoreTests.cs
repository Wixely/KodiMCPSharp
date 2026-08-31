using System.Text.Json;
using KodiMCPSharp.Configuration;
using KodiMCPSharp.Security;
using KodiMCPSharp.Services;
using Microsoft.Extensions.Options;

namespace KodiMCPSharp.Tests;

public sealed class PlaybackNotificationStoreTests
{
    [Fact]
    public void Record_StoresOnlyBoundedSafeFieldsAndSupportsCursor()
    {
        var store = CreateStore(10);
        var first = Parse("""
            {"jsonrpc":"2.0","method":"Player.OnPlay","params":{"data":{"item":{"id":731,"type":"episode","title":"Synthetic episode","season":2,"episode":3,"file":"/synthetic-secret-path/video.mkv"},"player":{"playerid":1,"speed":1,"time":{"hours":0,"minutes":4,"seconds":5,"milliseconds":6}}},"sender":"xbmc"}}
            """);
        var ignored = Parse("""{"jsonrpc":"2.0","method":"VideoLibrary.OnUpdate","params":{"data":{"item":{"title":"Ignored"}}}}""");

        Assert.True(store.Record("room", first.RootElement, DateTimeOffset.UnixEpoch));
        Assert.False(store.Record("room", ignored.RootElement, DateTimeOffset.UnixEpoch));
        var result = store.Read("room", null, 10);

        var item = Assert.Single(result.Events);
        Assert.Equal("play", item.Event);
        Assert.Equal("Synthetic episode", item.Label);
        Assert.Equal(2, item.SeasonNumber);
        Assert.Equal(3, item.EpisodeNumber);
        Assert.Empty(store.Read("room", item.Sequence, 10).Events);
        Assert.DoesNotContain("synthetic-secret-path", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("731", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public void Record_EvictsOldestEventsAtConfiguredCapacity()
    {
        var store = CreateStore(10);
        for (var index = 0; index < 12; index++)
        {
            using var notification = Parse("{\"method\":\"Player.OnPause\",\"params\":{\"data\":{\"item\":{\"type\":\"movie\",\"title\":\"Synthetic " +
                index + "\"},\"player\":{\"playerid\":1}}}}");
            Assert.True(store.Record("room", notification.RootElement, DateTimeOffset.UnixEpoch.AddSeconds(index)));
        }

        var result = store.Read("room", null, 200);
        Assert.Equal(10, result.Returned);
        Assert.Equal("Synthetic 2", result.Events[0].Label);
        Assert.Equal("Synthetic 11", result.Events[^1].Label);
    }

    private static PlaybackNotificationStore CreateStore(int capacity) => new(
        Options.Create(new KodiOptions
        {
            PlaybackNotifications = new PlaybackNotificationOptions { Enabled = true, Capacity = capacity },
            Instances = [new() { Alias = "room", WebSocketEndpoint = "ws://example.invalid:9090/jsonrpc" }],
        }),
        new SafeText());

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json);
}
