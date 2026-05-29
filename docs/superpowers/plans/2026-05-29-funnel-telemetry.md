# Funnel Telemetry + Value-First Trial Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the install→activate→reach-offer funnel observable, and surface a value-first trial offer right after the first free scan succeeds.

**Architecture:** A fire-and-forget `ITelemetryService` in the .NET tool POSTs 4 anonymous funnel events to a new `/api/event` edge function on the existing `livingdocs-web` (Next.js + Neon) site, which stores them in a new `events` table surfaced in `admin.html`. The `scan_repo` tool gains a value-first upsell when stale docs are found and the user isn't Pro.

**Tech Stack:** .NET 10 / C# (xUnit 2.9.3), Next.js 16 edge functions, Neon Postgres (HTTP `/sql` API), vanilla JS admin dashboard.

**Two repos:**
- `livingdocs` — this repo. Working dir: `/Users/dinesh/Documents/LivingDocs/Repo/Untitled/livingdocs`
- `livingdocs-web` — web. Working dir: `/Users/dinesh/Documents/LivingDocs/livingdocs-web`

**Rollout order:** Web side first (Tasks 1–4) so the endpoint exists before clients emit. Then .NET (Tasks 5–11). Then docs (Task 12).

**Commit note:** every commit message ends with:
```
Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>
```

---

## File Structure

**livingdocs-web (new/modified):**
- Create: `api/event.js` — edge ingest for product events
- Modify: `api/analytics.js` — add `funnel` block to response
- Modify: `admin.html` — render "Product Funnel" panel
- Modify: `app/privacy/page.tsx` — disclose anonymous telemetry

**livingdocs (new/modified):**
- Create: `src/LivingDocs.Core/Interfaces/ITelemetryService.cs`
- Create: `src/LivingDocs.Core/Services/TelemetryService.cs`
- Create: `src/LivingDocs.Tests/TelemetryServiceTests.cs`
- Modify: `src/LivingDocs.McpServer/Program.cs` — DI registration, `mcp_started`, CLI `index_success`
- Modify: `src/LivingDocs.McpServer/Tools/LicenseGuard.cs` — `upsell_shown` (source=pro_tool)
- Modify: `src/LivingDocs.McpServer/Tools/ConnectorTools.cs` — pass telemetry to guard (3 methods)
- Modify: `src/LivingDocs.McpServer/Tools/ProTools.cs` — pass telemetry to guard (3 methods)
- Modify: `src/LivingDocs.McpServer/Tools/LivingDocsTools.cs` — `index_success` + value-first offer in `scan_repo`
- Modify: `README.md` — telemetry + opt-out section
- Create: `docs/distribution-plan.md` — Part D action plan

---

# PART C — Web side (do first)

> No JS test harness exists in `livingdocs-web`; these tasks use manual verification (matching the existing untested `track.js`/`analytics.js`). All commands run from `/Users/dinesh/Documents/LivingDocs/livingdocs-web`.

## Task 1: `api/event.js` — product event ingest

**Files:**
- Create: `/Users/dinesh/Documents/LivingDocs/livingdocs-web/api/event.js`

- [ ] **Step 1: Write the edge function**

Create `api/event.js` (mirrors the existing `api/track.js` pattern — same Neon helper, CORS, bot filter):

```js
export const config = { runtime: 'edge' };

const KNOWN_EVENTS = new Set([
  'first_run',
  'mcp_started',
  'index_success',
  'upsell_shown',
]);

async function neonQuery(hostname, password, query, params = []) {
  const res = await fetch(`https://${hostname}/sql`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${password}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ query, params }),
  });
  return res.json();
}

export default async function handler(req) {
  const cors = {
    'Access-Control-Allow-Origin': '*',
    'Access-Control-Allow-Methods': 'POST, OPTIONS',
    'Access-Control-Allow-Headers': 'Content-Type',
  };

  if (req.method === 'OPTIONS') return new Response(null, { status: 200, headers: cors });
  if (req.method !== 'POST') return new Response('Method Not Allowed', { status: 405 });

  const ua = req.headers.get('user-agent') || '';
  if (/bot|crawler|spider|headless|preview|lighthouse|pingdom/i.test(ua)) {
    return new Response(JSON.stringify({ ok: true, skipped: true }), { status: 200, headers: cors });
  }

  let body = {};
  try { body = await req.json(); } catch {}

  const event = body.event;
  if (!KNOWN_EVENTS.has(event)) {
    return new Response(JSON.stringify({ ok: true, skipped: true }), { status: 200, headers: cors });
  }

  const country = req.headers.get('x-vercel-ip-country') || '';
  const city = req.headers.get('x-vercel-ip-city') || '';
  const installId = typeof body.installId === 'string' ? body.installId.slice(0, 64) : '';
  const version = typeof body.version === 'string' ? body.version.slice(0, 32) : '';
  const os = typeof body.os === 'string' ? body.os.slice(0, 16) : '';
  const ts = Number.isFinite(body.ts) ? body.ts : Date.now();
  const props = body.props && typeof body.props === 'object' ? body.props : {};

  const dbUrl = new URL(process.env.DATABASE_URL);
  const hostname = dbUrl.hostname;
  const password = decodeURIComponent(dbUrl.password);

  await neonQuery(hostname, password, `
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
    )
  `);

  await neonQuery(hostname, password,
    `INSERT INTO events (ts, event, install_id, version, os, props, country, city, ua)
     VALUES ($1, $2, $3, $4, $5, $6, $7, $8, $9)`,
    [ts, event, installId, version, os, JSON.stringify(props), country, decodeURIComponent(city), ua]
  );

  return new Response(JSON.stringify({ ok: true }), {
    status: 200,
    headers: { 'Content-Type': 'application/json', ...cors },
  });
}
```

- [ ] **Step 2: Verify it parses (lint)**

Run: `cd /Users/dinesh/Documents/LivingDocs/livingdocs-web && npx eslint api/event.js`
Expected: no errors (warnings about `console` etc. acceptable; no parse errors).

- [ ] **Step 3: Smoke-test locally against the deployed DB (optional but recommended)**

If `DATABASE_URL` is available locally, run a quick Node check; otherwise defer to Step 4 (post-deploy curl). Skip if no local DB access.

- [ ] **Step 4: Commit**

```bash
cd /Users/dinesh/Documents/LivingDocs/livingdocs-web
git add api/event.js
git commit -m "feat: add /api/event edge function for product telemetry

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 2: Extend `api/analytics.js` with funnel data

