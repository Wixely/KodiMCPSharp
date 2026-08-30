# Security policy

Do not include Kodi endpoints, credentials, media paths, viewing history, logs, screenshots, add-on parameters, or real library metadata in a public report.

Report suspected vulnerabilities privately to the repository owner once the public repository exists. Until then, contact the owner through the existing private channel used to receive this source.

KodiMCPSharp binds to loopback by default and refuses a non-loopback bind without an MCP password. Kodi mutations and local learned-route writes have independent disabled-by-default gates. Kodi credentials and raw paths stay in server-side configuration, memory, or the protected learned-route data directory and are not MCP inputs or normal log fields.

Treat the learned-route directory as sensitive runtime state. A route is accepted only from a server-issued handle carrying Kodi-observed add-on provenance, but the persisted target may still contain add-on query or account context. Keep the directory writable and readable only by the service account, exclude it from backups intended for public sharing, and never commit its JSON files.

Parameterized learned routes can replace only one complete value of an approved search-like query key found in a Kodi-observed route. Caller input is bounded and encoded as a single query value. Internal parameter names, templates, and expanded targets are never returned through MCP or written to ordinary logs.
