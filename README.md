# KodiMCPSharp

KodiMCPSharp is a read-first MCP server for inspecting, browsing, and controlling media on one or more Kodi instances through Kodi's supported JSON-RPC HTTP interface. It is written in C# for .NET 10 and exposes a stateless Streamable HTTP MCP endpoint.

Status: browsing, persistent fixed and single-input learned add-on routes, guarded playback, player controls, seeking, volume, stream selection, playback modes, playlist mutations, watch-state updates, and idempotent favourite changes are implemented. Every state-changing category remains disabled by default and has its own deployment gate.

## Available tools

| Tool | Purpose |
| --- | --- |
| `kodi_list_instances` | Probe configured aliases and report JSON-RPC availability/version |
| `kodi_get_capabilities` | Report the effective read-only policy, tool catalogue, and handle policy |
| `kodi_get_status` | Read application volume/mute state and active player summaries |
| `kodi_search_library` | Search movies and TV shows by title, year, and genre; search episodes, songs, or albums by name |
| `kodi_list_genres` | List valid movie, TV-show, or music genres |
| `kodi_list_recent` | List recently added movies, episodes, albums, or songs |
| `kodi_list_continue_watching` | List in-progress movies, episodes, or TV shows with resume state |
| `kodi_list_recently_watched_shows` | List unique shows by their most recently played episode |
| `kodi_list_recently_watched_movies` | List movies in true last-played order |
| `kodi_browse_tv_show` | Traverse an opaque TV-show handle into seasons and playable episodes |
| `kodi_list_favourites` | List safe favourite summaries |
| `kodi_search_favourites` | Search favourite titles with an optional safe type filter |
| `kodi_add_favourite` | Idempotently add a server-issued media or folder handle to favourites; disabled by default |
| `kodi_remove_favourite` | Idempotently remove one exact supported favourite handle; disabled by default |
| `kodi_set_episode_watch_state` | Gated watched/unwatched update for one episode handle |
| `kodi_bulk_set_episode_watch_state` | Preview or apply a capped TV-show episode range update from one episode handle |
| `kodi_play_movie` | Resolve and play a movie by title and optional exact year in one MCP call |
| `kodi_play_episode` | Resolve and play an exact show/season/episode in one MCP call |
| `kodi_play_next_episode` | Resolve and play a show's next episode in one MCP call |
| `kodi_resume` | Resume an in-progress library movie or episode by title in one MCP call |
| `kodi_list_addons` | List enabled add-ons and issue handles for browsable roots |
| `kodi_capture_current_addon_page` | Capture a user-opened add-on results page as an opaque browse handle |
| `kodi_browse` | List a closed source root or traverse a server-issued folder handle |
| `kodi_save_addon_route` | Persist a fixed route or infer one safe search input from a server-issued add-on handle; disabled by default |
| `kodi_list_addon_routes` | List learned routes and issue fresh opaque handles after restart |
| `kodi_bind_addon_route` | Bind text to a safely inferred learned search route and issue an opaque handle |
| `kodi_forget_addon_route` | Remove a learned route using its opaque handle; disabled by default |
| `kodi_play_item` | Play one server-issued playable handle and verify observed player state; disabled by default |
| `kodi_player_control` | Pause, resume, toggle, stop, next, or previous |
| `kodi_seek` | Seek by percentage, bounded relative seconds, or Kodi step |
| `kodi_set_volume` | Set volume and/or mute and verify the observed values |
| `kodi_select_stream` | Select an enumerated audio, video, or subtitle stream; turn subtitles off |
| `kodi_set_playback_mode` | Set repeat (`off`, `one`, `all`) and/or shuffle |
| `kodi_playlist_add` | Add a server-issued playable handle to the audio or video playlist |
| `kodi_playlist_remove` | Remove a validated zero-based playlist position |
| `kodi_playlist_clear` | Clear the audio or video playlist |
| `kodi_show_fullscreen_video` | Bring active video back to Kodi's full-screen window and dismiss Kodi overlays/screensaver |

