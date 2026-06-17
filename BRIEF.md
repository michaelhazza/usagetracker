# Build Prompt v3: Windows Usage Widget (paste-token, multi-account)

> **Status:** Revised from v2 after a development-readiness review. Changes in v3 are about
> de-risking the one thing the whole product hinges on (a real, captured usage request) and
> closing the gaps that would otherwise cause mid-build churn. See **§0 Readiness & sequencing**
> first — it is the most important section.

Paste this into Claude Code as the project brief.

---

## 0. Readiness & sequencing (read this first)

This brief is **build-ready as a framework**, and becomes **build-ready as a working product** the
moment one real artifact exists (see Prerequisite below). Build in two parallel tracks so the risky
20% does not gate the safe 80%.

### Prerequisite artifact (unblocks everything)

Before the live data path can be proven, the owner must capture **one real usage request per
provider** and commit them as redacted test fixtures:

- **Claude:** Settings → Usage → DevTools (F12) → Network → refresh the usage panel → find the
  fetch/XHR whose JSON contains session %, weekly %, and reset times → **Save as HAR** (or
  Copy as cURL). Redact all secrets (see §8 redaction list) before committing.
- **Codex:** the equivalent usage request the Codex client makes.

Commit these (secrets stripped, structure intact) to `tests/fixtures/`. They become both the source
of truth for the JSON mappings **and** the input to the offline parser tests (§12). Until they
exist, ship the default templates with `FILL ME IN` markers and treat the live path as unproven.

### Track A — buildable now, against fixtures (no live endpoint needed)

Adapter interface, `AccountSource`, Credential Manager store, config load/save + schema versioning,
template/mapping engine, redaction, hostname allowlist (fail-closed), polling + per-account
isolation, tray icon, popup UI, single-instance, packaging. All unit-tested against fixtures.

### Track B — live validation (gated on the prerequisite + risk findings)

Slot real templates in; confirm whether a server-side replay survives the provider's edge
protection (see §1 risk R1). **If replay proves unreliable, the `LocalhostBrowserExtension` source
is promoted from stretch goal to the primary data path** — design Track A so that swap is a config
change, not a rewrite.

---

## 1. Known risks & explicit decisions required

These are real-world unknowns that affect whether the paste-token approach works at all. Each needs
an owner decision, not a silent assumption.

- **R1 — Edge/anti-automation (Cloudflare).** claude.ai sits behind Cloudflare. A bare server-side
  `HttpClient` replay using only a copied cookie/bearer frequently returns 403 / a JS challenge
  even with valid credentials, because it lacks the browser's `User-Agent`, full header set, and TLS
  fingerprint. **Mitigation:** replay the *entire* captured header set (including `User-Agent` and
  any `x-*`/client headers), not just auth. **Decision:** accept that some challenges are unbeatable
  from outside the browser; if replay is unreliable, fall back to the extension (R2).
- **R2 — Token lifetime.** The web session token may rotate frequently (possibly ~hourly). If so,
  "paste a token" becomes "re-paste 4 tokens every hour," which undermines the glanceable premise.
  **Decision:** treat paste-token as the *bridge* and the MV3 extension as the *durable* path. Set
  expectations in the README; surface expiry clearly per-row (see §9).
- **R3 — Account safety / ToS.** Programmatically polling 4+ accounts on a fixed cadence is exactly
  the pattern abuse-detection looks for. **Decision (owner):** state an acceptable poll cadence
  ceiling and risk tolerance up front; the polling defaults in §11 follow from it. Conservative by
  default.

---

## Goal

Build a **native Windows system-tray / taskbar widget** that shows real-time usage limits for
**multiple Claude accounts** (4+) and **OpenAI Codex** (1) in one glanceable view. Each account
shows two progress bars:

- **Current session** — the rolling 5-hour window, with % used and a reset countdown.
- **Weekly (all models)** — the 7-day window, with % used and a reset countdown.

Each Claude account is logged into a **separate browser profile**, so accounts are added by
**pasting a token** copied from that profile's dev tools — NOT by reading CLI logins. Adding a 5th
account must be as easy as pasting another token.

## Tech stack

- **C# + .NET 8 + WPF**, published as a single self-contained, portable `.exe` (no runtime install,
  runs without admin rights).
