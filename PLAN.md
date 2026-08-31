# KodiMCPSharp starting plan

- Status: Browsing, opaque current-page capture, persistent fixed and single-input learned add-on routes, independently gated media controls, watch-state updates, and idempotent favourite changes implemented; Fen Light learned-search acceptance passed live, second-add-on acceptance pending
- Created: 2026-08-30
- Owner: TBD
- Target stack: C# and .NET 10
- Intended family: Wixely MCPSharp
- Intended host/license: Public GitHub / MIT; remote not yet created

## Product boundary

KodiMCPSharp is a standalone Kodi MCP server. It owns Kodi connection management, instance aliases, supported remote-protocol clients, structured media/search/browse models, opaque item handles, guarded player operations, and Kodi-specific diagnostics.

It operates entirely through Kodi-supported remote interfaces. Host operating-system administration, machine power control, application launching, input switching, and non-Kodi application control are outside its domain.

## First usable outcome

A trusted MCP client can:

1. list configured Kodi aliases and inspect connection/capability state;
2. inspect active players and now-playing metadata;
3. perform bounded library search and directory/favourites browsing;
4. traverse one compatible installed add-on without receiving a raw plug-in path;
5. receive opaque short-lived handles for folders and playable items; and
6. after the relevant controls are enabled, play a returned handle and operate player state, seeking, volume, streams, modes, and playlists through bounded semantic tools.

The browsing slice remained read-only until connection, pagination, redaction, and handle behavior were verified on the target Kodi installation. Guarded play-by-handle was added afterward; add-on compatibility validation remains open.

## Implemented decisions (2026-08-30)

- Use authenticated JSON-RPC HTTP POST for the initial request/response client. WebSocket notifications and reconnect state are deferred until polling behavior is measured on the target.
- Host a stateless Streamable HTTP MCP endpoint at `/mcp` by default.
- Allow zero configured instances for safe startup/package testing; tools that need Kodi require a unique default or explicit alias.
- Refuse non-loopback MCP binding unless a server password is configured.
- Keep ordinary discovered raw targets in memory behind 192-bit random handles scoped to an alias, allowed action, kind, and expiry. Handles do not survive restart. An explicitly learned fixed add-on route may persist in protected runtime storage and is reissued behind a fresh handle.
- Return artwork presence only, not Kodi artwork URLs or image paths.
- Implement library search for movies, TV shows, episodes, songs, and albums; implement favourites, enabled add-ons, sources, and handle-based directory traversal.
- Support composable title, exact-year, and genre filters for movie and TV-show searches using Kodi's typed filter rules; require at least one filter and return safe genre metadata.
- Implement bounded genre discovery, recently added views, continue-watching views, and TV show → season → episode traversal. Keep Kodi library identifiers behind action-scoped opaque handles.
- Keep all control gates false by default. Implement play-by-handle, pause/resume/toggle/stop/next/previous, bounded seek, volume/mute, enumerated stream selection, repeat/shuffle, and handle-based playlist add/remove/clear behind independent gates plus global read-only mode; raw targets remain server-side.
- Provide a dedicated, independently gated full-screen-video action using Kodi's closed `fullscreenvideo` window enum; do not expose arbitrary GUI windows or input actions.
- Persist fixed, server-observed add-on browse/play routes under `kodimcpsharp_data/addon-routes` beside the executable by default, with an operator-configurable directory, per-instance/add-on JSON documents, atomic writes, bounded route counts, and a separate disabled-by-default local-write gate. Do not accept or return raw route targets.
- Support a narrow parameterized-route contract: infer one complete string value only from an approved search-like query key in a server-observed browse route, store the internal template privately, and percent-encode bound input into a fresh opaque handle. Do not permit routing/action keys, multiple parameters, raw templates, or Kodi input injection.
- Publish as a compressed self-contained single file. Defer NativeAOT because MCP attribute discovery currently relies on runtime metadata.
- Pin the current MCPSharp-family baseline (`ModelContextProtocol.AspNetCore` 1.4.0 and .NET 10 family packages) after checking the public family repositories on 2026-08-30.

## Investigation work

