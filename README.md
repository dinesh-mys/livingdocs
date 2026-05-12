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
            StaleDocDetector
```

## Prerequisites

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Git (must be on `PATH`)
- An [Anthropic API key](https://console.anthropic.com/) for Claude suggestions

## Quickstart

```bash
# 1. Clone
git clone https://github.com/dinesh-mys/livingdocs
cd livingdocs

# 2. Set API key
cp .env.example .env
# Edit .env and set ANTHROPIC_API_KEY=sk-ant-...

# 3. Build & test
make build
make test

# 4. Scan a repo
make scan REPO=/path/to/your/repo
```

## Scan output

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

## Projects

| Project | Purpose |
|---|---|
| `LivingDocs.Core` | Shared engine — git scanning, doc extraction, stale detection, Claude API |
| `LivingDocs.McpServer` | CLI + MCP server (Claude Desktop / Claude Code) |
| `LivingDocs.CopilotExt` | GitHub Copilot Extension webhook API |
| `LivingDocs.Tests` | xUnit test suite (36 tests) |

## Supported languages

C#, TypeScript, JavaScript, Python, Go

## Environment variables

| Variable | Required | Description |
|---|---|---|
| `ANTHROPIC_API_KEY` | Yes (for suggestions) | Anthropic API key — `sk-ant-...` |

## Sprint Board

[GitHub Issues](https://github.com/dinesh-mys/livingdocs/issues)