No tool accepts a JSON-RPC method, JSON payload, endpoint, filesystem path, URL, add-on ID, or `plugin://` path. Ordinary Kodi-originated targets remain in memory behind random, instance-scoped handles that expire after 15 minutes by default. An explicitly learned fixed add-on route persists server-side and is reissued as a fresh opaque handle after restart; its raw target is never returned through MCP.

### Learned add-on routes

KodiMCPSharp can remember a fixed folder or playable target discovered while browsing an add-on. Call `kodi_save_addon_route` with an opaque add-on handle and a semantic name such as `trending_movies`, then use `kodi_list_addon_routes` later to obtain a fresh handle for `kodi_browse` or `kodi_play_item`. `kodi_forget_addon_route` accepts the same kind of route handle.

For a single-text search route, save a Kodi-observed browse handle whose target already contains a known sample search and pass that literal as `sampleValue`. KodiMCPSharp accepts it only when it matches exactly one complete value under a closed set of search-like query keys; routing keys such as mode/action cannot become inputs. The stored route reports `requiresInput=true`. Pass its route handle and new text to `kodi_bind_addon_route`, then pass the returned opaque handle to `kodi_browse`. Input is length/control validated and percent-encoded as one query value, so it cannot add parameters.

Route writes are local service state and require `Kodi:LearnedRoutes:AllowWrite=true`; they do not require `Kodi:ReadOnly=false` and do not mutate Kodi. The default store is `kodimcpsharp_data/addon-routes` beside the executable and can be changed with `Kodi:LearnedRoutes:Directory`. Relative paths are resolved from the executable directory. Files are separated by Kodi alias and add-on identity, written atomically, and restricted to the service account on Unix-like systems. The complete directory is sensitive runtime state because add-on targets can contain account or query context; do not commit, log, or share it.

The parameterized slice supports one string input inferred from a complete observed search value. Live acceptance confirms semantic navigation to movie and TV-show search entries in a complex add-on and to the search entry in a representative simple add-on. After the user completes a keyboard/dialog search and leaves its results page visible, `kodi_capture_current_addon_page` reads only Kodi's fixed `Container.FolderPath` label, validates that it belongs to an enabled plug-in source, and returns an opaque browse handle suitable for `kodi_save_addon_route`. KodiMCPSharp does not return that path, synthesize unobserved routes, support multiple parameters, or automate Kodi keyboard/dialog input. Add-ons that do not expose a stable results directory still require an add-on-specific adapter.

Generic add-on compatibility is intentionally directory-based. `kodi_list_addons` asks Kodi only for enabled `xbmc.python.pluginsource` add-ons, and `kodi_browse` can traverse any bounded menu that Kodi exposes through `Files.GetDirectory` using same-add-on opaque handles. A simple installed add-on root and three nested levels of a complex installed add-on have been accepted live. Navigation stops when an add-on requires a keyboard/dialog, returns no structured directory, crosses add-on provenance, or exposes an action that cannot be classified as browse or play. Those boundaries prevent generic support from becoming arbitrary plug-in execution or GUI input injection.

### Movie and TV-show search

`kodi_search_library` accepts optional `query`, `year`, and `genre` filters for the `movies` and `tvshows` domains. At least one filter is required. Multiple supplied filters are combined with AND, so `query="alien"`, `year=1979`, and `genre="science fiction"` returns only entries matching all three. Kodi evaluates title and genre using its `contains` operator; year uses exact matching. Results include title, year, genres, safe playback metadata, and an opaque playable handle where Kodi supplies a playable target.

The existing episode, song, and album searches continue to use `query`; year and genre filters are rejected for those domains rather than being silently ignored.

`kodi_list_genres` discovers valid genre names before searching. `kodi_list_recent` and `kodi_list_continue_watching` provide bounded discovery views without requiring a search term. Movie and episode results explicitly report `watched`, `partially-watched`, or `unwatched` from Kodi's play count and resume position. Item summaries advertise their closed `availableActions`, such as `play`, `browse`, `add-favourite`, or `set-watch-state`, so clients do not need to probe invalid mutations. `kodi_search_favourites` performs case-insensitive title search and can constrain Kodi's closed favourite types (`media`, `window`, `script`, or `androidapp`) without returning their executable targets; unsupported favourite types include a bounded reason and no handle. TV-show results carry an opaque library handle; pass it to `kodi_browse_tv_show` to list seasons, then pass a returned season handle to the same tool to list playable episodes. Kodi database identifiers and episode paths remain server-side.