**Files:**
- Modify: `/Users/dinesh/Documents/LivingDocs/livingdocs-web/api/analytics.js`

- [ ] **Step 1: Add an events funnel query alongside the visits query**

Replace the body of `handler` (keep the auth gate and `dbUrl` lines) so it also returns a `funnel`. The full new file:

```js
export const config = { runtime: 'edge' };

async function neonQuery(hostname, password, query) {
  const res = await fetch(`https://${hostname}/sql`, {
    method: 'POST',
    headers: {
      Authorization: `Bearer ${password}`,
      'Content-Type': 'application/json',
    },
    body: JSON.stringify({ query, params: [] }),
  });
  return res.json();
}

export default async function handler(req) {
  const url = new URL(req.url);
  const token = req.headers.get('x-admin-token') || url.searchParams.get('token');

  if (!process.env.ADMIN_TOKEN || token !== process.env.ADMIN_TOKEN) {
    return new Response('Unauthorized', { status: 401 });
  }

  const dbUrl = new URL(process.env.DATABASE_URL);
  const hostname = dbUrl.hostname;
  const password = decodeURIComponent(dbUrl.password);

  const visitsData = await neonQuery(hostname, password,
    `SELECT ts, ip, country, city, page, referrer, ua
     FROM visits
     ORDER BY ts DESC
     LIMIT 5000`);

  const visits = (visitsData.rows || []).map(r => ({
    ts: Number(r.ts),
    ip: r.ip,
    country: r.country,
    city: r.city,
    page: r.page,
    referrer: r.referrer,
    ua: r.ua,
  }));

  // Product funnel: distinct installs per event. Tolerates a missing events table.
  let funnel = { first_run: 0, mcp_started: 0, index_success: 0, upsell_shown: 0 };
  try {
    const funnelData = await neonQuery(hostname, password,
      `SELECT event, COUNT(DISTINCT install_id) AS installs
       FROM events
       GROUP BY event`);
    for (const r of funnelData.rows || []) {
      if (r.event in funnel) funnel[r.event] = Number(r.installs);
    }
  } catch {}

  return new Response(JSON.stringify({ visits, funnel }), {
    status: 200,
    headers: {
      'Content-Type': 'application/json',
      'Cache-Control': 'no-store',
    },
  });
}
```

- [ ] **Step 2: Verify it parses**

Run: `cd /Users/dinesh/Documents/LivingDocs/livingdocs-web && npx eslint api/analytics.js`
Expected: no parse errors.

- [ ] **Step 3: Commit**

```bash
cd /Users/dinesh/Documents/LivingDocs/livingdocs-web
git add api/analytics.js
git commit -m "feat: return product funnel counts from /api/analytics

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 3: Add "Product Funnel" panel to `admin.html`

**Files:**
- Modify: `/Users/dinesh/Documents/LivingDocs/livingdocs-web/admin.html`

- [ ] **Step 1: Locate the render path**

Run: `cd /Users/dinesh/Documents/LivingDocs/livingdocs-web && grep -n "visits\|analytics\|fetch\|function render\|<body" admin.html | head -40`
Note the function that fetches `/api/analytics` and where it injects HTML, and pick a container element id near the top of the dashboard body to insert the funnel panel before the visits table.

- [ ] **Step 2: Add a funnel container in the HTML body**

Insert this block immediately after the opening dashboard container / before the visits section (use the element discovered in Step 1):

```html
<section id="funnel-section" style="margin:24px 0;padding:16px;border:1px solid #2a2a2a;border-radius:8px;">
  <h2 style="margin:0 0 12px;font-size:16px;">Product Funnel (distinct installs)</h2>
  <div id="funnel" style="display:flex;gap:12px;flex-wrap:wrap;"></div>
</section>
```

