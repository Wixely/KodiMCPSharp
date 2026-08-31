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
    private const int MaximumBulkWatchStateChanges = 100;
    private const int MaximumEpisodesPerTvShow = 2000;
    private const int EpisodeReadPageSize = 200;
    private static readonly string[] ReadTools =
    [
        "kodi_list_instances", "kodi_get_capabilities", "kodi_get_status",
        "kodi_search_library", "kodi_list_genres", "kodi_list_recent", "kodi_list_continue_watching",
        "kodi_browse_tv_show", "kodi_list_favourites", "kodi_search_favourites", "kodi_list_addons", "kodi_browse",
        "kodi_list_addon_routes", "kodi_bind_addon_route",
    ];

    private readonly KodiInstanceRegistry _registry;
    private readonly IHandleStore _handles;
    private readonly ILearnedRouteStore _learnedRoutes;
    private readonly KodiOptions _options;
    private readonly SafeText _safeText;
    private readonly TimeProvider _timeProvider;

    public KodiService(
        KodiInstanceRegistry registry,
        IHandleStore handles,
        ILearnedRouteStore learnedRoutes,
        IOptions<KodiOptions> options,
        SafeText safeText,
        TimeProvider timeProvider)
    {
        _registry = registry;
        _handles = handles;
        _learnedRoutes = learnedRoutes;
        _options = options.Value;
        _safeText = safeText;
        _timeProvider = timeProvider;
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
            "kodi_save_addon_route", "kodi_forget_addon_route", "kodi_set_episode_watch_state",
            "kodi_bulk_set_episode_watch_state", "kodi_play_movie", "kodi_play_episode", "kodi_play_next_episode",
            "kodi_resume",
            "kodi_add_favourite", "kodi_remove_favourite",
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
            ["watchState"] = !_options.ReadOnly && _options.Controls.AllowWatchState,
            ["favourites"] = !_options.ReadOnly && _options.Controls.AllowFavourites,
            ["learnedRouteWrites"] = _options.LearnedRoutes.AllowWrite,
            ["navigation"] = false,
            ["addonActivation"] = false,
            ["administration"] = false,
        },
        Handles: new HandlePolicySummary(_options.Handles.LifetimeMinutes, _options.Handles.Capacity, false),
        LearnedRoutes: new LearnedRoutePolicySummary(true, _options.LearnedRoutes.AllowWrite, _options.LearnedRoutes.MaximumRoutesPerAddon));

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
            return await OpenTargetAsync(instance, entry.Target, false, "play", cancellationToken);
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    private static async Task<PlaybackResult> OpenTargetAsync(
        RegisteredKodiInstance instance,
        string target,
        bool resume,
        string requested,
        CancellationToken cancellationToken)
    {
        var response = await instance.Client.CallAsync("Player.Open", writer =>
        {
            writer.WritePropertyName("item");
            writer.WriteStartObject();
            writer.WriteString("file", target);
            writer.WriteEndObject();
            if (resume)
            {
                writer.WritePropertyName("options");
                writer.WriteStartObject();
                writer.WriteBoolean("resume", true);
                writer.WriteEndObject();
            }
        }, cancellationToken);
        var accepted = response.ValueKind == JsonValueKind.String &&
                       response.GetString()?.Equals("OK", StringComparison.OrdinalIgnoreCase) == true;

        for (var attempt = 0; attempt < 8; attempt++)
        {
            var observation = await ObserveActivePlayerAsync(instance, cancellationToken);
            if (observation is not null)
            {
                return new PlaybackResult(instance.Alias, requested, accepted, true, "observed-playing",
                    observation.Value.PlayerId, observation.Value.Type, observation.Value.State);
            }
            if (attempt < 7) await Task.Delay(TimeSpan.FromMilliseconds(250), cancellationToken);
        }

        return new PlaybackResult(instance.Alias, requested, accepted, false,
            accepted ? "accepted-not-yet-observed" : "indeterminate", null, null, null);
    }

    public async Task<PlayNextEpisodeResult> PlayNextEpisodeAsync(
        string? alias,
        string show,
        CancellationToken cancellationToken)
    {
        var normalizedShow = NormalizeSearchText(show, "TV show", 200)
            ?? throw new McpException("TV show is required.");
        if (_options.ReadOnly) throw new McpException("Playback is blocked while Kodi:ReadOnly is true.");
        if (!_options.Controls.AllowPlayback)
            throw new McpException("Playback is disabled. Set Kodi:Controls:AllowPlayback=true to enable play-next-episode.");

        var instance = _registry.Resolve(alias);
        try
        {
            var resolution = await ResolveNextEpisodeAsync(instance, normalizedShow, cancellationToken)
                ?? throw new McpException($"No playable unwatched or partially watched episode was found for '{normalizedShow}' in favourites, the Kodi library, or learned add-on routes.");
            var handle = _handles.Create(instance.Alias, resolution.Candidate.Target, "video", "episode", HandleAction.Play,
                resolution.Candidate.AddonId, resolution.Candidate.AddonName);
            var playback = await PlayItemAsync(instance.Alias, handle, cancellationToken);
            return new PlayNextEpisodeResult(
                instance.Alias,
                normalizedShow,
                resolution.Source,
                resolution.SelectionBasis,
                resolution.Candidate.Label,
                resolution.Candidate.Season,
                resolution.Candidate.Episode,
                playback.Accepted,
                playback.Observed,
                playback.Outcome,
                playback.PlayerId,
                playback.State);
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public async Task<ResolvedPlaybackResult> PlayMovieAsync(
        string? alias,
        string title,
        int? year,
        CancellationToken cancellationToken)
    {
        var normalizedTitle = NormalizeSearchText(title, "Movie title", 200)
            ?? throw new McpException("Movie title is required.");
        if (year is < 1 or > 9999) throw new McpException("Year must be between 1 and 9999.");
        EnsureControl(_options.Controls.AllowPlayback, "movie playback", "AllowPlayback");
        var instance = _registry.Resolve(alias);
        try
        {
            var resolution = await ResolveMovieAsync(instance, normalizedTitle, year, cancellationToken)
                ?? throw new McpException($"No unambiguous playable movie was found for '{normalizedTitle}' in favourites, the Kodi library, or learned add-on routes.");
            var handle = _handles.Create(instance.Alias, resolution.Target, "video", "movie", HandleAction.Play,
                resolution.AddonId, resolution.AddonName);
            var playback = await PlayItemAsync(instance.Alias, handle, cancellationToken);
            return new ResolvedPlaybackResult(
                instance.Alias, normalizedTitle, "movie", resolution.Source, resolution.SelectionBasis,
                resolution.Label, resolution.Year, null, null, playback.Accepted, playback.Observed,
                playback.Outcome, playback.PlayerId, playback.State);
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public async Task<ResolvedPlaybackResult> PlayEpisodeAsync(
        string? alias,
        string show,
        int season,
        int episode,
        CancellationToken cancellationToken)
    {
        var normalizedShow = NormalizeSearchText(show, "TV show", 200)
            ?? throw new McpException("TV show is required.");
        if (season is < 0 or > 1000) throw new McpException("Season must be between 0 and 1000.");
        if (episode is < 0 or > 10000) throw new McpException("Episode must be between 0 and 10000.");
        EnsureControl(_options.Controls.AllowPlayback, "episode playback", "AllowPlayback");
        var instance = _registry.Resolve(alias);
        try
        {
            var resolution = await ResolveExactEpisodeAsync(instance, normalizedShow, season, episode, cancellationToken)
                ?? throw new McpException($"No unambiguous playable episode was found for '{normalizedShow}' season {season}, episode {episode} in favourites, the Kodi library, or learned add-on routes.");
            var handle = _handles.Create(instance.Alias, resolution.Candidate.Target, "video", "episode", HandleAction.Play,
                resolution.Candidate.AddonId, resolution.Candidate.AddonName);
            var playback = await PlayItemAsync(instance.Alias, handle, cancellationToken);
            return new ResolvedPlaybackResult(
                instance.Alias, normalizedShow, "episode", resolution.Source, resolution.SelectionBasis,
                resolution.Candidate.Label, null, resolution.Candidate.Season, resolution.Candidate.Episode,
                playback.Accepted, playback.Observed, playback.Outcome, playback.PlayerId, playback.State);
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public async Task<ResolvedPlaybackResult> ResumeAsync(
        string? alias,
        string title,
        string domain,
        CancellationToken cancellationToken)
    {
        var normalizedTitle = NormalizeSearchText(title, "Title", 200)
            ?? throw new McpException("Title is required.");
        var normalizedDomain = domain.Trim().ToLowerInvariant();
        if (normalizedDomain is not ("auto" or "movies" or "episodes"))
            throw new McpException("Domain must be auto, movies, or episodes.");
        EnsureControl(_options.Controls.AllowPlayback, "resume playback", "AllowPlayback");
        var instance = _registry.Resolve(alias);
        try
        {
            var resolution = await ResolveResumeAsync(instance, normalizedTitle, normalizedDomain, cancellationToken)
                ?? throw new McpException($"No partially watched {normalizedDomain} title matching '{normalizedTitle}' was found in the Kodi library.");
            var playback = await OpenTargetAsync(instance, resolution.Target, true, "resume", cancellationToken);
            return new ResolvedPlaybackResult(
                instance.Alias, normalizedTitle, resolution.MediaType, "library", "largest-resume-position",
                resolution.Label, resolution.Year, resolution.Season, resolution.Episode, playback.Accepted,
                playback.Observed, playback.Outcome, playback.PlayerId, playback.State);
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

    public Task<FavouriteMutationResult> AddFavouriteAsync(
        string? alias,
        string handle,
        CancellationToken cancellationToken) =>
        SetFavouritePresenceAsync(alias, handle, true, cancellationToken);

    public Task<FavouriteMutationResult> RemoveFavouriteAsync(
        string? alias,
        string handle,
        CancellationToken cancellationToken) =>
        SetFavouritePresenceAsync(alias, handle, false, cancellationToken);

    private async Task<FavouriteMutationResult> SetFavouritePresenceAsync(
        string? alias,
        string handle,
        bool present,
        CancellationToken cancellationToken)
    {
        EnsureControl(_options.Controls.AllowFavourites, "favourite changes", "AllowFavourites");
        var instance = _registry.Resolve(alias);
        var entry = _handles.Resolve(handle, instance.Alias,
            present ? HandleAction.AddFavourite : HandleAction.RemoveFavourite);
        var favourite = entry.Favourite
            ?? throw new McpException("The item handle does not contain a supported favourite target.");

        try
        {
            var before = await GetFavouriteMatchCountAsync(instance, favourite, cancellationToken);
            if ((present && before > 0) || (!present && before == 0))
            {
                return new FavouriteMutationResult(
                    instance.Alias, present ? "present" : "absent", present ? "present" : "absent",
                    false, true, "already-in-requested-state", before, before,
                    _safeText.Clean(favourite.Title), favourite.Type);
            }
            if (before > 1)
            {
                throw new McpException(
                    "Kodi contains duplicate exact favourites for this handle. No change was made because a toggle would be ambiguous.");
            }

            var response = await instance.Client.CallAsync("Favourites.AddFavourite", writer =>
            {
                WriteFavourite(writer, favourite);
            }, cancellationToken);
            var accepted = IsAccepted(response);
            var after = await GetFavouriteMatchCountAsync(instance, favourite, cancellationToken);
            var observed = present ? after > 0 : after == 0;
            return new FavouriteMutationResult(
                instance.Alias, present ? "present" : "absent",
                after > 0 ? "present" : "absent", accepted, observed,
                observed ? "observed-complete" : accepted ? "accepted-postcondition-not-observed" : "indeterminate",
                before, after, _safeText.Clean(favourite.Title), favourite.Type);
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    private static async Task<int> GetFavouriteMatchCountAsync(
        RegisteredKodiInstance instance,
        FavouriteDescriptor favourite,
        CancellationToken cancellationToken)
    {
        var result = await instance.Client.CallAsync("Favourites.GetFavourites", writer =>
        {
            writer.WriteString("type", favourite.Type);
            WriteStringArray(writer, "properties", ["path", "window", "windowparameter", "thumbnail"]);
        }, cancellationToken);
        return result.TryGetProperty("favourites", out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().Count(item => FavouriteMatches(item, favourite))
            : 0;
    }

    private static void WriteFavourite(Utf8JsonWriter writer, FavouriteDescriptor favourite)
    {
        writer.WriteString("title", favourite.Title);
        writer.WriteString("type", favourite.Type);
        if (favourite.Path is not null) writer.WriteString("path", favourite.Path);
        if (favourite.Window is not null) writer.WriteString("window", favourite.Window);
        if (favourite.WindowParameter is not null) writer.WriteString("windowparameter", favourite.WindowParameter);
        if (favourite.Thumbnail is not null) writer.WriteString("thumbnail", favourite.Thumbnail);
    }

    private static bool FavouriteMatches(JsonElement item, FavouriteDescriptor favourite) =>
        string.Equals(FavouriteTitle(item), favourite.Title, StringComparison.Ordinal) &&
        string.Equals(GetString(item, "type"), favourite.Type, StringComparison.OrdinalIgnoreCase) &&
        EqualOptional(GetString(item, "path"), favourite.Path) &&
        EqualOptional(GetString(item, "window"), favourite.Window) &&
        EqualOptional(GetString(item, "windowparameter"), favourite.WindowParameter);

    private static bool EqualOptional(string? left, string? right) =>
        string.Equals(string.IsNullOrEmpty(left) ? null : left, string.IsNullOrEmpty(right) ? null : right, StringComparison.Ordinal);

    public async Task<EpisodeWatchStateResult> SetEpisodeWatchStateAsync(
        string? alias,
        string handle,
        string state,
        CancellationToken cancellationToken)
    {
        EnsureControl(_options.Controls.AllowWatchState, "episode watch-state control", "AllowWatchState");
        var normalizedState = state.Trim().ToLowerInvariant() switch
        {
            "watched" => "watched",
            "unwatched" => "unwatched",
            _ => throw new McpException("Episode watch state must be watched or unwatched."),
        };
        var instance = _registry.Resolve(alias);
        var entry = _handles.Resolve(handle, instance.Alias, HandleAction.SetWatchState);
        if (entry.Kind != "episode" || entry.LibraryId is null)
            throw new McpException("The handle is not a watch-state-capable episode handle.");

        try
        {
            var response = await instance.Client.CallAsync("VideoLibrary.SetEpisodeDetails", writer =>
            {
                writer.WriteNumber("episodeid", entry.LibraryId.Value);
                writer.WriteNumber("playcount", normalizedState == "watched" ? 1 : 0);
                writer.WritePropertyName("resume");
                writer.WriteStartObject();
                writer.WriteNumber("position", 0);
                writer.WriteNumber("total", 0);
                writer.WriteEndObject();
            }, cancellationToken);
            var accepted = IsAccepted(response);
            var details = await instance.Client.CallAsync("VideoLibrary.GetEpisodeDetails", writer =>
            {
                writer.WriteNumber("episodeid", entry.LibraryId.Value);
                WriteStringArray(writer, "properties", ["playcount", "resume"]);
            }, cancellationToken);
            var observedState = details.TryGetProperty("episodedetails", out var episode)
                ? GetWatchState(episode)
                : null;
            var observed = observedState == normalizedState;
            return new EpisodeWatchStateResult(
                instance.Alias, normalizedState, observedState, accepted, observed,
                observed ? "observed-complete" : accepted ? "accepted-postcondition-not-observed" : "indeterminate");
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    public async Task<BulkEpisodeWatchStateResult> BulkSetEpisodeWatchStateAsync(
        string? alias,
        string handle,
        string state,
        string range,
        bool preview,
        bool includeSpecials,
        CancellationToken cancellationToken)
    {
        var normalizedState = NormalizeEpisodeWatchState(state);
        var normalizedRange = range.Trim().ToLowerInvariant() switch
        {
            "before" => "before",
            "through" => "through",
            "after" => "after",
            "all" => "all",
            _ => throw new McpException("Episode watch-state range must be before, through, after, or all."),
        };
        if (!preview) EnsureControl(_options.Controls.AllowWatchState, "bulk episode watch-state control", "AllowWatchState");

        var instance = _registry.Resolve(alias);
        var entry = _handles.Resolve(handle, instance.Alias, HandleAction.SetWatchState);
        if (entry.Kind != "episode" || entry.LibraryId is null)
            throw new McpException("The handle is not a watch-state-capable library episode handle.");

        try
        {
            var selectedResult = await instance.Client.CallAsync("VideoLibrary.GetEpisodeDetails", writer =>
            {
                writer.WriteNumber("episodeid", entry.LibraryId.Value);
                WriteStringArray(writer, "properties", ["tvshowid", "season", "episode"]);
            }, cancellationToken);
            if (!selectedResult.TryGetProperty("episodedetails", out var selected) ||
                GetInt(selected, "tvshowid") is not { } tvShowId ||
                GetInt(selected, "season") is not { } selectedSeason ||
                GetInt(selected, "episode") is not { } selectedEpisode)
                throw new McpException("Kodi did not return a numbered TV-show position for the selected episode.");
            if (selectedSeason == 0 && !includeSpecials && normalizedRange != "all")
                throw new McpException("A season-zero anchor requires includeSpecials=true for a relative bulk range.");

            var allEpisodes = await ReadTvShowEpisodesAsync(instance, tvShowId, cancellationToken);
            var skippedUnnumbered = allEpisodes.Count(episode => episode.Season is null || episode.Episode is null);
            var eligible = allEpisodes
                .Where(episode => episode.Season is not null && episode.Episode is not null)
                .Where(episode => includeSpecials || episode.Season > 0)
                .Where(episode => IsInEpisodeRange(
                    episode.Season!.Value, episode.Episode!.Value, selectedSeason, selectedEpisode, normalizedRange))
                .OrderBy(episode => episode.Season)
                .ThenBy(episode => episode.Episode)
                .ThenBy(episode => episode.EpisodeId)
                .ToArray();
            var changing = eligible.Where(episode => !IsDesiredWatchState(episode, normalizedState)).ToArray();
            var alreadyTarget = eligible.Length - changing.Length;
            var capExceeded = changing.Length > MaximumBulkWatchStateChanges;

            if (preview || capExceeded)
            {
                if (!preview && capExceeded)
                    throw new McpException($"The bulk change would update {changing.Length} episodes, exceeding the {MaximumBulkWatchStateChanges}-episode cap. Choose a narrower range.");
                var planItems = PrioritizeChangingEpisodes(eligible, normalizedState).Take(MaximumBulkWatchStateChanges)
                    .Select(episode => ToBulkItem(episode, IsDesiredWatchState(episode, normalizedState) ? "already-target" : "would-change"))
                    .ToArray();
                return new BulkEpisodeWatchStateResult(
                    instance.Alias, normalizedState, normalizedRange, true, includeSpecials,
                    eligible.Length, changing.Length, alreadyTarget, skippedUnnumbered,
                    0, 0, 0, MaximumBulkWatchStateChanges, capExceeded, planItems.Length, planItems);
            }

            var outcomes = eligible.ToDictionary(
                episode => episode.EpisodeId,
                episode => IsDesiredWatchState(episode, normalizedState) ? "already-target" : "pending");
            var accepted = new HashSet<int>();
            foreach (var episode in changing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                try
                {
                    var response = await instance.Client.CallAsync("VideoLibrary.SetEpisodeDetails", writer =>
                    {
                        writer.WriteNumber("episodeid", episode.EpisodeId);
                        writer.WriteNumber("playcount", normalizedState == "watched" ? 1 : 0);
                        writer.WritePropertyName("resume");
                        writer.WriteStartObject();
                        writer.WriteNumber("position", 0);
                        writer.WriteNumber("total", 0);
                        writer.WriteEndObject();
                    }, cancellationToken);
                    if (IsAccepted(response))
                    {
                        accepted.Add(episode.EpisodeId);
                        outcomes[episode.EpisodeId] = "accepted-awaiting-verification";
                    }
                    else
                    {
                        outcomes[episode.EpisodeId] = "not-accepted";
                    }
                }
                catch (KodiRpcException exception) when (exception.Kind == KodiFailureKind.Remote)
                {
                    outcomes[episode.EpisodeId] = "remote-failure";
                }
            }

            var observed = (await ReadTvShowEpisodesAsync(instance, tvShowId, cancellationToken))
                .ToDictionary(episode => episode.EpisodeId);
            var verified = 0;
            foreach (var episodeId in accepted)
            {
                if (observed.TryGetValue(episodeId, out var observedEpisode) &&
                    IsDesiredWatchState(observedEpisode, normalizedState))
                {
                    outcomes[episodeId] = "observed-complete";
                    verified++;
                }
                else
                {
                    outcomes[episodeId] = "accepted-postcondition-not-observed";
                }
            }
            var failed = changing.Length - verified;
            var resultItems = PrioritizeChangingEpisodes(eligible, normalizedState)
                .Take(MaximumBulkWatchStateChanges)
                .Select(episode => ToBulkItem(episode, outcomes[episode.EpisodeId]))
                .ToArray();
            return new BulkEpisodeWatchStateResult(
                instance.Alias, normalizedState, normalizedRange, false, includeSpecials,
                eligible.Length, changing.Length, alreadyTarget, skippedUnnumbered,
                accepted.Count, verified, failed, MaximumBulkWatchStateChanges, false, resultItems.Length, resultItems);
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    private async Task<IReadOnlyList<BulkEpisodeRecord>> ReadTvShowEpisodesAsync(
        RegisteredKodiInstance instance,
        int tvShowId,
        CancellationToken cancellationToken)
    {
        var episodes = new List<BulkEpisodeRecord>();
        var start = 0;
        var total = int.MaxValue;
        while (start < total)
        {
            var end = Math.Min(start + EpisodeReadPageSize, MaximumEpisodesPerTvShow + 1);
            var result = await instance.Client.CallAsync("VideoLibrary.GetEpisodes", writer =>
            {
                writer.WriteNumber("tvshowid", tvShowId);
                WriteStringArray(writer, "properties", ["title", "season", "episode", "playcount", "resume"]);
                WriteLimits(writer, start, end);
            }, cancellationToken);
            var values = GetArray(result, "episodes");
            var limits = GetLimits(result, start, values.Length);
            total = limits.Total;
            if (total > MaximumEpisodesPerTvShow)
                throw new McpException($"The TV show contains more than the supported {MaximumEpisodesPerTvShow} episodes.");
            foreach (var item in values)
            {
                if (GetInt(item, "episodeid") is not { } episodeId) continue;
                var resumePosition = item.TryGetProperty("resume", out var resume) && resume.ValueKind == JsonValueKind.Object
                    ? GetDouble(resume, "position") ?? 0
                    : 0;
                episodes.Add(new BulkEpisodeRecord(
                    episodeId,
                    _safeText.Clean(GetString(item, "label") ?? GetString(item, "title")),
                    GetInt(item, "season"),
                    GetInt(item, "episode"),
                    GetInt(item, "playcount") ?? 0,
                    resumePosition));
            }
            if (values.Length == 0) break;
            start = limits.End > start ? limits.End : start + values.Length;
        }
        return episodes;
    }

    private static bool IsInEpisodeRange(
        int season,
        int episode,
        int selectedSeason,
        int selectedEpisode,
        string range)
    {
        var comparison = season != selectedSeason ? season.CompareTo(selectedSeason) : episode.CompareTo(selectedEpisode);
        return range switch
        {
            "before" => comparison < 0,
            "through" => comparison <= 0,
            "after" => comparison > 0,
            "all" => true,
            _ => false,
        };
    }

    private static bool IsDesiredWatchState(BulkEpisodeRecord episode, string state) =>
        state == "watched"
            ? episode.PlayCount > 0 && episode.ResumePosition <= 0
            : episode.PlayCount <= 0 && episode.ResumePosition <= 0;

    private static IEnumerable<BulkEpisodeRecord> PrioritizeChangingEpisodes(
        IEnumerable<BulkEpisodeRecord> episodes,
        string state) =>
        episodes.OrderBy(episode => IsDesiredWatchState(episode, state) ? 1 : 0)
            .ThenBy(episode => episode.Season)
            .ThenBy(episode => episode.Episode)
            .ThenBy(episode => episode.EpisodeId);

    private static string GetWatchState(BulkEpisodeRecord episode) =>
        episode.PlayCount > 0 ? "watched" : episode.ResumePosition > 0 ? "partially-watched" : "unwatched";

    private static BulkEpisodeWatchStateItem ToBulkItem(BulkEpisodeRecord episode, string outcome) =>
        new(episode.Label, episode.Season!.Value, episode.Episode!.Value, GetWatchState(episode), outcome);

    private static string NormalizeEpisodeWatchState(string state) => state.Trim().ToLowerInvariant() switch
    {
        "watched" => "watched",
        "unwatched" => "unwatched",
        _ => throw new McpException("Episode watch state must be watched or unwatched."),
    };

    private sealed record BulkEpisodeRecord(
        int EpisodeId,
        string? Label,
        int? Season,
        int? Episode,
        int PlayCount,
        double ResumePosition);

    public async Task<PageSummary> SearchFavouritesAsync(
        string? alias,
        string query,
        string? favouriteType,
        int page,
        int pageSize,
        CancellationToken cancellationToken)
    {
        var instance = _registry.Resolve(alias);
        var normalizedQuery = NormalizeSearchText(query, "Favourite title query", 200)
            ?? throw new McpException("Favourite title query is required.");
        var normalizedType = NormalizeFavouriteType(favouriteType);
        var (start, end) = Bounds(page, pageSize);
        try
        {
            var result = await instance.Client.CallAsync("Favourites.GetFavourites", writer =>
            {
                if (normalizedType is not null) writer.WriteString("type", normalizedType);
                WriteStringArray(writer, "properties", ["path", "window", "windowparameter", "thumbnail"]);
            }, cancellationToken);

            var matches = result.TryGetProperty("favourites", out var favourites) && favourites.ValueKind == JsonValueKind.Array
                ? favourites.EnumerateArray()
                    .Where(item => (GetString(item, "label") ?? GetString(item, "title") ?? string.Empty)
                        .Contains(normalizedQuery, StringComparison.OrdinalIgnoreCase))
                    .ToArray()
                : [];
            var items = matches.Skip(start).Take(end - start)
                .Select(item => ParseItem(instance.Alias, item, "files", "favourites"))
                .ToArray();
            return new PageSummary(instance.Alias, start, start + items.Length, matches.Length, items);
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
                // Kodi 21 rejects "type" in Addon.Fields even though it returns type as a base field.
                WriteStringArray(writer, "properties", ["name", "version", "summary", "description", "enabled"]);
                writer.WritePropertyName("limits");
                writer.WriteStartObject(); writer.WriteNumber("start", start); writer.WriteNumber("end", end); writer.WriteEndObject();
            }, cancellationToken);

            var addons = new List<AddonSummary>();
            if (result.TryGetProperty("addons", out var values) && values.ValueKind == JsonValueKind.Array)
            {
                foreach (var addon in values.EnumerateArray())
                {
                    var id = GetString(addon, "addonid");
                    var name = _safeText.Clean(GetString(addon, "name") ?? GetString(addon, "label"));
                    var enabled = GetBool(addon, "enabled") ?? true;
                    var browsable = enabled && id is not null && AddonId().IsMatch(id);
                    var handle = browsable
                        ? _handles.Create(instance.Alias, $"plugin://{id}/", "files", "addon-folder", HandleAction.Browse, id, name)
                        : null;
                    addons.Add(new AddonSummary(
                        name,
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

    public async Task<LearnedRouteSummary> SaveAddonRouteAsync(
        string? alias,
        string handle,
        string name,
        string? sampleValue,
        CancellationToken cancellationToken)
    {
        EnsureLearnedRouteWrites();
        FileLearnedRouteStore.ValidateName(name);
        var instance = _registry.Resolve(alias);
        var entry = _handles.Resolve(handle, instance.Alias, HandleAction.None);
        var actions = entry.Actions & (HandleAction.Browse | HandleAction.Play);
        if (entry.AddonId is null || actions == HandleAction.None)
        {
            throw new McpException("Only a browsable or playable handle discovered inside a Kodi add-on can be learned.");
        }
        if (!Uri.TryCreate(entry.Target, UriKind.Absolute, out var target) ||
            !target.Scheme.Equals("plugin", StringComparison.OrdinalIgnoreCase) ||
            !target.Host.Equals(entry.AddonId, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException("The handle is not a reusable route belonging to its originating Kodi add-on.");
        }

        var targetToPersist = entry.Target;
        LearnedRouteParameter? parameter = null;
        if (sampleValue is not null)
        {
            if ((actions & HandleAction.Browse) == 0)
            {
                throw new McpException("Parameterized learned routes must originate from a browsable add-on item.");
            }
            var normalizedSample = ValidateLearnedRouteInput(sampleValue, "Sample value");
            targetToPersist = InferSingleInputTemplate(entry.Target, normalizedSample);
            parameter = new LearnedRouteParameter("input", "string", 200);
            actions = HandleAction.Browse;
        }

        var route = await _learnedRoutes.SaveAsync(new LearnedRouteEntry(
            instance.Alias,
            entry.AddonId,
            entry.AddonName,
            name.Trim(),
            targetToPersist,
            entry.Media,
            entry.Kind,
            actions,
            _timeProvider.GetUtcNow(),
            parameter), cancellationToken);
        return CreateLearnedRouteSummary(route);
    }

    public async Task<LearnedRoutePageSummary> ListAddonRoutesAsync(string? alias, CancellationToken cancellationToken)
    {
        var instance = _registry.Resolve(alias);
        var routes = await _learnedRoutes.ListAsync(instance.Alias, cancellationToken);
        return new LearnedRoutePageSummary(instance.Alias, routes.Select(CreateLearnedRouteSummary).ToArray());
    }

    public BoundLearnedRouteSummary BindAddonRoute(string? alias, string handle, string input)
    {
        var instance = _registry.Resolve(alias);
        var entry = _handles.Resolve(handle, instance.Alias, HandleAction.BindLearnedRoute);
        var normalizedInput = ValidateLearnedRouteInput(input, "Route input");
        var resultActions = entry.TemplateResultActions & (HandleAction.Browse | HandleAction.Play);
        if (entry.AddonId is null || entry.LearnedRouteName is null || resultActions == HandleAction.None ||
            CountOccurrences(entry.Target, FileLearnedRouteStore.InputPlaceholder) != 1)
        {
            throw new McpException("The learned-route handle does not contain a valid input template.");
        }
        var target = entry.Target.Replace(
            FileLearnedRouteStore.InputPlaceholder, Uri.EscapeDataString(normalizedInput), StringComparison.Ordinal);
        if (!Uri.TryCreate(target, UriKind.Absolute, out var parsed) ||
            !parsed.Scheme.Equals("plugin", StringComparison.OrdinalIgnoreCase) ||
            !parsed.Host.Equals(entry.AddonId, StringComparison.OrdinalIgnoreCase))
        {
            throw new McpException("The learned route could not produce a valid add-on target.");
        }
        var boundHandle = _handles.Create(instance.Alias, target, entry.Media, entry.Kind, resultActions,
            entry.AddonId, entry.AddonName);
        return new BoundLearnedRouteSummary(
            instance.Alias,
            entry.LearnedRouteName,
            (resultActions & HandleAction.Browse) != 0,
            (resultActions & HandleAction.Play) != 0,
            boundHandle);
    }

    public async Task<LearnedRouteMutationResult> ForgetAddonRouteAsync(
        string? alias,
        string handle,
        CancellationToken cancellationToken)
    {
        EnsureLearnedRouteWrites();
        var instance = _registry.Resolve(alias);
        var entry = _handles.Resolve(handle, instance.Alias, HandleAction.ManageLearnedRoute);
        if (entry.AddonId is null || entry.LearnedRouteName is null)
        {
            throw new McpException("The learned-route handle does not contain route management metadata.");
        }
        var removed = await _learnedRoutes.DeleteAsync(
            instance.Alias, entry.AddonId, entry.LearnedRouteName, cancellationToken);
        return new LearnedRouteMutationResult(instance.Alias, entry.LearnedRouteName, removed);
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
            return ParseItemPage(instance.Alias, result, "files", entry.Media, start,
                addonId: entry.AddonId, addonName: entry.AddonName);
        }
        catch (KodiRpcException exception)
        {
            throw ToMcpException(instance.Alias, exception);
        }
    }

    private async Task<MovieResolution?> ResolveMovieAsync(
        RegisteredKodiInstance instance,
        string title,
        int? year,
        CancellationToken cancellationToken)
    {
        if (year is null)
        {
            try
            {
                var favourites = await instance.Client.CallAsync("Favourites.GetFavourites", writer =>
                {
                    writer.WriteString("type", "media");
                    WriteStringArray(writer, "properties", ["path"]);
                }, cancellationToken);
                var matches = GetArray(favourites, "favourites")
                    .Where(item => FavouriteTitle(item).Contains(title, StringComparison.OrdinalIgnoreCase))
                    .Where(item => GetString(item, "type") == "media" && IsPlausibleMovieFavouriteTarget(GetString(item, "path")))
                    .ToArray();
                var selected = SelectUniqueTitleMatch(matches, title);
                if (selected is { } favourite)
                {
                    var target = GetString(favourite, "path")!;
                    return new MovieResolution(
                        "favourite", "unique-title-match", target, _safeText.Clean(FavouriteTitle(favourite)), null,
                        GetPluginAddonId(target), null);
                }
            }
            catch (KodiRpcException exception) when (exception.Kind == KodiFailureKind.Remote)
            {
                // Continue to the library when this Kodi version cannot filter media favourites.
            }
        }

        try
        {
            var movies = await instance.Client.CallAsync("VideoLibrary.GetMovies", writer =>
            {
                WriteStringArray(writer, "properties", ["title", "year", "file"]);
                var filters = new List<SearchFilterRule> { new("title", "contains", title) };
                if (year is not null) filters.Add(new("year", "is", year.Value.ToString(CultureInfo.InvariantCulture)));
                WriteSearchFilter(writer, filters);
                WriteLimits(writer, 0, 25);
            }, cancellationToken);
            var candidates = GetArray(movies, "movies")
                .Where(item => !string.IsNullOrWhiteSpace(GetString(item, "file")))
                .Where(item => year is null || GetInt(item, "year") == year)
                .ToArray();
            var selected = SelectUniqueTitleMatch(candidates, title);
            if (selected is { } movie)
            {
                var target = GetString(movie, "file")!;
                return new MovieResolution(
                    "library", year is null ? "unique-title-match" : "unique-title-year-match", target,
                    _safeText.Clean(ItemTitle(movie)), GetInt(movie, "year"), GetPluginAddonId(target), null);
            }
        }
        catch (KodiRpcException exception) when (exception.Kind == KodiFailureKind.Remote)
        {
            // Continue to a learned add-on search when the video library is unavailable.
        }

        var routes = await _learnedRoutes.ListAsync(instance.Alias, cancellationToken);
        foreach (var route in routes.Where(route => route.Parameter is not null &&
                                                   (route.Actions & HandleAction.Browse) != 0 &&
                                                   route.Name.Contains("search", StringComparison.OrdinalIgnoreCase) &&
                                                   route.Name.Contains("movie", StringComparison.OrdinalIgnoreCase)))
        {
            var target = route.Target.Replace(
                FileLearnedRouteStore.InputPlaceholder, Uri.EscapeDataString(title), StringComparison.Ordinal);
            if (!route.AddonId.Equals(GetPluginAddonId(target), StringComparison.OrdinalIgnoreCase)) continue;
            var items = await ReadAddonDirectoryAsync(instance, target, cancellationToken);
            var candidates = items
                .Where(item => !IsDirectoryItem(item) && !string.IsNullOrWhiteSpace(GetString(item, "file")))
                .Where(item => ItemTitle(item).Contains(title, StringComparison.OrdinalIgnoreCase))
                .Where(item => year is null || GetInt(item, "year") == year)
                .ToArray();
            var selected = SelectUniqueTitleMatch(candidates, title);
            if (selected is not { } movie) continue;
            var movieTarget = GetString(movie, "file")!;
            if (!route.AddonId.Equals(GetPluginAddonId(movieTarget), StringComparison.OrdinalIgnoreCase)) continue;
            return new MovieResolution(
                "learned-addon-route", year is null ? "unique-title-match" : "unique-title-year-match",
                movieTarget, _safeText.Clean(ItemTitle(movie)), GetInt(movie, "year"), route.AddonId, route.AddonName);
        }
        return null;
    }

    private async Task<ResumeResolution?> ResolveResumeAsync(
        RegisteredKodiInstance instance,
        string title,
        string domain,
        CancellationToken cancellationToken)
    {
        var candidates = new List<ResumeResolution>();
        if (domain is "auto" or "movies")
        {
            var movies = await instance.Client.CallAsync("VideoLibrary.GetMovies", writer =>
            {
                WriteStringArray(writer, "properties", ["title", "year", "resume", "file"]);
                WriteSearchFilter(writer,
                [
                    new SearchFilterRule("title", "contains", title),
                    new SearchFilterRule("inprogress", "true", string.Empty),
                ]);
                WriteLimits(writer, 0, 25);
                WriteSort(writer, "lastplayed", "descending");
            }, cancellationToken);
            candidates.AddRange(GetArray(movies, "movies").Select(item => CreateResumeResolution(item, "movie"))
                .Where(candidate => candidate is not null).Cast<ResumeResolution>());
        }

        if (domain is "auto" or "episodes")
        {
            var episodes = await instance.Client.CallAsync("VideoLibrary.GetEpisodes", writer =>
            {
                WriteStringArray(writer, "properties", ["title", "showtitle", "season", "episode", "resume", "file"]);
                WriteSearchFilter(writer, [new SearchFilterRule("inprogress", "true", string.Empty)]);
                WriteLimits(writer, 0, 200);
                WriteSort(writer, "lastplayed", "descending");
            }, cancellationToken);
            candidates.AddRange(GetArray(episodes, "episodes")
                .Where(item => (GetString(item, "showtitle") ?? string.Empty).Contains(title, StringComparison.OrdinalIgnoreCase) ||
                               ItemTitle(item).Contains(title, StringComparison.OrdinalIgnoreCase))
                .Select(item => CreateResumeResolution(item, "episode"))
                .Where(candidate => candidate is not null).Cast<ResumeResolution>());
        }

        var exact = candidates.Where(candidate => candidate.MatchTitle.Equals(title, StringComparison.OrdinalIgnoreCase)).ToArray();
        var matches = exact.Length > 0 ? exact : candidates
            .Where(candidate => candidate.MatchTitle.Contains(title, StringComparison.OrdinalIgnoreCase)).ToArray();
        return matches.OrderByDescending(candidate => candidate.ResumePosition).FirstOrDefault();
    }

    private ResumeResolution? CreateResumeResolution(JsonElement item, string mediaType)
    {
        var target = GetString(item, "file");
        var resume = item.TryGetProperty("resume", out var resumeValue) && resumeValue.ValueKind == JsonValueKind.Object
            ? GetDouble(resumeValue, "position") ?? 0
            : 0;
        if (string.IsNullOrWhiteSpace(target) || resume <= 0) return null;
        var matchTitle = mediaType == "episode" ? GetString(item, "showtitle") ?? ItemTitle(item) : ItemTitle(item);
        return new ResumeResolution(
            mediaType, target, matchTitle, _safeText.Clean(ItemTitle(item)), GetInt(item, "year"),
            GetInt(item, "season"), GetInt(item, "episode"), resume);
    }

    private async Task<NextEpisodeResolution?> ResolveExactEpisodeAsync(
        RegisteredKodiInstance instance,
        string show,
        int season,
        int episode,
        CancellationToken cancellationToken)
    {
        try
        {
            var favourites = await instance.Client.CallAsync("Favourites.GetFavourites", writer =>
            {
                writer.WriteString("type", "window");
                WriteStringArray(writer, "properties", ["window", "windowparameter"]);
            }, cancellationToken);
            var matchingFavourites = GetArray(favourites, "favourites")
                .Where(item => FavouriteTitle(item).Contains(show, StringComparison.OrdinalIgnoreCase))
                .Where(IsSafeFavouriteVideoWindow)
                .Where(item => GetPluginAddonId(GetString(item, "windowparameter") ?? string.Empty) is not null)
                .ToArray();
            var selectedFavourite = SelectUniqueTitleMatch(matchingFavourites, show);
            if (selectedFavourite is { } favourite)
            {
                var target = GetString(favourite, "windowparameter");
                var addonId = target is null ? null : GetPluginAddonId(target);
                if (target is not null && addonId is not null)
                {
                    var firstLevel = await ReadAddonDirectoryAsync(instance, target, cancellationToken);
                    var candidate = SelectExactEpisode(firstLevel, season, episode, addonId, FavouriteTitle(favourite));
                    if (candidate is not null) return new NextEpisodeResolution("favourite", "exact-season-episode", candidate);
                    foreach (var folder in firstLevel.Where(IsDirectoryItem).Take(12))
                    {
                        var folderTarget = GetString(folder, "file");
                        if (folderTarget is null || !addonId.Equals(GetPluginAddonId(folderTarget), StringComparison.OrdinalIgnoreCase)) continue;
                        candidate = SelectExactEpisode(
                            await ReadAddonDirectoryAsync(instance, folderTarget, cancellationToken),
                            season, episode, addonId, FavouriteTitle(favourite));
                        if (candidate is not null) return new NextEpisodeResolution("favourite", "exact-season-episode", candidate);
                    }
                }
            }
        }
        catch (KodiRpcException exception) when (exception.Kind == KodiFailureKind.Remote)
        {
            // Continue to the library.
        }

        try
        {
            var shows = await instance.Client.CallAsync("VideoLibrary.GetTVShows", writer =>
            {
                WriteStringArray(writer, "properties", ["title"]);
                WriteSearchFilter(writer, [new SearchFilterRule("title", "contains", show)]);
                WriteLimits(writer, 0, 25);
            }, cancellationToken);
            var selectedShow = SelectUniqueTitleMatch(
                GetArray(shows, "tvshows").Where(item => GetInt(item, "tvshowid") is not null).ToArray(), show);
            if (selectedShow is { } showItem && GetInt(showItem, "tvshowid") is { } tvShowId)
            {
                var episodes = await instance.Client.CallAsync("VideoLibrary.GetEpisodes", writer =>
                {
                    writer.WriteNumber("tvshowid", tvShowId);
                    writer.WriteNumber("season", season);
                    WriteStringArray(writer, "properties", ["title", "season", "episode", "file", "playcount", "resume"]);
                    WriteLimits(writer, 0, MaximumEpisodesPerTvShow);
                }, cancellationToken);
                var candidate = SelectExactEpisode(GetArray(episodes, "episodes"), season, episode, null, null);
                if (candidate is not null) return new NextEpisodeResolution("library", "exact-season-episode", candidate);
            }
        }
        catch (KodiRpcException exception) when (exception.Kind == KodiFailureKind.Remote)
        {
            // Continue to learned add-on routes.
        }

        var routes = await _learnedRoutes.ListAsync(instance.Alias, cancellationToken);
        foreach (var route in routes.Where(route => route.Parameter is not null &&
                                                   (route.Actions & HandleAction.Browse) != 0 &&
                                                   route.Name.Contains("search", StringComparison.OrdinalIgnoreCase) &&
                                                   (route.Name.Contains("tv", StringComparison.OrdinalIgnoreCase) ||
                                                    route.Name.Contains("show", StringComparison.OrdinalIgnoreCase))))
        {
            var target = route.Target.Replace(
                FileLearnedRouteStore.InputPlaceholder, Uri.EscapeDataString(show), StringComparison.Ordinal);
            if (!route.AddonId.Equals(GetPluginAddonId(target), StringComparison.OrdinalIgnoreCase)) continue;
            var searchItems = await ReadAddonDirectoryAsync(instance, target, cancellationToken);
            var showItem = SelectUniqueTitleMatch(
                searchItems.Where(IsDirectoryItem).Where(item => ItemTitle(item).Contains(show, StringComparison.OrdinalIgnoreCase)).ToArray(), show);
            var showTarget = showItem is null ? null : GetString(showItem.Value, "file");
            if (showTarget is null || !route.AddonId.Equals(GetPluginAddonId(showTarget), StringComparison.OrdinalIgnoreCase)) continue;
            var firstLevel = await ReadAddonDirectoryAsync(instance, showTarget, cancellationToken);
            var candidate = SelectExactEpisode(firstLevel, season, episode, route.AddonId, route.AddonName);
            if (candidate is not null)
                return new NextEpisodeResolution("learned-addon-route", "exact-season-episode", candidate);
            foreach (var folder in firstLevel.Where(IsDirectoryItem).Take(12))
            {
                var folderTarget = GetString(folder, "file");
                if (folderTarget is null || !route.AddonId.Equals(GetPluginAddonId(folderTarget), StringComparison.OrdinalIgnoreCase)) continue;
                candidate = SelectExactEpisode(
                    await ReadAddonDirectoryAsync(instance, folderTarget, cancellationToken),
                    season, episode, route.AddonId, route.AddonName);
                if (candidate is not null)
                    return new NextEpisodeResolution("learned-addon-route", "exact-season-episode", candidate);
            }
        }
        return null;
    }

    private EpisodeCandidate? SelectExactEpisode(
        IEnumerable<JsonElement> items,
        int season,
        int episode,
        string? addonId,
        string? addonName)
    {
        var matches = items.Select((item, index) => CreateEpisodeCandidate(item, index, addonId, addonName))
            .Where(candidate => candidate is not null && candidate.Season == season && candidate.Episode == episode)
            .Cast<EpisodeCandidate>()
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    private static JsonElement? SelectUniqueTitleMatch(IReadOnlyList<JsonElement> items, string title)
    {
        var exact = items.Where(item => ItemTitle(item).Equals(title, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (exact.Length == 1) return exact[0];
        if (exact.Length > 1) return null;
        var contains = items.Where(item => ItemTitle(item).Contains(title, StringComparison.OrdinalIgnoreCase)).ToArray();
        return contains.Length == 1 ? contains[0] : null;
    }

    private static bool IsPlausibleMovieFavouriteTarget(string? target)
    {
        if (string.IsNullOrWhiteSpace(target)) return false;
        if (Uri.TryCreate(target, UriKind.Absolute, out var uri))
        {
            if (uri.Scheme.Equals("videodb", StringComparison.OrdinalIgnoreCase) &&
                uri.Host.Equals("movies", StringComparison.OrdinalIgnoreCase)) return true;
            if (uri.Scheme.Equals("plugin", StringComparison.OrdinalIgnoreCase) &&
                uri.Host.StartsWith("plugin.video.", StringComparison.OrdinalIgnoreCase)) return true;
            target = uri.AbsolutePath;
        }
        var extension = Path.GetExtension(target);
        return extension.ToLowerInvariant() is
            ".3g2" or ".3gp" or ".asf" or ".avi" or ".flv" or ".m2ts" or ".m4v" or ".mkv" or
            ".mov" or ".mp4" or ".mpeg" or ".mpg" or ".mts" or ".strm" or ".ts" or ".vob" or ".webm" or ".wmv";
    }

    private sealed record MovieResolution(
        string Source,
        string SelectionBasis,
        string Target,
        string? Label,
        int? Year,
        string? AddonId,
        string? AddonName);

    private sealed record ResumeResolution(
        string MediaType,
        string Target,
        string MatchTitle,
        string? Label,
        int? Year,
        int? Season,
        int? Episode,
        double ResumePosition);

    private async Task<NextEpisodeResolution?> ResolveNextEpisodeAsync(
        RegisteredKodiInstance instance,
        string show,
        CancellationToken cancellationToken)
    {
        NextEpisodeResolution? favourite = null;
        try
        {
            favourite = await ResolveFavouriteEpisodesAsync(instance, show, cancellationToken);
        }
        catch (KodiRpcException exception) when (exception.Kind == KodiFailureKind.Remote)
        {
            // An unsupported or stale favourite must not prevent the structured library fallback.
        }
        if (favourite is not null) return favourite;

        NextEpisodeResolution? library = null;
        try
        {
            library = await ResolveLibraryEpisodesAsync(instance, show, cancellationToken);
        }
        catch (KodiRpcException exception) when (exception.Kind == KodiFailureKind.Remote)
        {
            // A missing/disabled library surface may still be satisfied by a learned add-on route.
        }
        if (library is not null) return library;

        return await ResolveLearnedAddonEpisodesAsync(instance, show, cancellationToken);
    }

    private async Task<NextEpisodeResolution?> ResolveFavouriteEpisodesAsync(
        RegisteredKodiInstance instance,
        string show,
        CancellationToken cancellationToken)
    {
        var result = await instance.Client.CallAsync("Favourites.GetFavourites", writer =>
        {
            WriteStringArray(writer, "properties", ["path", "window", "windowparameter"]);
        }, cancellationToken);
        if (!result.TryGetProperty("favourites", out var favourites) || favourites.ValueKind != JsonValueKind.Array)
            return null;

        var matching = favourites.EnumerateArray()
            .Where(item => FavouriteTitle(item).Contains(show, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(item => FavouriteTitle(item).Equals(show, StringComparison.OrdinalIgnoreCase))
            .ToArray();
        foreach (var favourite in matching)
        {
            var type = GetString(favourite, "type");
            var target = GetString(favourite, "path");
            if (type == "window" && IsSafeFavouriteVideoWindow(favourite))
                target ??= GetString(favourite, "windowparameter");
            var addonId = target is null ? null : GetPluginAddonId(target);
            if (type != "window" || target is null || addonId is null) continue;

            var items = await ReadAddonDirectoryAsync(instance, target, cancellationToken);
            var candidate = SelectNextEpisode(items, addonId, FavouriteTitle(favourite));
            if (candidate is not null)
                return new NextEpisodeResolution("favourite", candidate.SelectionBasis, candidate.Candidate);
        }
        return null;
    }

    private async Task<NextEpisodeResolution?> ResolveLibraryEpisodesAsync(
        RegisteredKodiInstance instance,
        string show,
        CancellationToken cancellationToken)
    {
        var shows = await instance.Client.CallAsync("VideoLibrary.GetTVShows", writer =>
        {
            WriteStringArray(writer, "properties", ["title"]);
            WriteSearchFilter(writer, [new SearchFilterRule("title", "contains", show)]);
            WriteLimits(writer, 0, 25);
        }, cancellationToken);
        if (!shows.TryGetProperty("tvshows", out var values) || values.ValueKind != JsonValueKind.Array) return null;
        var selectedShow = values.EnumerateArray()
            .Where(value => GetInt(value, "tvshowid") is not null)
            .OrderByDescending(value => (GetString(value, "label") ?? GetString(value, "title") ?? string.Empty)
                .Equals(show, StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault();
        if (selectedShow.ValueKind == JsonValueKind.Undefined || GetInt(selectedShow, "tvshowid") is not { } tvShowId)
            return null;

        var episodes = await instance.Client.CallAsync("VideoLibrary.GetEpisodes", writer =>
        {
            writer.WriteNumber("tvshowid", tvShowId);
            WriteStringArray(writer, "properties", ["title", "showtitle", "season", "episode", "playcount", "resume", "file"]);
            WriteLimits(writer, 0, 200);
            WriteSort(writer, "episode");
        }, cancellationToken);
        var candidate = SelectNextEpisode(GetArray(episodes, "episodes"), null, show);
        return candidate is null ? null : new NextEpisodeResolution("library", candidate.SelectionBasis, candidate.Candidate);
    }

    private async Task<NextEpisodeResolution?> ResolveLearnedAddonEpisodesAsync(
        RegisteredKodiInstance instance,
        string show,
        CancellationToken cancellationToken)
    {
        var routes = await _learnedRoutes.ListAsync(instance.Alias, cancellationToken);
        foreach (var route in routes
                     .Where(route => (route.Actions & HandleAction.Browse) != 0)
                     .OrderByDescending(route => route.Name.Contains("next", StringComparison.OrdinalIgnoreCase) &&
                                                 route.Name.Contains("episode", StringComparison.OrdinalIgnoreCase)))
        {
            string target;
            if (route.Parameter is null)
            {
                if (!route.Name.Contains("next", StringComparison.OrdinalIgnoreCase) &&
                    !route.Name.Contains("episode", StringComparison.OrdinalIgnoreCase)) continue;
                target = route.Target;
            }
            else
            {
                if (!route.Name.Contains("search", StringComparison.OrdinalIgnoreCase) ||
                    (!route.Name.Contains("tv", StringComparison.OrdinalIgnoreCase) &&
                     !route.Name.Contains("show", StringComparison.OrdinalIgnoreCase))) continue;
                target = route.Target.Replace(FileLearnedRouteStore.InputPlaceholder, Uri.EscapeDataString(show), StringComparison.Ordinal);
            }
            if (GetPluginAddonId(target) is not { } targetAddonId ||
                !targetAddonId.Equals(route.AddonId, StringComparison.OrdinalIgnoreCase)) continue;

            var firstLevel = await ReadAddonDirectoryAsync(instance, target, cancellationToken);
            var direct = SelectNextEpisode(firstLevel, route.AddonId, route.AddonName);
            if (direct is not null)
                return new NextEpisodeResolution("learned-addon-route", direct.SelectionBasis, direct.Candidate);

            var showItem = firstLevel
                .Where(item => ItemTitle(item).Contains(show, StringComparison.OrdinalIgnoreCase))
                .OrderByDescending(item => ItemTitle(item).Equals(show, StringComparison.OrdinalIgnoreCase))
                .FirstOrDefault();
            var showTarget = GetString(showItem, "file");
            if (showTarget is null || GetPluginAddonId(showTarget) is not { } showAddonId ||
                !showAddonId.Equals(route.AddonId, StringComparison.OrdinalIgnoreCase)) continue;

            var secondLevel = await ReadAddonDirectoryAsync(instance, showTarget, cancellationToken);
            var candidate = SelectNextEpisode(secondLevel, route.AddonId, route.AddonName);
            if (candidate is not null)
                return new NextEpisodeResolution("learned-addon-route", candidate.SelectionBasis, candidate.Candidate);

            var combined = new List<JsonElement>();
            foreach (var season in secondLevel.Where(IsDirectoryItem).Take(12))
            {
                var seasonTarget = GetString(season, "file");
                if (seasonTarget is null || !route.AddonId.Equals(GetPluginAddonId(seasonTarget), StringComparison.OrdinalIgnoreCase)) continue;
                combined.AddRange(await ReadAddonDirectoryAsync(instance, seasonTarget, cancellationToken));
            }
            candidate = SelectNextEpisode(combined, route.AddonId, route.AddonName);
            if (candidate is not null)
                return new NextEpisodeResolution("learned-addon-route", candidate.SelectionBasis, candidate.Candidate);
        }
        return null;
    }

    private static string FavouriteTitle(JsonElement item) =>
        GetString(item, "label") ?? GetString(item, "title") ?? string.Empty;

    private static string ItemTitle(JsonElement item) =>
        GetString(item, "label") ?? GetString(item, "title") ?? string.Empty;

    private static async Task<JsonElement[]> ReadAddonDirectoryAsync(
        RegisteredKodiInstance instance,
        string target,
        CancellationToken cancellationToken)
    {
        try
        {
            var result = await instance.Client.CallAsync("Files.GetDirectory", writer =>
            {
                writer.WriteString("directory", target);
                writer.WriteString("media", "video");
                WriteStringArray(writer, "properties", ["title", "year", "playcount", "resume"]);
                WriteLimits(writer, 0, 200);
            }, cancellationToken);
            return GetArray(result, "files");
        }
        catch (KodiRpcException exception) when (exception.Kind == KodiFailureKind.Remote)
        {
            return [];
        }
    }

    private static JsonElement[] GetArray(JsonElement result, string property) =>
        result.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array
            ? values.EnumerateArray().ToArray()
            : [];

    private static bool IsDirectoryItem(JsonElement item) =>
        GetString(item, "filetype") == "directory" || (GetString(item, "file")?.EndsWith('/') ?? false);

    private EpisodeSelection? SelectNextEpisode(
        IEnumerable<JsonElement> items,
        string? addonId,
        string? addonName)
    {
        var candidates = items.Select((item, index) => CreateEpisodeCandidate(item, index, addonId, addonName))
            .Where(candidate => candidate is not null)
            .Cast<EpisodeCandidate>()
            .ToArray();
        if (candidates.Length == 0) return null;
        var available = candidates.Where(candidate => !candidate.IsMarkedUnavailable).ToArray();
        if (available.Length > 0) candidates = available;
        var ordered = candidates.OrderBy(candidate => candidate.Season ?? int.MaxValue)
            .ThenBy(candidate => candidate.Episode ?? int.MaxValue)
            .ThenBy(candidate => candidate.SourceIndex)
            .ToArray();
        var partial = ordered.FirstOrDefault(candidate => candidate.PlayCount <= 0 && candidate.ResumePosition > 0);
        if (partial is not null) return new EpisodeSelection(partial, "resume-partially-watched");
        var unwatched = ordered.FirstOrDefault(candidate => candidate.PlayCount <= 0);
        return unwatched is null ? null : new EpisodeSelection(unwatched, "first-unwatched");
    }

    private EpisodeCandidate? CreateEpisodeCandidate(
        JsonElement item,
        int sourceIndex,
        string? addonId,
        string? addonName)
    {
        var target = GetString(item, "file");
        if (string.IsNullOrWhiteSpace(target) || IsDirectoryItem(item)) return null;
        var type = GetString(item, "type") ?? GetString(item, "mediatype");
        if (type is not null && type != "episode" && GetString(item, "filetype") != "file") return null;
        var targetAddonId = GetPluginAddonId(target);
        if (addonId is not null && !addonId.Equals(targetAddonId, StringComparison.OrdinalIgnoreCase)) return null;
        var season = GetInt(item, "season") ?? GetQueryInt(target, "season");
        var episode = GetInt(item, "episode") ?? GetQueryInt(target, "episode") ?? GetQueryInt(target, "ep");
        var resumePosition = item.TryGetProperty("resume", out var resume) && resume.ValueKind == JsonValueKind.Object
            ? GetDouble(resume, "position") ?? 0
            : 0;
        var label = _safeText.Clean(ItemTitle(item));
        return new EpisodeCandidate(
            target,
            label,
            season,
            episode,
            GetInt(item, "playcount") ?? 0,
            resumePosition,
            sourceIndex,
            (label?.Contains("[COLOR red]", StringComparison.OrdinalIgnoreCase) ?? false),
            targetAddonId ?? addonId,
            addonName);
    }

    private static int? GetQueryInt(string target, string key)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri)) return null;
        foreach (var segment in uri.Query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var separator = segment.IndexOf('=');
            if (separator <= 0) continue;
            string decodedKey;
            string decodedValue;
            try
            {
                decodedKey = Uri.UnescapeDataString(segment[..separator]);
                decodedValue = Uri.UnescapeDataString(segment[(separator + 1)..]);
            }
            catch (UriFormatException)
            {
                continue;
            }
            if (decodedKey.Equals(key, StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(decodedValue, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) && value >= 0)
                return value;
        }
        return null;
    }

    private sealed record EpisodeCandidate(
        string Target,
        string? Label,
        int? Season,
        int? Episode,
        int PlayCount,
        double ResumePosition,
        int SourceIndex,
        bool IsMarkedUnavailable,
        string? AddonId,
        string? AddonName);

    private sealed record NextEpisodeResolution(string Source, string SelectionBasis, EpisodeCandidate Candidate);

    private sealed record EpisodeSelection(EpisodeCandidate Candidate, string SelectionBasis);

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

    private void EnsureLearnedRouteWrites()
    {
        if (!_options.LearnedRoutes.AllowWrite)
        {
            throw new McpException("Learned-route writes are disabled. Set Kodi:LearnedRoutes:AllowWrite=true to enable saving and forgetting routes.");
        }
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
        int? clientTake = null,
        string? addonId = null,
        string? addonName = null)
    {
        var items = new List<MediaItemSummary>();
        var sourceCount = 0;
        if (result.TryGetProperty(property, out var values) && values.ValueKind == JsonValueKind.Array)
        {
            sourceCount = values.GetArrayLength();
            var selected = values.EnumerateArray().Skip(clientSkip ?? 0);
            if (clientTake is not null) selected = selected.Take(clientTake.Value);
            foreach (var value in selected) items.Add(ParseItem(alias, value, media, property, addonId, addonName));
        }
        var limits = GetLimits(result, fallbackStart, sourceCount);
        if (clientSkip is not null)
        {
            limits = (clientSkip.Value, clientSkip.Value + items.Count, limits.Total);
        }
        return new PageSummary(alias, limits.Start, limits.End, limits.Total, items);
    }

    private MediaItemSummary ParseItem(
        string alias,
        JsonElement item,
        string media,
        string? context = null,
        string? addonId = null,
        string? addonName = null)
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
        var isSafeFavouriteWindow = context == "favourites" && type == "window" && IsSafeFavouriteVideoWindow(item);
        if (target is null && isSafeFavouriteWindow) target = GetString(item, "windowparameter");
        isSafeFavouriteWindow = isSafeFavouriteWindow && target is not null && GetPluginAddonId(target) is not null;
        var isFolder = context is "sources" or "tvshows" or "seasons" || fileType == "directory" || (target?.EndsWith('/') ?? false) || isSafeFavouriteWindow;
        var isPlayable = context is "movies" or "episodes" or "songs" || fileType == "file" || type is "movie" or "episode" or "song" or "musicvideo" ||
                         (context == "favourites" && type == "media");
        var title = GetString(item, "label") ?? GetString(item, "title");
        string? handle = null;
        var availableActions = HandleAction.None;
        if (context == "tvshows" && GetInt(item, "tvshowid") is { } tvShowId)
        {
            availableActions = HandleAction.LibraryBrowse;
            handle = _handles.Create(alias, tvShowId.ToString(CultureInfo.InvariantCulture), media, "tvshow", availableActions);
        }
        else if (context == "seasons" && GetInt(item, "tvshowid") is { } seasonTvShowId && GetInt(item, "season") is { } seasonNumber)
        {
            availableActions = HandleAction.LibraryBrowse;
            handle = _handles.Create(alias, $"{seasonTvShowId.ToString(CultureInfo.InvariantCulture)}:{seasonNumber.ToString(CultureInfo.InvariantCulture)}", media, "tvseason", availableActions);
        }
        else
        {
            var actions = string.IsNullOrWhiteSpace(target)
                ? HandleAction.None
                : (isFolder ? HandleAction.Browse : HandleAction.None) | (isPlayable ? HandleAction.Play : HandleAction.None);
            var libraryId = type == "episode" ? GetInt(item, "episodeid") : null;
            if (libraryId is not null) actions |= HandleAction.SetWatchState;
            FavouriteDescriptor? favourite = null;
            if (!string.IsNullOrWhiteSpace(target) && !string.IsNullOrWhiteSpace(title))
            {
                if (context == "favourites" && type == "media")
                {
                    favourite = new FavouriteDescriptor(title, "media", Path: target, Thumbnail: GetString(item, "thumbnail"));
                    actions |= HandleAction.RemoveFavourite;
                }
                else if (context == "favourites" && isSafeFavouriteWindow)
                {
                    favourite = new FavouriteDescriptor(title, "window", Window: GetString(item, "window"),
                        WindowParameter: target, Thumbnail: GetString(item, "thumbnail"));
                    actions |= HandleAction.RemoveFavourite;
                }
                else if (context != "favourites")
                {
                    favourite = isFolder && GetPluginAddonId(target) is not null
                        ? new FavouriteDescriptor(title, "window", Window: "videos", WindowParameter: target)
                        : new FavouriteDescriptor(title, "media", Path: target);
                    actions |= HandleAction.AddFavourite;
                }
            }
            if (actions != HandleAction.None)
            {
                availableActions = actions;
                var targetAddonId = target is null ? null : GetPluginAddonId(target);
                var effectiveAddonId = targetAddonId ?? addonId;
                var effectiveAddonName = targetAddonId is null || targetAddonId.Equals(addonId, StringComparison.OrdinalIgnoreCase)
                    ? addonName
                    : null;
                handle = _handles.Create(alias, target ?? string.Empty, media, type ?? "item", actions, effectiveAddonId, effectiveAddonName,
                    libraryId: libraryId, favourite: favourite);
            }
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
        var playCount = GetInt(item, "playcount");
        var hasWatchState = context is "episodes" or "movies" || type is "episode" or "movie";
        var watchState = hasWatchState
            ? playCount is > 0
                ? "watched"
                : resumePosition is > 0
                    ? "partially-watched"
                    : "unwatched"
            : null;
        return new MediaItemSummary(
            _safeText.Clean(title),
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
            playCount,
            watchState,
            resumePosition,
            resumeTotal,
            hasArtwork,
            isFolder,
            isPlayable,
            handle,
            DescribeItemActions(availableActions),
            FavouriteUnsupportedReason(context, type, target, title, isSafeFavouriteWindow, handle));
    }

    private static string[] DescribeItemActions(HandleAction actions)
    {
        var result = new List<string>(5);
        if ((actions & HandleAction.Browse) != 0) result.Add("browse");
        if ((actions & HandleAction.Play) != 0) result.Add("play");
        if ((actions & HandleAction.LibraryBrowse) != 0) result.Add("browse-tv-show");
        if ((actions & HandleAction.SetWatchState) != 0) result.Add("set-watch-state");
        if ((actions & HandleAction.AddFavourite) != 0) result.Add("add-favourite");
        if ((actions & HandleAction.RemoveFavourite) != 0) result.Add("remove-favourite");
        return [.. result];
    }

    private static string? FavouriteUnsupportedReason(
        string? context,
        string? type,
        string? target,
        string? title,
        bool isSafeFavouriteWindow,
        string? handle)
    {
        if (context != "favourites" || handle is not null) return null;
        if (type is "script" or "androidapp") return "Executable favourite types are intentionally non-actionable.";
        if (type == "window" && !isSafeFavouriteWindow) return "This favourite window is outside the safe video add-on boundary.";
        if (string.IsNullOrWhiteSpace(target)) return "Kodi did not return a supported favourite target.";
        if (string.IsNullOrWhiteSpace(title)) return "Kodi did not return a usable favourite title.";
        return "Kodi did not return a supported structured action for this favourite.";
    }

    private LearnedRouteSummary CreateLearnedRouteSummary(LearnedRouteEntry route)
    {
        var actions = HandleAction.ManageLearnedRoute |
                      (route.Parameter is null ? route.Actions : HandleAction.BindLearnedRoute);
        var handle = _handles.Create(route.InstanceAlias, route.Target, route.Media, route.Kind, actions,
            route.AddonId, route.AddonName, route.Name,
            route.Parameter is null ? HandleAction.None : route.Actions);
        return new LearnedRouteSummary(
            _safeText.Clean(route.AddonName),
            _safeText.Clean(route.Name, 100) ?? "learned-route",
            _safeText.Clean(route.Media, 30) ?? "files",
            _safeText.Clean(route.Kind, 80) ?? "item",
            (route.Actions & HandleAction.Browse) != 0,
            (route.Actions & HandleAction.Play) != 0,
            route.Parameter is not null,
            route.Parameter?.Name,
            route.Parameter?.MaximumLength,
            route.SavedUtc,
            handle);
    }

    private static string InferSingleInputTemplate(string target, string sampleValue)
    {
        if (target.Contains(FileLearnedRouteStore.InputPlaceholder, StringComparison.Ordinal))
        {
            throw new McpException("The observed add-on route already contains the reserved input marker.");
        }
        var queryStart = target.IndexOf('?');
        var queryEnd = queryStart >= 0 ? target.IndexOf('#', queryStart + 1) : -1;
        if (queryStart < 0) throw new McpException("The observed add-on route has no query value matching the supplied sample.");
        if (queryEnd < 0) queryEnd = target.Length;

        var matches = new List<(int Start, int Length)>();
        var segmentStart = queryStart + 1;
        while (segmentStart <= queryEnd)
        {
            var separator = target.IndexOf('&', segmentStart, queryEnd - segmentStart);
            var segmentEnd = separator >= 0 ? separator : queryEnd;
            var equals = target.IndexOf('=', segmentStart, segmentEnd - segmentStart);
            if (equals >= 0)
            {
                var rawKey = target[segmentStart..equals];
                var valueStart = equals + 1;
                var rawValue = target[valueStart..segmentEnd];
                string decodedKey;
                string decodedValue;
                try
                {
                    decodedKey = Uri.UnescapeDataString(rawKey.Replace("+", " ", StringComparison.Ordinal));
                    decodedValue = Uri.UnescapeDataString(rawValue.Replace("+", " ", StringComparison.Ordinal));
                }
                catch (UriFormatException)
                {
                    throw new McpException("The observed add-on route contains invalid query encoding.");
                }
                if (FileLearnedRouteStore.IsApprovedTextInputKey(decodedKey) && decodedValue.Equals(sampleValue, StringComparison.Ordinal))
                {
                    matches.Add((valueStart, rawValue.Length));
                }
            }
            if (separator < 0) break;
            segmentStart = separator + 1;
        }

        if (matches.Count != 1)
        {
            throw new McpException("The sample value must match exactly one complete query value in the observed add-on route.");
        }
        var match = matches[0];
        return string.Concat(target.AsSpan(0, match.Start), FileLearnedRouteStore.InputPlaceholder,
            target.AsSpan(match.Start + match.Length));
    }

    private static string ValidateLearnedRouteInput(string value, string description)
    {
        var normalized = value.Trim();
        if (normalized.Length is < 1 or > 200 || normalized.Any(char.IsControl))
        {
            throw new McpException($"{description} must be 1-200 printable characters.");
        }
        return normalized;
    }

    private static int CountOccurrences(string value, string search)
    {
        var count = 0;
        var offset = 0;
        while ((offset = value.IndexOf(search, offset, StringComparison.Ordinal)) >= 0)
        {
            count++;
            offset += search.Length;
        }
        return count;
    }

    private static string? GetPluginAddonId(string target)
    {
        if (!Uri.TryCreate(target, UriKind.Absolute, out var uri) ||
            !uri.Scheme.Equals("plugin", StringComparison.OrdinalIgnoreCase) ||
            !AddonId().IsMatch(uri.Host)) return null;
        return uri.Host;
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

    private static string? NormalizeFavouriteType(string? favouriteType)
    {
        if (favouriteType is null) return null;
        return favouriteType.Trim().ToLowerInvariant() switch
        {
            "media" => "media",
            "window" => "window",
            "script" => "script",
            "androidapp" => "androidapp",
            _ => throw new McpException("Favourite type must be media, window, script, or androidapp when provided."),
        };
    }

    private static bool IsSafeFavouriteVideoWindow(JsonElement item) =>
        (GetString(item, "window") ?? string.Empty).Trim().ToLowerInvariant() is "video" or "videos" or "10025";

    private static string GetWatchState(JsonElement item)
    {
        if (GetInt(item, "playcount") is > 0) return "watched";
        return item.TryGetProperty("resume", out var resume) && resume.ValueKind == JsonValueKind.Object && GetDouble(resume, "position") is > 0
            ? "partially-watched"
            : "unwatched";
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