- Tray icon + a small borderless, always-on-top popup panel (one row per account).
- Secrets stored only in **Windows Credential Manager**. Non-secret config stored in `%APPDATA%`.
- **JSON mapping engine:** use a single, declared JSONPath implementation. Default to
  `Newtonsoft.Json` `SelectToken` (JSONPath subset) for familiarity, or `JsonPath.Net` if full
  JSONPath is needed — **pick one and document the supported syntax** so config authors aren't
  guessing.
- (Tauri v2 is an acceptable substitute, but keep the same architecture and contracts below.)

## Non-negotiable implementation contracts

These are hard requirements, not suggestions:

1. **Credential classes are distinct and never interchangeable:** a Claude web session token, an
   Anthropic API key, OpenAI/Codex credentials, and the future browser-extension source are
   different source types. Never assume a Claude web token can call api.anthropic.com.
2. **Never make a model-completion request to discover usage** unless a source is explicitly
   configured as an Anthropic API key with API probing turned on in settings. (Such a request bills
   real tokens.)
3. **Encrypt secrets, not config.** Tokens/keys live only in Windows Credential Manager. Endpoint
   request templates and JSON parsing rules are stored as plaintext, editable JSON in
   `%APPDATA%\UsageWidget\adapter-config.json`. Request templates store a `{{TOKEN}}` placeholder
   only — the real secret is injected from Credential Manager at send time and never written to the
   template file.
4. **Hostname allowlist per adapter, fail closed.** Each adapter declares an explicit list of
   allowed hostnames; any request to a hostname outside that list must fail rather than proceed.
   This is unit-tested (§12).
5. **Per-account failure isolation.** A failed, expired, or slow account must never block or delay
   refreshes for any other account. Each refresh runs with an independent timeout (default **10s**)
   and its own `CancellationToken`.
6. **Time handling.** Parse all reset timestamps as UTC into `DateTimeOffset`; if only a duration is
   returned, convert it to an absolute reset time at refresh. Render countdowns using local Windows
   time. Derive period start (e.g. session window start = `resetAt − 5h`) for the elapsed marker.
7. **Redaction by default.** Logs and diagnostics must redact `Authorization`, `Cookie`,
   `Set-Cookie`, access/refresh tokens, account IDs, org IDs, emails, and raw response bodies.
   Redaction is unit-tested (§12).
8. **No telemetry, crash upload, analytics, or third-party network calls** of any kind.
9. **Single instance.** Launching a second copy focuses/opens the existing tray popup instead of
   starting a new process.

## Architecture (data layer must be swappable)

- A **provider adapter interface** that takes a stored credential + a request template and returns a
  normalized struct (see §3 for the exact schema, including counts-or-percent and the error
  taxonomy).
- An **`AccountSource`** type, supported from day one:
  - `ClaudeWebToken` (primary)
  - `AnthropicApiKey` (optional fallback only — see below)
  - `CodexPastedToken`
  - `CodexAuthJson` (reads `%USERPROFILE%\.codex\auth.json` and WSL `~/.codex/auth.json`)
  - `LocalhostBrowserExtension` (reserved — wire the type now, implement later; see R1/R2 for why it
    may be promoted to primary)
- All endpoint URLs, methods, headers, and JSON mappings live in the editable adapter-config, not
  in compiled code.

## 3. Normalized result contract (exact schema)

The adapter returns a discriminated result, **not** a struct with a free-text `error` string.

```jsonc
// Success
{
  "accountLabel": "string",          // resolved identity if available, else nickname
  "session": { "used": 0, "limit": 0, "pct": 0.0, "resetAt": "ISO-8601 UTC" },
  "weekly":  { "used": 0, "limit": 0, "pct": 0.0, "resetAt": "ISO-8601 UTC" },
  "fetchedAt": "ISO-8601 UTC"
}
```

- **Counts OR percent.** The mapping schema must accept **either** `used`+`limit` **or** `pct`. If
  counts are present, derive `pct`; if only `pct` is present, leave counts null. Never discard
  absolute numbers when the endpoint provides them.