`kodi_list_recently_watched_shows` and `kodi_list_recently_watched_movies` are the viewing-history views. The show tool reads up to 500 played episodes in descending Kodi `lastplayed` order, deduplicates them by show while preserving that order, and returns the latest episode and timestamp for up to 50 unique shows. The movie tool returns up to 50 movies directly in `lastplayed` order. These are distinct from `kodi_list_recent`, which means recently added media, and `kodi_list_continue_watching`, which means unfinished media.

`kodi_play_next_episode` is the low-call path for requests such as “play the next episode of Example Show.” One MCP call performs a favourites-first lookup, then falls back to the Kodi TV library, and finally to bounded learned add-on routes. It resumes a partially watched episode before choosing the first unwatched episode in season/episode order. Safe video-window favourites and add-on routes remain server-observed; the agent supplies only a show title. Generic add-on fallback is available only when a browse-capable `next episodes` route or parameterized TV-search route has already been learned. KodiMCPSharp does not crawl arbitrary add-ons, accept raw plug-in paths, or inject text into add-on UI dialogs.

`kodi_play_movie` and `kodi_play_episode` provide the same single-call resolution for a named movie or exact episode. They prefer a unique safe favourite, then the structured library, then a compatible learned search route. `kodi_resume` searches Kodi's authoritative in-progress library state and opens the best title match with Kodi's explicit resume option; `auto`, `movies`, and `episodes` are the only accepted domains. All four single-call playback operations require the playback gate and never return the resolved path or plug-in target.

Favourite changes use separate add and remove tools even though Kodi's underlying method is a toggle. KodiMCPSharp reads the exact current state before calling Kodi and re-reads it afterward, making ordinary retries idempotent. Duplicate exact favourites are reported without toggling. Media favourites remain playable and safe video-window plug-in favourites remain browseable; script, Android-app, arbitrary-window, and other executable favourites receive no action handle. Rename, artwork changes, and ordering are not exposed because Kodi JSON-RPC has no dedicated operations for them.

### Bulk episode watch state

`kodi_bulk_set_episode_watch_state` anchors a range to one server-issued library episode handle. `before` excludes the anchor, `through` includes it and everything earlier, `after` excludes it, and `all` selects every eligible episode in the same TV show. Preview is the default and does not require the write gate; applying requires `Kodi:ReadOnly=false` and `Kodi:Controls:AllowWatchState=true`. At most 100 episodes may change in one call, and returned per-episode details are also capped at 100 with changing episodes prioritized. Aggregate counts still cover the complete matched range.

Only episodes with both season and episode numbers are eligible. Season zero is excluded unless `includeSpecials=true`; a season-zero relative anchor requires that explicit opt-in. Multiple Kodi records at the same season/episode coordinate are treated as separate versions and updated together when that coordinate is in range. Every actual update sets the requested play count and clears resume, while leaving last-played metadata untouched. Kodi remote failures are reported per episode where the connection remains usable; cancellation or a connection/authentication failure stops further work. Re-previewing and retrying is safe because current state is re-read and episodes already in the requested state are not mutated.

## Requirements

- .NET 10 SDK to build; published self-contained builds do not require an installed runtime.
- Kodi with **Allow control of Kodi via HTTP** enabled.
- Network access from KodiMCPSharp to Kodi's configured webserver.
- Kodi webserver authentication is strongly recommended. Never expose Kodi's control interfaces directly to the internet.

The first slice uses JSON-RPC over HTTP POST at Kodi's `/jsonrpc` endpoint. WebSocket notifications are intentionally deferred; status calls read current state directly.

## Configure

The checked-in [`KodiMCPSharp.json`](src/KodiMCPSharp/KodiMCPSharp.json) contains safe server defaults and no Kodi endpoint or credential. Put private settings in `src/KodiMCPSharp/KodiMCPSharp.Local.json` for local source runs, or beside the published executable as `KodiMCPSharp.Local.json`. That filename is ignored by Git.

