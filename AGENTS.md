# WinUI project guidance

"C:\Users\paul\Projects\winui\learn.exe" to access microsoft documentation

- Put failures, gaps, and open todos first in summaries.

## Hub features

For features related to MCPHub, use `C:SERSPAULPROJECTSMCP-HUBMCPHUB_FORK` (SHARED HUB PROJECT, `MCP_HUB_ROOT` OVERRIDE) as the
reference implementation. Read its `AGENTS.md` and the relevant scoped guides
before changing the fork. Consult its backend routes, services, types, tests,
and dashboard behavior when implementing corresponding native WinUI features.

The native integration is in `FluentDesktop.Core/Hub/HubService.cs`,
`FluentDesktop/ViewModels/HubViewModel.cs`, `FluentDesktop/Views/HubPage.xaml`,
and `scripts/hubctl.ps1`. Keep native behavior aligned with the fork's actual
APIs and configuration semantics. Do not hard-code the Flutter checkout path.

Keep local hub settings, credentials, data, dependencies, and generated builds
out of commits. Build the fork with pnpm; do not edit generated output.

`mcp-hub/ssh-mcp` vendors tufantunc/ssh-mcp. Use its upstream npm scripts
(`npm ci`, `npm run build`) for this server. `SshMcpSettingsStore` writes the
native settings and TOML profile under `%APPDATA%\fluent_desktop`, and registers
the built stdio server as `ssh-mcp` in the local MCPHub settings. Preserve other
hub registrations and per-server metadata when updating it.
