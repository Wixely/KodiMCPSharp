# Third-party notices

KodiMCPSharp directly uses the following distributed runtime dependencies:

| Component | License | Purpose |
| --- | --- | --- |
| .NET / Microsoft.Extensions | MIT | Runtime, hosting, configuration, Windows Service integration |
| ModelContextProtocol .NET SDK | MIT | MCP Streamable HTTP server |
| Serilog | Apache-2.0 | Structured console and rolling-file logging |

xUnit and Microsoft.NET.Test.Sdk are development/test dependencies and are not distributed with the application release.

The dependency versions are pinned centrally in `Directory.Packages.props`. The authoritative license text for each dependency is distributed by its upstream project and NuGet package.
