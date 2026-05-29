# Funnel Telemetry + Value-First Trial — Design

**Date:** 2026-05-29
**Status:** Approved
**Author:** dinesh (with Claude)

## Problem

LivingDocs.Mcp has ~1,400 NuGet downloads but **zero** trial starts and zero
paid conversions. The Polar checkout link has **zero clicks** — meaning the
problem is not pricing or the offer, it is *reach/activation*: almost nobody who
installs the tool ever reaches the point where they would pay.

We are flying blind:

- NuGet download counts are inflated by CI/proxy/bot traffic — they do not tell
  us how many real humans installed, configured, or got value.
- There is **no telemetry** between "downloaded" (vanity metric) and "paid"
  (zero). We cannot see where humans drop out of the funnel.

This work makes the funnel **observable** and moves the trial offer **earlier**
so it appears right after the first free success instead of being buried behind a
Pro-tool rejection.

Out of scope (handled as actions, not code): checking the Polar dashboard
(already done — zero clicks) and choosing a distribution channel (covered by the
Part D action-plan doc).

## Goals

1. Instrument 4 funnel events so we can see install → activate → reach-offer.
2. Surface a **value-first** trial offer after the first successful free scan.
3. Reuse the existing `livingdocs-web` analytics pipeline (Neon + admin.html) —
   no new third-party service.
4. Ship a short distribution action plan the founder executes manually.

## Non-Goals

- No conversion tracking inside events (payments come from Polar, read
  separately). The funnel ends at `upsell_shown`.
- No PostHog or new analytics vendor.
- No PII, no repo paths, no file names, no source content in telemetry.

## Architecture

Two repos are touched:

- `livingdocs` (this repo, .NET) — emits events, shows the value-first offer.
- `livingdocs-web` (Next.js 16 + Neon Postgres) — ingests + displays events.

```
MCP server / CLI (.NET)
   │  fire-and-forget JSON POST
   ▼
https://livingdocs-web.vercel.app/api/event   (new edge function)
   │  INSERT
   ▼
Neon Postgres  →  events table   (separate from existing visits table)
   ▲
   │  admin-token read
api/analytics.js (extended)  →  admin.html "Product Funnel" section
```

## Part A — C# Telemetry

### `ITelemetryService` (LivingDocs.Core/Interfaces)

```csharp
public interface ITelemetryService
{
    void Track(string @event, IReadOnlyDictionary<string, string>? props = null);
}
```

Fire-and-forget by design — `Track` returns immediately, never blocks the caller.

### `TelemetryService` (LivingDocs.Core/Services)

Behavioral contract (all are hard requirements):

- **Never throws.** All exceptions are swallowed inside the background task.
- **Never blocks.** `Track` schedules the POST on a background task and returns.
- **2-second timeout** on the HTTP call.
- **Never writes to stdout/stderr.** In MCP stdio mode any stray write corrupts
  the JSON-RPC stream. Telemetry only makes an outbound HTTP call.
- **Honors opt-out** (see below) — when disabled, `Track` is a no-op.

Endpoint: `https://livingdocs-web.vercel.app/api/event`

Payload:

```json
{
  "event": "index_success",
  "installId": "<guid>",
  "version": "2.0.2",
  "os": "osx",
  "ts": 1717000000000,
  "props": { "source": "scan" }
}
```

### Anonymous install ID

- A random GUID, generated **once** and stored at `~/.livingdocs/install-id`
  (`%USERPROFILE%\.livingdocs\install-id` on Windows).
- On creation (file did not previously exist) the service emits `first_run`.
- No PII. The server (Vercel) sees the request IP for geo only, exactly as the
  existing `track.js` already does for web visits.

### Opt-out (default ON)

Telemetry is on by default (matches the original "anonymous, opt-out" decision).
It is disabled when **either** is set:

- `DO_NOT_TRACK=1` (the cross-tool standard), or
- `LIVINGDOCS_TELEMETRY=off`

Disclosure:

- **CLI commands** print a one-line notice on first run:
  `ℹ Anonymous usage stats are on (no code or paths collected). Disable with DO_NOT_TRACK=1.`
- **MCP mode cannot print** (stdio) — disclosure lives in the README and the
  web privacy page instead.

### The 4 events

| Event | Fires from | `props` | Meaning |
|-------|-----------|---------|---------|
| `first_run` | install-id file first created (`TelemetryService` ctor / ensure-id) | — | new human installed |
| `mcp_started` | `Program.cs` server bootstrap (before `RunAsync`) | — | a client actually launched the server ("MCP-configured") |
| `index_success` | `index_repo` tool **and** CLI `RunIndexAsync`, when chunk count > 0 | `{ chunks }` (bucketed/coarse) | reached first real value |
| `upsell_shown` | `LicenseGuard.RequireProAsync` and `scan_repo` | `{ source: "pro_tool" \| "scan" }` | hit the offer |

