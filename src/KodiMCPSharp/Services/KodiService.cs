using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using KodiMCPSharp.Configuration;
using KodiMCPSharp.Kodi;
using KodiMCPSharp.Security;
using Microsoft.Extensions.Options;
using ModelContextProtocol;

namespace KodiMCPSharp.Services;

public sealed partial class KodiService
{
    private static readonly string[] ReadTools =
    [
        "kodi_list_instances", "kodi_get_capabilities", "kodi_get_status",
        "kodi_search_library", "kodi_list_genres", "kodi_list_recent", "kodi_list_continue_watching",
        "kodi_browse_tv_show", "kodi_list_favourites", "kodi_list_addons", "kodi_browse",
    ];

    private readonly KodiInstanceRegistry _registry;
    private readonly IHandleStore _handles;
    private readonly KodiOptions _options;
    private readonly SafeText _safeText;

    public KodiService(
        KodiInstanceRegistry registry,
        IHandleStore handles,
        IOptions<KodiOptions> options,
        SafeText safeText)
    {
        _registry = registry;
        _handles = handles;
        _options = options.Value;
        _safeText = safeText;
    }

    public async Task<IReadOnlyList<InstanceSummary>> ListInstancesAsync(CancellationToken cancellationToken)
    {
        var tasks = _registry.Instances.Select(async instance =>
        {
            try
            {
                var result = await instance.Client.CallAsync("JSONRPC.Version", cancellationToken: cancellationToken);
                var version = result.TryGetProperty("version", out var value)
                    ? JoinVersion(value)
                    : null;
                return new InstanceSummary(instance.Alias, "available", version, null);
            }
            catch (KodiRpcException exception)
            {
                return new InstanceSummary(instance.Alias, "unavailable", null, exception.Kind.ToString().ToLowerInvariant());
            }
        });
        return await Task.WhenAll(tasks);
    }

    public CapabilitySummary GetCapabilities() => new(
        ReadOnly: _options.ReadOnly,
        Transport: "kodi-json-rpc-http",
        Instances: _registry.Instances.Select(instance => instance.Alias).Order(StringComparer.OrdinalIgnoreCase).ToArray(),
        ReadTools: ReadTools,
        ControlTools:
        [
            "kodi_play_item", "kodi_player_control", "kodi_seek", "kodi_set_volume",
            "kodi_select_stream", "kodi_set_playback_mode", "kodi_playlist_add",
            "kodi_playlist_remove", "kodi_playlist_clear", "kodi_show_fullscreen_video",
        ],
        ControlGates: new Dictionary<string, bool>
        {
            ["playback"] = !_options.ReadOnly && _options.Controls.AllowPlayback,
            ["playerControl"] = !_options.ReadOnly && _options.Controls.AllowPlayerControl,
            ["seek"] = !_options.ReadOnly && _options.Controls.AllowSeek,
            ["volume"] = !_options.ReadOnly && _options.Controls.AllowVolume,
            ["streamSelection"] = !_options.ReadOnly && _options.Controls.AllowStreamSelection,
            ["playbackModes"] = !_options.ReadOnly && _options.Controls.AllowPlaybackModes,
            ["playlists"] = !_options.ReadOnly && _options.Controls.AllowPlaylists,
            ["fullscreenVideo"] = !_options.ReadOnly && _options.Controls.AllowFullscreenVideo,
            ["navigation"] = false,
            ["addonActivation"] = false,
            ["administration"] = false,
        },
        Handles: new HandlePolicySummary(_options.Handles.LifetimeMinutes, _options.Handles.Capacity, false));

