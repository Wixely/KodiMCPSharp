# Security policy

Do not include Kodi endpoints, credentials, media paths, viewing history, logs, screenshots, add-on parameters, or real library metadata in a public report.

Report suspected vulnerabilities privately to the repository owner once the public repository exists. Until then, contact the owner through the existing private channel used to receive this source.

KodiMCPSharp is read-only in its current milestone. It binds to loopback by default and refuses a non-loopback bind without an MCP password. Kodi credentials and raw paths stay in server-side configuration or memory and are not MCP inputs or normal log fields.
