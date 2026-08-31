using System.Text.Json;
using KodiMCPSharp.Configuration;
using KodiMCPSharp.Kodi;
using KodiMCPSharp.Security;
using KodiMCPSharp.Services;
using Microsoft.Extensions.Options;

namespace KodiMCPSharp.Tests;

public sealed class KodiServiceTests
{
    [Fact]
    public async Task Search_RedactsSensitiveLabelsAndNeverReturnsRawTarget()
    {
        const string rawTarget = "smb://account:password@server/private/movie.mkv";
        var fake = new FakeKodiClient((method, _) => method switch
        {
            "VideoLibrary.GetMovies" => Element($$"""
                {"limits":{"start":0,"end":1,"total":1},"movies":[{"label":"{{rawTarget}}","type":"movie","file":"{{rawTarget}}","year":2026}]}
                """),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var result = await service.SearchLibraryAsync("room", "synthetic", "movies", null, null, 0, 25, TestContext.Current.CancellationToken);
        var serialized = JsonSerializer.Serialize(result);

        Assert.Equal("[redacted]", result.Items.Single().Label);
        Assert.StartsWith("h_", result.Items.Single().Handle, StringComparison.Ordinal);
        Assert.Contains("play", result.Items.Single().AvailableActions);
        Assert.Contains("add-favourite", result.Items.Single().AvailableActions);
        Assert.DoesNotContain(rawTarget, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddonBrowse_UsesServerIssuedHandleWithoutExposingPluginPath()
    {
        string? receivedDirectory = null;
        var requestedTypeProperty = false;
        string? requestedAddonType = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Addons.GetAddons" => CaptureParameters(write, root =>
            {
                requestedAddonType = root.GetProperty("type").GetString();
                requestedTypeProperty = root.GetProperty("properties").EnumerateArray()
                    .Any(value => value.GetString() == "type");
            }, """
                {"limits":{"start":0,"end":1,"total":1},"addons":[{"addonid":"plugin.video.synthetic","name":"Synthetic","type":"xbmc.python.pluginsource","enabled":true}]}
                """),
            "Files.GetDirectory" => CaptureDirectory(write, value => receivedDirectory = value),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);
        var addons = await service.ListAddonsAsync("room", 0, 25, TestContext.Current.CancellationToken);

        var result = await service.BrowseAsync("room", addons.Addons.Single().Handle, "video", 0, 25, TestContext.Current.CancellationToken);

        Assert.Equal("plugin://plugin.video.synthetic/", receivedDirectory);
        Assert.Equal("xbmc.python.pluginsource", requestedAddonType);
        Assert.False(requestedTypeProperty);
        Assert.DoesNotContain("plugin://", JsonSerializer.Serialize(addons), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plugin://", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task CaptureCurrentAddonPage_UsesOnlyFixedInfoLabelAndReturnsOpaqueHandle()
    {
        const string target = "plugin://plugin.video.synthetic/?action=search&query=KodiMCPRouteTest";
        string[]? requestedLabels = null;
        string? requestedAddonId = null;
        string? receivedDirectory = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "XBMC.GetInfoLabels" => CaptureParameters(write, root => requestedLabels = root.GetProperty("labels")
                .EnumerateArray().Select(value => value.GetString()!).ToArray(), $$$"""
                {"Container.FolderPath":"{{{target}}}"}
                """),
            "Addons.GetAddonDetails" => CaptureParameters(write,
                root => requestedAddonId = root.GetProperty("addonid").GetString(),
                "{\"addonid\":\"plugin.video.synthetic\",\"name\":\"Synthetic\",\"type\":\"xbmc.python.pluginsource\",\"enabled\":true}"),
            "Files.GetDirectory" => CaptureDirectory(write, value => receivedDirectory = value),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var captured = await service.CaptureCurrentAddonPageAsync("room", TestContext.Current.CancellationToken);
        await service.BrowseAsync("room", captured.Handle, "video", 0, 25, TestContext.Current.CancellationToken);

        Assert.NotNull(requestedLabels);
        Assert.Equal(["Container.FolderPath"], requestedLabels);
        Assert.Equal("plugin.video.synthetic", requestedAddonId);
        Assert.Equal(target, receivedDirectory);
        Assert.True(captured.CanBrowse);
        Assert.DoesNotContain("plugin://", JsonSerializer.Serialize(captured), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("KodiMCPRouteTest", JsonSerializer.Serialize(captured), StringComparison.Ordinal);
    }

    [Fact]
    public async Task CaptureCurrentAddonPage_RejectsNonPluginPage()
    {
        var fake = new FakeKodiClient((method, _) => method switch
        {
            "XBMC.GetInfoLabels" => Element("{\"Container.FolderPath\":\"videodb://movies/titles/\"}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() =>
            service.CaptureCurrentAddonPageAsync("room", TestContext.Current.CancellationToken));

        Assert.Contains("not a capturable add-on directory", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ListInstances_ClassifiesFailureWithoutEndpointDetails()
    {
        var fake = new FakeKodiClient((_, _) => throw new KodiRpcException(KodiFailureKind.Unavailable, "Kodi could not be reached."));
        var service = CreateService(fake);

        var result = await service.ListInstancesAsync(TestContext.Current.CancellationToken);

        Assert.Equal("unavailable", result.Single().Availability);
        Assert.Equal("unavailable", result.Single().FailureKind);
    }

    [Fact]
    public async Task Favourites_ArePagedLocallyWithoutUnsupportedLimitsParameter()
    {
        var wroteLimits = false;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Favourites.GetFavourites" => CaptureParameters(write, root => wroteLimits = root.TryGetProperty("limits", out _), """
                {"limits":{"start":0,"end":3,"total":3},"favourites":[{"label":"One"},{"label":"Two"},{"label":"Three"}]}
                """),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var result = await service.ListFavouritesAsync("room", 1, 1, TestContext.Current.CancellationToken);

        Assert.False(wroteLimits);
        Assert.Equal(1, result.Start);
        Assert.Equal(2, result.End);
        Assert.Equal(3, result.Total);
        Assert.Equal("Two", result.Items.Single().Label);
    }

    [Fact]
    public async Task FavouriteSearch_IsCaseInsensitiveFilteredAndPagedLocally()
    {
        string? requestedType = null;
        var wroteLimits = false;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Favourites.GetFavourites" => CaptureParameters(write, root =>
            {
                requestedType = root.GetProperty("type").GetString();
                wroteLimits = root.TryGetProperty("limits", out _);
            }, """
                {"favourites":[
                  {"label":"Other","type":"media","path":"synthetic-other"},
                  {"label":"STAR One","type":"media","path":"synthetic-one"},
                  {"label":"A star Two","type":"media","path":"synthetic-two"}
                ]}
                """),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var result = await service.SearchFavouritesAsync("room", "star", "MEDIA", 1, 1, TestContext.Current.CancellationToken);

        Assert.Equal("media", requestedType);
        Assert.False(wroteLimits);
        Assert.Equal(1, result.Start);
        Assert.Equal(2, result.End);
        Assert.Equal(2, result.Total);
        var item = Assert.Single(result.Items);
        Assert.Equal("A star Two", item.Label);
        Assert.StartsWith("h_", item.Handle, StringComparison.Ordinal);
        Assert.True(item.IsPlayable);
        Assert.Contains("play", item.AvailableActions);
        Assert.Contains("remove-favourite", item.AvailableActions);
        Assert.Null(item.UnsupportedReason);
        Assert.DoesNotContain("synthetic-two", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task FavouriteSearch_UsesKodiTitleAndSafelyHandlesVideoAddonWindows()
    {
        const string target = "plugin://plugin.video.synthetic/?action=show";
        var fake = new FakeKodiClient((method, _) => method switch
        {
            "Favourites.GetFavourites" => Element($$"""
                {"favourites":[{"title":"Synthetic Lantern","type":"window","window":"videos","windowparameter":"{{target}}"}]}
                """),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var result = await service.SearchFavouritesAsync("room", "lantern", "window", 0, 25, TestContext.Current.CancellationToken);

        var item = Assert.Single(result.Items);
        Assert.Equal("Synthetic Lantern", item.Label);
        Assert.True(item.IsFolder);
        Assert.False(item.IsPlayable);
        Assert.StartsWith("h_", item.Handle, StringComparison.Ordinal);
        Assert.Contains("browse", item.AvailableActions);
        Assert.Contains("remove-favourite", item.AvailableActions);
        Assert.Null(item.UnsupportedReason);
        Assert.DoesNotContain(target, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddFavourite_UsesMediaHandleAndVerifiesPresence()
    {
        const string target = "synthetic-library-target";
        var favouriteReads = 0;
        JsonElement? addParameters = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetMovies" => Element($$"""
                {"limits":{"start":0,"end":1,"total":1},"movies":[{"label":"Synthetic Film","type":"movie","file":"{{target}}"}]}
                """),
            "Favourites.GetFavourites" => Element(favouriteReads++ == 0
                ? "{\"favourites\":[]}"
                : $$"""{"favourites":[{"title":"Synthetic Film","type":"media","path":"{{target}}"}]}"""),
            "Favourites.AddFavourite" => CaptureParameters(write, root => addParameters = root.Clone(), "\"OK\""),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowFavourites = true);
        var movies = await service.SearchLibraryAsync("room", "Synthetic", "movies", null, null, 0, 25, TestContext.Current.CancellationToken);

        var result = await service.AddFavouriteAsync("room", Assert.Single(movies.Items).Handle!, TestContext.Current.CancellationToken);

        Assert.True(result.Accepted);
        Assert.True(result.Observed);
        Assert.Equal("observed-complete", result.Completion);
        Assert.Equal("Synthetic Film", addParameters?.GetProperty("title").GetString());
        Assert.Equal("media", addParameters?.GetProperty("type").GetString());
        Assert.Equal(target, addParameters?.GetProperty("path").GetString());
        Assert.DoesNotContain(target, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddFavourite_WhenAlreadyPresent_DoesNotToggle()
    {
        var toggles = 0;
        var fake = new FakeKodiClient((method, _) => method switch
        {
            "Favourites.GetFavourites" => Element("{\"favourites\":[{\"title\":\"Synthetic Film\",\"type\":\"media\",\"path\":\"synthetic-target\"}]}"),
            "Favourites.AddFavourite" => throw new InvalidOperationException($"Unexpected toggle {++toggles}"),
            _ => throw new InvalidOperationException(method),
        });
        var (service, handles) = CreateControlServiceWithHandles(fake, controls => controls.AllowFavourites = true);
        var handle = handles.Create("room", "synthetic-target", "video", "movie",
            HandleAction.Play | HandleAction.AddFavourite,
            favourite: new FavouriteDescriptor("Synthetic Film", "media", Path: "synthetic-target"));

        var result = await service.AddFavouriteAsync("room", handle, TestContext.Current.CancellationToken);

        Assert.False(result.Accepted);
        Assert.True(result.Observed);
        Assert.Equal("already-in-requested-state", result.Completion);
        Assert.Equal(0, toggles);
    }

    [Fact]
    public async Task RemoveFavourite_WhenAlreadyAbsent_DoesNotToggle()
    {
        var toggles = 0;
        var fake = new FakeKodiClient((method, _) => method switch
        {
            "Favourites.GetFavourites" => Element("{\"favourites\":[]}"),
            "Favourites.AddFavourite" => throw new InvalidOperationException($"Unexpected toggle {++toggles}"),
            _ => throw new InvalidOperationException(method),
        });
        var (service, handles) = CreateControlServiceWithHandles(fake, controls => controls.AllowFavourites = true);
        var handle = handles.Create("room", "synthetic-target", "video", "media",
            HandleAction.Play | HandleAction.RemoveFavourite,
            favourite: new FavouriteDescriptor("Synthetic Film", "media", Path: "synthetic-target"));

        var result = await service.RemoveFavouriteAsync("room", handle, TestContext.Current.CancellationToken);

        Assert.False(result.Accepted);
        Assert.True(result.Observed);
        Assert.Equal("already-in-requested-state", result.Completion);
        Assert.Equal(0, toggles);
    }

    [Fact]
    public async Task RemoveFavourite_UsesExactHandleAndVerifiesAbsence()
    {
        const string target = "plugin://plugin.video.synthetic/?mode=show";
        var favouriteReads = 0;
        var toggles = 0;
        var present = $$"""{"favourites":[{"title":"Synthetic Show","type":"window","window":"videos","windowparameter":"{{target}}"}]}""";
        var fake = new FakeKodiClient((method, _) => method switch
        {
            "Favourites.GetFavourites" => Element(favouriteReads++ < 2 ? present : "{\"favourites\":[]}"),
            "Favourites.AddFavourite" => Element(++toggles == 1 ? "\"OK\"" : "\"unexpected\""),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowFavourites = true);
        var favourites = await service.ListFavouritesAsync("room", 0, 25, TestContext.Current.CancellationToken);

        var result = await service.RemoveFavouriteAsync("room", Assert.Single(favourites.Items).Handle!, TestContext.Current.CancellationToken);

        Assert.True(result.Accepted);
        Assert.True(result.Observed);
        Assert.Equal(1, toggles);
        Assert.DoesNotContain(target, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RemoveFavourite_RefusesDuplicateExactMatchesWithoutToggling()
    {
        const string favourites = "{\"favourites\":[{\"title\":\"Duplicate\",\"type\":\"media\",\"path\":\"synthetic\"},{\"title\":\"Duplicate\",\"type\":\"media\",\"path\":\"synthetic\"}]}";
        var toggles = 0;
        var fake = new FakeKodiClient((method, _) => method switch
        {
            "Favourites.GetFavourites" => Element(favourites),
            "Favourites.AddFavourite" => throw new InvalidOperationException($"Unexpected toggle {++toggles}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowFavourites = true);
        var listed = await service.ListFavouritesAsync("room", 0, 25, TestContext.Current.CancellationToken);

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() =>
            service.RemoveFavouriteAsync("room", listed.Items[0].Handle!, TestContext.Current.CancellationToken));

        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(0, toggles);
    }

    [Fact]
    public async Task FavouriteChanges_AreDisabledByDefaultAndExecutableFavouritesHaveNoHandle()
    {
        var fake = new FakeKodiClient((method, _) => method switch
        {
            "Favourites.GetFavourites" => Element("{\"favourites\":[{\"title\":\"Unsafe\",\"type\":\"script\",\"path\":\"script.synthetic\"}]}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, _ => { });
        var listed = await service.ListFavouritesAsync("room", 0, 25, TestContext.Current.CancellationToken);

        var item = Assert.Single(listed.Items);
        Assert.Null(item.Handle);
        Assert.Empty(item.AvailableActions);
        Assert.Contains("non-actionable", item.UnsupportedReason, StringComparison.Ordinal);
        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() =>
            service.AddFavouriteAsync("room", "h_untrusted", TestContext.Current.CancellationToken));
        Assert.Contains("AllowFavourites", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AlbumSearch_UsesKodiAlbumFilterAndSortFields()
    {
        string? filterField = null;
        string? sortMethod = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "AudioLibrary.GetAlbums" => CaptureParameters(write, root =>
            {
                filterField = root.GetProperty("filter").GetProperty("field").GetString();
                sortMethod = root.GetProperty("sort").GetProperty("method").GetString();
            }, "{\"limits\":{\"start\":0,\"end\":0,\"total\":0},\"albums\":[]}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        _ = await service.SearchLibraryAsync("room", "synthetic", "albums", null, null, 0, 1, TestContext.Current.CancellationToken);

        Assert.Equal("album", filterField);
        Assert.Equal("album", sortMethod);
    }

    [Fact]
    public async Task MovieSearch_CombinesTitleYearAndGenreFilters()
    {
        var filters = new List<(string Field, string Operator, string Value)>();
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetMovies" => CaptureParameters(write, root =>
            {
                foreach (var filter in root.GetProperty("filter").GetProperty("and").EnumerateArray())
                {
                    filters.Add((
                        filter.GetProperty("field").GetString()!,
                        filter.GetProperty("operator").GetString()!,
                        filter.GetProperty("value").GetString()!));
                }
            }, """
                {"limits":{"start":0,"end":1,"total":1},"movies":[{"label":"Synthetic","type":"movie","year":1989,"genre":["Comedy"],"file":"synthetic-target"}]}
                """),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var result = await service.SearchLibraryAsync("room", "synthetic", "movies", 1989, "Comedy", 0, 25, TestContext.Current.CancellationToken);

        Assert.Equal(
        [
            ("title", "contains", "synthetic"),
            ("year", "is", "1989"),
            ("genre", "contains", "Comedy"),
        ], filters);
        Assert.Equal(["Comedy"], result.Items.Single().Genres);
    }

    [Fact]
    public async Task TvShowSearch_AllowsGenreWithoutTitle()
    {
        string? field = null;
        string? value = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetTVShows" => CaptureParameters(write, root =>
            {
                var filter = root.GetProperty("filter");
                field = filter.GetProperty("field").GetString();
                value = filter.GetProperty("value").GetString();
            }, "{\"limits\":{\"start\":0,\"end\":0,\"total\":0},\"tvshows\":[]}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        _ = await service.SearchLibraryAsync("room", null, "tvshows", null, "Drama", 0, 25, TestContext.Current.CancellationToken);

        Assert.Equal("genre", field);
        Assert.Equal("Drama", value);
    }

    [Fact]
    public async Task Search_RequiresAFilterAndRestrictsVideoOnlyFilters()
    {
        var service = CreateService(new FakeKodiClient((_, _) => Element("{}")));

        var missing = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() =>
            service.SearchLibraryAsync("room", null, "movies", null, null, 0, 25, TestContext.Current.CancellationToken));
        var unsupported = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() =>
            service.SearchLibraryAsync("room", "song", "songs", 2000, null, 0, 25, TestContext.Current.CancellationToken));

        Assert.Contains("at least one", missing.Message, StringComparison.Ordinal);
        Assert.Contains("only for movies and tvshows", unsupported.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Genres_UsesClosedVideoDomainAndBoundedLimits()
    {
        string? requestedType = null;
        int? requestedStart = null;
        int? requestedEnd = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetGenres" => CaptureParameters(write, root =>
            {
                requestedType = root.GetProperty("type").GetString();
                requestedStart = root.GetProperty("limits").GetProperty("start").GetInt32();
                requestedEnd = root.GetProperty("limits").GetProperty("end").GetInt32();
            }, "{\"limits\":{\"start\":2,\"end\":3,\"total\":5},\"genres\":[{\"label\":\"Comedy\"}]}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var result = await service.ListGenresAsync("room", "tvshows", 1, 2, TestContext.Current.CancellationToken);

        Assert.Equal("tvshow", requestedType);
        Assert.Equal(2, requestedStart);
        Assert.Equal(4, requestedEnd);
        Assert.Equal("Comedy", Assert.Single(result.Genres).Name);
        Assert.Equal(5, result.Total);
    }

    [Fact]
    public async Task VideoTags_MapClosedDomainAndReturnSafeNames()
    {
        string? requestedType = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetTags" => CaptureParameters(write,
                root => requestedType = root.GetProperty("type").GetString(),
                """{"limits":{"start":0,"end":1,"total":1},"tags":[{"title":"Featured"}]}"""),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var result = await service.ListVideoTagsAsync("room", "tvshows", 0, 25, TestContext.Current.CancellationToken);

        Assert.Equal("tvshow", requestedType);
        Assert.Equal("Featured", Assert.Single(result.Tags).Name);
    }

    [Fact]
    public async Task MovieSets_ReturnOpaqueBrowseHandleAndPlayableMovies()
    {
        const string target = "synthetic-set-movie";
        int? requestedSetId = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetMovieSets" => Element("""
                {"limits":{"start":0,"end":1,"total":1},"sets":[{"setid":42,"title":"Synthetic Collection","plot":"Plot"}]}
                """),
            "VideoLibrary.GetMovieSetDetails" => CaptureParameters(write, root =>
            {
                requestedSetId = root.GetProperty("setid").GetInt32();
                Assert.True(root.TryGetProperty("movies", out _));
            }, $$$"""
                {"setdetails":{"limits":{"start":0,"end":1,"total":1},"movies":[{"movieid":7,"label":"Synthetic Movie","type":"movie","file":"{{{target}}}"}]}}
                """),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var sets = await service.ListMovieSetsAsync("room", 0, 25, TestContext.Current.CancellationToken);
        var movies = await service.BrowseMovieSetAsync("room", Assert.Single(sets.Sets).Handle, 0, 25,
            TestContext.Current.CancellationToken);

        Assert.Equal(42, requestedSetId);
        Assert.Contains("play", Assert.Single(movies.Items).AvailableActions);
        Assert.DoesNotContain(target, JsonSerializer.Serialize(movies), StringComparison.Ordinal);
    }

    [Fact]
    public async Task VideoDetails_ReturnRichSafeMetadataAndRejectPaths()
    {
        const string target = "synthetic-detailed-movie";
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetMovies" => CaptureParameters(write, root =>
            {
                Assert.Equal(2, root.GetProperty("limits").GetProperty("end").GetInt32());
                Assert.Equal("title", root.GetProperty("filter").GetProperty("and")[0].GetProperty("field").GetString());
            }, $$$"""
                {"limits":{"start":0,"end":1,"total":1},"movies":[{
                  "movieid":7,"label":"Synthetic Movie","originaltitle":"Original","year":2025,
                  "plot":"Plot","tagline":"Tagline","rating":8.5,"votes":"123","studio":["Studio"],
                  "director":["Director"],"writer":["Writer"],"tag":["Featured"],
                  "cast":[{"name":"Actor","role":"Role","order":0}],"type":"movie","file":"{{{target}}}"
                }]}
                """),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var result = await service.GetVideoDetailsAsync("room", "movies", "Synthetic Movie", 2025,
            TestContext.Current.CancellationToken);

        Assert.Equal(8.5, result.Rating);
        Assert.Equal("Actor", Assert.Single(result.Cast).Name);
        Assert.Contains("play", result.Item.AvailableActions);
        Assert.DoesNotContain(target, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task RecentMovies_ReturnPlayableOpaqueHandles()
    {
        const string target = "synthetic-recent-target";
        var fake = new FakeKodiClient((method, _) => method switch
        {
            "VideoLibrary.GetRecentlyAddedMovies" => Element($$"""
                {"limits":{"start":0,"end":1,"total":1},"movies":[{"label":"Recent","type":"movie","file":"{{target}}","year":2025}]}
                """),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var result = await service.ListRecentAsync("room", "movies", 0, 25, TestContext.Current.CancellationToken);
        var item = Assert.Single(result.Items);

        Assert.True(item.IsPlayable);
        Assert.StartsWith("h_", item.Handle, StringComparison.Ordinal);
        Assert.DoesNotContain(target, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ContinueWatching_UsesInProgressFilterAndReturnsResumeState()
    {
        string? field = null;
        string? filterOperator = null;
        string? sortOrder = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetMovies" => CaptureParameters(write, root =>
            {
                field = root.GetProperty("filter").GetProperty("field").GetString();
                filterOperator = root.GetProperty("filter").GetProperty("operator").GetString();
                sortOrder = root.GetProperty("sort").GetProperty("order").GetString();
            }, """
                {"limits":{"start":0,"end":1,"total":1},"movies":[{"label":"Partial","type":"movie","file":"synthetic-target","resume":{"position":120,"total":600}}]}
                """),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var result = await service.ListContinueWatchingAsync("room", "movies", 0, 25, TestContext.Current.CancellationToken);

        Assert.Equal("inprogress", field);
        Assert.Equal("true", filterOperator);
        Assert.Equal("descending", sortOrder);
        var item = Assert.Single(result.Items);
        Assert.Equal(120, item.ResumePositionSeconds);
        Assert.Equal("partially-watched", item.WatchState);
    }

    [Fact]
    public async Task RecentlyWatchedShows_SortsByLastPlayedAndDeduplicatesShows()
    {
        string? filterField = null;
        string? filterOperator = null;
        string? sortMethod = null;
        string? sortOrder = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetEpisodes" => CaptureParameters(write, root =>
            {
                filterField = root.GetProperty("filter").GetProperty("field").GetString();
                filterOperator = root.GetProperty("filter").GetProperty("operator").GetString();
                sortMethod = root.GetProperty("sort").GetProperty("method").GetString();
                sortOrder = root.GetProperty("sort").GetProperty("order").GetString();
            }, """
                {"limits":{"start":0,"end":3,"total":8},"episodes":[
                  {"label":"Newest","showtitle":"Example Show","season":2,"episode":3,"lastplayed":"2026-08-31 12:00:00"},
                  {"label":"Older duplicate","showtitle":"Example Show","season":2,"episode":2,"lastplayed":"2026-08-30 12:00:00"},
                  {"label":"Other","showtitle":"Other Show","season":1,"episode":4,"lastplayed":"2026-08-29 12:00:00"}
                ]}
                """),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var result = await service.ListRecentlyWatchedShowsAsync("room", 10, TestContext.Current.CancellationToken);

        Assert.Equal("lastplayed", filterField);
        Assert.Equal("after", filterOperator);
        Assert.Equal("lastplayed", sortMethod);
        Assert.Equal("descending", sortOrder);
        Assert.Equal(2, result.Returned);
        Assert.True(result.HasMoreHistory);
        Assert.Collection(result.Shows,
            show =>
            {
                Assert.Equal("Example Show", show.Show);
                Assert.Equal("Newest", show.LastEpisode);
                Assert.Equal(3, show.EpisodeNumber);
            },
            show => Assert.Equal("Other Show", show.Show));
    }

    [Fact]
    public async Task RecentlyWatchedMovies_UsesLastPlayedFilterAndDescendingSort()
    {
        int? requestedEnd = null;
        string? filterField = null;
        string? filterOperator = null;
        string? sortMethod = null;
        string? sortOrder = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetMovies" => CaptureParameters(write, root =>
            {
                requestedEnd = root.GetProperty("limits").GetProperty("end").GetInt32();
                filterField = root.GetProperty("filter").GetProperty("field").GetString();
                filterOperator = root.GetProperty("filter").GetProperty("operator").GetString();
                sortMethod = root.GetProperty("sort").GetProperty("method").GetString();
                sortOrder = root.GetProperty("sort").GetProperty("order").GetString();
            }, """
                {"limits":{"start":0,"end":2,"total":4},"movies":[
                  {"label":"Newest","year":2026,"lastplayed":"2026-08-31 12:00:00"},
                  {"label":"Older","year":2025,"lastplayed":"2026-08-30 12:00:00"}
                ]}
                """),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var result = await service.ListRecentlyWatchedMoviesAsync("room", 2, TestContext.Current.CancellationToken);

        Assert.Equal(2, requestedEnd);
        Assert.Equal("lastplayed", filterField);
        Assert.Equal("after", filterOperator);
        Assert.Equal("lastplayed", sortMethod);
        Assert.Equal("descending", sortOrder);
        Assert.True(result.HasMoreHistory);
        Assert.Collection(result.Movies,
            movie =>
            {
                Assert.Equal("Newest", movie.Title);
                Assert.Equal(2026, movie.Year);
            },
            movie => Assert.Equal("Older", movie.Title));
    }

    [Fact]
    public async Task ListUpNext_SelectsPartialThenFirstUnwatchedAfterLatestWatched()
    {
        const string partialTarget = "synthetic-partial-next";
        const string unwatchedTarget = "synthetic-unwatched-next";
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetInProgressTVShows" => Element("""
                {"limits":{"start":0,"end":2,"total":2},"tvshows":[
                  {"tvshowid":2,"label":"Show B","lastplayed":"2026-08-31 12:00:00"},
                  {"tvshowid":1,"label":"Show A","lastplayed":"2026-08-30 12:00:00"}
                ]}
                """),
            "VideoLibrary.GetEpisodes" => GetEpisodesResponse(write, partialTarget, unwatchedTarget),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var result = await service.ListUpNextAsync("room", 10, TestContext.Current.CancellationToken);

        Assert.Collection(result.Shows,
            show =>
            {
                Assert.Equal("Show B", show.Show);
                Assert.Equal("resume-partially-watched", show.SelectionBasis);
                Assert.Equal(120, show.ResumePositionSeconds);
            },
            show =>
            {
                Assert.Equal("Show A", show.Show);
                Assert.Equal(2, show.EpisodeNumber);
                Assert.Equal("first-unwatched-after-latest-watched", show.SelectionBasis);
            });
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(partialTarget, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(unwatchedTarget, serialized, StringComparison.Ordinal);
    }

    private static JsonElement GetEpisodesResponse(Action<Utf8JsonWriter>? write, string partialTarget, string unwatchedTarget)
    {
        var tvShowId = 0;
        CaptureParameters(write, parameters => tvShowId = parameters.GetProperty("tvshowid").GetInt32(), "{}");
        return Element(tvShowId switch
        {
            1 => $$$"""
                {"limits":{"start":0,"end":3,"total":3},"episodes":[
                  {"label":"Earlier gap","season":0,"episode":1,"playcount":0,"file":"synthetic-gap"},
                  {"label":"Watched A","season":1,"episode":1,"playcount":1,"file":"synthetic-watched-a"},
                  {"label":"Next A","season":1,"episode":2,"playcount":0,"file":"{{{unwatchedTarget}}}"}
                ]}
                """,
            2 => $$$"""
                {"limits":{"start":0,"end":2,"total":2},"episodes":[
                  {"label":"Watched B","season":2,"episode":2,"playcount":1,"file":"synthetic-watched-b"},
                  {"label":"Partial B","season":2,"episode":3,"playcount":0,"resume":{"position":120},"file":"{{{partialTarget}}}"}
                ]}
                """,
            _ => throw new InvalidOperationException($"Unexpected TV show id {tvShowId}."),
        });
    }

    [Fact]
    public async Task EpisodeResults_ReportExplicitWatchState()
    {
        var fake = new FakeKodiClient((method, _) => method switch
        {
            "VideoLibrary.GetRecentlyAddedEpisodes" => Element("""
                {"limits":{"start":0,"end":3,"total":3},"episodes":[
                  {"label":"Watched","type":"episode","file":"synthetic-watched","playcount":1,"resume":{"position":0,"total":600}},
                  {"label":"Partial","type":"episode","file":"synthetic-partial","playcount":0,"resume":{"position":120,"total":600}},
                  {"label":"Unwatched","type":"episode","file":"synthetic-unwatched","playcount":0,"resume":{"position":0,"total":600}}
                ]}
                """),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var result = await service.ListRecentAsync("room", "episodes", 0, 25, TestContext.Current.CancellationToken);

        Assert.Collection(result.Items,
            item => Assert.Equal("watched", item.WatchState),
            item => Assert.Equal("partially-watched", item.WatchState),
            item => Assert.Equal("unwatched", item.WatchState));
    }

    [Fact]
    public async Task EpisodeWatchState_IsBlockedByDefault()
    {
        var service = CreateService(new FakeKodiClient((_, _) => Element("{}")));

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() =>
            service.SetEpisodeWatchStateAsync("room", "h_invalid", "watched", TestContext.Current.CancellationToken));

        Assert.Contains("ReadOnly", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task EpisodeWatchState_UsesOpaqueLibraryIdClearsResumeAndVerifies()
    {
        int? changedEpisodeId = null;
        int? changedPlayCount = null;
        double? changedResumePosition = null;
        double? changedResumeTotal = null;
        int? verifiedEpisodeId = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetRecentlyAddedEpisodes" => Element("""
                {"limits":{"start":0,"end":1,"total":1},"episodes":[{"label":"Episode","type":"episode","episodeid":77,"file":"synthetic-episode","playcount":0}]}
                """),
            "VideoLibrary.SetEpisodeDetails" => CaptureParameters(write, root =>
            {
                changedEpisodeId = root.GetProperty("episodeid").GetInt32();
                changedPlayCount = root.GetProperty("playcount").GetInt32();
                changedResumePosition = root.GetProperty("resume").GetProperty("position").GetDouble();
                changedResumeTotal = root.GetProperty("resume").GetProperty("total").GetDouble();
            }, "\"OK\""),
            "VideoLibrary.GetEpisodeDetails" => CaptureParameters(write, root =>
            {
                verifiedEpisodeId = root.GetProperty("episodeid").GetInt32();
            }, "{\"episodedetails\":{\"playcount\":1,\"resume\":{\"position\":0,\"total\":0}}}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowWatchState = true);
        var episodes = await service.ListRecentAsync("room", "episodes", 0, 25, TestContext.Current.CancellationToken);
        var handle = Assert.Single(episodes.Items).Handle!;

        var result = await service.SetEpisodeWatchStateAsync("room", handle, "watched", TestContext.Current.CancellationToken);

        Assert.Equal(77, changedEpisodeId);
        Assert.Equal(1, changedPlayCount);
        Assert.Equal(0, changedResumePosition);
        Assert.Equal(0, changedResumeTotal);
        Assert.Equal(77, verifiedEpisodeId);
        Assert.True(result.Accepted);
        Assert.True(result.Observed);
        Assert.Equal("watched", result.ObservedState);
        Assert.DoesNotContain("77", JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task BulkEpisodeWatchState_PreviewUsesClosedRangeAndExcludesSpecials()
    {
        var toggles = 0;
        var fake = new FakeKodiClient((method, _) => method switch
        {
            "VideoLibrary.GetEpisodeDetails" => Element("{\"episodedetails\":{\"tvshowid\":42,\"season\":1,\"episode\":2}}"),
            "VideoLibrary.GetEpisodes" => Element("""
                {"limits":{"start":0,"end":4,"total":4},"episodes":[
                  {"episodeid":1,"label":"Special","season":0,"episode":1,"playcount":0,"resume":{"position":0}},
                  {"episodeid":2,"label":"First","season":1,"episode":1,"playcount":1,"resume":{"position":0}},
                  {"episodeid":3,"label":"Second","season":1,"episode":2,"playcount":0,"resume":{"position":120}},
                  {"episodeid":4,"label":"Unnumbered","season":1,"playcount":0,"resume":{"position":0}}
                ]}
                """),
            "VideoLibrary.SetEpisodeDetails" => throw new InvalidOperationException($"Unexpected mutation {++toggles}"),
            _ => throw new InvalidOperationException(method),
        });
        var options = new KodiOptions { DefaultAlias = "room", Handles = new HandleOptions { LifetimeMinutes = 15, Capacity = 100 } };
        var registry = new KodiInstanceRegistry([new RegisteredKodiInstance("room", fake)], "room");
        var handles = new InMemoryHandleStore(15, 100, TimeProvider.System);
        var handle = handles.Create("room", "synthetic-episode", "video", "episode", HandleAction.SetWatchState, libraryId: 3);
        var service = new KodiService(registry, handles, new TestLearnedRouteStore(), Options.Create(options), new SafeText(), TimeProvider.System);

        var result = await service.BulkSetEpisodeWatchStateAsync(
            "room", handle, "watched", "through", true, false, TestContext.Current.CancellationToken);

        Assert.True(result.Preview);
        Assert.Equal(2, result.Matched);
        Assert.Equal(1, result.WouldChange);
        Assert.Equal(1, result.AlreadyTarget);
        Assert.Equal(1, result.SkippedUnnumbered);
        Assert.Equal(2, result.Returned);
        Assert.Equal(0, toggles);
        Assert.Collection(result.Episodes,
            item => Assert.Equal("would-change", item.Outcome),
            item => Assert.Equal("already-target", item.Outcome));
    }

    [Fact]
    public async Task BulkEpisodeWatchState_AppliesAndVerifiesOneReread()
    {
        var episodeReads = 0;
        var changedIds = new List<int>();
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetEpisodeDetails" => Element("{\"episodedetails\":{\"tvshowid\":42,\"season\":1,\"episode\":2}}"),
            "VideoLibrary.GetEpisodes" => Element(episodeReads++ == 0
                ? "{\"limits\":{\"start\":0,\"end\":2,\"total\":2},\"episodes\":[{\"episodeid\":1,\"label\":\"First\",\"season\":1,\"episode\":1,\"playcount\":0,\"resume\":{\"position\":0}},{\"episodeid\":2,\"label\":\"Second\",\"season\":1,\"episode\":2,\"playcount\":0,\"resume\":{\"position\":120}}]}"
                : "{\"limits\":{\"start\":0,\"end\":2,\"total\":2},\"episodes\":[{\"episodeid\":1,\"label\":\"First\",\"season\":1,\"episode\":1,\"playcount\":1,\"resume\":{\"position\":0}},{\"episodeid\":2,\"label\":\"Second\",\"season\":1,\"episode\":2,\"playcount\":1,\"resume\":{\"position\":0}}]}"),
            "VideoLibrary.SetEpisodeDetails" => CaptureParameters(write, root => changedIds.Add(root.GetProperty("episodeid").GetInt32()), "\"OK\""),
            _ => throw new InvalidOperationException(method),
        });
        var (service, handles) = CreateControlServiceWithHandles(fake, controls => controls.AllowWatchState = true);
        var handle = handles.Create("room", "synthetic-episode", "video", "episode", HandleAction.SetWatchState, libraryId: 2);

        var result = await service.BulkSetEpisodeWatchStateAsync(
            "room", handle, "watched", "through", false, false, TestContext.Current.CancellationToken);

        Assert.False(result.Preview);
        Assert.Equal([1, 2], changedIds);
        Assert.Equal(2, result.Updated);
        Assert.Equal(2, result.Verified);
        Assert.Equal(0, result.Failed);
        Assert.Equal(2, episodeReads);
        Assert.All(result.Episodes, item => Assert.Equal("observed-complete", item.Outcome));
    }

    [Fact]
    public async Task BulkEpisodeWatchState_PreviewReportsCapWithoutMutating()
    {
        var episodeValues = Enumerable.Range(1, 101).Select(number => new
        {
            episodeid = number,
            label = $"Synthetic {number}",
            season = 1,
            episode = number,
            playcount = 0,
            resume = new { position = 0 },
        }).ToArray();
        var episodeJson = JsonSerializer.Serialize(new
        {
            limits = new { start = 0, end = 101, total = 101 },
            episodes = episodeValues,
        });
        var fake = new FakeKodiClient((method, _) => method switch
        {
            "VideoLibrary.GetEpisodeDetails" => Element("{\"episodedetails\":{\"tvshowid\":42,\"season\":1,\"episode\":101}}"),
            "VideoLibrary.GetEpisodes" => Element(episodeJson),
            _ => throw new InvalidOperationException(method),
        });
        var options = new KodiOptions { DefaultAlias = "room", Handles = new HandleOptions { LifetimeMinutes = 15, Capacity = 100 } };
        var registry = new KodiInstanceRegistry([new RegisteredKodiInstance("room", fake)], "room");
        var handles = new InMemoryHandleStore(15, 100, TimeProvider.System);
        var handle = handles.Create("room", "synthetic-episode", "video", "episode", HandleAction.SetWatchState, libraryId: 101);
        var service = new KodiService(registry, handles, new TestLearnedRouteStore(), Options.Create(options), new SafeText(), TimeProvider.System);

        var result = await service.BulkSetEpisodeWatchStateAsync(
            "room", handle, "watched", "through", true, false, TestContext.Current.CancellationToken);

        Assert.True(result.CapExceeded);
        Assert.Equal(101, result.WouldChange);
        Assert.Equal(100, result.Returned);
        Assert.Equal(100, result.Episodes.Count);
    }

    [Fact]
    public async Task BulkEpisodeWatchState_ApplyRequiresWatchStateGate()
    {
        var rpcCalls = 0;
        var fake = new FakeKodiClient((_, _) =>
        {
            rpcCalls++;
            return Element("{}");
        });
        var (service, handles) = CreateControlServiceWithHandles(fake, _ => { });
        var handle = handles.Create("room", "synthetic-episode", "video", "episode", HandleAction.SetWatchState, libraryId: 1);

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() =>
            service.BulkSetEpisodeWatchStateAsync(
                "room", handle, "watched", "all", false, false, TestContext.Current.CancellationToken));

        Assert.Contains("AllowWatchState", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, rpcCalls);
    }

    [Fact]
    public async Task BulkEpisodeWatchState_ApplyRefusesOverCapBeforeMutation()
    {
        var episodeValues = Enumerable.Range(1, 101).Select(number => new
        {
            episodeid = number,
            label = $"Synthetic {number}",
            season = 1,
            episode = number,
            playcount = 0,
            resume = new { position = 0 },
        }).ToArray();
        var episodeJson = JsonSerializer.Serialize(new
        {
            limits = new { start = 0, end = 101, total = 101 },
            episodes = episodeValues,
        });
        var mutations = 0;
        var fake = new FakeKodiClient((method, _) => method switch
        {
            "VideoLibrary.GetEpisodeDetails" => Element("{\"episodedetails\":{\"tvshowid\":42,\"season\":1,\"episode\":101}}"),
            "VideoLibrary.GetEpisodes" => Element(episodeJson),
            "VideoLibrary.SetEpisodeDetails" => throw new InvalidOperationException($"Unexpected mutation {++mutations}"),
            _ => throw new InvalidOperationException(method),
        });
        var (service, handles) = CreateControlServiceWithHandles(fake, controls => controls.AllowWatchState = true);
        var handle = handles.Create("room", "synthetic-episode", "video", "episode", HandleAction.SetWatchState, libraryId: 101);

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() =>
            service.BulkSetEpisodeWatchStateAsync(
                "room", handle, "watched", "through", false, false, TestContext.Current.CancellationToken));

        Assert.Contains("100-episode cap", exception.Message, StringComparison.Ordinal);
        Assert.Equal(0, mutations);
    }

    [Fact]
    public async Task BulkEpisodeWatchState_RetrySkipsEpisodesAlreadyAtTarget()
    {
        var mutations = 0;
        var fake = new FakeKodiClient((method, _) => method switch
        {
            "VideoLibrary.GetEpisodeDetails" => Element("{\"episodedetails\":{\"tvshowid\":42,\"season\":1,\"episode\":1}}"),
            "VideoLibrary.GetEpisodes" => Element("{\"limits\":{\"start\":0,\"end\":1,\"total\":1},\"episodes\":[{\"episodeid\":1,\"label\":\"Complete\",\"season\":1,\"episode\":1,\"playcount\":1,\"resume\":{\"position\":0}}]}"),
            "VideoLibrary.SetEpisodeDetails" => throw new InvalidOperationException($"Unexpected retry mutation {++mutations}"),
            _ => throw new InvalidOperationException(method),
        });
        var (service, handles) = CreateControlServiceWithHandles(fake, controls => controls.AllowWatchState = true);
        var handle = handles.Create("room", "synthetic-episode", "video", "episode", HandleAction.SetWatchState, libraryId: 1);

        var result = await service.BulkSetEpisodeWatchStateAsync(
            "room", handle, "watched", "all", false, false, TestContext.Current.CancellationToken);

        Assert.Equal(0, result.WouldChange);
        Assert.Equal(1, result.AlreadyTarget);
        Assert.Equal(0, result.Updated);
        Assert.Equal("already-target", Assert.Single(result.Episodes).Outcome);
        Assert.Equal(0, mutations);
    }

    [Fact]
    public async Task TvShowBrowse_UsesOpaqueShowAndSeasonHandles()
    {
        int? requestedTvShowId = null;
        int? requestedSeason = null;
        string[] requestedEpisodeProperties = [];
        const string episodeTarget = "synthetic-private-episode-target";
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetTVShows" => Element("""
                {"limits":{"start":0,"end":1,"total":1},"tvshows":[{"label":"Synthetic Show","tvshowid":42,"year":2020}]}
                """),
            "VideoLibrary.GetSeasons" => CaptureParameters(write, root =>
            {
                requestedTvShowId = root.GetProperty("tvshowid").GetInt32();
            }, """
                {"limits":{"start":0,"end":1,"total":1},"seasons":[{"label":"Season 2","tvshowid":42,"season":2,"episode":10,"watchedepisodes":3}]}
                """),
            "VideoLibrary.GetEpisodes" => CaptureParameters(write, root =>
            {
                requestedTvShowId = root.GetProperty("tvshowid").GetInt32();
                requestedSeason = root.GetProperty("season").GetInt32();
                requestedEpisodeProperties = root.GetProperty("properties").EnumerateArray().Select(value => value.GetString()!).ToArray();
            }, $$"""
                {"limits":{"start":0,"end":1,"total":1},"episodes":[{"label":"Episode 1","season":2,"episode":1,"file":"{{episodeTarget}}"}]}
                """),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);
        var shows = await service.SearchLibraryAsync("room", "Synthetic", "tvshows", null, null, 0, 25, TestContext.Current.CancellationToken);
        var showHandle = Assert.Single(shows.Items).Handle!;

        var seasons = await service.BrowseTvShowAsync("room", showHandle, 0, 25, TestContext.Current.CancellationToken);
        var seasonItem = Assert.Single(seasons.Items);
        var episodes = await service.BrowseTvShowAsync("room", seasonItem.Handle!, 0, 25, TestContext.Current.CancellationToken);
        var episode = Assert.Single(episodes.Items);

        Assert.Equal(42, requestedTvShowId);
        Assert.Equal(2, requestedSeason);
        Assert.True(seasonItem.IsFolder);
        Assert.Equal(2, seasonItem.SeasonNumber);
        Assert.Equal(10, seasonItem.EpisodeCount);
        Assert.Equal(3, seasonItem.WatchedEpisodeCount);
        Assert.True(episode.IsPlayable);
        Assert.Equal(1, episode.EpisodeNumber);
        Assert.DoesNotContain("year", requestedEpisodeProperties, StringComparer.Ordinal);
        Assert.DoesNotContain("genre", requestedEpisodeProperties, StringComparer.Ordinal);
        Assert.DoesNotContain(episodeTarget, JsonSerializer.Serialize(episodes), StringComparison.Ordinal);
    }

    [Fact]
    public async Task EpisodeViews_RequestOnlyKodiEpisodeDetailFields()
    {
        var propertySets = new List<string[]>();
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetRecentlyAddedEpisodes" => CaptureParameters(write, root =>
            {
                propertySets.Add(root.GetProperty("properties").EnumerateArray().Select(value => value.GetString()!).ToArray());
            }, "{\"limits\":{\"start\":0,\"end\":0,\"total\":0},\"episodes\":[]}"),
            "VideoLibrary.GetEpisodes" => CaptureParameters(write, root =>
            {
                propertySets.Add(root.GetProperty("properties").EnumerateArray().Select(value => value.GetString()!).ToArray());
            }, "{\"limits\":{\"start\":0,\"end\":0,\"total\":0},\"episodes\":[]}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        _ = await service.ListRecentAsync("room", "episodes", 0, 25, TestContext.Current.CancellationToken);
        _ = await service.ListContinueWatchingAsync("room", "episodes", 0, 25, TestContext.Current.CancellationToken);

        Assert.Equal(2, propertySets.Count);
        Assert.All(propertySets, properties =>
        {
            Assert.DoesNotContain("year", properties, StringComparer.Ordinal);
            Assert.DoesNotContain("genre", properties, StringComparer.Ordinal);
        });
    }

    [Fact]
    public void Capabilities_KeepEveryControlGateDisabled()
    {
        var service = CreateService(new FakeKodiClient((_, _) => Element("{}")));

        var result = service.GetCapabilities();

        Assert.True(result.ReadOnly);
        Assert.All(result.ControlGates, gate => Assert.False(gate.Value));
    }

    [Fact]
    public async Task Playback_IsBlockedByDefault()
    {
        var service = CreateService(new FakeKodiClient((_, _) => Element("{}")));

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() =>
            service.PlayItemAsync("room", "h_invalid", TestContext.Current.CancellationToken));

        Assert.Contains("ReadOnly", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Playback_UsesServerIssuedHandleAndObservesPlayer()
    {
        string? openedTarget = null;
        const string target = "synthetic-library-target";
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Player.Open" => CaptureParameters(write, root => openedTarget = root.GetProperty("item").GetProperty("file").GetString(), "\"OK\""),
            "Player.GetActivePlayers" => Element("[{\"playerid\":1,\"type\":\"video\"}]"),
            "Player.GetProperties" => Element("{\"speed\":1}"),
            _ => throw new InvalidOperationException(method),
        });
        var options = new KodiOptions
        {
            DefaultAlias = "room",
            ReadOnly = false,
            Controls = new KodiControlOptions { AllowPlayback = true },
            Handles = new HandleOptions { LifetimeMinutes = 15, Capacity = 100 },
        };
        var registry = new KodiInstanceRegistry([new RegisteredKodiInstance("room", fake)], "room");
        var handles = new InMemoryHandleStore(15, 100, TimeProvider.System);
        var handle = handles.Create("room", target, "video", "movie", HandleAction.Play);
        var service = new KodiService(registry, handles, new TestLearnedRouteStore(), Options.Create(options), new SafeText(), TimeProvider.System);

        var result = await service.PlayItemAsync("room", handle, TestContext.Current.CancellationToken);

        Assert.Equal(target, openedTarget);
        Assert.True(result.Accepted);
        Assert.True(result.Observed);
        Assert.Equal("playing", result.State);
        Assert.Equal(1, result.PlayerId);
    }

    [Fact]
    public async Task PlayNextEpisode_PrefersFavouriteAndSelectsFirstUnwatchedEpisode()
    {
        const string favouriteTarget = "plugin://plugin.video.synthetic/?show=Example";
        const string episodeTarget = "plugin://plugin.video.synthetic/?season=1&episode=2";
        string? openedTarget = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Favourites.GetFavourites" => Element($$$"""
                {"favourites":[{"title":"Example Show","type":"window","window":"videos","windowparameter":"{{{favouriteTarget}}}"}]}
                """),
            "Files.GetDirectory" => Element($$$"""
                {"files":[
                  {"label":"Pilot","type":"episode","filetype":"file","file":"plugin://plugin.video.synthetic/?season=1\u0026episode=1","playcount":1},
                  {"label":"Second","type":"episode","filetype":"file","file":"{{{episodeTarget.Replace("&", "\\u0026", StringComparison.Ordinal)}}}","playcount":0}
                ]}
                """),
            "Player.Open" => CaptureParameters(write, root => openedTarget = root.GetProperty("item").GetProperty("file").GetString(), "\"OK\""),
            "Player.GetActivePlayers" => Element("[{\"playerid\":1,\"type\":\"video\"}]"),
            "Player.GetProperties" => Element("{\"speed\":1}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowPlayback = true);

        var result = await service.PlayNextEpisodeAsync("room", "Example Show", TestContext.Current.CancellationToken);

        Assert.Equal("favourite", result.Source);
        Assert.Equal("first-unwatched", result.SelectionBasis);
        Assert.Equal(1, result.SeasonNumber);
        Assert.Equal(2, result.EpisodeNumber);
        Assert.Equal(episodeTarget, openedTarget);
        Assert.True(result.Observed);
        Assert.DoesNotContain(episodeTarget, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlayNextEpisode_FallsBackToLibraryAndResumesPartialEpisode()
    {
        const string episodeTarget = "synthetic-library-episode";
        string? openedTarget = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Favourites.GetFavourites" => Element("{\"favourites\":[]}"),
            "VideoLibrary.GetTVShows" => Element("{\"tvshows\":[{\"label\":\"Example Show\",\"tvshowid\":42}]}"),
            "VideoLibrary.GetEpisodes" => Element($$$"""
                {"episodes":[{"label":"Partial","type":"episode","file":"{{{episodeTarget}}}","season":2,"episode":3,"playcount":0,"resume":{"position":120,"total":600}}]}
                """),
            "Player.Open" => CaptureParameters(write, root => openedTarget = root.GetProperty("item").GetProperty("file").GetString(), "\"OK\""),
            "Player.GetActivePlayers" => Element("[{\"playerid\":1,\"type\":\"video\"}]"),
            "Player.GetProperties" => Element("{\"speed\":1}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowPlayback = true);

        var result = await service.PlayNextEpisodeAsync("room", "Example Show", TestContext.Current.CancellationToken);

        Assert.Equal("library", result.Source);
        Assert.Equal("resume-partially-watched", result.SelectionBasis);
        Assert.Equal(episodeTarget, openedTarget);
    }

    [Fact]
    public async Task PlayNextEpisode_UsesBoundedLearnedTvSearchRouteAsFinalFallback()
    {
        const string searchTarget = "plugin://plugin.video.synthetic/?mode=search&query=__KODIMCPSHARP_ROUTE_INPUT__";
        const string showTarget = "plugin://plugin.video.synthetic/?mode=show&id=42";
        const string episodeTarget = "plugin://plugin.video.synthetic/?mode=play&season=1&episode=1";
        var directoryReads = 0;
        string? openedTarget = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Favourites.GetFavourites" => Element("{\"favourites\":[]}"),
            "VideoLibrary.GetTVShows" => Element("{\"tvshows\":[]}"),
            "Files.GetDirectory" when directoryReads++ == 0 => Element($$$"""
                {"files":[{"label":"Example Show","filetype":"directory","file":"{{{showTarget.Replace("&", "\\u0026", StringComparison.Ordinal)}}}"}]}
                """),
            "Files.GetDirectory" => Element($$$"""
                {"files":[{"label":"Pilot","type":"episode","filetype":"file","file":"{{{episodeTarget.Replace("&", "\\u0026", StringComparison.Ordinal)}}}","playcount":0}]}
                """),
            "Player.Open" => CaptureParameters(write, root => openedTarget = root.GetProperty("item").GetProperty("file").GetString(), "\"OK\""),
            "Player.GetActivePlayers" => Element("[{\"playerid\":1,\"type\":\"video\"}]"),
            "Player.GetProperties" => Element("{\"speed\":1}"),
            _ => throw new InvalidOperationException(method),
        });
        var routeStore = new TestLearnedRouteStore();
        await routeStore.SaveAsync(new LearnedRouteEntry(
            "room", "plugin.video.synthetic", "Synthetic", "search_tvshows", searchTarget,
            "files", "directory", HandleAction.Browse, DateTimeOffset.UtcNow,
            new LearnedRouteParameter("input", "string", 200)), TestContext.Current.CancellationToken);
        var options = new KodiOptions
        {
            DefaultAlias = "room",
            ReadOnly = false,
            Controls = new KodiControlOptions { AllowPlayback = true },
            Handles = new HandleOptions { LifetimeMinutes = 15, Capacity = 100 },
        };
        var registry = new KodiInstanceRegistry([new RegisteredKodiInstance("room", fake)], "room");
        var handles = new InMemoryHandleStore(15, 100, TimeProvider.System);
        var service = new KodiService(registry, handles, routeStore, Options.Create(options), new SafeText(), TimeProvider.System);

        var result = await service.PlayNextEpisodeAsync("room", "Example Show", TestContext.Current.CancellationToken);

        Assert.Equal("learned-addon-route", result.Source);
        Assert.Equal(episodeTarget, openedTarget);
        Assert.Equal(2, directoryReads);
    }

    [Fact]
    public async Task PlayMovie_PrefersUniqueMediaFavouriteWhenYearIsOmitted()
    {
        const string target = "synthetic-favourite-movie.mkv";
        string? openedTarget = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Favourites.GetFavourites" => Element($$$"""
                {"favourites":[{"title":"Example Film","type":"media","path":"{{{target}}}"}]}
                """),
            "Player.Open" => CaptureParameters(write, root => openedTarget = root.GetProperty("item").GetProperty("file").GetString(), "\"OK\""),
            "Player.GetActivePlayers" => Element("[{\"playerid\":1,\"type\":\"video\"}]"),
            "Player.GetProperties" => Element("{\"speed\":1}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowPlayback = true);

        var result = await service.PlayMovieAsync("room", "Example Film", null, TestContext.Current.CancellationToken);

        Assert.Equal("favourite", result.Source);
        Assert.Equal(target, openedTarget);
        Assert.True(result.Observed);
        Assert.DoesNotContain(target, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlayRandomMovie_UsesClosedFiltersRandomSortAndObservedPlayback()
    {
        const string target = "synthetic-random-movie";
        string? sortMethod = null;
        string[]? filterFields = null;
        string? openedTarget = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetMovies" => CaptureParameters(write, root =>
            {
                sortMethod = root.GetProperty("sort").GetProperty("method").GetString();
                filterFields = root.GetProperty("filter").GetProperty("and").EnumerateArray()
                    .Select(filter => filter.GetProperty("field").GetString()!).ToArray();
            }, $$$"""
                {"movies":[{"label":"Random","year":2025,"file":"{{{target}}}"}]}
                """),
            "Player.Open" => CaptureParameters(write,
                root => openedTarget = root.GetProperty("item").GetProperty("file").GetString(), "\"OK\""),
            "Player.GetActivePlayers" => Element("[{\"playerid\":1,\"type\":\"video\"}]"),
            "Player.GetProperties" => Element("{\"speed\":1}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowPlayback = true);

        var result = await service.PlayRandomAsync("room", "movies", "unwatched", 2025, "Comedy", 7,
            TestContext.Current.CancellationToken);

        Assert.Equal("random", sortMethod);
        Assert.NotNull(filterFields);
        Assert.Equal(["playcount", "year", "genre", "rating"], filterFields);
        Assert.Equal(target, openedTarget);
        Assert.Equal("random-filter-match", result.SelectionBasis);
        Assert.True(result.Observed);
        Assert.DoesNotContain(target, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task PlayMovie_WithYearUsesUniqueLibraryMatch()
    {
        const string target = "synthetic-library-movie";
        string? openedTarget = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetMovies" => Element($$$"""
                {"movies":[{"label":"Example Film","year":2025,"file":"{{{target}}}"}]}
                """),
            "Player.Open" => CaptureParameters(write, root => openedTarget = root.GetProperty("item").GetProperty("file").GetString(), "\"OK\""),
            "Player.GetActivePlayers" => Element("[{\"playerid\":1,\"type\":\"video\"}]"),
            "Player.GetProperties" => Element("{\"speed\":1}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowPlayback = true);

        var result = await service.PlayMovieAsync("room", "Example Film", 2025, TestContext.Current.CancellationToken);

        Assert.Equal("library", result.Source);
        Assert.Equal(2025, result.Year);
        Assert.Equal(target, openedTarget);
    }

    [Fact]
    public async Task PlayMovie_DoesNotTreatAudioFavouriteAsMovie()
    {
        const string movieTarget = "synthetic-library-movie.mkv";
        string? openedTarget = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Favourites.GetFavourites" => Element("{\"favourites\":[{\"title\":\"Example Film\",\"type\":\"media\",\"path\":\"synthetic-audio.mp3\"}]}"),
            "VideoLibrary.GetMovies" => Element($$$"""{"movies":[{"label":"Example Film","year":2025,"file":"{{{movieTarget}}}"}]}"""),
            "Player.Open" => CaptureParameters(write, root => openedTarget = root.GetProperty("item").GetProperty("file").GetString(), "\"OK\""),
            "Player.GetActivePlayers" => Element("[{\"playerid\":1,\"type\":\"video\"}]"),
            "Player.GetProperties" => Element("{\"speed\":1}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowPlayback = true);

        var result = await service.PlayMovieAsync("room", "Example Film", null, TestContext.Current.CancellationToken);

        Assert.Equal("library", result.Source);
        Assert.Equal(movieTarget, openedTarget);
    }

    [Fact]
    public async Task PlayEpisode_UsesExactEpisodeFromFavouriteShow()
    {
        const string favouriteTarget = "plugin://plugin.video.synthetic/?show=example";
        const string episodeTarget = "plugin://plugin.video.synthetic/?season=2&episode=3";
        string? openedTarget = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Favourites.GetFavourites" => Element($$$"""
                {"favourites":[{"title":"Example Show","type":"window","window":"videos","windowparameter":"{{{favouriteTarget}}}"}]}
                """),
            "Files.GetDirectory" => Element($$$"""
                {"files":[{"label":"Exact","type":"episode","filetype":"file","file":"{{{episodeTarget.Replace("&", "\\u0026", StringComparison.Ordinal)}}}"}]}
                """),
            "Player.Open" => CaptureParameters(write, root => openedTarget = root.GetProperty("item").GetProperty("file").GetString(), "\"OK\""),
            "Player.GetActivePlayers" => Element("[{\"playerid\":1,\"type\":\"video\"}]"),
            "Player.GetProperties" => Element("{\"speed\":1}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowPlayback = true);

        var result = await service.PlayEpisodeAsync("room", "Example Show", 2, 3, TestContext.Current.CancellationToken);

        Assert.Equal("favourite", result.Source);
        Assert.Equal(2, result.SeasonNumber);
        Assert.Equal(3, result.EpisodeNumber);
        Assert.Equal(episodeTarget, openedTarget);
    }

    [Fact]
    public async Task PlayEpisode_FallsBackToExactLibraryEpisode()
    {
        const string target = "synthetic-library-episode";
        string? openedTarget = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Favourites.GetFavourites" => Element("{\"favourites\":[]}"),
            "VideoLibrary.GetTVShows" => Element("{\"tvshows\":[{\"label\":\"Example Show\",\"tvshowid\":42}]}"),
            "VideoLibrary.GetEpisodes" => Element($$$"""
                {"episodes":[{"label":"Exact","type":"episode","season":2,"episode":3,"file":"{{{target}}}"}]}
                """),
            "Player.Open" => CaptureParameters(write, root => openedTarget = root.GetProperty("item").GetProperty("file").GetString(), "\"OK\""),
            "Player.GetActivePlayers" => Element("[{\"playerid\":1,\"type\":\"video\"}]"),
            "Player.GetProperties" => Element("{\"speed\":1}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowPlayback = true);

        var result = await service.PlayEpisodeAsync("room", "Example Show", 2, 3, TestContext.Current.CancellationToken);

        Assert.Equal("library", result.Source);
        Assert.Equal(target, openedTarget);
    }

    [Fact]
    public async Task PlayEpisode_AmbiguousFavouriteNamesFallBackToLibrary()
    {
        const string target = "synthetic-library-episode";
        var favouriteDirectoryReads = 0;
        var fake = new FakeKodiClient((method, _) => method switch
        {
            "Favourites.GetFavourites" => Element("{\"favourites\":[{\"title\":\"Example Show\",\"type\":\"window\",\"window\":\"videos\",\"windowparameter\":\"plugin://plugin.video.one/?show=example\"},{\"title\":\"Example Show\",\"type\":\"window\",\"window\":\"videos\",\"windowparameter\":\"plugin://plugin.video.two/?show=example\"}]}"),
            "Files.GetDirectory" => throw new InvalidOperationException($"Unexpected ambiguous favourite traversal {++favouriteDirectoryReads}"),
            "VideoLibrary.GetTVShows" => Element("{\"tvshows\":[{\"label\":\"Example Show\",\"tvshowid\":42}]}"),
            "VideoLibrary.GetEpisodes" => Element($$$"""{"episodes":[{"label":"Exact","type":"episode","season":2,"episode":3,"file":"{{{target}}}"}]}"""),
            "Player.Open" => Element("\"OK\""),
            "Player.GetActivePlayers" => Element("[{\"playerid\":1,\"type\":\"video\"}]"),
            "Player.GetProperties" => Element("{\"speed\":1}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowPlayback = true);

        var result = await service.PlayEpisodeAsync("room", "Example Show", 2, 3, TestContext.Current.CancellationToken);

        Assert.Equal("library", result.Source);
        Assert.Equal(0, favouriteDirectoryReads);
    }

    [Fact]
    public async Task ResumeMovie_UsesKodiResumeOptionWithoutExposingTarget()
    {
        const string target = "synthetic-resume-movie";
        bool? requestedResume = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetMovies" => Element($$$"""
                {"movies":[{"label":"Example Film","year":2025,"file":"{{{target}}}","resume":{"position":420,"total":7200}}]}
                """),
            "Player.Open" => CaptureParameters(write, root => requestedResume = root.GetProperty("options").GetProperty("resume").GetBoolean(), "\"OK\""),
            "Player.GetActivePlayers" => Element("[{\"playerid\":1,\"type\":\"video\"}]"),
            "Player.GetProperties" => Element("{\"speed\":1}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowPlayback = true);

        var result = await service.ResumeAsync("room", "Example Film", "movies", TestContext.Current.CancellationToken);

        Assert.True(requestedResume);
        Assert.Equal("movie", result.MediaType);
        Assert.True(result.Observed);
        Assert.DoesNotContain(target, JsonSerializer.Serialize(result), StringComparison.Ordinal);
    }

    [Fact]
    public async Task ResumeAuto_SelectsMostAdvancedMatchingEpisode()
    {
        const string target = "synthetic-resume-episode-two";
        string? openedTarget = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "VideoLibrary.GetMovies" => Element("{\"movies\":[]}"),
            "VideoLibrary.GetEpisodes" => Element($$$"""
                {"episodes":[
                  {"label":"One","showtitle":"Example Show","season":1,"episode":1,"file":"synthetic-resume-episode-one","resume":{"position":60}},
                  {"label":"Two","showtitle":"Example Show","season":1,"episode":2,"file":"{{{target}}}","resume":{"position":600}}
                ]}
                """),
            "Player.Open" => CaptureParameters(write, root => openedTarget = root.GetProperty("item").GetProperty("file").GetString(), "\"OK\""),
            "Player.GetActivePlayers" => Element("[{\"playerid\":1,\"type\":\"video\"}]"),
            "Player.GetProperties" => Element("{\"speed\":1}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowPlayback = true);

        var result = await service.ResumeAsync("room", "Example Show", "auto", TestContext.Current.CancellationToken);

        Assert.Equal(target, openedTarget);
        Assert.Equal("episode", result.MediaType);
        Assert.Equal(2, result.EpisodeNumber);
    }

    [Fact]
    public async Task PlayerControl_UsesClosedActionAndVerifiesPausedState()
    {
        bool? requestedPlay = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Player.PlayPause" => CaptureParameters(write, root => requestedPlay = root.GetProperty("play").GetBoolean(), "{\"speed\":0}"),
            "Player.GetActivePlayers" => Element("[{\"playerid\":1,\"type\":\"video\"}]"),
            "Player.GetProperties" => Element("{\"speed\":0,\"percentage\":25}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowPlayerControl = true);

        var result = await service.PlayerControlAsync("room", 1, "pause", TestContext.Current.CancellationToken);

        Assert.False(requestedPlay);
        Assert.True(result.Accepted);
        Assert.True(result.Observed);
        Assert.Equal("paused", result.State);
    }

    [Fact]
    public async Task FullscreenVideo_UsesClosedKodiWindowAndVerifiesScreensaverDismissal()
    {
        string? requestedWindow = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Player.GetActivePlayers" => Element("[{\"playerid\":1,\"type\":\"video\"}]"),
            "GUI.ActivateWindow" => CaptureParameters(write, root => requestedWindow = root.GetProperty("window").GetString(), "\"OK\""),
            "GUI.GetProperties" => Element("{\"currentwindow\":{\"id\":12005,\"label\":\"Full screen video\"},\"fullscreen\":true}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowFullscreenVideo = true);

        var result = await service.ShowFullscreenVideoAsync("room", TestContext.Current.CancellationToken);

        Assert.Equal("fullscreenvideo", requestedWindow);
        Assert.True(result.Accepted);
        Assert.True(result.Observed);
        Assert.Equal(1, result.PlayerId);
    }

    [Fact]
    public async Task Seek_SerializesBoundedRelativeSeconds()
    {
        int? seconds = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Player.Seek" => CaptureParameters(write, root => seconds = root.GetProperty("value").GetProperty("seconds").GetInt32(), "{\"percentage\":50}"),
            "Player.GetActivePlayers" => Element("[{\"playerid\":1,\"type\":\"video\"}]"),
            "Player.GetProperties" => Element("{\"speed\":1,\"percentage\":50}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowSeek = true);

        var result = await service.SeekAsync("room", 1, "relative_seconds", -30, TestContext.Current.CancellationToken);

        Assert.Equal(-30, seconds);
        Assert.True(result.Observed);
        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() =>
            service.SeekAsync("room", 1, "percentage", 101, TestContext.Current.CancellationToken));
        Assert.Contains("between 0 and 100", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Volume_SetsBothValuesAndVerifiesObservation()
    {
        int? requestedVolume = null;
        bool? requestedMute = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Application.SetVolume" => CaptureParameters(write, root => requestedVolume = root.GetProperty("volume").GetInt32(), "75"),
            "Application.SetMute" => CaptureParameters(write, root => requestedMute = root.GetProperty("mute").GetBoolean(), "true"),
            "Application.GetProperties" => Element("{\"volume\":75,\"muted\":true}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowVolume = true);

        var result = await service.SetVolumeAsync("room", 75, true, TestContext.Current.CancellationToken);

        Assert.Equal(75, requestedVolume);
        Assert.True(requestedMute);
        Assert.True(result.Observed);
    }

    [Fact]
    public async Task StreamSelection_UsesReportedIndexAndVerifiesObservation()
    {
        int? requestedStream = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Player.SetAudioStream" => CaptureParameters(write, root => requestedStream = root.GetProperty("stream").GetInt32(), "\"OK\""),
            "Player.GetProperties" => Element("{\"currentaudiostream\":{\"index\":2},\"speed\":1}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowStreamSelection = true);

        var result = await service.SelectStreamAsync("room", 1, "audio", 2, true, TestContext.Current.CancellationToken);

        Assert.Equal(2, requestedStream);
        Assert.True(result.Accepted);
        Assert.True(result.Observed);
    }

    [Fact]
    public async Task PlaybackMode_SetsRepeatAndShuffleAndVerifiesObservation()
    {
        string? requestedRepeat = null;
        bool? requestedShuffle = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Player.SetRepeat" => CaptureParameters(write, root => requestedRepeat = root.GetProperty("repeat").GetString(), "\"OK\""),
            "Player.SetShuffle" => CaptureParameters(write, root => requestedShuffle = root.GetProperty("shuffle").GetBoolean(), "\"OK\""),
            "Player.GetProperties" => Element("{\"repeat\":\"all\",\"shuffled\":true,\"speed\":1}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowPlaybackModes = true);

        var result = await service.SetPlaybackModeAsync("room", 1, "all", true, TestContext.Current.CancellationToken);

        Assert.Equal("all", requestedRepeat);
        Assert.True(requestedShuffle);
        Assert.True(result.Observed);
    }

    [Fact]
    public async Task PlaylistAdd_UsesOpaqueHandleAndVerifiesSizeIncrease()
    {
        const string target = "synthetic-library-target";
        string? requestedTarget = null;
        var sizeReads = 0;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Playlist.GetPlaylists" => Element("[{\"playlistid\":1,\"type\":\"video\"}]"),
            "Playlist.GetProperties" => Element(sizeReads++ == 0 ? "{\"size\":2}" : "{\"size\":3}"),
            "Playlist.Add" => CaptureParameters(write, root => requestedTarget = root.GetProperty("item").GetProperty("file").GetString(), "\"OK\""),
            _ => throw new InvalidOperationException(method),
        });
        var (service, handles) = CreateControlServiceWithHandles(fake, controls => controls.AllowPlaylists = true);
        var handle = handles.Create("room", target, "video", "movie", HandleAction.Play);

        var result = await service.PlaylistAddAsync("room", handle, "video", TestContext.Current.CancellationToken);

        Assert.Equal(target, requestedTarget);
        Assert.True(result.Accepted);
        Assert.True(result.Observed);
    }

    [Fact]
    public async Task GetQueue_ReturnsPositionsAndOpaquePlayableItems()
    {
        const string firstTarget = "synthetic-queue-one";
        const string secondTarget = "synthetic-queue-two";
        int? requestedStart = null;
        int? requestedEnd = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Playlist.GetPlaylists" => Element("[{\"playlistid\":1,\"type\":\"video\"}]"),
            "Playlist.GetItems" => CaptureParameters(write, root =>
            {
                requestedStart = root.GetProperty("limits").GetProperty("start").GetInt32();
                requestedEnd = root.GetProperty("limits").GetProperty("end").GetInt32();
            }, $$$"""
                {"limits":{"start":2,"end":4,"total":6},"items":[
                  {"label":"One","type":"movie","file":"{{{firstTarget}}}"},
                  {"label":"Two","type":"episode","file":"{{{secondTarget}}}","season":1,"episode":2}
                ]}
                """),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var result = await service.GetQueueAsync("room", "video", 1, 2, TestContext.Current.CancellationToken);

        Assert.Equal(2, requestedStart);
        Assert.Equal(4, requestedEnd);
        Assert.Collection(result.Items,
            item =>
            {
                Assert.Equal(2, item.Position);
                Assert.StartsWith("h_", item.Item.Handle, StringComparison.Ordinal);
            },
            item => Assert.Equal(3, item.Position));
        var serialized = JsonSerializer.Serialize(result);
        Assert.DoesNotContain(firstTarget, serialized, StringComparison.Ordinal);
        Assert.DoesNotContain(secondTarget, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task MoveQueueItem_UsesAdjacentSwapsAndVerifiesDestination()
    {
        var swaps = new List<(int First, int Second)>();
        var itemReads = 0;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Playlist.GetPlaylists" => Element("[{\"playlistid\":1,\"type\":\"video\"}]"),
            "Playlist.GetProperties" => Element("{\"size\":4}"),
            "Playlist.GetItems" => CaptureParameters(write, _ => itemReads++,
                "{\"limits\":{\"start\":0,\"end\":1,\"total\":4},\"items\":[{\"label\":\"Moved\",\"type\":\"movie\",\"file\":\"synthetic-moved\"}]}"),
            "Playlist.Swap" => CaptureParameters(write, root => swaps.Add((
                root.GetProperty("position1").GetInt32(), root.GetProperty("position2").GetInt32())), "\"OK\""),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateControlService(fake, controls => controls.AllowPlaylists = true);

        var result = await service.MoveQueueItemAsync("room", "video", 1, 3, TestContext.Current.CancellationToken);

        Assert.Equal(2, itemReads);
        Assert.Equal([(1, 2), (2, 3)], swaps);
        Assert.True(result.Accepted);
        Assert.True(result.Observed);
    }

    [Fact]
    public async Task Status_ProvidesPlayerModesAndEnumeratedStreams()
    {
        var fake = new FakeKodiClient((method, _) => method switch
        {
            "Application.GetProperties" => Element("{\"volume\":40,\"muted\":false,\"name\":\"Kodi\",\"version\":{\"major\":21,\"minor\":2}}"),
            "Player.GetActivePlayers" => Element("[{\"playerid\":1,\"type\":\"video\"}]"),
            "Player.GetProperties" => Element("""
                {"speed":1,"playlistid":1,"position":2,"repeat":"all","shuffled":true,"canseek":true,
                 "currentaudiostream":{"index":1},"currentvideostream":{"index":0},"currentsubtitle":{"index":3},"subtitleenabled":true,
                 "audiostreams":[{"index":1,"name":"Main","language":"eng","codec":"aac","channels":2,"isdefault":true}],
                 "videostreams":[{"index":0,"name":"Picture","codec":"h264"}],
                 "subtitles":[{"index":3,"name":"English","language":"eng","isforced":false,"isimpaired":false}]}
                """),
            "Player.GetItem" => Element("{\"item\":{\"label\":\"Synthetic\",\"type\":\"movie\"}}"),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);

        var result = await service.GetStatusAsync("room", TestContext.Current.CancellationToken);
        var player = Assert.Single(result.Players);

        Assert.Equal(1, player.CurrentAudioStream);
        Assert.Equal(0, player.CurrentVideoStream);
        Assert.Equal(3, player.CurrentSubtitle);
        Assert.True(player.SubtitlesEnabled);
        Assert.Equal("all", player.Repeat);
        Assert.True(player.Shuffled);
        Assert.Single(player.AudioStreams);
        Assert.Single(player.VideoStreams);
        Assert.Single(player.Subtitles);
    }

    [Fact]
    public async Task LearnedAddonRoute_PersistsOnlyObservedPluginHandleAndCanBeReused()
    {
        string? receivedDirectory = null;
        var directoryReads = 0;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Addons.GetAddons" => Element("""
                {"limits":{"start":0,"end":1,"total":1},"addons":[{"addonid":"plugin.video.synthetic","name":"Synthetic","type":"xbmc.python.pluginsource","enabled":true}]}
                """),
            "Files.GetDirectory" when directoryReads++ == 0 => Element("""
                {"limits":{"start":0,"end":1,"total":1},"files":[{"label":"Search movies","file":"plugin://plugin.video.synthetic/?mode=search_movies","filetype":"directory"}]}
                """),
            "Files.GetDirectory" => CaptureDirectory(write, value => receivedDirectory = value),
            _ => throw new InvalidOperationException(method),
        });
        var options = new KodiOptions
        {
            DefaultAlias = "room",
            LearnedRoutes = new LearnedRouteOptions { AllowWrite = true },
            Handles = new HandleOptions { LifetimeMinutes = 15, Capacity = 100 },
        };
        var registry = new KodiInstanceRegistry([new RegisteredKodiInstance("room", fake)], "room");
        var handles = new InMemoryHandleStore(15, 100, TimeProvider.System);
        var routeStore = new TestLearnedRouteStore();
        var service = new KodiService(registry, handles, routeStore, Options.Create(options), new SafeText(), TimeProvider.System);

        var addons = await service.ListAddonsAsync("room", 0, 25, TestContext.Current.CancellationToken);
        var menu = await service.BrowseAsync("room", Assert.Single(addons.Addons).Handle, "video", 0, 25, TestContext.Current.CancellationToken);
        var saved = await service.SaveAddonRouteAsync("room", Assert.Single(menu.Items).Handle!, "search_movies", null, TestContext.Current.CancellationToken);
        var listed = await service.ListAddonRoutesAsync("room", TestContext.Current.CancellationToken);
        await service.BrowseAsync("room", Assert.Single(listed.Routes).Handle, "video", 0, 25, TestContext.Current.CancellationToken);

        Assert.Equal("Synthetic", saved.AddonName);
        Assert.Equal("search_movies", saved.Name);
        Assert.Equal("plugin://plugin.video.synthetic/?mode=search_movies", receivedDirectory);
        Assert.DoesNotContain("plugin://", JsonSerializer.Serialize(listed), StringComparison.OrdinalIgnoreCase);

        var forgotten = await service.ForgetAddonRouteAsync("room", saved.Handle, TestContext.Current.CancellationToken);
        Assert.True(forgotten.Removed);
        Assert.Empty((await service.ListAddonRoutesAsync("room", TestContext.Current.CancellationToken)).Routes);
    }

    [Fact]
    public async Task LearnedAddonRoute_WriteIsDisabledByDefault()
    {
        var service = CreateService(new FakeKodiClient((_, _) => Element("{}")));

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() =>
            service.SaveAddonRouteAsync("room", "h_untrusted", "search_movies", null, TestContext.Current.CancellationToken));

        Assert.Contains("AllowWrite", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ParameterizedLearnedRoute_InfersObservedValueAndBindsEncodedInput()
    {
        string? receivedDirectory = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Files.GetDirectory" => CaptureDirectory(write, value => receivedDirectory = value),
            _ => throw new InvalidOperationException(method),
        });
        var options = new KodiOptions
        {
            DefaultAlias = "room",
            LearnedRoutes = new LearnedRouteOptions { AllowWrite = true },
            Handles = new HandleOptions { LifetimeMinutes = 15, Capacity = 100 },
        };
        var registry = new KodiInstanceRegistry([new RegisteredKodiInstance("room", fake)], "room");
        var handles = new InMemoryHandleStore(15, 100, TimeProvider.System);
        var service = new KodiService(registry, handles, new TestLearnedRouteStore(), Options.Create(options), new SafeText(), TimeProvider.System);
        var observedHandle = handles.Create(
            "room",
            "plugin://plugin.video.synthetic/?mode=search_movies&query=Alien",
            "files",
            "directory",
            HandleAction.Browse,
            "plugin.video.synthetic",
            "Synthetic");

        var saved = await service.SaveAddonRouteAsync(
            "room", observedHandle, "search_movies", "Alien", TestContext.Current.CancellationToken);
        var bound = service.BindAddonRoute("room", saved.Handle, "Dune & mode=unsafe");
        await service.BrowseAsync("room", bound.Handle, "video", 0, 25, TestContext.Current.CancellationToken);

        Assert.True(saved.RequiresInput);
        Assert.Equal("input", saved.InputName);
        Assert.Equal(200, saved.InputMaximumLength);
        Assert.True(bound.CanBrowse);
        Assert.Equal("plugin://plugin.video.synthetic/?mode=search_movies&query=Dune%20%26%20mode%3Dunsafe", receivedDirectory);
        Assert.DoesNotContain("plugin://", JsonSerializer.Serialize(saved), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Alien", JsonSerializer.Serialize(saved), StringComparison.Ordinal);

        var exception = await Assert.ThrowsAsync<ModelContextProtocol.McpException>(() => service.SaveAddonRouteAsync(
            "room", observedHandle, "unsafe_mode", "search_movies", TestContext.Current.CancellationToken));
        Assert.Contains("exactly one", exception.Message, StringComparison.Ordinal);
    }

    private static KodiService CreateService(IKodiRpcClient client)
    {
        var options = new KodiOptions
        {
            DefaultAlias = "room",
            Handles = new HandleOptions { LifetimeMinutes = 15, Capacity = 100 },
        };
        var registry = new KodiInstanceRegistry([new RegisteredKodiInstance("room", client)], "room");
        var handles = new InMemoryHandleStore(15, 100, TimeProvider.System);
        return new KodiService(registry, handles, new TestLearnedRouteStore(), Options.Create(options), new SafeText(), TimeProvider.System);
    }

    private static KodiService CreateControlService(IKodiRpcClient client, Action<KodiControlOptions> configure) =>
        CreateControlServiceWithHandles(client, configure).Service;

    private static (KodiService Service, InMemoryHandleStore Handles) CreateControlServiceWithHandles(
        IKodiRpcClient client,
        Action<KodiControlOptions> configure)
    {
        var controls = new KodiControlOptions();
        configure(controls);
        var options = new KodiOptions
        {
            DefaultAlias = "room",
            ReadOnly = false,
            Controls = controls,
            Handles = new HandleOptions { LifetimeMinutes = 15, Capacity = 100 },
        };
        var registry = new KodiInstanceRegistry([new RegisteredKodiInstance("room", client)], "room");
        var handles = new InMemoryHandleStore(15, 100, TimeProvider.System);
        return (new KodiService(registry, handles, new TestLearnedRouteStore(), Options.Create(options), new SafeText(), TimeProvider.System), handles);
    }

    private static JsonElement CaptureDirectory(Action<Utf8JsonWriter>? write, Action<string?> capture)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write!(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        capture(document.RootElement.GetProperty("directory").GetString());
        return Element("{\"limits\":{\"start\":0,\"end\":0,\"total\":0},\"files\":[]}");
    }

    private static JsonElement CaptureParameters(Action<Utf8JsonWriter>? write, Action<JsonElement> capture, string response)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            write!(writer);
            writer.WriteEndObject();
        }
        using var document = JsonDocument.Parse(stream.ToArray());
        capture(document.RootElement);
        return Element(response);
    }

    private static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }

    private sealed class FakeKodiClient(Func<string, Action<Utf8JsonWriter>?, JsonElement> handler) : IKodiRpcClient
    {
        public Task<JsonElement> CallAsync(string method, Action<Utf8JsonWriter>? writeParameters = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(handler(method, writeParameters));
    }

    private sealed class TestLearnedRouteStore : ILearnedRouteStore
    {
        private readonly List<LearnedRouteEntry> _routes = [];

        public Task<LearnedRouteEntry> SaveAsync(LearnedRouteEntry route, CancellationToken cancellationToken)
        {
            _routes.RemoveAll(value => value.InstanceAlias.Equals(route.InstanceAlias, StringComparison.OrdinalIgnoreCase) &&
                                       value.AddonId.Equals(route.AddonId, StringComparison.Ordinal) &&
                                       value.Name.Equals(route.Name, StringComparison.OrdinalIgnoreCase));
            _routes.Add(route);
            return Task.FromResult(route);
        }

        public Task<IReadOnlyList<LearnedRouteEntry>> ListAsync(string instanceAlias, CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<LearnedRouteEntry>>(
                _routes.Where(value => value.InstanceAlias.Equals(instanceAlias, StringComparison.OrdinalIgnoreCase)).ToArray());

        public Task<bool> DeleteAsync(string instanceAlias, string addonId, string name, CancellationToken cancellationToken)
        {
            var removed = _routes.RemoveAll(value => value.InstanceAlias.Equals(instanceAlias, StringComparison.OrdinalIgnoreCase) &&
                                                     value.AddonId.Equals(addonId, StringComparison.Ordinal) &&
                                                     value.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) > 0;
            return Task.FromResult(removed);
        }
    }
}
