# KodiMCPSharp

- Status: Local planning repository; implementation not started
- Created: 2026-08-30
- Owner: Wixely
- Service family: Wixely MCPSharp
- Intended repository: `https://github.com/Wixely/KodiMCPSharp` (not yet created)
- Intended license: MIT

KodiMCPSharp will be an independent C# and .NET 10 MCP server for structured Kodi discovery, library browsing, add-on traversal, player inspection, and guarded playback control. It will use Kodi's supported remote interfaces and work with Kodi on any supported host operating system.

## Goals

- Connect to one or more configured Kodi instances by stable operator-defined alias.
- Report Kodi availability, version/capabilities, active players, now-playing metadata, progress, streams, volume, and playback state.
- Search and browse video, music, pictures, favourites, playlists, sources, and other supported Kodi directories with bounded results.
- List installed/enabled add-ons whose metadata Kodi exposes.
- Traverse compatible installed add-ons and `plugin://` directory items without requiring add-on-specific MCP servers.
- Play an item returned by a prior search/browse operation using an opaque short-lived handle rather than an agent-constructed path.
- Provide guarded playback, seek, track/stream, volume, playlist, and bounded Kodi-navigation operations.
- Keep viewing data and credentials inside the operator's network with no cloud account or telemetry requirement.

## MCPSharp family baseline

KodiMCPSharp is intended to be a first-class, independently deployable service in the public [Wixely MCPSharp family](https://github.com/Wixely). It should follow the established family shape while retaining its own source, configuration, release, and runtime boundaries.

Use the current public services as implementation references rather than dependencies:

- [HomeAssistantMCPSharp](https://github.com/Wixely/HomeAssistantMCPSharp) for a typed HTTP upstream client, per-feature toggles, allow/deny policies, media-oriented tools, and a broad but curated tool catalogue.
- [BambuMCPSharp](https://github.com/Wixely/BambuMCPSharp) for read-only defaults, granular control gates, structured device state, health reporting, Docker/service packaging, and release conventions.
- [RemoteAdminMCPSharp](https://github.com/Wixely/RemoteAdminMCPSharp) for named remote targets, protected configuration, guarded state-changing operations, service hosting, and redacted auditing/logging boundaries.
- [MCPHub](https://github.com/Wixely/MCPHub) for managed-service metadata, install/update expectations, configuration preservation, process health, and future catalogue integration.

The initial framework baseline should match the current family: .NET 10, `Microsoft.NET.Sdk`, ASP.NET Core through `Microsoft.AspNetCore.App`, `ModelContextProtocol.AspNetCore` with Streamable HTTP, the .NET Generic Host, `Microsoft.Extensions.Hosting.WindowsServices`, `System.Text.Json`, centrally managed package versions, Serilog console/rolling-file logging, and xUnit-based tests. Recheck the current family versions and conventions when scaffolding rather than copying stale package numbers.

## Platform boundary

- No host operating-system administration, machine power control, application launching, input switching, or non-Kodi application control.
- KodiMCPSharp must be deployable and useful with Kodi on any host platform supported by the selected Kodi remote interfaces.
- All runtime behavior must originate from Kodi capabilities and Kodi-owned state.

## Initial non-goals

- Installing, updating, enabling, disabling, configuring, or removing add-ons.
- Adding third-party repositories or installing packages by URL.
- Purchases, rentals, account changes, library cleaning, deletion, or other consequential administration.
- Arbitrary Kodi JSON-RPC method invocation or caller-supplied JSON payloads.
- Returning unredacted `plugin://`, filesystem, credential-bearing, or private media paths.
- Coordinate-based UI automation, screenshots, OCR, CAPTCHA, browser login, DRM interaction, or modal keyboard workflows.
- Add-on-specific adapters until generic directory traversal has been tested and shown insufficient for a named priority workflow.

## Add-on compatibility model

- Support add-ons that expose folders and playable items through Kodi's normal structured directory model.
- Preserve folder/playable distinctions, labels, media types, resume state, artwork references, pagination/context, and available actions when Kodi supplies them safely.
- Return an explicit unsupported/capability result for custom modal UI, keyboard entry, CAPTCHA, browser authentication, DRM interaction, or other UI-only flows.
- Redact credentials, tokens, cookies, query parameters, local paths, and private plug-in parameters from results, logs, handles, and audit records.
- Keep opaque item handles bounded, scoped to one configured Kodi instance, and short-lived.

## Safety model

- Read-only inspection and browsing enabled first.
- Separate capability gates for playback control, playlist changes, volume, Kodi window navigation, add-on item activation, and later administration.
- Never expose arbitrary JSON-RPC, raw `plugin://` paths, local filesystem paths, or general-purpose Kodi actions as MCP inputs.
- Re-read player/application state after control requests and distinguish accepted from observed complete or indeterminate outcomes.
- Bind locally/conservatively, require protected configuration, and define MCP authentication before network exposure.

## Documentation

- [Implementation starting plan](PLAN.md)
- [Repository instructions](AGENTS.md)

## Recommended next action

Record the first Kodi version and two or three priority installed add-ons, then prove read-only status, active-player inspection, library search, and generic directory traversal before adding playback controls. Owner: Agent.
