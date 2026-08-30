# KodiMCPSharp

KodiMCPSharp is a read-first MCP server for inspecting, browsing, and controlling media on one or more Kodi instances through Kodi's supported JSON-RPC HTTP interface. It is written in C# for .NET 10 and exposes a stateless Streamable HTTP MCP endpoint.

Status: browsing, guarded playback, player controls, seeking, volume, stream selection, playback modes, and playlist mutations are implemented. Every state-changing category remains disabled by default and has its own deployment gate.

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
| `kodi_browse_tv_show` | Traverse an opaque TV-show handle into seasons and playable episodes |
| `kodi_list_favourites` | List safe favourite summaries |
| `kodi_list_addons` | List enabled add-ons and issue handles for browsable roots |
| `kodi_browse` | List a closed source root or traverse a server-issued folder handle |
| `kodi_play_item` | Play one server-issued playable handle and verify observed player state; disabled by default |
| `kodi_player_control` | Pause, resume, toggle, stop, next, or previous |
| `kodi_seek` | Seek by percentage, bounded relative seconds, or Kodi step |
| `kodi_set_volume` | Set volume and/or mute and verify the observed values |
| `kodi_select_stream` | Select an enumerated audio, video, or subtitle stream; turn subtitles off |
| `kodi_set_playback_mode` | Set repeat (`off`, `one`, `all`) and/or shuffle |
| `kodi_playlist_add` | Add a server-issued playable handle to the audio or video playlist |
| `kodi_playlist_remove` | Remove a validated zero-based playlist position |
| `kodi_playlist_clear` | Clear the audio or video playlist |

No tool accepts a JSON-RPC method, JSON payload, endpoint, filesystem path, URL, add-on ID, or `plugin://` path. Kodi-originated targets remain in memory behind random, instance-scoped handles that expire after 15 minutes by default and do not survive restart.

### Movie and TV-show search

`kodi_search_library` accepts optional `query`, `year`, and `genre` filters for the `movies` and `tvshows` domains. At least one filter is required. Multiple supplied filters are combined with AND, so `query="alien"`, `year=1979`, and `genre="science fiction"` returns only entries matching all three. Kodi evaluates title and genre using its `contains` operator; year uses exact matching. Results include title, year, genres, safe playback metadata, and an opaque playable handle where Kodi supplies a playable target.

The existing episode, song, and album searches continue to use `query`; year and genre filters are rejected for those domains rather than being silently ignored.

`kodi_list_genres` discovers valid genre names before searching. `kodi_list_recent` and `kodi_list_continue_watching` provide bounded discovery views without requiring a search term. TV-show results carry an opaque library handle; pass it to `kodi_browse_tv_show` to list seasons, then pass a returned season handle to the same tool to list playable episodes. Kodi database identifiers and episode paths remain server-side.

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
- page, response-size, timeout, handle-lifetime, and handle-capacity settings have hard bounds;
- invalid TLS certificates are rejected unless explicitly opted out per instance.

Controls require `Kodi:ReadOnly=false` plus their independent `Kodi:Controls` gate: `AllowPlayback`, `AllowPlayerControl`, `AllowSeek`, `AllowVolume`, `AllowStreamSelection`, `AllowPlaybackModes`, or `AllowPlaylists`. The checked-in defaults keep `ReadOnly=true` and every gate false. Play and playlist-add accept only short-lived handles returned by search/browse; they cannot accept caller-supplied paths or URLs.

The MCP password can be supplied by a client as `Authorization: Bearer <password>` or `X-MCP-Password`. `/healthz` and `/readyz` do not reveal endpoints and remain available without that password.

## Build and run

```powershell
.\scripts\build.ps1
.\scripts\test.ps1
dotnet run --project .\src\KodiMCPSharp\KodiMCPSharp.csproj
```

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

Linux supports interactive execution and systemd. A hardened starting unit is provided at [`deploy/kodimcpsharp.service`](deploy/kodimcpsharp.service); create the service user, install files under `/opt/kodimcpsharp`, keep secrets in `/etc/kodimcpsharp/environment`, and grant the service user write access only to its log directory.

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
- a fixed 20-tool MCP catalogue with no raw-method/path/database-ID inputs and disabled-by-default control policy;
- player actions, seek bounds, volume/mute, enumerated stream selection, repeat/shuffle, and opaque-handle playlist mutations with synthetic postcondition checks;
- composable movie and TV-show title/year/genre filters, input validation, and safe genre metadata in results;
- genre discovery, recent and in-progress media views, and opaque TV-show/season hierarchy traversal.

Run `dotnet test KodiMCPSharp.slnx`. CI builds and tests on Windows and Linux and smoke-publishes `win-x64`, `linux-x64`, and `linux-arm64` artifacts with three-day retention.

## Project status and next action

Synthetic acceptance covers all media-control categories plus genre, recent, continue-watching, and TV hierarchy discovery. Live acceptance now covers every new discovery domain and opaque TV show → season → playable episode traversal. Expanded controls and representative add-on traversal remain to be verified live.

See [`PLAN.md`](PLAN.md) for open questions and milestone tracking. KodiMCPSharp is intended for a future public `Wixely/KodiMCPSharp` repository under the [MIT License](LICENSE), but this local repository has not been published.
