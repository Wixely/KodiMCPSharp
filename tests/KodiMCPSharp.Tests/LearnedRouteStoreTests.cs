using KodiMCPSharp.Services;
using ModelContextProtocol;

namespace KodiMCPSharp.Tests;

public sealed class LearnedRouteStoreTests
{
    [Fact]
    public async Task FileStore_PersistsAndReloadsFixedRoute()
    {
        var root = Path.Combine(Path.GetTempPath(), "KodiMCPSharp.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            var savedUtc = new DateTimeOffset(2026, 8, 30, 12, 0, 0, TimeSpan.Zero);
            var expected = new LearnedRouteEntry(
                "room",
                "plugin.video.synthetic",
                "Synthetic",
                "search_movies",
                $"plugin://plugin.video.synthetic/?mode=search_movies&query={FileLearnedRouteStore.InputPlaceholder}",
                "files",
                "directory",
                HandleAction.Browse,
                savedUtc,
                new LearnedRouteParameter("input", "string", 200));

            using (var writer = new FileLearnedRouteStore(root, 10))
            {
                await writer.SaveAsync(expected, TestContext.Current.CancellationToken);
            }
            using var reader = new FileLearnedRouteStore(root, 10);

            var actual = Assert.Single(await reader.ListAsync("room", TestContext.Current.CancellationToken));

            Assert.Equal(expected, actual);
            var documentPath = Path.Combine(root, "room", "plugin.video.synthetic.json");
            Assert.True(File.Exists(documentPath));
            Assert.True(await reader.DeleteAsync("room", "plugin.video.synthetic", "search_movies", TestContext.Current.CancellationToken));
            Assert.Empty(await reader.ListAsync("room", TestContext.Current.CancellationToken));
            Assert.False(File.Exists(documentPath));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FileStore_RejectsNonPluginTarget()
    {
        var root = Path.Combine(Path.GetTempPath(), "KodiMCPSharp.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new FileLearnedRouteStore(root, 10);
            var route = new LearnedRouteEntry(
                "room", "plugin.video.synthetic", "Synthetic", "unsafe", "https://example.invalid/private",
                "files", "directory", HandleAction.Browse, DateTimeOffset.UtcNow);

            await Assert.ThrowsAsync<McpException>(() => store.SaveAsync(route, TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }

    [Fact]
    public async Task FileStore_RejectsInputTemplateOnRoutingKey()
    {
        var root = Path.Combine(Path.GetTempPath(), "KodiMCPSharp.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var store = new FileLearnedRouteStore(root, 10);
            var route = new LearnedRouteEntry(
                "room",
                "plugin.video.synthetic",
                "Synthetic",
                "unsafe_mode",
                $"plugin://plugin.video.synthetic/?mode={FileLearnedRouteStore.InputPlaceholder}",
                "files",
                "directory",
                HandleAction.Browse,
                DateTimeOffset.UtcNow,
                new LearnedRouteParameter("input", "string", 200));

            await Assert.ThrowsAsync<McpException>(() => store.SaveAsync(route, TestContext.Current.CancellationToken));
            Assert.False(Directory.Exists(root));
        }
        finally
        {
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
