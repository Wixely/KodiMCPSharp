# Agent instructions

Read `README.md` and `PLAN.md` before designing or implementing changes.

## Project boundary

- KodiMCPSharp is a platform-independent Kodi MCP service. Do not add host operating-system administration, machine power, application-launch, input-switching, or non-Kodi control dependencies.
- Use Kodi's supported remote interfaces and structured media model. Do not expose arbitrary JSON-RPC requests, arbitrary `plugin://` paths, raw filesystem paths, or general input injection through MCP.
- Keep read-only browsing separate from playback control and administration. New write or consequential operations must be explicitly gated and disabled by default where appropriate.
- The project must work with Kodi running on any supported operating system reachable through its configured remote interface.

## MCPSharp family conventions

- Build KodiMCPSharp as an independently deployable Wixely MCPSharp service intended for public GitHub under MIT.
- Before scaffolding, inspect the current public `Wixely/HomeAssistantMCPSharp`, `Wixely/BambuMCPSharp`, `Wixely/RemoteAdminMCPSharp`, and `Wixely/MCPHub` repositories.
- Follow their current conventions for .NET/ASP.NET Core hosting, `ModelContextProtocol.AspNetCore` Streamable HTTP, typed upstream clients, configuration layering, health endpoints, Windows Service behavior, Serilog, feature gates, Docker, tests, release packaging, and MCPHub metadata where those conventions fit this project's risks.
- Use those repositories as design/source references only. Do not add project, package, source-copy, runtime, or release dependencies on another service repository.
- Recheck current package versions and ecosystem conventions during implementation; do not blindly copy pinned versions from an older service.

## Development defaults

- Use C# on .NET 10 with top-level statements.
- Prefer NativeAOT and a single self-contained executable when the selected MCP and Kodi dependencies support them; document exceptions.
- Use Windows PowerShell 5.1-compatible automation. Do not introduce Python, Node.js, Tailwind CSS, or tooling that requires them without explicit permission.
- Treat Windows as the primary development/host platform and Linux as secondary.
- Support interactive execution and Windows Service hosting on Windows, interactive execution and systemd on Linux, and Docker deployment.
- Include repository-local VS Code build, run, and debug configurations when the runnable solution is scaffolded.
- Prefer MIT or Apache-2.0 dependencies and document all distributed dependencies and assets.

## Security and source control

- Never commit Kodi addresses, usernames, passwords, tokens, library paths, add-on parameters, viewing history, screenshots, logs, or real media metadata.
- Store secrets in protected runtime configuration, never in sample JSON, environment templates, tool arguments, or logs.
- This local repository is intended for `Wixely/KodiMCPSharp` on public GitHub under MIT, but no remote exists yet. Do not create the remote or publish source until explicitly requested.
- Before any future public push, configure the repository-local Wixely GitHub identity and complete the full outgoing-history privacy review.
- Keep `PLAN.md` current as decisions are made; preserve assumptions and unresolved choices as such.