- [ ] **Step 3: Render the funnel from the analytics response**

In the JS that handles the `/api/analytics` JSON (where `data.visits` is consumed), add funnel rendering. Use `data.funnel`:

```js
function renderFunnel(funnel) {
  if (!funnel) return;
  const stages = [
    ['first_run', 'Installed'],
    ['mcp_started', 'MCP launched'],
    ['index_success', 'Indexed (value)'],
    ['upsell_shown', 'Saw offer'],
  ];
  const el = document.getElementById('funnel');
  el.innerHTML = '';
  let prev = null;
  for (const [key, label] of stages) {
    const n = funnel[key] || 0;
    const drop = prev !== null && prev > 0
      ? ` <small style="color:#888">(${Math.round((n / prev) * 100)}% of prev)</small>`
      : '';
    const card = document.createElement('div');
    card.style.cssText = 'min-width:140px;padding:12px;background:#161616;border-radius:6px;';
    card.innerHTML = `<div style="font-size:24px;font-weight:600">${n}</div>` +
                     `<div style="font-size:12px;color:#aaa">${label}${drop}</div>`;
    el.appendChild(card);
    prev = n;
  }
}
```

Then call `renderFunnel(data.funnel);` in the same place the visits render is called.

- [ ] **Step 4: Verify locally**

Open `admin.html` logic mentally / via `npx serve` if available. At minimum confirm no JS syntax errors:
Run: `cd /Users/dinesh/Documents/LivingDocs/livingdocs-web && node --check admin.html 2>/dev/null || echo "node --check skips HTML; verify the <script> block has no syntax errors manually"`
Expected: the `<script>` block parses (review the inline JS once).

- [ ] **Step 5: Commit**

```bash
cd /Users/dinesh/Documents/LivingDocs/livingdocs-web
git add admin.html
git commit -m "feat: render product funnel panel in admin dashboard

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 4: Disclose telemetry on the privacy page

**Files:**
- Modify: `/Users/dinesh/Documents/LivingDocs/livingdocs-web/app/privacy/page.tsx`

- [ ] **Step 1: Read the page to match its JSX style**

Run: `cd /Users/dinesh/Documents/LivingDocs/livingdocs-web && sed -n '1,60p' app/privacy/page.tsx`

- [ ] **Step 2: Add a telemetry paragraph**

Insert a section matching the surrounding JSX (heading + paragraph) with this content:

> **Anonymous product analytics.** The LivingDocs CLI / MCP server sends anonymous usage events (install, server start, successful index, and when an upgrade prompt is shown) to help us understand which steps people complete. These events contain a random installation ID, the app version, and your operating system — never your code, file paths, repository names, or any personal information. To opt out, set the environment variable `DO_NOT_TRACK=1` or `LIVINGDOCS_TELEMETRY=off`.

- [ ] **Step 3: Verify build**

Run: `cd /Users/dinesh/Documents/LivingDocs/livingdocs-web && npx tsc --noEmit -p tsconfig.json 2>&1 | head -20`
Expected: no new type errors from the edit.

- [ ] **Step 4: Commit**

```bash
cd /Users/dinesh/Documents/LivingDocs/livingdocs-web
git add app/privacy/page.tsx
git commit -m "docs: disclose anonymous product telemetry on privacy page

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

- [ ] **Step 5: Deploy the web side**

Push/deploy `livingdocs-web` so `https://livingdocs-web.vercel.app/api/event` is live before the .NET clients emit. Then verify:

```bash
curl -s -X POST https://livingdocs-web.vercel.app/api/event \
  -H 'Content-Type: application/json' \
  -d '{"event":"first_run","installId":"test-curl","version":"0.0.0","os":"test","ts":0,"props":{}}'
```
Expected: `{"ok":true}`. (Then confirm it appears in the admin funnel as 1 install for "Installed".)

---

# PART A + B — .NET side

> All commands run from `/Users/dinesh/Documents/LivingDocs/Repo/Untitled/livingdocs`. Build with `dotnet build`; test with `dotnet test`.

## Task 5: `ITelemetryService` + `TelemetryService` (TDD)

**Files:**
- Create: `src/LivingDocs.Core/Interfaces/ITelemetryService.cs`
- Create: `src/LivingDocs.Core/Services/TelemetryService.cs`
- Test: `src/LivingDocs.Tests/TelemetryServiceTests.cs`

- [ ] **Step 1: Write the interface**

Create `src/LivingDocs.Core/Interfaces/ITelemetryService.cs`:

```csharp
namespace LivingDocs.Core.Interfaces;

/// <summary>
/// Sends anonymous funnel telemetry. All methods are safe to call from any
/// context — they never throw and never write to stdout/stderr.
/// </summary>
public interface ITelemetryService
{
    /// <summary>Fire-and-forget. Returns immediately; the send happens in the background.</summary>
    void Track(string @event, IReadOnlyDictionary<string, string>? props = null);

    /// <summary>Awaitable variant for short-lived (CLI) processes that exit before a fire-and-forget send would complete.</summary>
    Task TrackAsync(string @event, IReadOnlyDictionary<string, string>? props = null);
}
```

