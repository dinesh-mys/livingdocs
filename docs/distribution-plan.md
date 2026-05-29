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