`index_success` may fire more than once per install; the funnel counts **distinct
installs per event**, so repeats do not distort drop-off.

### Wiring

- Register `ITelemetryService` as a DI singleton in `Program.cs`.
- Inject into `LivingDocsTools` methods that need it (`index_repo`, `scan_repo`)
  via MCP's existing constructor/parameter DI.
- `LicenseGuard.RequireProAsync` gains an `ITelemetryService` parameter; its
  callers in `ProTools` / `ConnectorTools` already receive DI and pass it
  through.
- CLI paths (`RunIndexAsync`) construct a `TelemetryService` directly (no host),
  matching how they already `new` up services.
- `mcp_started` and `first_run` are emitted from the server bootstrap / id
  creation respectively.

## Part B — Value-First Trial Offer (`scan_repo`)

`scan_repo` currently lists stale docs and stops. New behavior:

- Inject `ILicenseService` into `scan_repo`.
- After listing stale docs, **if** stale docs were found **and** the license is
  not valid/Pro, append:

  ```
  💡 Found N stale doc(s). write_back can fix them automatically, in place —
     free for 7 days, no card needed → <polar store url>
  ```

- Emit `upsell_shown` with `props.source = "scan"`.
- Never shown to valid Pro/Enterprise users.

The Polar store URL is the existing constant
(`https://buy.polar.sh/polar_cl_LcRKdosjt3TwpUkKBSoDOPOP6ea6ArOfKpyB91MSdiM`).

## Part C — Web Side (`livingdocs-web`)

### `api/event.js` (new edge function)

Mirrors `track.js`:

- `runtime: 'edge'`, CORS preflight, bot-UA filter.
- Reads geo headers (`x-vercel-ip-country`, `x-vercel-ip-city`) and IP exactly
  like `track.js`.
- Auto-creates and inserts into a **new `events` table**:

  ```sql
  CREATE TABLE IF NOT EXISTS events (
    id bigserial PRIMARY KEY,
    ts bigint NOT NULL,
    event text NOT NULL,
    install_id text,
    version text,
    os text,
    props jsonb,
    country text,
    city text,
    ua text,
    created_at timestamptz DEFAULT now()
  );
  ```

- Validates `event` is one of the 4 known names; ignores unknown events.

### `api/analytics.js` (extended)

- Keep the existing `visits` response unchanged.
- Add a `funnel` block: distinct `install_id` count per event, plus total events,
  computed with a `GROUP BY event` query against `events`. Same admin-token gate.

### `admin.html` (extended)

- Add a "Product Funnel" panel rendering the four stages in order with counts and
  drop-off % between stages:
  `first_run → mcp_started → index_success → upsell_shown`.

### Privacy page

- Add a short paragraph to `app/privacy/page.tsx` disclosing anonymous product
  telemetry and the opt-out env vars.

## Part D — Distribution Action Plan

A markdown doc (`docs/distribution-plan.md` in this repo) — no code. Contents:

1. **Get 10 real humans** via manual recruitment (DMs to teams with doc pain;
   offer to set it up on a call; watch where they get stuck).
2. **Pick ONE channel** and work it — Confluence/Forge Marketplace + the Vercel
   landing page (buyer-aligned) over NuGet (bot-heavy, buyer-absent).
3. **What to watch** in the new funnel to know it is working: ratio of
   `first_run → index_success` (activation) and `index_success → upsell_shown`
   (offer reach), cross-referenced with Polar checkout clicks.

## Error Handling

- Telemetry: every failure is swallowed in the background task; no ret., no
  user-visible error, no stdout/stderr writes.
- `api/event.js`: invalid JSON or unknown event → `200 { ok:true, skipped:true }`
  (never error the client).
- Install-id file: if `~/.livingdocs` is unwritable, fall back to an in-memory
  GUID for the process and disable `first_run` emission (telemetry still works,
  just without a stable id).

## Testing

- `TelemetryService`: opt-out via each env var disables `Track`; `Track` never
  throws even when the endpoint is unreachable; install-id is created once and
  reused; payload shape is correct (test with an injected fake HTTP handler).
- `scan_repo`: CTA appended only when stale docs found AND license invalid; not
  shown to Pro; `upsell_shown` emitted with `source=scan`.
- `LicenseGuard`: `upsell_shown` emitted with `source=pro_tool` on free path.
- Web: `api/event.js` rejects unknown events; inserts known events; bot UA
  skipped. (Manual/integration — matches existing untested web functions.)

## Rollout

1. Land web side first (`api/event.js` + analytics + admin) so the endpoint
   exists before clients emit.
2. Land C# telemetry + value-first offer.
3. Bump version, publish, watch the funnel populate.
