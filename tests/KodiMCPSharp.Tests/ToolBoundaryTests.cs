using System.Reflection;
using KodiMCPSharp.Tools;
using ModelContextProtocol.Server;

namespace KodiMCPSharp.Tests;

public sealed class ToolBoundaryTests
{
    [Fact]
    public void ToolCatalogue_ContainsOnlyExpectedBoundedTools()
    {
        var names = typeof(KodiTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Select(method => method.GetCustomAttribute<McpServerToolAttribute>())
            .Where(attribute => attribute is not null)
            .Select(attribute => attribute!.Name!)
            .Order(StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
        [
            "kodi_add_favourite", "kodi_bind_addon_route", "kodi_browse", "kodi_browse_movie_set", "kodi_browse_tv_show", "kodi_bulk_set_episode_watch_state", "kodi_capture_current_addon_page", "kodi_check_addon_routes", "kodi_forget_addon_route", "kodi_get_capabilities",
            "kodi_get_queue", "kodi_get_status", "kodi_get_video_details", "kodi_library_maintenance", "kodi_list_addon_routes", "kodi_list_addons", "kodi_list_continue_watching",
            "kodi_list_favourites", "kodi_list_genres", "kodi_list_instances", "kodi_list_movie_sets", "kodi_list_pvr_channels", "kodi_list_pvr_recordings", "kodi_list_pvr_timers", "kodi_list_recent", "kodi_list_recently_played_music", "kodi_list_recently_watched_movies", "kodi_list_recently_watched_shows", "kodi_list_up_next", "kodi_list_video_tags",
            "kodi_move_queue_item", "kodi_play_episode", "kodi_play_item", "kodi_play_movie", "kodi_play_music", "kodi_play_next_episode", "kodi_play_pvr", "kodi_play_random", "kodi_player_control",
            "kodi_playlist_add", "kodi_playlist_clear", "kodi_playlist_remove", "kodi_remove_favourite", "kodi_resume",
            "kodi_save_addon_route", "kodi_search_favourites", "kodi_search_library", "kodi_seek", "kodi_select_stream", "kodi_set_episode_watch_state", "kodi_set_playback_mode", "kodi_set_volume",
            "kodi_show_fullscreen_video",
        ], names);
    }

    [Fact]
    public void ToolInputs_DoNotExposeRawMethodJsonOrPathEscapeHatches()
    {
        var forbidden = new[] { "method", "json", "path", "plugin", "directory", "url", "endpoint", "credential", "password" };
        var parameterNames = typeof(KodiTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Where(method => method.GetCustomAttribute<McpServerToolAttribute>() is not null)
            .SelectMany(method => method.GetParameters())
            .Select(parameter => parameter.Name ?? string.Empty)
            .ToArray();

        Assert.DoesNotContain(parameterNames, name => forbidden.Contains(name, StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void SearchTool_OffersOptionalTitleYearAndGenreFilters()
    {
        var method = typeof(KodiTools).GetMethod(nameof(KodiTools.SearchLibrary))!;
        var parameters = method.GetParameters().ToDictionary(parameter => parameter.Name!, StringComparer.Ordinal);

        Assert.Null(parameters["query"].DefaultValue);
        Assert.Null(parameters["year"].DefaultValue);
        Assert.Null(parameters["genre"].DefaultValue);
        Assert.Equal("movies", parameters["domain"].DefaultValue);
    }
}
