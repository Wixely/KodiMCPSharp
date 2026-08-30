# KodiMCPSharp starting plan

- Status: Ready for investigation and implementation planning
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
6. after playback controls are enabled, play one previously returned handle and verify the resulting player state.

The first slice should remain read-only until connection, pagination, redaction, add-on capability detection, and handle behavior are reliable.

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
- `kodi_get_capabilities` - effective protocol, feature, and write-control gates.

Search and browse responses should carry labels, type, playable/folder state, media summary, resume/progress state, safe artwork references, pagination metadata, and opaque handles. They must not expose raw paths by default.

## Candidate guarded control tools

- `kodi_play_item` for a server-issued playable handle.
- `kodi_player_control` using a closed action enum such as play, pause, resume, stop, next, and previous.
- `kodi_seek` with bounded absolute/relative inputs and an explicit player handle.
- `kodi_set_volume` with validated range and mute control.
- `kodi_select_stream` for an enumerated audio/subtitle stream returned by player inspection.
- `kodi_playlist_add` and other playlist mutations only behind their own gate.
- `kodi_navigate` using a small named Kodi window/action allowlist if structured workflows require it.

After control requests, query current state and return requested, accepted, observed complete, failed, timed out, or indeterminate separately.

## Add-on traversal design

- Start from Kodi-discovered add-ons, sources, favourites, or directory results rather than caller-provided plug-in identifiers.
- Store raw paths only server-side for the lifetime of an opaque handle.
- Scope handles to one Kodi instance, one result kind, allowed follow-up actions, and an expiry.
- Enforce maximum depth, item count, page size, total traversal time, and concurrent add-on requests.
- Preserve enough safe context for an agent to choose an item without exposing private URL/path parameters.
- Detect unsupported UI-only workflows and return an explanation rather than falling back silently to input injection.
- Add specific adapters only through a documented decision identifying the add-on, missing generic capability, licensing, security boundary, and maintenance cost.

## Configuration outline

- Server listen address/port and MCP authentication.
- Named Kodi instances with alias, server-side endpoint, optional protected credentials, and capability overrides.
- Connection, request, add-on traversal, and postcondition-verification timeouts.
- Read-only mode and independent gates for playback, seek, volume, playlists, navigation, add-on activation, and future administration.
- Search/browse pagination and maximum-result limits.
- Handle lifetime, capacity, and redaction policy.
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
- How long should opaque browse/play handles live, and should they survive service restart?
- Which state-changing operations require per-call confirmation in addition to deployment gates?
- Which initial release and MCPHub catalogue milestone should follow technical acceptance?

## Next actions

- [ ] Record the target Kodi version, enabled remote interfaces, authentication mode, and priority add-ons. - Owner: User / Agent
- [ ] Verify typed status, player, library, favourites, directory, and add-on methods against the target. - Owner: Agent
- [ ] Define redaction and opaque-handle contracts before returning directory or plug-in results. - Owner: Agent
- [ ] Build a fake Kodi server and read-only client vertical slice with bounded status/search/browse operations. - Owner: Agent
- [ ] Test one simple and one complex installed add-on and document the generic compatibility boundary. - Owner: Agent
- [ ] Add play-from-handle and basic player controls behind explicit gates, with postcondition checks. - Owner: User / Agent
- [ ] After technical acceptance, create `Wixely/KodiMCPSharp`, complete the public pre-push review, publish under MIT, and add MCPHub integration as a separately verified milestone. - Owner: User / Agent

## Recommended next action

Implement the typed read-only client and opaque-handle browse spike against synthetic fixtures, then validate it on the target Kodi installation and two representative add-ons. Owner: Agent.