- Verify Kodi's currently supported JSON-RPC transports, authentication, version negotiation, notifications/events, error behavior, and capability discovery against the target version.
- Compare HTTP request/response use with WebSocket notifications and decide the minimum reliable transport set.
- Map active player, application, library, files/directory, favourites, playlist, add-on, GUI/window, and input capabilities without exposing a generic method proxy.
- Test generic directory traversal against at least one simple folder/playable add-on and one complex or UI-heavy add-on.
- Measure pagination, timeouts, slow add-on behavior, cancellation, reconnects, stale handles, duplicate labels, artwork URLs, special paths, and command/postcondition timing.
- Determine which response fields may contain credentials, private paths, viewing history, account identifiers, or add-on tokens and define allowlist/redaction rules.

## Proposed architecture

- **Host:** `Microsoft.NET.Sdk` .NET 10 executable using `Microsoft.AspNetCore.App`, the Generic Host, `ModelContextProtocol.AspNetCore` Streamable HTTP, and health/readiness diagnostics.
- **Instance inventory:** validated configuration for one or more Kodi instances; MCP tools use aliases only.
- **Kodi protocol client:** typed JSON-RPC contracts, version/capability negotiation, cancellation, bounded responses, and classified errors.
- **Application operations:** search, browse, player, playlist, favourites, add-on traversal, and safe Kodi navigation over the typed client.
- **Handle store:** instance-scoped, short-lived opaque handles mapping reviewed results to safe follow-up actions.
- **Learned-route store:** protected runtime JSON that persists explicitly selected, Kodi-observed fixed add-on targets and recreates opaque handles after restart.
- **Capability policy:** read-only default plus independent gates for playback and other state changes.
- **Tool layer:** small semantic MCP tools; no raw JSON-RPC or caller-supplied paths.

Use the current public Wixely MCPSharp repositories as scaffolding references: HomeAssistantMCPSharp for typed upstream HTTP and per-feature policies, BambuMCPSharp for read-only/control safety and packaging, RemoteAdminMCPSharp for named remote targets and protected operations, and MCPHub for managed-service integration expectations. Retain the family conventions for central package management, JSON/environment/command-line configuration, Serilog, service hosting, Docker, xUnit tests, GitHub Actions, and release assets unless a documented Kodi-specific constraint requires a change.

These are reference implementations, not dependencies. KodiMCPSharp must not reference their projects, consume their service assemblies, copy their configuration identities, or require them at runtime.

Use source-generated `System.Text.Json` contracts where practical for trimming and NativeAOT. Do not model the entire Kodi API before the first vertical slice proves which contracts are needed.

## Candidate read-only MCP tools

- `kodi_list_instances` - aliases, availability, version, and high-level capabilities.
- `kodi_get_status` - application state, volume/mute, active players, and bounded now-playing summaries.
- `kodi_get_player` - detailed state for one active player handle.
- `kodi_search_library` - bounded typed search across selected media domains.
- `kodi_list_favourites` - safe favourite summaries and handles.
- `kodi_browse` - browse a library/source/add-on folder represented by a server-issued handle.
- `kodi_list_addons` - bounded installed/enabled add-on metadata with sensitive fields omitted.
- `kodi_capture_current_addon_page` - capture a user-opened plug-in results directory behind an opaque handle without returning its target.
- `kodi_get_capabilities` - effective protocol, feature, and write-control gates.
- `kodi_list_addon_routes` - persistent learned-route summaries and fresh opaque handles.

Search and browse responses should carry labels, type, playable/folder state, media summary, resume/progress state, safe artwork references, pagination metadata, and opaque handles. They must not expose raw paths by default.

## Candidate guarded control tools

- `kodi_play_item` for a server-issued playable handle.
- `kodi_player_control` using a closed action enum such as play, pause, resume, stop, next, and previous.
- `kodi_seek` with bounded absolute/relative inputs and an explicit player handle.
- `kodi_set_volume` with validated range and mute control.
- `kodi_select_stream` for an enumerated audio/subtitle stream returned by player inspection.
- `kodi_playlist_add` and other playlist mutations only behind their own gate.
- `kodi_navigate` using a small named Kodi window/action allowlist if structured workflows require it.
- `kodi_save_addon_route` and `kodi_forget_addon_route` for independently gated local route-store mutations.

After control requests, query current state and return requested, accepted, observed complete, failed, timed out, or indeterminate separately.

## Add-on traversal design