    public async Task<PlaybackResult> PlayItemAsync(string? alias, string handle, CancellationToken cancellationToken)
    {
        if (_options.ReadOnly)
        {
            throw new McpException("Playback is blocked while Kodi:ReadOnly is true.");
        }
        if (!_options.Controls.AllowPlayback)
        {
            throw new McpException("Playback is disabled. Set Kodi:Controls:AllowPlayback=true to enable play-by-handle.");
        }
        var instance = _registry.Resolve(alias);
        var entry = _handles.Resolve(handle, instance.Alias, HandleAction.Play);
        try
        {
            var response = await instance.Client.CallAsync("Player.Open", writer =>
            {
                writer.WritePropertyName("item");
                writer.WriteStartObject();
                writer.WriteString("file", entry.Target);
                writer.WriteEndObject();
            }, cancellationToken);
            var accepted = response.ValueKind == JsonValueKind.String &&
                           response.GetString()?.Equals("OK", StringComparison.OrdinalIgnoreCase) == true;

            for (var attempt = 0; attempt < 8; attempt++)
            {
                var observation = await ObserveActivePlayerAsync(instance, cancellationToken);
                if (observation is not null)
                {
                    return new PlaybackResult(instance.Alias, "play", accepted, true, "observed-playing", observation.Value.PlayerId, observation.Value.Type, observation.Value.State);
                }
                if (attempt < 7) await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
            }

            return new PlaybackResult(instance.Alias, "play", accepted, false, accepted ? "accepted-not-yet-observed" : "indeterminate", null, null, null);
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public async Task<MediaControlResult> PlayerControlAsync(
        string? alias,
        int playerId,
        string action,
        CancellationToken cancellationToken)
    {
        EnsureControl(_options.Controls.AllowPlayerControl, "player control", "AllowPlayerControl");
        ValidatePlayerId(playerId);
        var normalized = action.Trim().ToLowerInvariant();
        if (normalized is not ("pause" or "resume" or "toggle" or "stop" or "next" or "previous"))
            throw new McpException("Action must be pause, resume, toggle, stop, next, or previous.");
        var instance = _registry.Resolve(alias);
        try
        {
            JsonElement response;
            if (normalized == "stop")
            {
                response = await instance.Client.CallAsync("Player.Stop", writer => writer.WriteNumber("playerid", playerId), cancellationToken);
            }
            else if (normalized is "next" or "previous")
            {
                response = await instance.Client.CallAsync("Player.GoTo", writer =>
                {
                    writer.WriteNumber("playerid", playerId);
                    writer.WriteString("to", normalized);
                }, cancellationToken);
            }
            else
            {
                response = await instance.Client.CallAsync("Player.PlayPause", writer =>
                {
                    writer.WriteNumber("playerid", playerId);
                    if (normalized == "toggle") writer.WriteString("play", "toggle");
                    else writer.WriteBoolean("play", normalized == "resume");
                }, cancellationToken);
            }

            var observation = await ObservePlayerAsync(instance, playerId, cancellationToken);
            var observed = normalized switch
            {
                "pause" => observation?.State == "paused",
                "resume" => observation?.State == "playing",
                "stop" => observation is null,
                _ => observation is not null,
            };
            var state = normalized == "stop" && observation is null ? "stopped" : observation?.State;
            return ControlResult(instance.Alias, normalized, IsAccepted(response), observed, playerId, state,
                new Dictionary<string, object?> { ["playerType"] = observation?.Type });
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public async Task<MediaControlResult> SeekAsync(
        string? alias,
        int playerId,
        string mode,
        double value,
        CancellationToken cancellationToken)
    {
        EnsureControl(_options.Controls.AllowSeek, "seek", "AllowSeek");
        ValidatePlayerId(playerId);
        var normalized = mode.Trim().ToLowerInvariant();
        if (normalized is not ("percentage" or "relative_seconds" or "smallforward" or "smallbackward" or "bigforward" or "bigbackward"))
            throw new McpException("Seek mode must be percentage, relative_seconds, smallforward, smallbackward, bigforward, or bigbackward.");
        if (normalized == "percentage" && (value < 0 || value > 100)) throw new McpException("Percentage seek value must be between 0 and 100.");
        if (normalized == "relative_seconds" && (value < -86400 || value > 86400 || value != Math.Truncate(value)))
            throw new McpException("Relative seek value must be a whole number of seconds between -86400 and 86400.");

        var instance = _registry.Resolve(alias);
        try
        {
            var response = await instance.Client.CallAsync("Player.Seek", writer =>
            {
                writer.WriteNumber("playerid", playerId);
                writer.WritePropertyName("value");
                writer.WriteStartObject();
                if (normalized == "percentage") writer.WriteNumber("percentage", value);
                else if (normalized == "relative_seconds") writer.WriteNumber("seconds", (int)value);
                else writer.WriteString("step", normalized);
                writer.WriteEndObject();
            }, cancellationToken);
            var observation = await ObservePlayerAsync(instance, playerId, cancellationToken);
            return ControlResult(instance.Alias, $"seek:{normalized}", IsAccepted(response), observation is not null, playerId, observation?.State,
                new Dictionary<string, object?>
                {
                    ["requestedValue"] = normalized is "percentage" or "relative_seconds" ? value : null,
                    ["percentage"] = observation?.Percentage,
                });
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public async Task<MediaControlResult> SetVolumeAsync(
        string? alias,
        int? volume,
        bool? muted,
        CancellationToken cancellationToken)
    {
        EnsureControl(_options.Controls.AllowVolume, "volume control", "AllowVolume");
        if (volume is null && muted is null) throw new McpException("Provide volume, muted, or both.");
        if (volume is < 0 or > 100) throw new McpException("Volume must be between 0 and 100.");
        var instance = _registry.Resolve(alias);
        try
        {
            if (volume is not null)
            {
                _ = await instance.Client.CallAsync("Application.SetVolume", writer => writer.WriteNumber("volume", volume.Value), cancellationToken);
            }
            if (muted is not null)
            {
                _ = await instance.Client.CallAsync("Application.SetMute", writer => writer.WriteBoolean("mute", muted.Value), cancellationToken);
            }
            var observed = await instance.Client.CallAsync("Application.GetProperties", writer =>
                WriteStringArray(writer, "properties", ["volume", "muted"]), cancellationToken);
            var observedVolume = GetInt(observed, "volume");
            var observedMuted = GetBool(observed, "muted");
            var matches = (volume is null || volume == observedVolume) && (muted is null || muted == observedMuted);
            return ControlResult(instance.Alias, "set-volume", true, matches, null, null,
                new Dictionary<string, object?> { ["volume"] = observedVolume, ["muted"] = observedMuted });
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public async Task<MediaControlResult> SelectStreamAsync(
        string? alias,
        int playerId,
        string kind,
        int index,
        bool enableSubtitles,
        CancellationToken cancellationToken)
    {
        EnsureControl(_options.Controls.AllowStreamSelection, "stream selection", "AllowStreamSelection");
        ValidatePlayerId(playerId);
        var normalized = kind.Trim().ToLowerInvariant();
        if (normalized is not ("audio" or "video" or "subtitle")) throw new McpException("Stream kind must be audio, video, or subtitle.");
        if (index < 0 && !(normalized == "subtitle" && index == -1)) throw new McpException("Stream index must be zero or greater; use -1 only to turn subtitles off.");
        var instance = _registry.Resolve(alias);
        try
        {
            var method = normalized switch
            {
                "audio" => "Player.SetAudioStream",
                "video" => "Player.SetVideoStream",
                _ => "Player.SetSubtitle",
            };
            var response = await instance.Client.CallAsync(method, writer =>
            {
                writer.WriteNumber("playerid", playerId);
                if (normalized == "subtitle")
                {
                    if (index == -1) writer.WriteString("subtitle", "off");
                    else writer.WriteNumber("subtitle", index);
                    writer.WriteBoolean("enable", index >= 0 && enableSubtitles);
                }
                else
                {
                    writer.WriteNumber("stream", index);
                }
            }, cancellationToken);
            var properties = await GetPlayerPropertiesAsync(instance, playerId,
                ["currentaudiostream", "currentvideostream", "currentsubtitle", "subtitleenabled", "speed"], cancellationToken);
            var observedIndex = normalized switch
            {
                "audio" => GetNestedInt(properties, "currentaudiostream", "index"),
                "video" => GetNestedInt(properties, "currentvideostream", "index"),
                _ => GetNestedInt(properties, "currentsubtitle", "index"),
            };
            var subtitlesEnabled = GetBool(properties, "subtitleenabled");
            var matches = normalized == "subtitle" && index == -1 ? subtitlesEnabled == false : observedIndex == index;
            return ControlResult(instance.Alias, $"select-{normalized}-stream", IsAccepted(response), matches, playerId,
                (GetDouble(properties, "speed") ?? 0) > 0 ? "playing" : "paused",
                new Dictionary<string, object?> { ["streamIndex"] = observedIndex, ["subtitlesEnabled"] = subtitlesEnabled });
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public async Task<MediaControlResult> SetPlaybackModeAsync(
        string? alias,
        int playerId,
        string? repeat,
        bool? shuffled,
        CancellationToken cancellationToken)
    {
        EnsureControl(_options.Controls.AllowPlaybackModes, "playback mode control", "AllowPlaybackModes");
        ValidatePlayerId(playerId);
        if (repeat is null && shuffled is null) throw new McpException("Provide repeat, shuffled, or both.");
        var normalizedRepeat = repeat?.Trim().ToLowerInvariant();
        if (normalizedRepeat is not null && normalizedRepeat is not ("off" or "one" or "all")) throw new McpException("Repeat must be off, one, or all.");
        var instance = _registry.Resolve(alias);
        try
        {
            if (normalizedRepeat is not null)
            {
                _ = await instance.Client.CallAsync("Player.SetRepeat", writer =>
                {
                    writer.WriteNumber("playerid", playerId);
                    writer.WriteString("repeat", normalizedRepeat);
                }, cancellationToken);
            }
            if (shuffled is not null)
            {
                _ = await instance.Client.CallAsync("Player.SetShuffle", writer =>
                {
                    writer.WriteNumber("playerid", playerId);
                    writer.WriteBoolean("shuffle", shuffled.Value);
                }, cancellationToken);
            }
            var properties = await GetPlayerPropertiesAsync(instance, playerId, ["repeat", "shuffled", "speed"], cancellationToken);
            var observedRepeat = GetString(properties, "repeat");
            var observedShuffle = GetBool(properties, "shuffled");
            var matches = (normalizedRepeat is null || normalizedRepeat == observedRepeat) && (shuffled is null || shuffled == observedShuffle);
            return ControlResult(instance.Alias, "set-playback-mode", true, matches, playerId,
                (GetDouble(properties, "speed") ?? 0) > 0 ? "playing" : "paused",
                new Dictionary<string, object?> { ["repeat"] = observedRepeat, ["shuffled"] = observedShuffle });
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public async Task<MediaControlResult> PlaylistAddAsync(
        string? alias,
        string handle,
        string media,
        CancellationToken cancellationToken)
    {
        EnsureControl(_options.Controls.AllowPlaylists, "playlist control", "AllowPlaylists");
        var instance = _registry.Resolve(alias);
        var entry = _handles.Resolve(handle, instance.Alias, HandleAction.Play);
        try
        {
            var playlistId = await ResolvePlaylistIdAsync(instance, media, cancellationToken);
            var beforeSize = await GetPlaylistSizeAsync(instance, playlistId, cancellationToken);
            var response = await instance.Client.CallAsync("Playlist.Add", writer =>
            {
                writer.WriteNumber("playlistid", playlistId);
                writer.WritePropertyName("item"); writer.WriteStartObject(); writer.WriteString("file", entry.Target); writer.WriteEndObject();
            }, cancellationToken);
            var size = await GetPlaylistSizeAsync(instance, playlistId, cancellationToken);
            return ControlResult(instance.Alias, "playlist-add", IsAccepted(response), size == beforeSize + 1, null, null,
                new Dictionary<string, object?> { ["playlistId"] = playlistId, ["media"] = NormalizePlaylistMedia(media), ["size"] = size });
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public Task<MediaControlResult> PlaylistRemoveAsync(string? alias, string media, int position, CancellationToken cancellationToken) =>
        MutatePlaylistAsync(alias, media, "remove", position, cancellationToken);

    public Task<MediaControlResult> PlaylistClearAsync(string? alias, string media, CancellationToken cancellationToken) =>
        MutatePlaylistAsync(alias, media, "clear", null, cancellationToken);

    public async Task<MediaControlResult> ShowFullscreenVideoAsync(string? alias, CancellationToken cancellationToken)
    {
        EnsureControl(_options.Controls.AllowFullscreenVideo, "full-screen video display", "AllowFullscreenVideo");
        var instance = _registry.Resolve(alias);
        try
        {
            var players = await instance.Client.CallAsync("Player.GetActivePlayers", cancellationToken: cancellationToken);
            var videoPlayer = players.ValueKind == JsonValueKind.Array
                ? players.EnumerateArray().FirstOrDefault(player => GetString(player, "type") == "video")
                : default;
            var playerId = videoPlayer.ValueKind == JsonValueKind.Object ? GetInt(videoPlayer, "playerid") : null;
            if (playerId is null) throw new McpException("Kodi does not currently have an active video player.");

            var response = await instance.Client.CallAsync("GUI.ActivateWindow", writer => writer.WriteString("window", "fullscreenvideo"), cancellationToken);
            var properties = await instance.Client.CallAsync("GUI.GetProperties", writer =>
                WriteStringArray(writer, "properties", ["currentwindow", "fullscreen"]), cancellationToken);
            var currentWindow = properties.TryGetProperty("currentwindow", out var window)
                ? _safeText.Clean(GetString(window, "label"), 100)
                : null;
            var normalizedWindow = currentWindow?.Replace(" ", string.Empty, StringComparison.Ordinal);
            var observed = normalizedWindow is not null &&
                           normalizedWindow.Contains("fullscreen", StringComparison.OrdinalIgnoreCase) &&
                           normalizedWindow.Contains("video", StringComparison.OrdinalIgnoreCase);
            return ControlResult(instance.Alias, "show-fullscreen-video", IsAccepted(response), observed, playerId, "playing",
                new Dictionary<string, object?>
                {
                    ["currentWindow"] = currentWindow,
                    ["applicationFullscreen"] = GetBool(properties, "fullscreen"),
                });
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public async Task<KodiStatusSummary> GetStatusAsync(string? alias, CancellationToken cancellationToken)
    {
        var instance = _registry.Resolve(alias);
        try
        {
            var applicationTask = instance.Client.CallAsync("Application.GetProperties", writer =>
                WriteStringArray(writer, "properties", ["volume", "muted", "name", "version"]), cancellationToken);
            var playersTask = instance.Client.CallAsync("Player.GetActivePlayers", cancellationToken: cancellationToken);
            await Task.WhenAll(applicationTask, playersTask);

            var application = await applicationTask;
            var activePlayers = await playersTask;
            var playerTasks = activePlayers.ValueKind == JsonValueKind.Array
                ? activePlayers.EnumerateArray().Select(player => GetPlayerAsync(instance, player, cancellationToken)).ToArray()
                : [];
            var players = await Task.WhenAll(playerTasks);

            return new KodiStatusSummary(
                instance.Alias,
                GetInt(application, "volume"),
                GetBool(application, "muted"),
                _safeText.Clean(GetString(application, "name"), 100),
                application.TryGetProperty("version", out var version) ? JoinVersion(version) : null,
                players);
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public async Task<PageSummary> SearchLibraryAsync(
        string? alias,
        string? query,
        string domain,
        int? year,
        string? genre,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var descriptor = SearchDescriptor.Resolve(domain);
        var normalizedQuery = NormalizeSearchText(query, "Title query", 200);
        var normalizedGenre = NormalizeSearchText(genre, "Genre", 100);
        if (year is < 1 or > 9999) throw new McpException("Year must be between 1 and 9999.");
        if (!descriptor.SupportsYearAndGenre && (year is not null || normalizedGenre is not null))
            throw new McpException("Year and genre filters are supported only for movies and tvshows.");
        if (normalizedQuery is null && year is null && normalizedGenre is null)
            throw new McpException("Provide at least one title query, year, or genre filter.");

        var filters = new List<SearchFilterRule>(3);
        if (normalizedQuery is not null) filters.Add(new(descriptor.FilterField, "contains", normalizedQuery));
        if (year is not null) filters.Add(new("year", "is", year.Value.ToString(CultureInfo.InvariantCulture)));
        if (normalizedGenre is not null) filters.Add(new("genre", "contains", normalizedGenre));
        var instance = _registry.Resolve(alias);
        var (start, end) = Bounds(page, pageSize);
        try
        {
            var result = await instance.Client.CallAsync(descriptor.Method, writer =>
            {
                WriteStringArray(writer, "properties", descriptor.Properties);
                writer.WritePropertyName("limits");
                writer.WriteStartObject(); writer.WriteNumber("start", start); writer.WriteNumber("end", end); writer.WriteEndObject();
                WriteSearchFilter(writer, filters);
                WriteSort(writer, descriptor.SortMethod);
            }, cancellationToken);
            return ParseItemPage(instance.Alias, result, descriptor.ResultProperty, descriptor.Media, start);
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public async Task<GenrePageSummary> ListGenresAsync(
        string? alias,
        string domain,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var descriptor = GenreDescriptor.Resolve(domain);
        var instance = _registry.Resolve(alias);
        var (start, end) = Bounds(page, pageSize);
        try
        {
            var result = await instance.Client.CallAsync(descriptor.Method, writer =>
            {
                if (descriptor.VideoType is not null) writer.WriteString("type", descriptor.VideoType);
                writer.WritePropertyName("limits");
                writer.WriteStartObject(); writer.WriteNumber("start", start); writer.WriteNumber("end", end); writer.WriteEndObject();
                WriteSort(writer);
            }, cancellationToken);
            var genres = result.TryGetProperty("genres", out var values) && values.ValueKind == JsonValueKind.Array
                ? values.EnumerateArray()
                    .Select(value => new GenreSummary(_safeText.Clean(GetString(value, "label") ?? GetString(value, "title"), 100)))
                    .Where(value => value.Name is not null)
                    .ToArray()
                : [];
            var limits = GetLimits(result, start, genres.Length);
            return new GenrePageSummary(instance.Alias, descriptor.Domain, limits.Start, limits.End, limits.Total, genres);
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public Task<PageSummary> ListRecentAsync(
        string? alias,
        string domain,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        ListMediaViewAsync(alias, RecentDescriptor.Resolve(domain), false, page, pageSize, cancellationToken);

    public Task<PageSummary> ListContinueWatchingAsync(
        string? alias,
        string domain,
        int page,
        int pageSize,
        CancellationToken cancellationToken) =>
        ListMediaViewAsync(alias, ContinueDescriptor.Resolve(domain), true, page, pageSize, cancellationToken);

    public async Task<PageSummary> BrowseTvShowAsync(
        string? alias,
        string handle,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var instance = _registry.Resolve(alias);
        var entry = _handles.Resolve(handle, instance.Alias, HandleAction.LibraryBrowse);
        var (start, end) = Bounds(page, pageSize);
        try
        {
            if (entry.Kind == "tvshow" && int.TryParse(entry.Target, NumberStyles.None, CultureInfo.InvariantCulture, out var tvShowId))
            {
                var result = await instance.Client.CallAsync("VideoLibrary.GetSeasons", writer =>
                {
                    writer.WriteNumber("tvshowid", tvShowId);
                    WriteStringArray(writer, "properties", ["title", "season", "showtitle", "playcount", "episode", "watchedepisodes", "tvshowid"]);
                    WriteLimits(writer, start, end);
                    WriteSort(writer, "season");
                }, cancellationToken);
                return ParseItemPage(instance.Alias, result, "seasons", "video", start);
            }

            if (entry.Kind == "tvseason" && TryParseSeasonTarget(entry.Target, out tvShowId, out var season))
            {
                var result = await instance.Client.CallAsync("VideoLibrary.GetEpisodes", writer =>
                {
                    writer.WriteNumber("tvshowid", tvShowId);
                    writer.WriteNumber("season", season);
                    WriteStringArray(writer, "properties", ["title", "showtitle", "season", "episode", "runtime", "playcount", "resume", "file"]);
                    WriteLimits(writer, start, end);
                    WriteSort(writer, "episode");
                }, cancellationToken);
                return ParseItemPage(instance.Alias, result, "episodes", "video", start);
            }

            throw new McpException("The handle is not a valid TV show or season handle.");
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    private async Task<PageSummary> ListMediaViewAsync(
        string? alias,
        MediaViewDescriptor descriptor,
        bool inProgressFilter,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var instance = _registry.Resolve(alias);
        var (start, end) = Bounds(page, pageSize);
        try
        {
            var result = await instance.Client.CallAsync(descriptor.Method, writer =>
            {
                WriteStringArray(writer, "properties", descriptor.Properties);
                WriteLimits(writer, start, end);
                if (inProgressFilter && descriptor.RequiresInProgressFilter)
                    WriteSearchFilter(writer, [new SearchFilterRule("inprogress", "true", string.Empty)]);
                if (inProgressFilter) WriteSort(writer, "lastplayed", "descending");
            }, cancellationToken);
            return ParseItemPage(instance.Alias, result, descriptor.ResultProperty, descriptor.Media, start);
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public async Task<PageSummary> ListFavouritesAsync(string? alias, int page, int pageSize, CancellationToken cancellationToken)
    {
        var instance = _registry.Resolve(alias);
        var (start, end) = Bounds(page, pageSize);
        try
        {
            var result = await instance.Client.CallAsync("Favourites.GetFavourites", writer =>
            {
                WriteStringArray(writer, "properties", ["path", "window", "windowparameter", "thumbnail"]);
            }, cancellationToken);
            return ParseItemPage(instance.Alias, result, "favourites", "files", 0, start, end - start);
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public async Task<AddonPageSummary> ListAddonsAsync(string? alias, int page, int pageSize, CancellationToken cancellationToken)
    {
        var instance = _registry.Resolve(alias);
        var (start, end) = Bounds(page, pageSize);
        try
        {
            var result = await instance.Client.CallAsync("Addons.GetAddons", writer =>
            {
                writer.WriteBoolean("enabled", true);
                WriteStringArray(writer, "properties", ["name", "version", "summary", "description", "enabled", "type"]);
                writer.WritePropertyName("limits");
                writer.WriteStartObject(); writer.WriteNumber("start", start); writer.WriteNumber("end", end); writer.WriteEndObject();
            }, cancellationToken);

            var addons = new List<AddonSummary>();
            if (result.TryGetProperty("addons", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var addon in values.EnumerateArray())
                {
                    var id = GetString(addon, "addonid");
                    var enabled = GetBool(addon, "enabled") ?? true;
                    var browsable = enabled && id is not null && AddonId().IsMatch(id);
                    var handle = browsable
                        ? _handles.Create(instance.Alias, $"plugin://{id}/", "files", "addon-folder", HandleAction.Browse)
                        : null;
                    addons.Add(new AddonSummary(
                        _safeText.Clean(GetString(addon, "name") ?? GetString(addon, "label")),
                        _safeText.Clean(GetString(addon, "type"), 100),
                        _safeText.Clean(GetString(addon, "version"), 50),
                        _safeText.Clean(GetString(addon, "summary") ?? GetString(addon, "description")),
                        enabled,
                        browsable,
                        handle));
                }
            }
            var limits = GetLimits(result, start, addons.Count);
            return new AddonPageSummary(instance.Alias, limits.Start, limits.End, limits.Total, addons);
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public async Task<PageSummary> BrowseAsync(
        string? alias,
        string? handle,
        string root,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var instance = _registry.Resolve(alias);
        var (start, end) = Bounds(page, pageSize);
        var normalizedRoot = NormalizeMedia(root);
        try
        {
            if (string.IsNullOrWhiteSpace(handle))
            {
                var sources = await instance.Client.CallAsync("Files.GetSources", writer => writer.WriteString("media", normalizedRoot), cancellationToken);
                return ParseItemPage(instance.Alias, sources, "sources", normalizedRoot, 0, start, end - start);
            }

            var entry = _handles.Resolve(handle, instance.Alias, HandleAction.Browse);
            var result = await instance.Client.CallAsync("Files.GetDirectory", writer =>
            {
                writer.WriteString("directory", entry.Target);
                writer.WriteString("media", entry.Media);
                WriteStringArray(writer, "properties", ["title", "artist", "album", "year", "duration", "playcount", "resume"]);
                writer.WritePropertyName("limits");
                writer.WriteStartObject(); writer.WriteNumber("start", start); writer.WriteNumber("end", end); writer.WriteEndObject();
                WriteSort(writer);
            }, cancellationToken);
            return ParseItemPage(instance.Alias, result, "files", entry.Media, start);
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    private async Task<PlayerSummary> GetPlayerAsync(RegisteredKodiInstance instance, JsonElement player, CancellationToken cancellationToken)
    {
        var playerId = GetInt(player, "playerid") ?? throw new KodiRpcException(KodiFailureKind.Protocol, "Kodi returned an active player without an identifier.");
        var type = _safeText.Clean(GetString(player, "type"), 30) ?? "unknown";
        var propertiesTask = instance.Client.CallAsync("Player.GetProperties", writer =>
        {
            writer.WriteNumber("playerid", playerId);
            WriteStringArray(writer, "properties",
            [
                "speed", "percentage", "time", "totaltime", "playlistid", "position", "repeat", "shuffled", "canseek",
                "currentaudiostream", "audiostreams", "currentvideostream", "videostreams",
                "subtitleenabled", "currentsubtitle", "subtitles",
            ]);
        }, cancellationToken);
        var itemTask = instance.Client.CallAsync("Player.GetItem", writer =>
        {
            writer.WriteNumber("playerid", playerId);
            WriteStringArray(writer, "properties", ["title", "artist", "album", "year", "duration", "runtime", "playcount", "resume"]);
        }, cancellationToken);
        await Task.WhenAll(propertiesTask, itemTask);
        var properties = await propertiesTask;
        var itemResult = await itemTask;
        var speed = GetDouble(properties, "speed");
        return new PlayerSummary(
            playerId,
            type,
            speed switch { > 0 => "playing", 0 => "paused", _ => "unknown" },
            speed,
            GetDouble(properties, "percentage"),
            ParseTime(properties, "time"),
            ParseTime(properties, "totaltime"),
            GetInt(properties, "playlistid"),
            GetInt(properties, "position"),
            _safeText.Clean(GetString(properties, "repeat"), 20),
            GetBool(properties, "shuffled"),
            GetBool(properties, "canseek"),
            GetNestedInt(properties, "currentaudiostream", "index"),
            GetNestedInt(properties, "currentvideostream", "index"),
            GetNestedInt(properties, "currentsubtitle", "index"),
            GetBool(properties, "subtitleenabled"),
            ParseStreams(properties, "audiostreams"),
            ParseStreams(properties, "videostreams"),
            ParseStreams(properties, "subtitles"),
            itemResult.TryGetProperty("item", out var item) ? ParseItem(instance.Alias, item, type) : null);
    }

    private static async Task<(int PlayerId, string Type, string State)?> ObserveActivePlayerAsync(
        RegisteredKodiInstance instance,
        CancellationToken cancellationToken)
    {
        var players = await instance.Client.CallAsync("Player.GetActivePlayers", cancellationToken: cancellationToken);
        if (players.ValueKind != JsonValueKind.Array) return null;
        var player = players.EnumerateArray().FirstOrDefault();
        if (player.ValueKind != JsonValueKind.Object) return null;
        var playerId = GetInt(player, "playerid");
        if (playerId is null) return null;
        var type = GetString(player, "type") ?? "unknown";
        var properties = await instance.Client.CallAsync("Player.GetProperties", writer =>
        {
            writer.WriteNumber("playerid", playerId.Value);
            WriteStringArray(writer, "properties", ["speed"]);
        }, cancellationToken);
        var speed = GetDouble(properties, "speed");
        var state = speed switch { > 0 => "playing", 0 => "paused", _ => "active" };
        return (playerId.Value, type, state);
    }

    private static async Task<PlayerObservation?> ObservePlayerAsync(
        RegisteredKodiInstance instance,
        int playerId,
        CancellationToken cancellationToken)
    {
        var players = await instance.Client.CallAsync("Player.GetActivePlayers", cancellationToken: cancellationToken);
        if (players.ValueKind != JsonValueKind.Array) return null;
        var player = players.EnumerateArray().FirstOrDefault(value => GetInt(value, "playerid") == playerId);
        if (player.ValueKind != JsonValueKind.Object) return null;
        var properties = await GetPlayerPropertiesAsync(instance, playerId, ["speed", "percentage"], cancellationToken);
        var speed = GetDouble(properties, "speed");
        return new PlayerObservation(
            GetString(player, "type") ?? "unknown",
            speed switch { > 0 => "playing", 0 => "paused", _ => "active" },
            GetDouble(properties, "percentage"));
    }

    private static Task<JsonElement> GetPlayerPropertiesAsync(
        RegisteredKodiInstance instance,
        int playerId,
        IEnumerable<string> properties,
        CancellationToken cancellationToken) =>
        instance.Client.CallAsync("Player.GetProperties", writer =>
        {
            writer.WriteNumber("playerid", playerId);
            WriteStringArray(writer, "properties", properties);
        }, cancellationToken);

    private async Task<MediaControlResult> MutatePlaylistAsync(
        string? alias,
        string media,
        string action,
        int? position,
        CancellationToken cancellationToken)
    {
        EnsureControl(_options.Controls.AllowPlaylists, "playlist control", "AllowPlaylists");
        if (action == "remove" && position is < 0) throw new McpException("Playlist position must be zero or greater.");
        var instance = _registry.Resolve(alias);
        try
        {
            var normalizedMedia = NormalizePlaylistMedia(media);
            var playlistId = await ResolvePlaylistIdAsync(instance, normalizedMedia, cancellationToken);
            var beforeSize = await GetPlaylistSizeAsync(instance, playlistId, cancellationToken);
            if (action == "remove" && position is { } requestedPosition && requestedPosition >= beforeSize)
                throw new McpException($"Playlist position must be less than the current size ({beforeSize}).");
            var response = await instance.Client.CallAsync(action == "clear" ? "Playlist.Clear" : "Playlist.Remove", writer =>
            {
                writer.WriteNumber("playlistid", playlistId);
                if (position is not null) writer.WriteNumber("position", position.Value);
            }, cancellationToken);
            var afterSize = await GetPlaylistSizeAsync(instance, playlistId, cancellationToken);
            var expectedSize = action == "clear" ? 0 : Math.Max(0, beforeSize - 1);
            return ControlResult(instance.Alias, $"playlist-{action}", IsAccepted(response), afterSize == expectedSize, null, null,
                new Dictionary<string, object?>
                {
                    ["playlistId"] = playlistId,
                    ["media"] = normalizedMedia,
                    ["size"] = afterSize,
                    ["removedPosition"] = position,
                });
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    private static async Task<int> ResolvePlaylistIdAsync(
        RegisteredKodiInstance instance,
        string media,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizePlaylistMedia(media);
        var playlists = await instance.Client.CallAsync("Playlist.GetPlaylists", cancellationToken: cancellationToken);
        if (playlists.ValueKind == JsonValueKind.Array)
        {
            foreach (var playlist in playlists.EnumerateArray())
            {
                if (GetString(playlist, "type") == normalized && GetInt(playlist, "playlistid") is { } id) return id;
            }
        }
        throw new McpException($"Kodi did not report a {normalized} playlist.");
    }

    private static async Task<int> GetPlaylistSizeAsync(
        RegisteredKodiInstance instance,
        int playlistId,
        CancellationToken cancellationToken)
    {
        var properties = await instance.Client.CallAsync("Playlist.GetProperties", writer =>
        {
            writer.WriteNumber("playlistid", playlistId);
            WriteStringArray(writer, "properties", ["size"]);
        }, cancellationToken);
        return GetInt(properties, "size") ?? 0;
    }

    private void EnsureControl(bool enabled, string operation, string gate)
    {
        if (_options.ReadOnly) throw new McpException($"{operation} is blocked while Kodi:ReadOnly is true.");
        if (!enabled) throw new McpException($"{operation} is disabled. Set Kodi:Controls:{gate}=true to enable it.");
    }

    private static void ValidatePlayerId(int playerId)
    {
        if (playerId is < 0 or > 2) throw new McpException("Player ID must be 0, 1, or 2 and should come from kodi_get_status.");
    }

    private static string NormalizePlaylistMedia(string media) => media.Trim().ToLowerInvariant() switch
    {
        "audio" => "audio",
        "video" => "video",
        _ => throw new McpException("Playlist media must be audio or video."),
    };

    private static bool IsAccepted(JsonElement response) =>
        response.ValueKind != JsonValueKind.String || response.GetString()?.Equals("OK", StringComparison.OrdinalIgnoreCase) == true;

    private static MediaControlResult ControlResult(
        string alias,
        string action,
        bool accepted,
        bool observed,
        int? playerId,
        string? state,
        IReadOnlyDictionary<string, object?> details) =>
        new(alias, action, accepted, observed, observed ? "observed-complete" : accepted ? "accepted-not-yet-observed" : "indeterminate", playerId, state, details);

    private sealed record PlayerObservation(string Type, string State, double? Percentage);

    private PageSummary ParseItemPage(
        string alias,
        JsonElement result,
        string property,
        string media,
        int fallbackStart,
        int? clientSkip = null,
        int? clientTake = null)
    {
        var items = new List<MediaItemSummary>();
        var sourceCount = 0;
        if (result.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array)
        {
            sourceCount = values.GetArrayLength();
            var selected = values.EnumerateArray().Skip(clientSkip ?? 0);
            if (clientTake is not null) selected = selected.Take(clientTake.Value);
            foreach (var value in selected) items.Add(ParseItem(alias, value, media, property));
        }
        var limits = GetLimits(result, fallbackStart, sourceCount);
        if (clientSkip is not null)
        {
            limits = (clientSkip.Value, clientSkip.Value + items.Count, limits.Total);
        }
        return new PageSummary(alias, limits.Start, limits.End, limits.Total, items);
    }

    private MediaItemSummary ParseItem(string alias, JsonElement item, string media, string? context = null)
    {
        var target = GetString(item, "file") ?? GetString(item, "path");
        var fileType = GetString(item, "filetype");
        var type = GetString(item, "type") ?? GetString(item, "mediatype") ?? fileType ?? context switch
        {
            "tvshows" => "tvshow",
            "seasons" => "season",
            "episodes" => "episode",
            "movies" => "movie",
            "songs" => "song",
            "albums" => "album",
            _ => null,
        };
        var isFolder = context is "sources" or "tvshows" or "seasons" || fileType == "directory" || (target?.EndsWith('/') ?? false);
        var isPlayable = context is "movies" or "episodes" or "songs" || fileType == "file" || type is "movie" or "episode" or "song" or "musicvideo";
        string? handle = null;
        if (context == "tvshows" && GetInt(item, "tvshowid") is { } tvShowId)
        {
            handle = _handles.Create(alias, tvShowId.ToString(CultureInfo.InvariantCulture), media, "tvshow", HandleAction.LibraryBrowse);
        }
        else if (context == "seasons" && GetInt(item, "tvshowid") is { } seasonTvShowId && GetInt(item, "season") is { } seasonNumber)
        {
            handle = _handles.Create(alias, $"{seasonTvShowId.ToString(CultureInfo.InvariantCulture)}:{seasonNumber.ToString(CultureInfo.InvariantCulture)}", media, "tvseason", HandleAction.LibraryBrowse);
        }
        else if (!string.IsNullOrWhiteSpace(target))
        {
            var actions = (isFolder ? HandleAction.Browse : HandleAction.None) | (isPlayable ? HandleAction.Play : HandleAction.None);
            if (actions != HandleAction.None) handle = _handles.Create(alias, target, media, type ?? "item", actions);
        }

        var artists = item.TryGetProperty("artist", out var artist) && artist.ValueKind == JsonValueKind.Array
            ? artist.EnumerateArray().Select(value => _safeText.Clean(value.GetString(), 100)).Where(value => value is not null).Cast<string>().ToArray()
            : [];
        var genres = item.TryGetProperty("genre", out var genre) && genre.ValueKind == JsonValueKind.Array
            ? genre.EnumerateArray().Select(value => _safeText.Clean(value.GetString(), 100)).Where(value => value is not null).Cast<string>().ToArray()
            : [];
        var hasArtwork = (item.TryGetProperty("thumbnail", out var thumbnail) && !string.IsNullOrEmpty(thumbnail.GetString())) ||
                         (item.TryGetProperty("art", out var art) && art.ValueKind == JsonValueKind.Object && art.EnumerateObject().Any());
        double? resumePosition = null;
        double? resumeTotal = null;
        if (item.TryGetProperty("resume", out var resume) && resume.ValueKind == JsonValueKind.Object)
        {
            resumePosition = GetDouble(resume, "position");
            resumeTotal = GetDouble(resume, "total");
        }
        return new MediaItemSummary(
            _safeText.Clean(GetString(item, "label") ?? GetString(item, "title")),
            _safeText.Clean(type, 80),
            GetInt(item, "year"),
            context is "episodes" or "seasons" ? GetInt(item, "season") : null,
            context == "episodes" ? GetInt(item, "episode") : null,
            context is "tvshows" or "seasons" ? GetInt(item, "episode") : null,
            context is "tvshows" or "seasons" ? GetInt(item, "watchedepisodes") : null,
            artists,
            _safeText.Clean(GetString(item, "album")),
            genres,
            GetInt(item, "duration") ?? GetInt(item, "runtime"),
            GetInt(item, "playcount"),
            resumePosition,
            resumeTotal,
            hasArtwork,
            isFolder,
            isPlayable,
            handle);
    }

    private (int Start, int End) Bounds(int page, int pageSize)
    {
        if (page < 0) throw new McpException("Page must be zero or greater.");
        var size = pageSize <= 0 ? Math.Min(25, _options.MaximumPageSize) : Math.Min(pageSize, _options.MaximumPageSize);
        try
        {
            var start = checked(page * size);
            return (start, checked(start + size));
        }
        catch (OverflowException)
        {
            throw new McpException("Requested page is too large.");
        }
    }

    private static string NormalizeMedia(string root) => root.Trim().ToLowerInvariant() switch
    {
        "video" => "video",
        "music" => "music",
        "pictures" => "pictures",
        "files" => "files",
        "programs" => "programs",
        _ => throw new McpException("Root must be video, music, pictures, files, or programs."),
    };

    private static void WriteStringArray(Utf8JsonWriter writer, string property, IEnumerable<string> values)
    {
        writer.WritePropertyName(property);
        writer.WriteStartArray();
        foreach (var value in values) writer.WriteStringValue(value);
        writer.WriteEndArray();
    }

    private static void WriteLimits(Utf8JsonWriter writer, int start, int end)
    {
        writer.WritePropertyName("limits");
        writer.WriteStartObject();
        writer.WriteNumber("start", start);
        writer.WriteNumber("end", end);
        writer.WriteEndObject();
    }

    private static void WriteSort(Utf8JsonWriter writer, string method = "label", string order = "ascending")
    {
        writer.WritePropertyName("sort");
        writer.WriteStartObject(); writer.WriteString("order", order); writer.WriteString("method", method); writer.WriteBoolean("ignorearticle", true); writer.WriteEndObject();
    }

    private static void WriteSearchFilter(Utf8JsonWriter writer, IReadOnlyList<SearchFilterRule> filters)
    {
        writer.WritePropertyName("filter");
        if (filters.Count == 1)
        {
            WriteSearchFilterRule(writer, filters[0]);
            return;
        }

        writer.WriteStartObject();
        writer.WritePropertyName("and");
        writer.WriteStartArray();
        foreach (var filter in filters) WriteSearchFilterRule(writer, filter);
        writer.WriteEndArray();
        writer.WriteEndObject();
    }

    private static void WriteSearchFilterRule(Utf8JsonWriter writer, SearchFilterRule filter)
    {
        writer.WriteStartObject();
        writer.WriteString("field", filter.Field);
        writer.WriteString("operator", filter.Operator);
        writer.WriteString("value", filter.Value);
        writer.WriteEndObject();
    }

    private static string? NormalizeSearchText(string? value, string label, int maximumLength)
    {
        if (value is null) return null;
        var normalized = value.Trim();
        if (normalized.Length == 0 || normalized.Length > maximumLength || normalized.Any(char.IsControl))
            throw new McpException($"{label} must contain 1 to {maximumLength} printable characters when provided.");
        return normalized;
    }

    private static bool TryParseSeasonTarget(string target, out int tvShowId, out int season)
    {
        tvShowId = 0;
        season = 0;
        var separator = target.IndexOf(':', StringComparison.Ordinal);
        return separator > 0 &&
               int.TryParse(target.AsSpan(0, separator), NumberStyles.None, CultureInfo.InvariantCulture, out tvShowId) &&
               int.TryParse(target.AsSpan(separator + 1), NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out season) &&
               tvShowId >= 0 && season >= 0;
    }

    private static (int Start, int End, int Total) GetLimits(JsonElement result, int fallbackStart, int count)
    {
        if (result.TryGetProperty("limits", out var limits))
        {
            return (GetInt(limits, "start") ?? fallbackStart, GetInt(limits, "end") ?? fallbackStart + count, GetInt(limits, "total") ?? fallbackStart + count);
        }
        return (fallbackStart, fallbackStart + count, fallbackStart + count);
    }

    private static TimeSummary? ParseTime(JsonElement source, string property)
    {
        if (!source.TryGetProperty(property, out var value) || value.ValueKind != JsonValueKind.Object) return null;
        return new TimeSummary(GetInt(value, "hours") ?? 0, GetInt(value, "minutes") ?? 0, GetInt(value, "seconds") ?? 0, GetInt(value, "milliseconds") ?? 0);
    }

    private StreamSummary[] ParseStreams(JsonElement source, string property)
    {
        if (!source.TryGetProperty(property, out var values) || values.ValueKind != JsonValueKind.Array) return [];
        return values.EnumerateArray()
            .Select(value => new StreamSummary(
                GetInt(value, "index") ?? -1,
                _safeText.Clean(GetString(value, "name"), 100),
                _safeText.Clean(GetString(value, "language"), 30),
                _safeText.Clean(GetString(value, "codec"), 30),
                GetInt(value, "channels"),
                GetBool(value, "isdefault"),
                GetBool(value, "isforced"),
                GetBool(value, "isimpaired")))
            .Where(stream => stream.Index >= 0)
            .ToArray();
    }

    private static string? JoinVersion(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.String) return value.GetString();
        var major = GetInt(value, "major");
        if (major is null) return null;
        return string.Join('.', major, GetInt(value, "minor") ?? 0, GetInt(value, "patch") ?? 0);
    }

    private static string? GetString(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result) && result.ValueKind == JsonValueKind.String ? result.GetString() : null;
    private static int? GetInt(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result) && result.TryGetInt32(out var number) ? number : null;
    private static int? GetNestedInt(JsonElement value, string property, string nestedProperty) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var nested) ? GetInt(nested, nestedProperty) : null;
    private static double? GetDouble(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result) && result.TryGetDouble(out var number) ? number : null;
    private static bool? GetBool(JsonElement value, string property) =>
        value.ValueKind == JsonValueKind.Object && value.TryGetProperty(property, out var result) && result.ValueKind is JsonValueKind.True or JsonValueKind.False ? result.GetBoolean() : null;

    private static McpException ToMcpException(string alias, KodiRpcException exception) =>
        new($"Kodi '{alias}' request failed ({exception.Kind.ToString().ToLowerInvariant()}): {exception.Message}");

    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$", RegexOptions.CultureInvariant)]
    private static partial Regex AddonId();

    private sealed record SearchDescriptor(
        string Method,
        string ResultProperty,
        string Media,
        string FilterField,
        string SortMethod,
        bool SupportsYearAndGenre,
        string[] Properties)
    {
        public static SearchDescriptor Resolve(string domain) => domain.Trim().ToLowerInvariant() switch
        {
            "movies" => new("VideoLibrary.GetMovies", "movies", "video", "title", "label", true, ["title", "year", "genre", "runtime", "playcount", "resume", "file"]),
            "tvshows" => new("VideoLibrary.GetTVShows", "tvshows", "video", "title", "label", true, ["title", "year", "genre", "playcount"]),
            "episodes" => new("VideoLibrary.GetEpisodes", "episodes", "video", "title", "label", false, ["title", "showtitle", "season", "episode", "runtime", "playcount", "resume", "file"]),
            "songs" => new("AudioLibrary.GetSongs", "songs", "music", "title", "label", false, ["title", "artist", "album", "duration", "track", "playcount", "file"]),
            "albums" => new("AudioLibrary.GetAlbums", "albums", "music", "album", "album", false, ["title", "artist", "year", "genre"]),
            _ => throw new McpException("Domain must be movies, tvshows, episodes, songs, or albums."),
        };
    }

    private sealed record SearchFilterRule(string Field, string Operator, string Value);

    private sealed record MediaViewDescriptor(
        string Method,
        string ResultProperty,
        string Media,
        bool RequiresInProgressFilter,
        string[] Properties);

    private static class GenreDescriptor
    {
        public static (string Method, string Domain, string? VideoType) Resolve(string domain) => domain.Trim().ToLowerInvariant() switch
        {
            "movies" => ("VideoLibrary.GetGenres", "movies", "movie"),
            "tvshows" => ("VideoLibrary.GetGenres", "tvshows", "tvshow"),
            "music" => ("AudioLibrary.GetGenres", "music", null),
            _ => throw new McpException("Genre domain must be movies, tvshows, or music."),
        };
    }

    private static class RecentDescriptor
    {
        public static MediaViewDescriptor Resolve(string domain) => domain.Trim().ToLowerInvariant() switch
        {
            "movies" => new("VideoLibrary.GetRecentlyAddedMovies", "movies", "video", false, ["title", "year", "genre", "runtime", "playcount", "resume", "file"]),
            "episodes" => new("VideoLibrary.GetRecentlyAddedEpisodes", "episodes", "video", false, ["title", "showtitle", "season", "episode", "runtime", "playcount", "resume", "file"]),
            "albums" => new("AudioLibrary.GetRecentlyAddedAlbums", "albums", "music", false, ["title", "artist", "year", "genre"]),
            "songs" => new("AudioLibrary.GetRecentlyAddedSongs", "songs", "music", false, ["title", "artist", "album", "year", "genre", "duration", "track", "playcount", "file"]),
            _ => throw new McpException("Recent domain must be movies, episodes, albums, or songs."),
        };
    }

    private static class ContinueDescriptor
    {
        public static MediaViewDescriptor Resolve(string domain) => domain.Trim().ToLowerInvariant() switch
        {
            "movies" => new("VideoLibrary.GetMovies", "movies", "video", true, ["title", "year", "genre", "runtime", "playcount", "lastplayed", "resume", "file"]),
            "episodes" => new("VideoLibrary.GetEpisodes", "episodes", "video", true, ["title", "showtitle", "season", "episode", "runtime", "playcount", "lastplayed", "resume", "file"]),
            "tvshows" => new("VideoLibrary.GetInProgressTVShows", "tvshows", "video", false, ["title", "year", "genre", "playcount", "lastplayed", "episode", "watchedepisodes"]),
            _ => throw new McpException("Continue-watching domain must be movies, episodes, or tvshows."),
        };
    }
}
