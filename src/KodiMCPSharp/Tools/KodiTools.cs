using System.ComponentModel;
using System.Text.Json;
using KodiMCPSharp.Services;
using ModelContextProtocol.Server;

namespace KodiMCPSharp.Tools;

[McpServerToolType]
public static class KodiTools
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = false,
    };

    [McpServerTool(Name = "kodi_list_instances"),
     Description("List configured Kodi aliases and probe their JSON-RPC availability. Endpoints and credentials are never returned.")]
    public static async Task<string> ListInstances(
        KodiService service,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.ListInstancesAsync(cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_get_capabilities"),
     Description("Show effective KodiMCPSharp features, read-only state, disabled control gates, and opaque-handle policy.")]
    public static string GetCapabilities(KodiService service) =>
        JsonSerializer.Serialize(service.GetCapabilities(), JsonOptions);

    [McpServerTool(Name = "kodi_get_status"),
     Description("Get application volume/mute state plus active players and safe now-playing metadata for one configured Kodi alias.")]
    public static async Task<string> GetStatus(
        KodiService service,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.GetStatusAsync(alias, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_search_library"),
     Description("Search a bounded Kodi library domain. Movies and TV shows support combinable title, year, and genre filters. Returns safe metadata and opaque short-lived item handles; never raw media paths.")]
    public static async Task<string> SearchLibrary(
        KodiService service,
        [Description("Text to find in media titles (1-200 printable characters). Optional when filtering movies or TV shows by year or genre.")] string? query = null,
        [Description("Closed library domain: movies, tvshows, episodes, songs, or albums.")] string domain = "movies",
        [Description("Exact release year from 1 to 9999. Supported for movies and tvshows.")] int? year = null,
        [Description("Genre text to match (1-100 printable characters). Supported for movies and tvshows.")] string? genre = null,
        [Description("Zero-based result page.")] int page = 0,
        [Description("Requested items per page; clamped to the configured maximum.")] int pageSize = 25,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.SearchLibraryAsync(alias, query, domain, year, genre, page, pageSize, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_list_genres"),
     Description("List bounded, safe genre names from Kodi's movie, TV-show, or music library.")]
    public static async Task<string> ListGenres(
        KodiService service,
        [Description("Closed genre domain: movies, tvshows, or music.")] string domain = "movies",
        [Description("Zero-based result page.")] int page = 0,
        [Description("Requested items per page; clamped to the configured maximum.")] int pageSize = 25,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.ListGenresAsync(alias, domain, page, pageSize, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_list_recent"),
     Description("List recently added movies, episodes, albums, or songs with bounded safe metadata and playable handles where available.")]
    public static async Task<string> ListRecent(
        KodiService service,
        [Description("Closed recent-media domain: movies, episodes, albums, or songs.")] string domain = "movies",
        [Description("Zero-based result page.")] int page = 0,
        [Description("Requested items per page; clamped to the configured maximum.")] int pageSize = 25,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.ListRecentAsync(alias, domain, page, pageSize, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_list_continue_watching"),
     Description("List in-progress movies, episodes, or TV shows with resume state and bounded safe metadata.")]
    public static async Task<string> ListContinueWatching(
        KodiService service,
        [Description("Closed continue-watching domain: movies, episodes, or tvshows.")] string domain = "movies",
        [Description("Zero-based result page.")] int page = 0,
        [Description("Requested items per page; clamped to the configured maximum.")] int pageSize = 25,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.ListContinueWatchingAsync(alias, domain, page, pageSize, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_browse_tv_show"),
     Description("Browse a TV-show handle into seasons, or a season handle into playable episodes. Only server-issued library handles are accepted.")]
    public static async Task<string> BrowseTvShow(
        KodiService service,
        [Description("Opaque TV-show or season handle returned by search, continue-watching, or an earlier TV-show browse.")] string handle,
        [Description("Zero-based result page.")] int page = 0,
        [Description("Requested items per page; clamped to the configured maximum.")] int pageSize = 25,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.BrowseTvShowAsync(alias, handle, page, pageSize, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_list_favourites"),
     Description("List a bounded page of Kodi favourites with safe metadata and opaque handles where a structured follow-up is supported.")]
    public static async Task<string> ListFavourites(
        KodiService service,
        [Description("Zero-based result page.")] int page = 0,
        [Description("Requested items per page; clamped to the configured maximum.")] int pageSize = 25,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.ListFavouritesAsync(alias, page, pageSize, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_search_favourites"),
     Description("Search Kodi favourites by title with an optional closed type filter. Returns bounded safe summaries; executable targets are never exposed.")]
    public static async Task<string> SearchFavourites(
        KodiService service,
        [Description("Case-insensitive favourite title text to find (1-200 printable characters).")] string query,
        [Description("Optional closed favourite type: media, window, script, or androidapp.")] string? favouriteType = null,
        [Description("Zero-based result page.")] int page = 0,
        [Description("Requested items per page; clamped to the configured maximum.")] int pageSize = 25,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.SearchFavouritesAsync(alias, query, favouriteType, page, pageSize, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_add_favourite"),
     Description("Add a media item or folder to Kodi favourites using a server-issued handle, with precondition and postcondition checks for idempotent retries. Disabled by default.")]
    public static async Task<string> AddFavourite(
        KodiService service,
        [Description("Opaque playable or browseable handle returned by library search, browsing, or an add-on result.")] string handle,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.AddFavouriteAsync(alias, handle, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_remove_favourite"),
     Description("Remove one exact supported Kodi favourite using its server-issued favourite handle, with duplicate detection and idempotent verification. Disabled by default.")]
    public static async Task<string> RemoveFavourite(
        KodiService service,
        [Description("Opaque favourite handle returned by favourite listing or search. Unsupported executable favourites do not receive handles.")] string handle,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.RemoveFavouriteAsync(alias, handle, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_set_episode_watch_state"),
     Description("Set one library episode watched or unwatched using a server-issued episode handle, clear its resume point, and verify Kodi's postcondition. Disabled by default.")]
    public static async Task<string> SetEpisodeWatchState(
        KodiService service,
        [Description("Opaque episode handle returned by episode search, recent/continue views, or TV-show browsing.")] string handle,
        [Description("Closed target state: watched or unwatched.")] string state,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.SetEpisodeWatchStateAsync(alias, handle, state, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_bulk_set_episode_watch_state"),
     Description("Preview or apply a bounded watched/unwatched change to episodes before, through, after, or across the TV show containing a server-issued episode handle. Preview is the default; mutation uses the watch-state gate.")]
    public static async Task<string> BulkSetEpisodeWatchState(
        KodiService service,
        [Description("Opaque library episode handle that anchors the TV show and range.")] string handle,
        [Description("Closed target state: watched or unwatched.")] string state,
        [Description("Closed range relative to the selected episode: before excludes it, through includes it, after excludes it, and all includes every eligible episode.")] string range = "through",
        [Description("When true, return the bounded change plan without mutating Kodi. Set false to apply it.")] bool preview = true,
        [Description("Include numbered season-zero specials. False by default.")] bool includeSpecials = false,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.BulkSetEpisodeWatchStateAsync(
            alias, handle, state, range, preview, includeSpecials, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_list_addons"),
     Description("List enabled Kodi add-ons with bounded, redacted metadata. Browsable add-ons receive an opaque root handle.")]
    public static async Task<string> ListAddons(
        KodiService service,
        [Description("Zero-based result page.")] int page = 0,
        [Description("Requested items per page; clamped to the configured maximum.")] int pageSize = 25,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.ListAddonsAsync(alias, page, pageSize, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_capture_current_addon_page"),
     Description("Capture Kodi's currently visible plug-in-source directory as an opaque browse handle. This bridges a user-completed keyboard/dialog workflow without exposing its internal plug-in URL or injecting input.")]
    public static async Task<string> CaptureCurrentAddonPage(
        KodiService service,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.CaptureCurrentAddonPageAsync(alias, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_browse"),
     Description("List Kodi sources for a closed media root, or browse a folder/add-on using a server-issued opaque handle. Caller-supplied paths are not accepted.")]
    public static async Task<string> Browse(
        KodiService service,
        [Description("Opaque folder handle from an earlier result. Omit to list the selected root's sources.")] string? handle = null,
        [Description("Closed source root used when handle is omitted: video, music, pictures, files, or programs.")] string root = "video",
        [Description("Zero-based result page.")] int page = 0,
        [Description("Requested items per page; clamped to the configured maximum.")] int pageSize = 25,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.BrowseAsync(alias, handle, root, page, pageSize, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_save_addon_route"),
     Description("Persist a fixed browse or play route from a server-issued add-on handle. Raw add-on targets are never accepted or returned. Requires Kodi:LearnedRoutes:AllowWrite=true.")]
    public static async Task<string> SaveAddonRoute(
        KodiService service,
        [Description("Opaque folder or playable handle discovered by browsing a Kodi add-on.")] string handle,
        [Description("Semantic route name, such as search_movies or trending_tvshows; 1-100 safe characters.")] string name,
        [Description("Optional literal value already present in exactly one complete query value of the observed route. When supplied, that value becomes the route's single typed input without exposing its internal parameter name.")] string? sampleValue = null,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.SaveAddonRouteAsync(alias, handle, name, sampleValue, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_list_addon_routes"),
     Description("List persistent learned add-on routes and issue fresh opaque handles for browsing, playback, or gated removal.")]
    public static async Task<string> ListAddonRoutes(
        KodiService service,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.ListAddonRoutesAsync(alias, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_bind_addon_route"),
     Description("Bind validated text to a parameterized learned add-on route and return a fresh opaque browse/play handle. The internal plug-in parameter and target remain server-side.")]
    public static string BindAddonRoute(
        KodiService service,
        [Description("Opaque parameterized learned-route handle returned by kodi_save_addon_route or kodi_list_addon_routes.")] string handle,
        [Description("Text to bind to the learned route's single string input; 1-200 printable characters.")] string input,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null) =>
        JsonSerializer.Serialize(service.BindAddonRoute(alias, handle, input), JsonOptions);

    [McpServerTool(Name = "kodi_forget_addon_route"),
     Description("Remove one persistent learned add-on route using its server-issued handle. Requires Kodi:LearnedRoutes:AllowWrite=true.")]
    public static async Task<string> ForgetAddonRoute(
        KodiService service,
        [Description("Opaque learned-route handle returned by kodi_save_addon_route or kodi_list_addon_routes.")] string handle,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.ForgetAddonRouteAsync(alias, handle, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_play_item"),
     Description("Play one server-issued playable handle and verify the observed player state. Requires Kodi:ReadOnly=false and Kodi:Controls:AllowPlayback=true; both block playback by default.")]
    public static async Task<string> PlayItem(
        KodiService service,
        [Description("Opaque playable handle returned by kodi_search_library or kodi_browse. Raw paths and URLs are not accepted.")] string handle,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.PlayItemAsync(alias, handle, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_play_next_episode"),
     Description("Find and play the next episode of a TV show in one MCP call. Searches safe Kodi favourites first, then the Kodi library, then bounded learned add-on routes. Requires the playback gate.")]
    public static async Task<string> PlayNextEpisode(
        KodiService service,
        [Description("TV-show title to resolve (1-200 printable characters).")] string show,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.PlayNextEpisodeAsync(alias, show, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_play_movie"),
     Description("Find and play a movie by title in one MCP call, with an optional exact year. Searches safe favourites first when unambiguous, then the Kodi library, then bounded learned add-on routes. Requires the playback gate.")]
    public static async Task<string> PlayMovie(
        KodiService service,
        [Description("Movie title to resolve (1-200 printable characters).")] string title,
        [Description("Optional exact release year from 1 to 9999.")] int? year = null,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.PlayMovieAsync(alias, title, year, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_play_episode"),
     Description("Find and play one exact TV episode by show title, season, and episode in one MCP call. Searches safe favourites first, then the Kodi library, then bounded learned add-on routes. Requires the playback gate.")]
    public static async Task<string> PlayEpisode(
        KodiService service,
        [Description("TV-show title to resolve (1-200 printable characters).")] string show,
        [Description("Season number from 0 to 1000.")] int season,
        [Description("Episode number from 0 to 10000.")] int episode,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.PlayEpisodeAsync(alias, show, season, episode, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_resume"),
     Description("Find and resume a partially watched library movie or episode by title in one MCP call. Uses Kodi's saved resume point and requires the playback gate.")]
    public static async Task<string> Resume(
        KodiService service,
        [Description("Movie title, TV-show title, or episode title to resolve (1-200 printable characters).")] string title,
        [Description("Closed domain: auto, movies, or episodes.")] string domain = "auto",
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.ResumeAsync(alias, title, domain, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_player_control"),
     Description("Pause, resume, toggle, stop, or skip the active item using a player ID from kodi_get_status. Requires the player-control gate.")]
    public static async Task<string> PlayerControl(
        KodiService service,
        [Description("Player ID from kodi_get_status: 0, 1, or 2.")] int playerId,
        [Description("Closed action: pause, resume, toggle, stop, next, or previous.")] string action,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.PlayerControlAsync(alias, playerId, action, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_seek"),
     Description("Seek by bounded percentage, relative seconds, or a Kodi step. Requires the seek gate.")]
    public static async Task<string> Seek(
        KodiService service,
        [Description("Player ID from kodi_get_status: 0, 1, or 2.")] int playerId,
        [Description("Closed mode: percentage, relative_seconds, smallforward, smallbackward, bigforward, or bigbackward.")] string mode,
        [Description("Percentage (0-100) or whole relative seconds (-86400 to 86400); ignored for step modes.")] double value = 0,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.SeekAsync(alias, playerId, mode, value, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_set_volume"),
     Description("Set bounded application volume, mute state, or both and verify the observed values. Requires the volume gate.")]
    public static async Task<string> SetVolume(
        KodiService service,
        [Description("Volume from 0 to 100. Omit to leave unchanged.")] int? volume = null,
        [Description("Mute state. Omit to leave unchanged.")] bool? muted = null,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.SetVolumeAsync(alias, volume, muted, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_select_stream"),
     Description("Select an enumerated audio, video, or subtitle stream reported by kodi_get_status. Requires the stream-selection gate.")]
    public static async Task<string> SelectStream(
        KodiService service,
        [Description("Player ID from kodi_get_status: 0, 1, or 2.")] int playerId,
        [Description("Closed stream kind: audio, video, or subtitle.")] string kind,
        [Description("Stream index reported by kodi_get_status; -1 turns subtitles off.")] int index,
        [Description("Enable subtitles after selecting a subtitle stream.")] bool enableSubtitles = true,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.SelectStreamAsync(alias, playerId, kind, index, enableSubtitles, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_set_playback_mode"),
     Description("Set repeat and/or shuffle for a player and verify the observed mode. Requires the playback-modes gate.")]
    public static async Task<string> SetPlaybackMode(
        KodiService service,
        [Description("Player ID from kodi_get_status: 0, 1, or 2.")] int playerId,
        [Description("Repeat mode: off, one, or all. Omit to leave unchanged.")] string? repeat = null,
        [Description("Shuffle state. Omit to leave unchanged.")] bool? shuffled = null,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.SetPlaybackModeAsync(alias, playerId, repeat, shuffled, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_playlist_add"),
     Description("Add a server-issued playable handle to Kodi's audio or video playlist. Requires the playlists gate.")]
    public static async Task<string> PlaylistAdd(
        KodiService service,
        [Description("Opaque playable handle from kodi_search_library or kodi_browse.")] string handle,
        [Description("Closed playlist media: audio or video.")] string media,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.PlaylistAddAsync(alias, handle, media, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_playlist_remove"),
     Description("Remove one zero-based position from Kodi's audio or video playlist. Requires the playlists gate.")]
    public static async Task<string> PlaylistRemove(
        KodiService service,
        [Description("Closed playlist media: audio or video.")] string media,
        [Description("Zero-based playlist position.")] int position,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.PlaylistRemoveAsync(alias, media, position, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_playlist_clear"),
     Description("Clear Kodi's audio or video playlist. Requires the playlists gate.")]
    public static async Task<string> PlaylistClear(
        KodiService service,
        [Description("Closed playlist media: audio or video.")] string media,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.PlaylistClearAsync(alias, media, cancellationToken), JsonOptions);

    [McpServerTool(Name = "kodi_show_fullscreen_video"),
     Description("Bring the active video player to Kodi's full-screen video window, dismissing Kodi screen overlays such as its screensaver. Requires the fullscreen-video gate.")]
    public static async Task<string> ShowFullscreenVideo(
        KodiService service,
        [Description("Configured Kodi alias. May be omitted only when a default or single instance is configured.")] string? alias = null,
        CancellationToken cancellationToken = default) =>
        JsonSerializer.Serialize(await service.ShowFullscreenVideoAsync(alias, cancellationToken), JsonOptions);
}