- Start from Kodi-discovered add-ons, sources, favourites, or directory results rather than caller-provided plug-in identifiers.
- Store raw paths only server-side. Persist only explicitly learned fixed plug-in targets that originated from a provenance-carrying Kodi handle.
- Scope handles to one Kodi instance, one result kind, allowed follow-up actions, and an expiry.
- Enforce maximum depth, item count, page size, total traversal time, and concurrent add-on requests.
- Preserve enough safe context for an agent to choose an item without exposing private URL/path parameters.
- Detect unsupported UI-only workflows and return an explanation rather than falling back silently to input injection.
- Add specific adapters only through a documented decision identifying the add-on, missing generic capability, licensing, security boundary, and maintenance cost.
- Treat learned-route documents as sensitive runtime state, invalidate or revalidate them after incompatible add-on changes, and never serialize their raw targets through MCP or logs.

## Configuration outline

- Server listen address/port and MCP authentication.
- Named Kodi instances with alias, server-side endpoint, optional protected credentials, and capability overrides.
- Connection, request, add-on traversal, and postcondition-verification timeouts.
- Read-only mode and independent gates for playback, seek, volume, playlists, navigation, add-on activation, and future administration.
- Search/browse pagination and maximum-result limits.
- Handle lifetime, capacity, and redaction policy.
- Learned-route directory, per-add-on capacity, and disabled-by-default write gate.
- Audit enablement, retention, and safe event fields.

Use JSON, environment variables, and command-line configuration consistently with the MCPSharp ecosystem. Never place real endpoints, credentials, library paths, add-on parameters, or viewing data in checked-in examples.

## Service and deployment requirements

- Run interactively and as a Windows Service on Windows.
- Run interactively and under systemd on Linux.
- Provide Docker with documented network access to externally hosted Kodi instances.
- Prefer a self-contained NativeAOT executable if MCP and JSON requirements support it; otherwise document the runtime exception.
- Include PowerShell 5.1 build/test/publish scripts and repository-local VS Code debugging.
- Add Windows and Linux CI, explicit short artifact retention, source-link/symbol/release packaging, and GitHub Release assets if public GitHub hosting is approved.

## Security and privacy requirements

- Threat-model credential theft, endpoint exposure, raw-method injection, malicious JSON/add-on metadata, handle tampering/replay, path/token leakage, denial of service, consequential playback/admin actions, and misleading command acknowledgements.
- Keep instance endpoints and credentials server-side; tools receive aliases and opaque handles.
- Require MCP authentication and conservative binding before exposure beyond localhost.
- Treat viewing history, library contents, local media paths, add-on data, artwork URLs, account identifiers, credentials, and plug-in parameters as sensitive.
- Allowlist returned fields and redact before logging, caching, handle storage diagnostics, or MCP serialization.
- Do not include raw requests/responses in ordinary logs or audit events.
- Prevent one configured Kodi instance's handles or credentials from being used against another.

## Test outline

- Typed JSON-RPC serialization and error-classification tests using captured synthetic fixtures.
- Fake Kodi server tests for authentication, version negotiation, pagination, timeouts, cancellation, notifications, malformed responses, and reconnects.
- Handle tests for opacity, scope, expiry, capacity, tamper resistance, allowed actions, and cross-instance isolation.
- Tool-schema tests proving arbitrary methods, JSON, paths, plug-in URLs, credentials, and unrestricted actions are absent.
- Redaction tests with synthetic credential-bearing paths and metadata.
- Effective-policy tests for read-only and every write category.
- Integration tests against a disposable Kodi instance with synthetic libraries/add-on fixtures where feasible.
- Manual acceptance on the target Kodi installation and priority add-ons without recording private data.
- Windows, Linux, and Docker publish/startup verification.

## Open questions

- Which Kodi version and host platforms are the first acceptance targets?
- Which two or three installed add-ons and exact workflows must work initially?
- Which JSON-RPC transport or combination is required for reliable state plus event updates?
- Is Kodi's remote interface already enabled on the target, and which authentication mode will be used?
- Which library domains are required initially: video, music, pictures, PVR/live TV, or all?
- Are favourites, playlists, subtitles/audio streams, and GUI/window navigation required in the first release?
- Should artwork be returned as safe proxied bytes, bounded URLs, or metadata only?
- What validation and version fingerprint should mark learned routes stale after an add-on update?
- Which state-changing operations require per-call confirmation in addition to deployment gates?
- Which initial release and MCPHub catalogue milestone should follow technical acceptance?

