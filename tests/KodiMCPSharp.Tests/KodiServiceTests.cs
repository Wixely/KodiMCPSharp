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
        Assert.DoesNotContain(rawTarget, serialized, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AddonBrowse_UsesServerIssuedHandleWithoutExposingPluginPath()
    {
        string? receivedDirectory = null;
        var fake = new FakeKodiClient((method, write) => method switch
        {
            "Addons.GetAddons" => Element("""
                {"limits":{"start":0,"end":1,"total":1},"addons":[{"addonid":"plugin.video.synthetic","name":"Synthetic","type":"xbmc.python.pluginsource","enabled":true}]}
                """),
            "Files.GetDirectory" => CaptureDirectory(write, value => receivedDirectory = value),
            _ => throw new InvalidOperationException(method),
        });
        var service = CreateService(fake);
        var addons = await service.ListAddonsAsync("room", 0, 25, TestContext.Current.CancellationToken);

        var result = await service.BrowseAsync("room", addons.Addons.Single().Handle, "video", 0, 25, TestContext.Current.CancellationToken);

        Assert.Equal("plugin://plugin.video.synthetic/", receivedDirectory);
        Assert.DoesNotContain("plugin://", JsonSerializer.Serialize(addons), StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("plugin://", JsonSerializer.Serialize(result), StringComparison.OrdinalIgnoreCase);
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
        Assert.Equal(120, Assert.Single(result.Items).ResumePositionSeconds);
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
        var service = new KodiService(registry, handles, Options.Create(options), new SafeText());

        var result = await service.PlayItemAsync("room", handle, TestContext.Current.CancellationToken);

        Assert.Equal(target, openedTarget);
        Assert.True(result.Accepted);
        Assert.True(result.Observed);
        Assert.Equal("playing", result.State);
        Assert.Equal(1, result.PlayerId);
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

    private static KodiService CreateService(IKodiRpcClient client)
    {
        var options = new KodiOptions
        {
            DefaultAlias = "room",
            Handles = new HandleOptions { LifetimeMinutes = 15, Capacity = 100 },
        };
        var registry = new KodiInstanceRegistry([new RegisteredKodiInstance("room", client)], "room");
        var handles = new InMemoryHandleStore(15, 100, TimeProvider.System);
        return new KodiService(registry, handles, Options.Create(options), new SafeText());
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
        return (new KodiService(registry, handles, Options.Create(options), new SafeText()), handles);
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
}