- [ ] **Step 2: Write the failing tests**

Create `src/LivingDocs.Tests/TelemetryServiceTests.cs`:

```csharp
using System.Net;
using System.Text;
using System.Text.Json;
using LivingDocs.Core.Services;

namespace LivingDocs.Tests;

public class TelemetryServiceTests : IDisposable
{
    private readonly Dictionary<string, string?> _original = new();
    private readonly string _tmpIdPath = Path.Combine(Path.GetTempPath(), $"ld-install-{Guid.NewGuid():N}");

    private void SetEnv(string key, string? value)
    {
        if (!_original.ContainsKey(key))
            _original[key] = Environment.GetEnvironmentVariable(key);
        Environment.SetEnvironmentVariable(key, value);
    }

    public void Dispose()
    {
        foreach (var (k, v) in _original)
            Environment.SetEnvironmentVariable(k, v);
        if (File.Exists(_tmpIdPath)) File.Delete(_tmpIdPath);
    }

    // Captures outgoing requests so we can assert payloads without a network.
    private sealed class CapturingHandler : HttpMessageHandler
    {
        public List<string> Bodies { get; } = new();
        public bool Throw { get; set; }
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken ct)
        {
            if (Throw) throw new HttpRequestException("boom");
            Bodies.Add(await request.Content!.ReadAsStringAsync(ct));
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent("{\"ok\":true}", Encoding.UTF8, "application/json"),
            };
        }
    }

    [Fact]
    public void IsEnabled_False_WhenDoNotTrackSet()
    {
        SetEnv("DO_NOT_TRACK", "1");
        SetEnv("LIVINGDOCS_TELEMETRY", null);
        var svc = new TelemetryService(new HttpClient(new CapturingHandler()), _tmpIdPath);
        Assert.False(svc.IsEnabled);
    }

    [Fact]
    public void IsEnabled_False_WhenLivingDocsTelemetryOff()
    {
        SetEnv("DO_NOT_TRACK", null);
        SetEnv("LIVINGDOCS_TELEMETRY", "off");
        var svc = new TelemetryService(new HttpClient(new CapturingHandler()), _tmpIdPath);
        Assert.False(svc.IsEnabled);
    }

    [Fact]
    public void IsEnabled_True_ByDefault()
    {
        SetEnv("DO_NOT_TRACK", null);
        SetEnv("LIVINGDOCS_TELEMETRY", null);
        var svc = new TelemetryService(new HttpClient(new CapturingHandler()), _tmpIdPath);
        Assert.True(svc.IsEnabled);
    }

    [Fact]
    public async Task TrackAsync_PostsEventWithInstallId()
    {
        SetEnv("DO_NOT_TRACK", null);
        SetEnv("LIVINGDOCS_TELEMETRY", null);
        var handler = new CapturingHandler();
        var svc = new TelemetryService(new HttpClient(handler), _tmpIdPath);
        handler.Bodies.Clear(); // drop the ctor's first_run

        await svc.TrackAsync("index_success", new Dictionary<string, string> { ["chunks"] = "10-50" });

        Assert.Single(handler.Bodies);
        using var doc = JsonDocument.Parse(handler.Bodies[0]);
        var root = doc.RootElement;
        Assert.Equal("index_success", root.GetProperty("event").GetString());
        Assert.False(string.IsNullOrWhiteSpace(root.GetProperty("installId").GetString()));
        Assert.Equal("10-50", root.GetProperty("props").GetProperty("chunks").GetString());
    }

    [Fact]
    public async Task TrackAsync_NoOp_WhenOptedOut()
    {
        SetEnv("DO_NOT_TRACK", "1");
        var handler = new CapturingHandler();
        var svc = new TelemetryService(new HttpClient(handler), _tmpIdPath);
        handler.Bodies.Clear();

        await svc.TrackAsync("mcp_started");

        Assert.Empty(handler.Bodies);
    }

    [Fact]
    public async Task TrackAsync_DoesNotThrow_WhenEndpointFails()
    {
        SetEnv("DO_NOT_TRACK", null);
        SetEnv("LIVINGDOCS_TELEMETRY", null);
        var handler = new CapturingHandler { Throw = true };
        var svc = new TelemetryService(new HttpClient(handler), _tmpIdPath);

        var ex = await Record.ExceptionAsync(() => svc.TrackAsync("mcp_started"));
        Assert.Null(ex);
    }

    [Fact]
    public void InstallId_CreatedOnce_ReusedAfter()
    {
        SetEnv("DO_NOT_TRACK", "1"); // disable network for this file-only test
        _ = new TelemetryService(new HttpClient(new CapturingHandler()), _tmpIdPath);
        Assert.True(File.Exists(_tmpIdPath));
        var first = File.ReadAllText(_tmpIdPath);
        _ = new TelemetryService(new HttpClient(new CapturingHandler()), _tmpIdPath);
        Assert.Equal(first, File.ReadAllText(_tmpIdPath));
    }
}
```