## Verification record

- 2026-08-30: Debug and Release builds completed without warnings.
- 2026-08-31: 87 xUnit tests passed using synthetic Kodi responses and an in-process HTTP transport, including every implemented control category, expanded player stream inventory, structured favourite search and idempotent favourite mutation, explicit watch-state discovery, gated single-episode and bounded bulk watch-state updates, one-call movie/exact-episode/next-episode/resume resolution, TV hierarchy handles, learned-route persistence and safe input binding, and live-discovered compatibility regressions.
- 2026-08-30: Read-only tool schema checked for raw method, JSON, path, URL, endpoint, and credential inputs.
- 2026-08-30: Windows `win-x64` self-contained single-file publish, `/healthz`, `/readyz`, MCP initialization, and seven-tool discovery smoke tests passed.
- 2026-08-30: Live Kodi 21.2.0 on Android with JSON-RPC API 13.5.0 accepted authenticated HTTP status, video/music source browsing, and bounded movie, TV show, episode, song, and album searches. No titles, paths, credentials, or viewing data were recorded.
- 2026-08-30: Guarded play-by-handle was enabled only in the ignored Android-box profile and live playback returned accepted plus observed-playing postconditions.
- 2026-08-30: Expanded MCP discovery exposed 16 bounded tools, including nine control tools, with all seven control categories enabled only in the ignored Android-box profile. Live control mutation was not attempted; the Kodi HTTP endpoint was unreachable during the final read-only status check.
- 2026-08-30: Movie and TV-show search gained optional composable title, exact-year, and genre filters with AND semantics. Synthetic contracts verify Kodi filter serialization and returned genre metadata.
- 2026-08-30: Added genre discovery, recently added media, continue-watching, and opaque-handle TV show/season browsing. The fixed MCP catalogue now contains 20 bounded tools.
- 2026-08-30: MCP 2025-06-18 runtime discovery verified all 20 tools and all four new input schemas. The configured Android endpoint accepted a TCP probe but all existing and new JSON-RPC calls failed as unavailable, so live data-contract acceptance remains pending.
- 2026-08-30: After the Android endpoint recovered, live acceptance passed all three genre domains, all four recent-media domains, all three continue-watching domains, and TV show → season → episode traversal with an opaque playable episode handle. The first pass exposed invalid `year`/`genre` episode detail requests; these were removed to match Kodi's schema and covered by a regression test.
- 2026-08-30: Added an independently gated `kodi_show_fullscreen_video` control using only Kodi's closed `fullscreenvideo` window. Live acceptance dismissed the screen overlay while preserving active playback and observed the `Fullscreen video` window.
- 2026-08-30: Added persistent fixed learned add-on routes with server-observed provenance, atomic per-instance/add-on JSON storage, fresh opaque handles after restart, and independently gated save/forget operations.
- 2026-08-30: Added one-input learned search routes by inferring a complete sample value only under approved search-like keys; binding validates and encodes caller text before issuing an opaque result handle. Multi-input and UI-dialog workflows remain deferred.
- 2026-08-30: A temporary Release service exposed all 25 previously implemented tools, including all four learned-route tools. The configured Android Kodi instance remained unavailable across three read-only probes, so Fen Light route persistence acceptance was not attempted and no route data was written.
- 2026-08-30: Live favourite acceptance found that Kodi returns favourite names in `title`, not `label`, and that Fen Light TV-show favourites are safe video-window plug-in routes. Search now supports Kodi's returned title shape, and only server-observed video-window favourites with valid plug-in targets receive browse-only handles. Live traversal found and played the next available episode of a favourite show and restored full-screen video; no title was retained.
- 2026-08-31: Added gated `kodi_play_next_episode` orchestration so the agent supplies only a show title in one MCP call. Resolution priority is safe native favourites, Kodi's TV library, then bounded learned add-on routes; arbitrary add-on crawling and UI text injection remain prohibited. Synthetic coverage verifies all three sources, partially watched precedence, opaque targets, and observed playback.
- 2026-08-31: Added separately gated `kodi_add_favourite` and `kodi_remove_favourite` operations. Both accept only action-scoped handles, inspect exact state before Kodi's toggle, verify afterward, and refuse ambiguous duplicates. Safe media and video plug-in favourites retain play/browse actions; executable favourite types remain non-actionable.
- 2026-08-31: Media summaries now advertise closed follow-up actions, and unsupported favourites return a bounded reason without an actionable handle.
- 2026-08-31: Added preview-first bulk episode watch-state ranges anchored by an opaque episode handle, with explicit inclusion rules, opt-in specials, numbered-episode filtering, a 100-change cap, bounded per-item outcomes, one post-mutation re-read, per-item remote-failure continuation, and idempotent retry behavior. The Release suite passes 79 tests.
- 2026-08-31: A fresh `win-x64` self-contained single-file package started successfully with safe defaults; `/healthz` and `/readyz` passed, symbols and safe configuration were included, and ignored local configuration was excluded. Runtime MCP 2025-06-18 discovery exposed exactly 31 tools, including favourite and bulk watch-state mutations, with preview and specials defaults verified and no forbidden raw-target inputs.
- 2026-08-31: Added one-call movie, exact-episode, and library-resume workflows. Movie and episode resolution stays favourites-first where applicable, rejects ambiguous matches, restricts movie favourites to plausible video targets, then uses the structured library and bounded learned search routes; resume uses Kodi's in-progress state and explicit `Player.Open` resume option. Kodi 21 add-on listing compatibility was restored by omitting the invalid requested `type` property while retaining Kodi's returned base field. The Release suite passes 87 tests and the source catalogue contains 34 tools.
- 2026-08-31: The current `linux-x64` self-contained package ran interactively under WSL2, passed `/healthz`, negotiated MCP 2025-06-18, and exposed all 34 tools including semantic movie, episode, and resume playback. Current `win-x64` and `linux-x64` packages both publish successfully.
- 2026-08-31: The repeatable Windows package smoke test passed safe startup, MCP 2025-06-18 negotiation, exact 34-tool discovery, forbidden-input inspection, and private local-configuration exclusion. Optional live add-on probing returns counts only.
- 2026-08-31: Kodi 21 accepted `Addons.GetAddons` after restricting the request to enabled `xbmc.python.pluginsource` add-ons and omitting `type` from the optional property list. A simple installed add-on returned its structured root, and a complex installed add-on traversed three directory levels through same-add-on opaque handles. No add-on names, menu labels, targets, or media metadata were retained. The target became unavailable before semantic search-menu acceptance.
- 2026-08-31: Live semantic navigation matched both complex add-on routes `Search → Movies` and `Search → TV Shows`, and matched the representative simple add-on's search entry, using only opaque handles. Parameter-template acceptance still requires the user to complete a known synthetic search in each add-on UI because Kodi keyboard/dialog input is deliberately outside the MCP boundary.
- 2026-08-31: Added read-only `kodi_capture_current_addon_page` using only the fixed `Container.FolderPath` info label plus enabled plug-in-source validation. Fen Light live acceptance captured a user-completed synthetic movie-search page, inferred one input, saved it, rebound and browsed a second synthetic value, reloaded and reused the route across a real process restart, and removed all temporary routes. The Release suite passes 89 tests and the packaged catalogue contains 35 tools; no raw target, real search, media metadata, or temporary route remained.
- Linux systemd and Docker runtime remain unverified.

