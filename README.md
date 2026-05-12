# LivingDocs

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

---

## Pro tier

Pro tools require a license key set as an environment variable:

```bash
export LIVINGDOCS_LICENSE_KEY=LD-xxxx-xxxx-xxxx
```

Or add it to the `env` block in your config:

```json
{
  "mcpServers": {
    "livingdocs": {
      "command": "livingdocs-mcp",
      "env": {
        "ANTHROPIC_API_KEY": "sk-ant-...",
        "LIVINGDOCS_LICENSE_KEY": "LD-xxxx-xxxx-xxxx"
      }
    }
  }
}
```

Get a license at **[polar.sh/dinesh-mys/livingdocs](https://polar.sh/dinesh-mys/livingdocs)**.

---

## Environment variables

| Variable | Required | Description |
|----------|----------|-------------|
| `ANTHROPIC_API_KEY` | Yes (for `query_docs` and `suggest_doc_update`) | Anthropic API key |
| `LIVINGDOCS_LICENSE_KEY` | Yes (for Pro tools) | License key from Polar.sh |

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
┌─────────────────────────────────────────────┐
│                  Interfaces                  │
│  Claude Desktop │ Claude Code │ VS Copilot  │
└───────┬─────────────────────────────┬────────┘
        │                             │
   livingdocs-mcp              (coming soon)
   (MCP server)              LivingDocs.CopilotExt
        │
   LivingDocs.Core
   ┌────┴────────────────┐
GitScanner  DocExtractor  StaleDocDetector  ClaudeService
```

## Projects

| Project | Purpose |
|---------|---------|
| `LivingDocs.Core` | Shared engine — git scanning, doc extraction, stale detection, Claude API |
| `LivingDocs.McpServer` | CLI + MCP server — published as `LivingDocs.Mcp` on NuGet |
| `LivingDocs.CopilotExt` | GitHub Copilot Extension webhook (coming soon) |
| `LivingDocs.Tests` | xUnit test suite |

## License

MIT — see [LICENSE](LICENSE).