- [ ] **Step 3: Run tests to verify they fail**

Run: `dotnet test --filter "FullyQualifiedName~TelemetryServiceTests"`
Expected: FAIL to compile — `TelemetryService` does not exist.

- [ ] **Step 4: Write the implementation**

Create `src/LivingDocs.Core/Services/TelemetryService.cs`:

```csharp
using System.Net.Http.Json;
using System.Reflection;
using System.Runtime.InteropServices;
using LivingDocs.Core.Interfaces;

namespace LivingDocs.Core.Services;

/// <summary>
/// Sends anonymous funnel telemetry to the LivingDocs web endpoint.
/// Fire-and-forget, never throws, 2-second timeout, and never writes to
/// stdout/stderr (so it is safe inside the MCP stdio JSON-RPC stream).
/// Disabled when DO_NOT_TRACK=1 or LIVINGDOCS_TELEMETRY=off.
/// </summary>
public sealed class TelemetryService : ITelemetryService
{
    private const string EndpointUrl = "https://livingdocs-web.vercel.app/api/event";

    private readonly HttpClient _http;
    private readonly string _installId;
    private readonly string _version;
    private readonly string _os;

    /// <summary>True when telemetry is enabled (no opt-out env var set).</summary>
    public bool IsEnabled { get; }

    /// <param name="http">HTTP client used for the POST. A 2s timeout is applied.</param>
    /// <param name="installIdPath">Override for the install-id file path (tests). Defaults to ~/.livingdocs/install-id.</param>
    /// <param name="noticeWriter">Optional writer for the first-run notice (CLI only; never pass in MCP stdio mode).</param>
    public TelemetryService(HttpClient http, string? installIdPath = null, TextWriter? noticeWriter = null)
    {
        _http = http;
        try { _http.Timeout = TimeSpan.FromSeconds(2); } catch { /* already started */ }

        IsEnabled = !IsOptedOut();
        _version = Assembly.GetEntryAssembly()?.GetName().Version?.ToString() ?? "unknown";
        _os = GetOs();

        var (id, isNew) = EnsureInstallId(installIdPath);
        _installId = id;

        if (isNew && IsEnabled)
        {
            noticeWriter?.WriteLine(
                "ℹ Anonymous usage stats are on (no code or paths collected). Disable with DO_NOT_TRACK=1.");
            Track("first_run");
        }
    }

    /// <inheritdoc />
    public void Track(string @event, IReadOnlyDictionary<string, string>? props = null)
        => _ = TrackAsync(@event, props);

    /// <inheritdoc />
    public async Task TrackAsync(string @event, IReadOnlyDictionary<string, string>? props = null)
    {
        if (!IsEnabled) return;
        try
        {
            var payload = new
            {
                @event,
                installId = _installId,
                version = _version,
                os = _os,
                ts = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                props = props ?? new Dictionary<string, string>(),
            };
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(2));
            await _http.PostAsJsonAsync(EndpointUrl, payload, cts.Token);
        }
        catch
        {
            // Telemetry must never affect the tool.
        }
    }

    private static bool IsOptedOut()
    {
        var dnt = Environment.GetEnvironmentVariable("DO_NOT_TRACK");
        if (dnt == "1" || string.Equals(dnt, "true", StringComparison.OrdinalIgnoreCase))
            return true;
        return string.Equals(
            Environment.GetEnvironmentVariable("LIVINGDOCS_TELEMETRY"), "off",
            StringComparison.OrdinalIgnoreCase);
    }

    private static (string id, bool isNew) EnsureInstallId(string? overridePath)
    {
        try
        {
            string file;
            if (!string.IsNullOrWhiteSpace(overridePath))
            {
                file = overridePath;
            }
            else
            {
                var dir = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
                    ".livingdocs");
                Directory.CreateDirectory(dir);
                file = Path.Combine(dir, "install-id");
            }

            if (File.Exists(file))
            {
                var existing = File.ReadAllText(file).Trim();
                if (!string.IsNullOrWhiteSpace(existing))
                    return (existing, false);
            }

            var dirName = Path.GetDirectoryName(file);
            if (!string.IsNullOrEmpty(dirName)) Directory.CreateDirectory(dirName);

            var id = Guid.NewGuid().ToString("N");
            File.WriteAllText(file, id);
            return (id, true);
        }
        catch
        {
            // Unwritable home dir — process-scoped id, suppress first_run.
            return (Guid.NewGuid().ToString("N"), false);
        }
    }

    private static string GetOs()
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return "windows";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX)) return "osx";
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux)) return "linux";
        return "unknown";
    }
}
```

- [ ] **Step 5: Run tests to verify they pass**

Run: `dotnet test --filter "FullyQualifiedName~TelemetryServiceTests"`
Expected: PASS (6 tests).

- [ ] **Step 6: Commit**

