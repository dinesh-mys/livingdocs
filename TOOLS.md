# LivingDocs MCP — Tool Reference

13 tools across 4 categories. Free tools work without a license key. Pro tools require `LIVINGDOCS_LICENSE_KEY` set to a valid Polar or enterprise key.

---

## Indexing / Connectors

| Tool | Tier | Description |
|------|------|-------------|
| `index_repo` | Free | Build or refresh the semantic search index for a local git repository |
| `index_slack` | Pro | Fetch Slack channel messages into the knowledge index — searchable via `query_docs` |
| `index_teams` | Pro | Fetch MS Teams channel messages into the knowledge index |
| `index_email` | Pro | Fetch email messages from an IMAP folder into the knowledge index |

### `index_repo`
```
index_repo repoPath=/path/to/repo
```
Scans all source files, extracts doc comments, and builds a BM25 + embedding index under `.livingdocs/`. Run this first before using `query_docs`.

### `index_slack`
```
index_slack repoPath=/path/to/repo channelId=C1234ABCD limit=200
```
Requires `SLACK_BOT_TOKEN` with `channels:history` and `channels:read` scopes. Messages are stored in the same index as code docs — query them with `query_docs`.

### `index_teams`
```
index_teams repoPath=/path/to/repo channelId=<teams-channel-id> limit=200
```
Requires `TEAMS_CLIENT_ID`, `TEAMS_CLIENT_SECRET`, `TEAMS_TENANT_ID`.

### `index_email`
```
index_email repoPath=/path/to/repo folder=INBOX limit=100
```
Requires `EMAIL_HOST`, `EMAIL_PORT`, `EMAIL_USER`, `EMAIL_PASSWORD`. Works with Gmail, Outlook, and any IMAP provider. Email quoting (`>` lines) is stripped automatically.

---

## Querying / Search

| Tool | Tier | Description |
|------|------|-------------|
| `query_docs` | Free | Semantic search across indexed code docs and connectors |
| `detect_gaps` | Free | Find undocumented areas in the codebase, ranked by commit activity |
| `departure_risk` | Free | Identify knowledge concentration / bus-factor risks by author |

### `query_docs`
```
query_docs repoPath=/path/to/repo query="why does the payment service use async processing?"
```
Searches across everything indexed — code comments, Slack threads, Teams messages, emails. Returns ranked results with file/source attribution.

### `detect_gaps`
```
detect_gaps repoPath=/path/to/repo
```
Finds source files with zero documentation comments (XML `///`, JSDoc, or docstrings), ranked by git commit frequency. Busiest undocumented files appear first — highest priority knowledge gaps.

### `departure_risk`
```
departure_risk repoPath=/path/to/repo
```
Identifies authors who are the sole or dominant contributor (≥60% of commits, ≥5 commits) to critical source files. Groups results by author so you can see who holds the most undocumented knowledge.

---

## Writing / Syncing

| Tool | Tier | Description |
|------|------|-------------|
| `write_docs` | Pro | Generate or update documentation, written to `docs/<File>.md` with a UTC timestamp |
| `write_back` | Free | Write Claude-generated doc comments directly back to source files |
| `suggest_doc_update` | Free | Suggest doc updates for a specific changed file |
| `sync_confluence` | Pro | Sync generated docs to Confluence automatically |

### `write_docs`
```
write_docs repoPath=/path/to/repo filePath=src/Auth.cs
```
Generates full documentation for a file and writes it to `docs/src/Auth.md`. Includes a UTC timestamp for audit trail. Use `suggest_doc_update` first if the file has no existing comments.

### `write_back`
```
write_back repoPath=/path/to/repo filePath=src/Auth.cs
```
Generates updated doc comments for every symbol in a file and writes them directly back to the source. Suggestions below 60% confidence are skipped and listed separately for manual review. Requires `ANTHROPIC_API_KEY`.

### `suggest_doc_update`
```
suggest_doc_update repoPath=/path/to/repo filePath=src/Auth.cs
```
Asks Claude to suggest a rewrite of stale or missing doc comments. Returns suggestions without writing to disk — use `write_back` to apply them.

### `sync_confluence`
```
sync_confluence repoPath=/path/to/repo filePath=src/Auth.cs
```
Pushes generated docs to a Confluence page. Creates the page if it doesn't exist. Requires `CONFLUENCE_BASE_URL`, `CONFLUENCE_EMAIL`, `CONFLUENCE_API_TOKEN`, `CONFLUENCE_SPACE_KEY`.

---

## Scanning

| Tool | Tier | Description |
|------|------|-------------|
| `scan_repo` | Free | Scan a repo for stale doc comments, scored 0–100% per file |
| `scan_org` | Pro | Scan all repos in a GitHub org and return an org-wide staleness report |

### `scan_repo`
```
scan_repo repoPath=/path/to/repo
```
Detects doc comments that haven't been updated since the code last changed. Returns a staleness score (0–100%) per file, sorted by severity.

### `scan_org`
```
scan_org orgName=your-github-org
```
Requires `GITHUB_TOKEN`. Clones and scans every repo in the org, returns an aggregated report. Useful for engineering leadership to get a cross-repo view of documentation health.

---

## Environment Variables

| Variable | Required by |
|----------|-------------|
| `ANTHROPIC_API_KEY` | `write_back`, `suggest_doc_update`, `write_docs` |
| `LIVINGDOCS_LICENSE_KEY` | All Pro tools |
| `GITHUB_TOKEN` | `scan_org` |
| `SLACK_BOT_TOKEN` | `index_slack` |
| `TEAMS_CLIENT_ID` / `TEAMS_CLIENT_SECRET` / `TEAMS_TENANT_ID` | `index_teams` |
| `EMAIL_HOST` / `EMAIL_PORT` / `EMAIL_USER` / `EMAIL_PASSWORD` | `index_email` |
| `CONFLUENCE_BASE_URL` / `CONFLUENCE_EMAIL` / `CONFLUENCE_API_TOKEN` / `CONFLUENCE_SPACE_KEY` | `sync_confluence` |

---

## Quick Start

```bash
# Install
dotnet tool install -g LivingDocs.Mcp

# Index your repo
index_repo repoPath=/path/to/repo

# Find knowledge gaps
detect_gaps repoPath=/path/to/repo

# Search across everything
query_docs repoPath=/path/to/repo query="your question here"
```

Pro trial → https://buy.polar.sh/polar_cl_LcRKdosjt3TwpUkKBSoDOPOP6ea6ArOfKpyB91MSdiM