## Next actions

### Requested media-state backlog (2026-08-30)

- [x] Add a single-call next-episode playback workflow with favourites-first, library-second, and learned-add-on-route fallback while keeping all targets and Kodi identifiers server-side. - Owner: Agent; completed: 2026-08-31
- [x] Add single-call named movie, exact episode, and resume workflows with closed inputs, opaque targets, and explicit Kodi resume behavior. - Owner: Agent; completed: 2026-08-31

- [x] Add `kodi_search_favourites` with bounded, case-insensitive title search plus a closed favourite-type filter (`media`, `window`, `script`, or `androidapp`); return only safe summaries and action-scoped opaque handles. - Owner: Agent; completed: 2026-08-30
- [x] Add independently gated semantic add/remove-favourite tools that accept only server-issued media/folder/favourite handles. Kodi JSON-RPC exposes `Favourites.AddFavourite`, whose implementation toggles an exact favourite through `AddOrRemove`; KodiMCPSharp presents separate add/remove intentions, inspects state before calling, and verifies the postcondition afterward so retries are idempotent. - Owner: Agent; completed: 2026-08-31
- [x] Preserve useful favourite operations already supported by structured targets: play a media favourite, browse a folder/window-parameter favourite where safe, filter by type, detect duplicates, and report unsupported or stale targets. Script, Android-app, arbitrary-window, raw-path, and general execution escape hatches remain unavailable. - Owner: Agent; completed: 2026-08-31
- [x] Investigate favourite rename, artwork update, and ordering separately. The current JSON-RPC surface has no dedicated remove/rename/reorder methods, so these are intentionally not emulated through GUI input or direct `favourites.xml` edits. - Owner: Agent; completed: 2026-08-31
- [x] Ensure every episode discovery surface reports explicit watch state derived from `playcount` and `resume`: `watched`, `partially-watched`, or `unwatched`, including resume position/total where Kodi supplies it. This must work when only later seasons have viewing history. - Owner: Agent; completed: 2026-08-30
- [x] Add an independently gated single-episode watch-state tool using an opaque episode handle and `VideoLibrary.SetEpisodeDetails`; support watched/unwatched, clear the resume point in either state, and re-read the episode to verify the postcondition. - Owner: Agent; completed: 2026-08-30
- [x] Add bounded bulk episode watch-state operations scoped to the selected episode's TV show. Support marking episodes before a selected episode watched, and symmetrical watched/unwatched ranges with a closed direction (`before`, `through`, `after`, or `all`) and explicit inclusion semantics. Resolve and order episodes by season/episode server-side, never accept database IDs, preview the affected count by default, cap the batch, and return bounded per-item plus complete aggregate outcomes. - Owner: Agent; completed: 2026-08-31
- [x] Define bulk semantics: season zero requires explicit opt-in, unnumbered entries are skipped and counted, multiple records at one coordinate are separate versions, mutations clear resume without writing last-played metadata, recoverable remote failures are per-item, cancellation stops before the next item, and retries re-read state and skip completed episodes. - Owner: Agent; completed: 2026-08-31