```bash
git add src/LivingDocs.Core/Interfaces/ITelemetryService.cs src/LivingDocs.Core/Services/TelemetryService.cs src/LivingDocs.Tests/TelemetryServiceTests.cs
git commit -m "feat: add anonymous fire-and-forget TelemetryService

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 6: Register DI + emit `mcp_started`

**Files:**
- Modify: `src/LivingDocs.McpServer/Program.cs:67` (DI block) and `:84` (RunAsync)

- [ ] **Step 1: Register the telemetry singleton**

In the DI block, after the `ILicenseService` registration (currently line 67), add:

```csharp
    .AddSingleton<ITelemetryService>(_ => new TelemetryService(new HttpClient()))
```

(`using LivingDocs.Core.Interfaces;` and `using LivingDocs.Core.Services;` are already imported at the top of Program.cs.)

- [ ] **Step 2: Emit `mcp_started` at server bootstrap**

Replace the final build/run line (currently `await builder.Build().RunAsync();`) with:

```csharp
var app = builder.Build();
app.Services.GetRequiredService<ITelemetryService>().Track("mcp_started");
await app.RunAsync();
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/LivingDocs.McpServer`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/LivingDocs.McpServer/Program.cs
git commit -m "feat: register telemetry DI and emit mcp_started on server boot

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 7: Emit `upsell_shown` from `LicenseGuard` (source=pro_tool)

**Files:**
- Modify: `src/LivingDocs.McpServer/Tools/LicenseGuard.cs`
- Modify: `src/LivingDocs.McpServer/Tools/ConnectorTools.cs` (3 methods)
- Modify: `src/LivingDocs.McpServer/Tools/ProTools.cs` (3 methods)

- [ ] **Step 1: Add telemetry parameter to `RequireProAsync`**

In `LicenseGuard.cs`, change the signature and emit on the free path. New file:

```csharp
using LivingDocs.Core.Interfaces;

internal static class LicenseGuard
{
    internal static async Task<string?> RequireProAsync(ILicenseService license, ITelemetryService telemetry)
    {
        var status = await license.GetStatusAsync();
        if (status.IsValid) return null;

        if (status.Plan == "free")
        {
            telemetry.Track("upsell_shown", new Dictionary<string, string> { ["source"] = "pro_tool" });
            return """
              ⚡ Pro feature — $10/month, free for 7 days. No credit card needed to start.

              Connect your team's knowledge wherever it lives:
              • index_slack      — index Slack channels and threads
              • index_teams      — index Microsoft Teams conversations
              • index_email      — index email for institutional knowledge
              • write_docs       — write AI-generated docs to .md files with timestamps
              • scan_org         — scan every repo in your GitHub org at once
              • sync_confluence  — push updated docs to Confluence automatically

              Start your free trial → https://buy.polar.sh/polar_cl_LcRKdosjt3TwpUkKBSoDOPOP6ea6ArOfKpyB91MSdiM

              Once you have your key: set LIVINGDOCS_LICENSE_KEY=<your-key>
              """;
        }

        return $"License error: {status.Error}";
    }
}
```

- [ ] **Step 2: Thread `ITelemetryService` through the 3 ConnectorTools methods**

In `ConnectorTools.cs`, add `ITelemetryService telemetry,` as a parameter to each of `IndexSlack`, `IndexTeams`, `IndexEmail` (place it right after the `ILicenseService license` parameter), and update each guard call. There are 3 call sites (lines ~27, ~81, ~135), each currently:

```csharp
        var licenseError = await LicenseGuard.RequireProAsync(license);
```

Change each to:

```csharp
        var licenseError = await LicenseGuard.RequireProAsync(license, telemetry);
```

Add `using LivingDocs.Core.Interfaces;` if not already present (it is, since `ILicenseService` is used).

- [ ] **Step 3: Thread `ITelemetryService` through the 3 ProTools methods**

In `ProTools.cs`, add `ITelemetryService telemetry,` after the `ILicenseService license` parameter in `SyncConfluence`, `WriteDocs`, and `ScanOrg`, and update each guard call (lines ~26, ~134, ~214) the same way:

```csharp
        var licenseError = await LicenseGuard.RequireProAsync(license, telemetry);
```

- [ ] **Step 4: Build to verify**

Run: `dotnet build src/LivingDocs.McpServer`
Expected: Build succeeded, 0 errors. (MCP DI injects `ITelemetryService` into the new tool parameters automatically because it is registered as a singleton.)

- [ ] **Step 5: Commit**

```bash
git add src/LivingDocs.McpServer/Tools/LicenseGuard.cs src/LivingDocs.McpServer/Tools/ConnectorTools.cs src/LivingDocs.McpServer/Tools/ProTools.cs
git commit -m "feat: emit upsell_shown(source=pro_tool) when Pro gate blocks

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 8: Emit `index_success` + value-first offer in `LivingDocsTools`

**Files:**
- Modify: `src/LivingDocs.McpServer/Tools/LivingDocsTools.cs` (`IndexRepo` ~line 86, `ScanRepo` ~line 17)

- [ ] **Step 1: Add `index_success` to the `index_repo` tool**