- **Error taxonomy (enum, not string).** Each failed refresh resolves to exactly one:
  - `Unauthorized` (401/403, expired/invalid token) → drives the per-row "re-paste token" prompt.
  - `RateLimited` (429) → drives backoff (§11); show "rate limited" not "error".
  - `NetworkTimeout` (DNS/connect/read timeout) → show "stale", retry next cycle.
  - `Challenge` (edge/anti-bot interstitial, see R1) → distinct message; likely needs full header
    replay or the extension path.
  - `ParseFailed` (HTTP 200 but mappings didn't resolve) → "config problem," point user at the
    Request Template editor.
- **Validation = successful parse, not identity.** "Success" means **at least one** of
  `session`/`weekly` resolves a `pct` (or `used`+`limit`). If identity (email/org) is present,
  display it; otherwise use the nickname. Identity is never required.

## 4. Request Template editor (resilience against undocumented endpoints)

The Claude usage endpoint is undocumented and will change. The app must not require a rebuild when
it does. Provide an in-app **Request Template editor** (and the equivalent JSON in
`adapter-config.json`) where the owner can set, per source type:

- URL
- HTTP method
- headers, using `{{TOKEN}}` where the secret goes (replay the *full* captured header set — see R1)
- optional request body
- allowed hostnames (the §contract-4 allowlist)
- JSONPath (or header-name) mappings for: session used/limit/pct, weekly used/limit/pct, session
  reset, weekly reset, and optional identity (email/org)

**Template scoping:** templates are keyed **per source type** (the default), with an **optional
per-account override** so one profile can diverge if a provider varies responses. The resolver picks
the account override if present, else the source-type default.

**Config schema versioning:** `adapter-config.json` carries a top-level `"schemaVersion"`. On load,
migrate older versions forward and back up the prior file (`adapter-config.<version>.bak`). Never
silently break a hand-edited config on upgrade.

## 5. Getting the data (no official API — replay the browser's own request)

1. **Claude (primary):** replay the request claude.ai's Settings → Usage page makes.
   - The owner captures the request per §0 (HAR/cURL) and pastes URL/headers/mappings into the
     Request Template editor. Replay the **full header set**, not just auth (R1).
   - Auth value comes from the `Authorization` bearer and/or the session cookie. Note: if the
     session cookie is httpOnly it won't appear via `document.cookie`; copy it from Application →
     Cookies or the Network request headers.
   - Do NOT hardcode a guessed endpoint. Ship a default template with `FILL ME IN` comments.
2. **Codex:** support both `CodexPastedToken` and `CodexAuthJson`. Replay the Codex usage request
   (endpoint captured per §0).
3. **Anthropic API fallback (optional, separate, off by default):** only for accounts explicitly
   added as `AnthropicApiKey`. The unified 5h/7d utilization values are returned as headers on an
   `api.anthropic.com/v1/messages` call, which bills real tokens — so this path requires explicit
   opt-in per account. Never run it for `ClaudeWebToken` accounts. If unreliable, mark it
   unsupported rather than guessing. (Low priority — owner's accounts use web tokens, not API keys.)

Reference implementations to read for request shapes (verify against the live Network tab, don't
copy blindly): `Zrnik/claude-usage-windows-taskbar-widget`,
`jens-duttke/usage-monitor-for-claude`, `CUStats` (UX).

## Account management

- **Add account:** pick source type → paste token (or point to auth.json) → optional nickname.
- **Validate by successful parse**, per §3 (at least one window resolves). Not by identity.
- Support an arbitrary number of accounts (4 Claude now, design for more).
- Each account = one row: label, service icon (C for Claude, X for Codex), two bars, countdowns.
- Reorder and delete accounts; deleting wipes the token from Credential Manager.
- **Expired-token detection:** an `Unauthorized` result puts that row in an error state with a
  "re-paste token" button; never fail silently or take down the widget.

## UI / behavior

- **Tray icon** shows the **highest current-session %** across accounts, color-coded: green < 75%,
  orange 75–90%, red ≥ 90%. *(Note: rendering live % text into a 16/32px icon means GDI-drawing the
  glyph and swapping `NotifyIcon` on each update — account for DPI scaling. Non-trivial; budget for
  it.)*
- Left-click opens the popup with all rows; hover shows a summary tooltip.
- Each bar: fill for % used; optional thin marker for elapsed time in the period (derived per
  contract #6); reset countdown.
- **Per-row state indicators** map to the §3 error taxonomy: ok / stale / rate-limited /
  re-paste-needed / config-problem.
- Right-click menu: refresh now, add account, settings, start-with-Windows toggle, quit.
- **Adaptive polling:** see §11 for exact numbers.

## 11. Polling — concrete numbers (tune from the R3 decision)

- **Default cadence:** 3 min per account, **staggered** so requests don't fire simultaneously.
- **Idle/locked:** when the session is locked or the user is idle > 15 min, slow to 15 min.
- **Backoff on 429/`RateLimited`:** exponential — 5 min, 10 min, 20 min, cap 60 min; reset on first
  success.
- **Per-request timeout:** 10s (contract #5).
- **Stale indicator:** show "stale" when the last *good* refresh is older than **2× the current
  cadence**.
- These are starting points; the cadence ceiling is governed by the R3 risk decision.

## Packaging & startup

- Single self-contained portable `.exe` (x64 and ARM64); runs without admin.
- Start minimized to tray; optional auto-start via the registry Run key or Startup folder.
- Single-instance enforcement (see contract #9).
- Graceful credential cleanup when an account is deleted or the app is uninstalled.
- Note WPF single-file/self-contained caveats per arch; verify the ARM64 build separately.

## 12. Testing strategy (offline, CI-able)

Because every acceptance criterion below needs live accounts, add a parallel offline suite that
needs **zero network** and runs in CI:

- **Fixtures:** the redacted captures from §0 in `tests/fixtures/` drive the parser tests.
- **Template/mapping engine:** given a fixture response + a template, assert the normalized struct
  (counts-or-percent derivation, both windows, identity fallback).
- **Redaction:** assert every item in the contract-#7 list is scrubbed from sample log lines and
  diagnostics.
- **Hostname allowlist:** assert a request to a non-allowlisted host **fails closed**.
- **Time/countdown math:** UTC parse, duration→absolute conversion, local render, elapsed marker.
- **Error taxonomy:** map representative responses (401, 429, timeout, challenge HTML, 200-but-
  unmapped) to the correct enum value.
- **Config migration:** load an older `schemaVersion` and assert forward migration + backup.

## Stretch goals (after core works)

- Implement the reserved **`LocalhostBrowserExtension`** source: a Manifest V3 extension installed
  per browser profile that reads usage from the live session (no token paste, no expiry) and POSTs
  it to a localhost endpoint the widget listens on. **(May be promoted to primary — see R1/R2.)**
- Threshold desktop notifications (e.g. 90%), with a time-aware mode that fires only when usage
  outpaces elapsed time.
- 14-day sparkline history per account (stored locally in `%APPDATA%`).
- ChatGPT (non-Codex) message-quota tracking — a *separate* data source from Codex; its own adapter
  or extension. Out of scope unless trivial.

## Acceptance criteria

**Offline (CI, no live accounts) — gates the framework:**

- Template engine maps fixture responses to the normalized struct (counts-or-percent, both windows).
- Redaction scrubs every secret/identifier in the contract-#7 list.
- Hostname allowlist fails closed for non-allowlisted hosts.
- Error taxonomy classifies 401 / 429 / timeout / challenge / 200-unmapped correctly.
- Config migrates an older `schemaVersion` and backs up the prior file.
- App is single-instance and starts minimized to tray when launched at Windows startup.

**Live (needs accounts + captured endpoints) — gates the product:**

- Runs as a Windows tray widget from a single portable .exe, without admin rights.
- Add 4+ Claude accounts purely by pasting tokens; each shows a live session + weekly bar with %
  and reset countdown; one Codex account works too.
- Tray icon reflects the worst current-session % across accounts, color-coded.
- One failed/expired account never prevents other accounts from refreshing.
- Expired tokens surface a per-row "re-paste" prompt without crashing the app.
- No completion request is ever made for a web-token account.
- Endpoint URL, method, allowed hostnames, headers, and JSON mappings are editable (via the Request
  Template editor / `adapter-config.json`) without recompiling.

## Deliverables

- Full .NET 8 WPF solution + `dotnet publish` instructions for self-contained x64/ARM64 exes.
- Offline test project with fixtures (§12).
- A README covering: how to capture the token **and the full request (HAR)** from each browser
  profile's DevTools (including the httpOnly-cookie case), how to fill in the Request Template, how
  to add/remove accounts, **realistic expectations on token expiry (R2) and the ToS gray area (R3)**,
  the edge/anti-automation caveat (R1), and exactly which config fields to edit when a provider
  changes its endpoint.
