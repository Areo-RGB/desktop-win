# vscode-lsp

Gives omp's `lsp` tool the language servers VS Code already ships, without VS Code
running and without installing anything new.

## What you get

| Server | Source | Covers |
| --- | --- | --- |
| `vscode-csharp` | `ms-dotnettools.csharp` → Roslyn Language Server (`Microsoft.CodeAnalysis.LanguageServer`) | `.cs`, `.csx` |
| `vscode-css` | VS Code built-in `css-language-features` | `.css`, `.scss`, `.less` |
| `vscode-html` | VS Code built-in `html-language-features` | `.html`, `.htm` |
| `vscode-json` | VS Code built-in `json-language-features` | `.json`, `.jsonc` |
| `vscode-powershell` | `ms-vscode.powershell` → PowerShell Editor Services | `.ps1`, `.psm1`, `.psd1` |

Servers are resolved at **run time**, never pinned to a version:

- extension servers come from the newest `<publisher.name>-<version>` directory under
  `~/.vscode/extensions` (also handles `.vscode-insiders`, `.vscodium`, `.cursor`, …);
- built-in servers are globbed out of `<vscode install>/*/resources/app/extensions/...`,
  because the intermediate directory is named after a build commit hash.

VS Code updates therefore do not break the configuration.

## Prerequisites

- Node on `PATH` (the bridge is ESM).
- C# server: .NET 10 runtime (Roslyn Language Server targets `net10.0`).
- PowerShell server: `pwsh` on `PATH`.

## Install

```
omp plugin marketplace add <path>/vscode-lsp-marketplace
omp plugin install vscode-lsp@vscode-lsp-marketplace --scope project
node omp-plugins/vscode-lsp-marketplace/plugins/vscode-lsp/bin/vscode-lsp.mjs install
```

`install` writes the server entries to `.omp/lsp.json` in the current directory.
Use `install --user` to write `~/.omp/agent/lsp.json` (all projects) or
`install --path <file>` for an explicit target. `uninstall` removes the
`vscode-*` entries again, `list` shows what would actually be executed.

Then make omp pick the config up:

```
{"action":"reload","file":"*"}   # via the lsp tool
```

## Status check

```
node .../bin/vscode-lsp.mjs list
```

```
csharp	C# (Roslyn, from ms-dotnettools.csharp)	dotnet C:\Users\paul\.vscode\extensions\ms-dotnettools.csharp-2.160.4-win32-x64\.roslyn\Microsoft.CodeAnalysis.LanguageServer.dll --stdio
...
```

## Overriding discovery

| Variable | Effect |
| --- | --- |
| `VSCODE_LSP_ROOT` | Extra extensions directories (`PATH`-separated) |
| `VSCODE_LSP_VSCODE` | Extra VS Code installation directories |

## Notes / limits

- The plugin's own `lspServers` field is **not** a working distribution channel:
  installation copies `lsp.json` into the plugin cache, which is not one of the
  config lookup paths (`<cwd>/.omp/lsp.*`, `~/.omp/agent/lsp.*`, …), so omp never
  reads it. Run `vscode-lsp.mjs install` instead.
- C# project-wide semantic features (hover/definition on types, completions) need
  the solution loaded; the C# Roslyn server loads it on `initialize`. Symbol
  navigation that leaves the open document may return empty until a build exists
  (`dotnet build`) that the server can read metadata from.
- Bridge overhead is one `node` process per server, plus one `dotnet`/`pwsh`
  process behind it.