In `IndexRepo`, add `ITelemetryService telemetry,` as the first parameter (before `IIndexService indexer`). After the `total == 0` early return, when indexing succeeded, emit the event. The relevant region becomes:

```csharp
    public static async Task<string> IndexRepo(
        ITelemetryService telemetry,
        IIndexService indexer,
        ISemanticSearchServiceFactory searchFactory,
        [Description("Absolute path to the local git repository")] string repoPath)
    {
        if (!Directory.Exists(repoPath))
            return $"Error: directory not found — {repoPath}";

        var total = await indexer.IndexRepoAsync(repoPath);

        if (total == 0)
            return $"No documentation comments found in '{repoPath}'. " +
                   $"Add doc comments (///, JSDoc, or docstrings) and re-run index_repo.";

        telemetry.Track("index_success", new Dictionary<string, string> { ["chunks"] = Bucket(total) });

        await using var search = searchFactory.Create(repoPath);
```

Add this private helper at the bottom of the `LivingDocsTools` class (before the closing brace):

```csharp
    // Coarse bucket so we never collect exact counts.
    private static string Bucket(int n) =>
        n switch
        {
            < 10  => "1-9",
            < 50  => "10-49",
            < 200 => "50-199",
            _     => "200+",
        };
```

Add `using LivingDocs.Core.Interfaces;` at the top if not present (it is — `IClaudeService` etc. are used).

- [ ] **Step 2: Add the value-first offer to `scan_repo`**

In `ScanRepo`, add two DI parameters (`ILicenseService license, ITelemetryService telemetry`) after `IStaleDocDetectorService detector`, and append the value-first CTA when stale docs are found and the user is not Pro. New method:

```csharp
    public static async Task<string> ScanRepo(
        IStaleDocDetectorService detector,
        ILicenseService license,
        ITelemetryService telemetry,
        [Description("Absolute path to the local git repository to scan")] string repoPath)
    {
        if (!Directory.Exists(repoPath))
            return $"Error: directory not found — {repoPath}";

        var result = await detector.DetectAsync(repoPath);

        if (result.StaleDocs.Count == 0)
            return $"Scanned {result.TotalFiles} file(s) in '{repoPath}'. All documentation looks fresh.";

        var sb = new StringBuilder();
        sb.AppendLine($"Scanned {result.TotalFiles} file(s) in '{repoPath}'.");
        sb.AppendLine($"Found {result.StaleDocs.Count} potentially stale doc(s):");
        sb.AppendLine();

        foreach (var doc in result.StaleDocs.OrderByDescending(d => d.StaleScore))
        {
            sb.AppendLine($"• {doc.FilePath}  (staleness: {doc.StaleScore:P0})");
            sb.AppendLine($"  Doc last updated : {doc.DocLastUpdated:yyyy-MM-dd}");
            sb.AppendLine($"  Code last changed: {doc.CodeLastChanged:yyyy-MM-dd}");
        }

        // Value-first trial offer: only for non-Pro users, only when there is value to act on.
        var status = await license.GetStatusAsync();
        if (!status.IsValid)
        {
            telemetry.Track("upsell_shown", new Dictionary<string, string> { ["source"] = "scan" });
            sb.AppendLine();
            sb.AppendLine($"💡 Found {result.StaleDocs.Count} stale doc(s). `write_back` can fix them");
            sb.AppendLine("   automatically, in place — free for 7 days, no card needed →");
            sb.AppendLine("   https://buy.polar.sh/polar_cl_LcRKdosjt3TwpUkKBSoDOPOP6ea6ArOfKpyB91MSdiM");
        }

        return sb.ToString().TrimEnd();
    }
```

- [ ] **Step 3: Build to verify**

Run: `dotnet build src/LivingDocs.McpServer`
Expected: Build succeeded, 0 errors.

- [ ] **Step 4: Commit**

```bash
git add src/LivingDocs.McpServer/Tools/LivingDocsTools.cs
git commit -m "feat: emit index_success and add value-first trial offer to scan_repo

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 9: Emit `index_success` + first-run notice from the CLI

**Files:**
- Modify: `src/LivingDocs.McpServer/Program.cs` (`RunIndexAsync` ~line 122)

- [ ] **Step 1: Track `index_success` in the CLI index path (awaited, since the process exits)**

In `RunIndexAsync`, after the index completes with `total > 0`, construct a telemetry service with a notice writer (CLI can print) and **await** the send so it completes before the process exits. Updated method:

```csharp
static async Task RunIndexAsync(string repoPath)
{
    if (!Directory.Exists(repoPath))
    {
        Console.Error.WriteLine($"Directory not found: {repoPath}");
        Environment.Exit(1);
    }

    Console.WriteLine($"Indexing {repoPath} ...");
    var claude   = TryCreateClaude();
    var factory  = new ClaudeAssistedSearchFactory(claude);
    var indexer  = new IndexService(new DocExtractorService(), factory);
    var total    = await indexer.IndexRepoAsync(repoPath);
    Console.WriteLine($"Indexed {total} chunk(s). Semantic search ready.");

    if (total > 0)
    {
        var telemetry = new TelemetryService(new HttpClient(), noticeWriter: Console.Out);
        await telemetry.TrackAsync("index_success",
            new Dictionary<string, string> { ["chunks"] = total < 10 ? "1-9" : total < 50 ? "10-49" : total < 200 ? "50-199" : "200+" });
    }
}
```

- [ ] **Step 2: Build to verify**

Run: `dotnet build src/LivingDocs.McpServer`
Expected: Build succeeded, 0 errors.

- [ ] **Step 3: Run the full test suite (nothing regressed)**

Run: `dotnet test`
Expected: all tests PASS (existing suite + the 6 new telemetry tests).

- [ ] **Step 4: Commit**

```bash
git add src/LivingDocs.McpServer/Program.cs
git commit -m "feat: track index_success from the CLI index command

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 10: Manual end-to-end smoke test