Use this local-only shape, replacing every placeholder on the operator machine:

```json
{
  "Server": {
    "Password": "<set-a-strong-mcp-password-before-network-binding>"
  },
  "Kodi": {
    "DefaultAlias": "living-room",
    "LearnedRoutes": {
      "Directory": "kodimcpsharp_data/addon-routes",
      "AllowWrite": true,
      "MaximumRoutesPerAddon": 100
    },
    "Instances": [
      {
        "Alias": "living-room",
        "Endpoint": "<absolute-http-or-https-endpoint-ending-in-/jsonrpc>",
        "Username": "<kodi-webserver-user>",
        "Password": "<kodi-webserver-password>"
      }
    ]
  }
}
```

Settings can also come from environment variables with the `KODIMCP_` prefix. Double underscores represent configuration nesting; for example, `KODIMCP_Server__Password` and `KODIMCP_Kodi__Instances__0__Alias`. Prefer a protected environment file or secret injection facility rather than command-line arguments, because command lines may be visible to other users.

Configuration is validated at startup:

- aliases allow only ASCII letters, digits, `-`, and `_` and are compared case-insensitively;
- endpoints must be absolute HTTP(S) URIs ending in `/jsonrpc`;
- a non-loopback MCP bind requires `Server:Password`;
- page, response-size, timeout, handle-lifetime, handle-capacity, and learned-route limits have hard bounds;
- invalid TLS certificates are rejected unless explicitly opted out per instance.

Controls require `Kodi:ReadOnly=false` plus their independent `Kodi:Controls` gate: `AllowPlayback`, `AllowPlayerControl`, `AllowSeek`, `AllowVolume`, `AllowStreamSelection`, `AllowPlaybackModes`, `AllowPlaylists`, `AllowFullscreenVideo`, `AllowWatchState`, or `AllowFavourites`. The checked-in defaults keep `ReadOnly=true` and every gate false. Play, playlist-add, watch-state, and favourite changes accept only short-lived handles returned by search/browse; they cannot accept caller-supplied paths, URLs, or Kodi database IDs. Setting an episode watched or unwatched clears its resume point and verifies the resulting state.

Learned-route writes use the separate `Kodi:LearnedRoutes:AllowWrite` gate, which is also false in checked-in configuration. This gate may be enabled while Kodi remains read-only because it writes only KodiMCPSharp's local route store.

The MCP password can be supplied by a client as `Authorization: Bearer <password>` or `X-MCP-Password`. `/healthz` and `/readyz` do not reveal endpoints and remain available without that password.

## Build and run

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
dotnet run --project .\src\KodiMCPSharp\KodiMCPSharp.csproj
```

After publishing Windows, run `scripts\smoke-package.ps1` against the executable to verify safe startup, MCP negotiation, the expected tool catalogue, forbidden-input absence, and local-config exclusion. Add `-ProbeAddons` only with a private configured package when Kodi is reachable; the probe reports counts rather than add-on identities.

The default MCP endpoint is `http://localhost:5712/mcp`. Health endpoints are `/healthz` (process health) and `/readyz` (whether at least one Kodi alias is configured). The service is allowed to start with no Kodi instances so packaging can be smoke-tested safely.

VS Code build, test, run, and debug definitions are included under `.vscode`.

## Publish and deploy

Create a compressed, self-contained single executable plus portable symbols with the PowerShell 5.1-compatible publisher:

```powershell
.\scripts\publish.ps1 -Runtime win-x64
.\scripts\publish.ps1 -Runtime linux-x64
```

NativeAOT is not enabled in this milestone. MCP attribute-based tool discovery still depends on runtime metadata; the release is instead a self-contained single-file executable. NativeAOT should be reconsidered after the MCP boundary is proven with generated metadata.

Windows supports interactive execution and Windows Service hosting. Install the published executable with the service manager of your choice and set its working directory/configuration permissions appropriately.

Linux supports interactive execution and systemd. A hardened starting unit is provided at [`deploy/kodimcpsharp.service`](deploy/kodimcpsharp.service); create the service user, install files under `/opt/kodimcpsharp`, keep secrets in `/etc/kodimcpsharp/environment`, and grant the service user write access only to its log and learned-route data directories.