- [x] Record the target Kodi version, enabled remote interface, and authentication mode. Priority add-ons remain to be selected. - Owner: User / Agent; completed: 2026-08-30
- [x] Verify typed status, player, library, favourites, directory, and add-on methods against the target. - Owner: Agent; completed: 2026-08-31
- [x] Define redaction and opaque-handle contracts before returning directory or plug-in results. - Owner: Agent; completed: 2026-08-30
- [x] Build a fake Kodi transport and read-only client vertical slice with bounded status/search/browse operations. - Owner: Agent; completed: 2026-08-30
- [x] Test one simple and one complex installed add-on and document the generic compatibility boundary. Structured directory menus are generic; keyboard/dialog input, unstructured results, cross-add-on targets, and unclassified actions remain outside the boundary. - Owner: Agent; completed: 2026-08-31
- [x] Add play-from-handle and bounded player, seek, volume, stream, mode, and playlist controls behind independent gates with postcondition checks. - Owner: User / Agent; completed: 2026-08-30
- [x] Add genre, recent, continue-watching, and TV hierarchy discovery without exposing Kodi IDs or paths. - Owner: Agent; completed: 2026-08-30
- [x] Add persistent fixed learned add-on routes without accepting or returning raw plug-in targets. - Owner: Agent; completed: 2026-08-30
- [ ] Validate single-input learned searches against Fen Light and another representative add-on. Fen Light capture, inference, binding, browsing, restart persistence, and cleanup are accepted; the representative simple add-on still awaits a user-completed synthetic search page. Design multi-input templates only if a demonstrated workflow requires them. - Owner: Agent / User
- [ ] Run a user-approved live acceptance pass for stop/start, seek, volume, stream, mode, and playlist controls without retaining private media data. - Owner: User / Agent
- [ ] After technical acceptance, create `Wixely/KodiMCPSharp`, complete the public pre-push review, publish under MIT, and add MCPHub integration as a separately verified milestone. - Owner: User / Agent

## Recommended next action

In the Kodi UI, complete a YouTube search using the exact synthetic text `KodiMCPRouteTest`, leave its results page visible, then tell the agent it is done. The agent can capture, learn, bind, browse, restart-test, and remove the second temporary parameterized route without retaining real search or media data. Owner: User; recommended review date: 2026-09-13.
