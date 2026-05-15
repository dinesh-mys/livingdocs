# LivingDocs

[![NuGet Version](https://img.shields.io/nuget/v/LivingDocs.Mcp)](https://www.nuget.org/packages/LivingDocs.Mcp)
[![NuGet Downloads](https://img.shields.io/nuget/dt/LivingDocs.Mcp)](https://www.nuget.org/packages/LivingDocs.Mcp)
[![GitHub Issues](https://img.shields.io/github/issues/dinesh-mys/livingdocs)](https://github.com/dinesh-mys/livingdocs/issues)
[![GitHub Discussions](https://img.shields.io/github/discussions/dinesh-mys/livingdocs)](https://github.com/dinesh-mys/livingdocs/discussions)
[![License: MIT](https://img.shields.io/badge/license-MIT-blue.svg)](LICENSE)

AI-powered documentation health monitor. Detects stale code comments, answers questions about your codebase, suggests updates using Claude, and works as an MCP server for Claude Desktop, Claude Code, and GitHub Copilot.

## Install

Requires [.NET 10 SDK](https://dotnet.microsoft.com/download).

```bash
dotnet tool install -g LivingDocs.Mcp
```

## Tools

| Tool | Tier | Description |
|------|------|-------------|
| `scan_repo` | Free | Scan a repo and list stale doc files with staleness % (0–100%) |
| `query_docs` | Free | Answer natural-language questions about your codebase using its doc comments |
| `suggest_doc_update` | Free | Ask Claude to rewrite a stale doc comment |
| `sync_confluence` | Pro | Write updated docs back to Confluence pages |
| `scan_org` | Pro | Scan all repos in a GitHub org and return an org-wide report |

## Quick test

```bash
livingdocs-mcp scan /path/to/your/repo
```

```
Scanning /path/to/repo ...
Files examined : 12
Stale docs     : 2

  [██████████]  100%  src/Tax.cs
               doc updated : 2024-09-01
               code changed: 2025-01-15

  [████░░░░░░]   40%  src/Auth.cs
               doc updated : 2024-12-01
               code changed: 2025-01-28
```

---

## Setup — Claude Desktop

Edit `~/Library/Application Support/Claude/claude_desktop_config.json`:

```json
{
  "mcpServers": {
    "livingdocs": {
      "command": "livingdocs-mcp",
      "env": {
        "ANTHROPIC_API_KEY": "sk-ant-..."
      }
    }
  }
}
```

Restart Claude Desktop. The `scan_repo`, `query_docs`, and `suggest_doc_update` tools will appear under the **+** menu.

---

## Setup — Claude Code

Add a `.mcp.json` file to your project root:

```json
{
  "mcpServers": {
    "livingdocs": {
      "command": "livingdocs-mcp",
      "env": {
        "ANTHROPIC_API_KEY": "sk-ant-..."
      }
    }
  }
}
```

Or register globally:

```bash
claude mcp add livingdocs -s user -- livingdocs-mcp
```

Then in Claude Code chat:

```
scan this repo for stale docs              → uses scan_repo
what does the Auth module do?              → uses query_docs
suggest a fix for src/Tax.cs              → uses suggest_doc_update
```

---

## Setup — GitHub Copilot (VS Code)

VS Code 1.99+ supports MCP servers in Copilot agent mode. Add a `.vscode/mcp.json` file to your project:

```json
{
  "servers": {
    "livingdocs": {
      "type": "stdio",
      "command": "livingdocs-mcp",
      "env": {
        "ANTHROPIC_API_KEY": "sk-ant-..."
      }
    }
  }
}
```

Open the Copilot chat panel, switch to **Agent mode**, and the `scan_repo`, `query_docs`, and `suggest_doc_update` tools will be available.

> **Note:** The `@livingdocs` Copilot Extension agent is built and deployed but pending GitHub Copilot plan availability for agent URL registration. The MCP server path above works fully in the meantime.

---

## Pro tier

Pro tools require a license key and your Polar.sh organization ID:

```bash
export LIVINGDOCS_LICENSE_KEY=LD-xxxx-xxxx-xxxx
export POLAR_ORGANIZATION_ID=<your-polar-org-uuid>
```

Or add them to the `env` block in your config:

```json
{
  "mcpServers": {
    "livingdocs": {
      "command": "livingdocs-mcp",
      "env": {
        "ANTHROPIC_API_KEY": "sk-ant-...",
        "LIVINGDOCS_LICENSE_KEY": "LD-xxxx-xxxx-xxxx",
        "POLAR_ORGANIZATION_ID": "<your-polar-org-uuid>",
        "POLAR_ACCESS_TOKEN": "<your-polar-access-token>"
      }
    }
  }
}
```

`POLAR_ORGANIZATION_ID` is your org UUID from **Polar Dashboard → Settings**. `POLAR_ACCESS_TOKEN` is an org-level access token (Polar Dashboard → Settings → API Keys). Without these, the key is accepted based on format only — useful for local development.

Get a license at **[buy.polar.sh — LivingDocs Pro](https://buy.polar.sh/polar_cl_LcRKdosjt3TwpUkKBSoDOPOP6ea6ArOfKpyB91MSdiM)**.

---

## Pro tool — sync_confluence

Detects stale doc comments in a file, generates updated documentation via Claude, and writes the results to the matching Confluence page (creates it if it doesn't exist).

**Required env vars:**

| Variable | Description |
|----------|-------------|
| `CONFLUENCE_BASE_URL` | Your Confluence Cloud URL, e.g. `https://mycompany.atlassian.net/wiki` |
| `CONFLUENCE_EMAIL` | Atlassian account email |
| `CONFLUENCE_API_TOKEN` | API token from [id.atlassian.com/manage-profile/security/api-tokens](https://id.atlassian.com/manage-profile/security/api-tokens) |
| `CONFLUENCE_SPACE_KEY` | Target space key, e.g. `DEV` (visible in the space URL) |

**Usage in Claude:**
```
sync_confluence on /path/to/repo for src/Tax.cs
```

Creates or updates a `[LivingDocs] Tax` page in your Confluence space with Claude's suggested documentation updates and confidence scores.

---

## Pro tool — scan_org

Scans every repository in a GitHub organisation and returns an org-wide staleness report. Shallow-clones each repo, runs stale-doc detection, then cleans up.

**Required env vars:**

| Variable | Description |
|----------|-------------|
| `GITHUB_TOKEN` | Personal access token with `repo` and `read:org` scopes — required for private repos and higher API rate limits |

**Usage in Claude:**
```
scan_org on my-company
```

**Example output:**
```
Org: my-company — 8 repos scanned | 142 files | 5 stale docs

⚠️ Repos with stale docs
api-service — 3 stale / 48 files
  - src/Auth.cs (100%)
  - src/Tax.cs (80%)
  - src/Payment.cs (40%)

payment-service — 2 stale / 31 files
  - src/Refund.cs (60%)
  - src/Invoice.cs (20%)

✅ Clean repos (6)
frontend, user-service, admin-api, ...
```

---

## Environment variables

| Variable | Required | Description |
|----------|----------|-------------|
| `ANTHROPIC_API_KEY` | Yes (for `query_docs` and `suggest_doc_update`) | Anthropic API key |
| `LIVINGDOCS_LICENSE_KEY` | Yes (for Pro tools) | License key from [buy.polar.sh — LivingDocs Pro](https://buy.polar.sh/polar_cl_LcRKdosjt3TwpUkKBSoDOPOP6ea6ArOfKpyB91MSdiM) |
| `POLAR_ORGANIZATION_ID` | Yes (for live license validation) | Polar.sh organization UUID — find it in **Polar Dashboard → Settings** |
| `POLAR_ACCESS_TOKEN` | Yes (for live license validation) | Polar.sh org access token — required for the validation API call |
| `POLAR_BENEFIT_ID` | No | Polar.sh benefit UUID for stricter per-benefit validation |
| `GITHUB_TOKEN` | Yes (for `scan_org` with private repos) | GitHub personal access token with `repo` + `read:org` scopes |
| `CONFLUENCE_BASE_URL` | Yes (for `sync_confluence`) | Confluence Cloud base URL |
| `CONFLUENCE_EMAIL` | Yes (for `sync_confluence`) | Atlassian account email |
| `CONFLUENCE_API_TOKEN` | Yes (for `sync_confluence`) | Atlassian API token |
| `CONFLUENCE_SPACE_KEY` | Yes (for `sync_confluence`) | Confluence space key, e.g. `DEV` |

## Supported languages

C#, TypeScript, JavaScript, Python, Go

## Build from source

```bash
git clone https://github.com/dinesh-mys/livingdocs
cd livingdocs
make build
make test
make scan REPO=/path/to/your/repo
```

## Architecture

```
┌──────────────────────────────────────────────────────┐
│                     Interfaces                        │
│  Claude Desktop │ Claude Code │ VS Copilot │ GitHub  │
└───────┬──────────────────────────────────┬───────────┘
        │                                  │
   livingdocs-mcp                  LivingDocs.CopilotExt
   (MCP server)                    /api/copilot/chat  (SSE)
        │                          /api/github/webhook
        └──────────────┬───────────┘
                LivingDocs.Core
   ┌────────────────┴────────────────────┐
GitScanner  DocExtractor  StaleDocDetector  ClaudeService
DocWriterService  ConfluenceService  GitHubOrgService
```

## Projects

| Project | Purpose |
|---------|---------|
| `LivingDocs.Core` | Shared engine — git scanning, doc extraction, stale detection, Claude API, Confluence sync, org scanning |
| `LivingDocs.McpServer` | CLI + MCP server — published as `LivingDocs.Mcp` on NuGet |
| `LivingDocs.CopilotExt` | GitHub Copilot Extension — SSE chat endpoint + PR merge webhook (agent URL registration pending GitHub plan availability) |
| `LivingDocs.Tests` | xUnit test suite (71 tests) |

## Feedback & Support

- **Bug reports** → [GitHub Issues](https://github.com/dinesh-mys/livingdocs/issues/new?template=bug_report.md)
- **Feature requests** → [GitHub Issues](https://github.com/dinesh-mys/livingdocs/issues/new?template=feature_request.md)
- **Questions & ideas** → [GitHub Discussions](https://github.com/dinesh-mys/livingdocs/discussions)

## License

MIT — see [LICENSE](LICENSE).