**Files:** none (verification only)

- [ ] **Step 1: Pack/run locally and exercise the CLI**

Run against this very repo (it has doc comments):

```bash
dotnet run --project src/LivingDocs.McpServer -- index "$(pwd)"
```
Expected: prints the first-run notice once, prints "Indexed N chunk(s)". Confirm `~/.livingdocs/install-id` now exists.

- [ ] **Step 2: Confirm events arrived**

Open the admin dashboard funnel (`https://livingdocs-web.vercel.app/admin`) and confirm `first_run` and `index_success` each show ≥1 install. (May take a few seconds.)

- [ ] **Step 3: Confirm opt-out works**

```bash
DO_NOT_TRACK=1 dotnet run --project src/LivingDocs.McpServer -- index "$(pwd)"
```
Expected: no first-run notice, no new events in admin (counts unchanged).

---

## Task 11: Document telemetry in README

**Files:**
- Modify: `README.md`

- [ ] **Step 1: Add a Telemetry section**

Run: `grep -n "## " README.md | head -40` to find a sensible insertion point (near the end, before any License/Footer section). Insert:

```markdown
## Telemetry

LivingDocs sends a small amount of **anonymous** usage data so we can see which
setup steps people complete (install, server start, first successful index, and
when an upgrade prompt is shown). Each event contains only a random installation
ID, the app version, and your OS — **never** your code, file paths, repository
names, or any personal data.

To opt out, set either environment variable:

```
DO_NOT_TRACK=1
# or
LIVINGDOCS_TELEMETRY=off
```
```

- [ ] **Step 2: Commit**

```bash
git add README.md
git commit -m "docs: document anonymous telemetry and opt-out in README

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Task 12: Distribution action plan (Part D)

**Files:**
- Create: `docs/distribution-plan.md`

- [ ] **Step 1: Write the action plan**

Create `docs/distribution-plan.md`:

```markdown
# LivingDocs Distribution Plan

**Context (2026-05-29):** ~1,400 NuGet downloads, 0 trial starts, 0 Polar
checkout clicks. The bottleneck is reach/activation, not pricing. NuGet counts
are bot/CI-inflated. We now have funnel telemetry — use it to find the real
drop-off.

## Phase 1 — Get 10 real humans (this is the only goal that matters now)

- Manually DM 15–20 people with the exact pain: teams with stale docs / large
  Confluence spaces / .NET shops / MCP-curious devs.
- Offer to set it up **on a 20-minute call**. Goal is not the sale — it's
  watching where they get stuck.
- After each call, note the exact step they struggled with. That is your
  highest-priority fix.

## Phase 2 — Work ONE channel

Pick the channel where the *buyer* (team lead / EM), not the random installer,
hangs out:

- **Primary: Confluence/Forge Marketplace + the Vercel landing page.** The
  Marketplace audience already feels doc pain and already pays for tools.
- **De-prioritize NuGet** as top-of-funnel — it is discovery-poor and bot-heavy.
  Keep it as the install mechanism, not the marketing surface.

## Phase 3 — Read the funnel weekly

From `https://livingdocs-web.vercel.app/admin` (Product Funnel panel):

- **Installed → Indexed (activation rate):** if this is low, setup friction is
  the problem (config, API key, MCP wiring). Fix onboarding.
- **Indexed → Saw offer (offer reach):** if low, people get value but never see
  the upsell. Surface it earlier / more often.
- **Saw offer → Polar checkout click (cross-reference Polar):** if low, the
  offer/price/trust is the problem. Now — and only now — tune the offer.

Do not optimize a later stage until the earlier stage has real volume.
```

- [ ] **Step 2: Commit**

```bash
git add docs/distribution-plan.md
git commit -m "docs: add distribution action plan

Co-Authored-By: Claude Opus 4.8 (1M context) <noreply@anthropic.com>"
```

---

## Final verification

- [ ] `dotnet test` — all green.
- [ ] `dotnet build` — 0 errors.
- [ ] Web deployed; `curl` to `/api/event` returns `{"ok":true}`; admin funnel shows test events.
- [ ] CLI run produces `first_run` + `index_success`; `DO_NOT_TRACK=1` suppresses both.
- [ ] `scan_repo` shows the value-first CTA for a non-Pro user on a repo with stale docs.