Docker builds a non-root, self-contained Linux image:

```powershell
docker compose build
docker compose up
```

The compose file publishes only to host loopback. Supply private configuration through a read-only untracked bind mount or protected environment/secret mechanism. Container networking must be able to reach Kodi; do not use public ingress to solve that connectivity.

## Security and privacy

- Media controls are independently gated and globally blocked by read-only mode. Navigation, arbitrary speed/actions, raw playlist targets, picture transforms, add-on activation, input injection, host control, and administration are not registered.
- The JSON-RPC transport is internal and calls only methods selected by the application; it is not a generic proxy.
- HTTP responses are time-limited, size-limited, depth-limited, request-ID checked, and classified without logging raw payloads.
- Ordinary output contains allowlisted summary fields. URI-like values, filesystem paths, control characters, and common credential-bearing strings are redacted.
- Persistent learned-route files retain raw add-on targets only as protected runtime state; MCP responses and ordinary logs continue to expose only opaque handles.
- Artwork is represented only as `hasArtwork`; URLs and Kodi image paths are not returned.
- Basic authentication protects Kodi credentials in transit only when the endpoint uses HTTPS. Use a trusted private network when Kodi is HTTP-only.
- `AllowInvalidTlsCertificate` is off by default and produces a warning when explicitly enabled.

Do not commit local configuration, logs, endpoints, credentials, media paths, viewing history, real metadata, screenshots, or add-on parameters. See [`SECURITY.md`](SECURITY.md).

## Testing

The xUnit suite uses only synthetic metadata and an in-process fake Kodi HTTP transport. It covers:

- JSON-RPC request shape, Basic authentication, response correlation, malformed responses, remote errors, authentication failures, and response-size limits;
- configuration and conservative network binding validation;
- path/URI redaction;
- opaque-handle action, expiry, capacity, and cross-instance isolation;
- add-on handle traversal without returning `plugin://` paths;
- a fixed 37-tool MCP catalogue with no raw-method/path/database-ID inputs and disabled-by-default control and route-write policies;
- atomic learned-route persistence, reload, write gating, add-on provenance, fixed-route reuse, and removal;
- single-string learned search inference, closed search-key policy, encoded binding, and opaque bound-route reuse;
- player actions, seek bounds, volume/mute, enumerated stream selection, repeat/shuffle, and opaque-handle playlist mutations with synthetic postcondition checks;
- composable movie and TV-show title/year/genre filters, input validation, and safe genre metadata in results;
- genre discovery, recent and in-progress media views, and opaque TV-show/season hierarchy traversal.
- case-insensitive favourite search, gated idempotent add/remove with duplicate refusal, plus explicit movie/episode watch-state discovery and gated single/bulk episode updates.
- one-call movie, exact-episode, next-episode, and resume playback with bounded resolution priority.

Run `dotnet test KodiMCPSharp.slnx`. CI builds and tests on Windows and Linux and smoke-publishes `win-x64`, `linux-x64`, and `linux-arm64` artifacts with three-day retention.

## Project status and next action

Synthetic acceptance covers all media-control categories plus genre, recent, continue-watching, recently watched shows and movies, TV hierarchy discovery, favourite search, idempotent favourite changes, bounded bulk watch-state changes, current add-on page capture, and single-call playback resolution. Fresh Windows and Linux self-contained packages publish successfully; the current Windows package exposes all 37 tools through MCP discovery. Live acceptance covers every discovery domain including show and movie viewing history, opaque TV show → season → playable episode traversal, Kodi 21 plug-in-source listing, simple and three-level complex add-on navigation, Fen Light parameter inference/binding/browsing across a process restart, and restoring active playback to Kodi's full-screen video window. Favourite mutation, bulk watch state, the latest one-call helpers, the complete control matrix, the second learned-search add-on, refreshed Linux runtime discovery, systemd, and Docker remain to be verified.

See [`PLAN.md`](PLAN.md) for open questions and milestone tracking. KodiMCPSharp is intended for a future public `Wixely/KodiMCPSharp` repository under the [MIT License](LICENSE), but this local repository has not been published.
