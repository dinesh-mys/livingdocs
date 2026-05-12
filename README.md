# LivingDocs

AI-powered documentation health monitor. Detects stale code comments and docstrings, suggests updates using Claude, and writes back to Confluence/Notion.

## Architecture

```
┌─────────────────────────────────────────┐
│              Interfaces                  │
│   Claude Code (MCP)  │  GitHub Copilot  │
└──────────┬───────────┴────────┬─────────┘
           │                    │
    LivingDocs.McpServer  LivingDocs.CopilotExt
           │                    │
           └────────┬───────────┘
                    │
           LivingDocs.Core
       ┌────────────┼────────────┐
  GitScanner  DocExtractor  ClaudeService
                    │
            Azure AI Search
```

## Quickstart

```bash
# 1. Clone
git clone https://github.com/dinesh-mys/livingdocs
cd livingdocs

# 2. Set API key
cp .env.example .env
# Edit .env and add your ANTHROPIC_API_KEY

# 3. Build & test
make build
make test

# 4. Scan a repo
make scan REPO=/path/to/your/repo
```

## Projects

| Project | Purpose |
|---|---|
| `LivingDocs.Core` | Shared engine — git scanning, doc extraction, Claude API |
| `LivingDocs.McpServer` | MCP server for Claude Desktop / Claude Code |
| `LivingDocs.CopilotExt` | GitHub Copilot Extension webhook API |
| `LivingDocs.Tests` | xUnit test suite |

## Sprint Board
[GitHub Issues](https://github.com/dinesh-mys/livingdocs/issues)
