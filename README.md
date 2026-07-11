# Usage Widget

A native Windows system-tray widget that shows real-time usage limits for multiple Claude accounts
(4+) and OpenAI Codex in one glanceable view. Each account shows a **current session** (rolling 5h)
and a **weekly** (7d) bar with % used and a reset countdown.

This repo contains **Track A** of the build (per [`BRIEF.md`](BRIEF.md)): the cross-platform,
fully-tested **core framework** plus the **Windows WPF app shell** wired to it. The live data path
is gated on captured request fixtures — see [Status](#status).

---

## Status

| Track | What | State |
|-------|------|-------|
| **A — framework** | adapters, template/mapping engine, redaction, allowlist, config + migration, polling/isolation, secret store, tray/popup shell | ✅ built, **151 offline tests passing** |
| **B — live path** | real Claude/Codex endpoint replay | ⛔ gated on a captured request fixture (§0) |

The Claude and Codex usage endpoints are undocumented, so their request templates ship as
`FILL ME IN` scaffolds. The app is fully functional the moment you paste a real captured
request into the Request Template editor (or `adapter-config.json`).

> ⚠️ **Read the risk register in [`BRIEF.md` §1](BRIEF.md):** edge/anti-automation (Cloudflare),
> short-lived web tokens, and the ToS gray area. These determine whether paste-token replay works in
> practice or whether the (reserved) browser-extension path becomes primary.

---

## Quickest way to run it (no compiling)

Every push builds the portable `.exe` on a Windows runner and attaches it to the CI run:

1. Go to the repo on GitHub → **Actions** tab → click the latest green **CI** run.
2. Scroll to **Artifacts** → download **`UsageWidget-portable`** (a zip).
3. Unzip it. Inside, open the **`win-x64`** folder (use `win-arm64` only on an ARM PC).
4. Double-click **`UsageWidget.exe`**. A widget icon appears in your system tray (bottom-right,
   near the clock — click the `^` to find it). No install, no admin.
   - First launch may show "Windows protected your PC" (unsigned app) → **More info → Run anyway**.

Then jump to [Adding your accounts](#adding-your-accounts).

---

## Project layout

```
UsageWidget.sln
src/
  UsageWidget.Core/        # net8.0, cross-platform — ALL logic. No WPF, fully unit-tested.
  UsageWidget.App/         # net8.0-windows, WPF + tray. Builds on Windows only.
tests/
  UsageWidget.Core.Tests/  # xUnit offline suite (the merge gate)
  fixtures/                # normalized §0 fixtures (redacted) consumed by the tests
.github/workflows/ci.yml   # Linux: offline tests | Windows: build + publish portable exe
```

`UsageWidget.Core` has no Windows dependencies and builds/tests on any OS — that is the offline CI
gate. `UsageWidget.App` references the WindowsDesktop SDK and builds only on Windows.

---

## Build & test

### Offline (any OS — the merge gate)

```bash
dotnet build src/UsageWidget.Core/UsageWidget.Core.csproj
dotnet test  tests/UsageWidget.Core.Tests/UsageWidget.Core.Tests.csproj
```

### Full solution + portable exe (Windows)

```powershell
dotnet build UsageWidget.sln -c Release

# Self-contained, single-file, portable — no runtime install, no admin.
dotnet publish src/UsageWidget.App/UsageWidget.App.csproj -c Release -r win-x64   --self-contained -o publish/win-x64
dotnet publish src/UsageWidget.App/UsageWidget.App.csproj -c Release -r win-arm64 --self-contained -o publish/win-arm64
```

The resulting `publish/<rid>/UsageWidget.exe` is a portable executable: copy it anywhere and run it.

---

## What the framework guarantees (and where it lives)

These are the non-negotiable contracts from the brief, each backed by tests:

- **Credential classes never interchangeable** — `AccountSource` (`Accounts/AccountSource.cs`).
- **Never a billed completion to discover usage** — the Anthropic API fallback is a separate,
  opt-in source with its own template; web-token accounts never touch `api.anthropic.com`.
- **Encrypt secrets, not config** — secrets only in `ISecretStore`
  (`Secrets/WindowsCredentialManagerStore.cs`); templates store `{{TOKEN}}` and inject at send time
  (`Templating/TemplateEngine.cs`).
- **Hostname allowlist, fail closed + inject-after-allowlist + redirects-off**
  (`Security/HostnameAllowlist.cs`, `Adapters/TemplateAdapter.cs`, `Net/HttpClientSender.cs`).
- **Per-account failure isolation** (`Polling/RefreshCoordinator.cs`).
- **UTC parse / local render time handling** (`Time/TimeMath.cs`).
- **Redaction on every surface** (`Security/Redactor.cs`).
- **No telemetry / network calls** beyond the configured adapters.
- **Single instance** (`App/SingleInstance.cs`).
- **Error taxonomy** Unauthorized / RateLimited / NetworkTimeout / Challenge / ParseFailed
  (`Model/RefreshErrorKind.cs`, `Net/ResponseClassifier.cs`), with Cloudflare interstitials detected
  as `Challenge`, not `ParseFailed` (`Security/ChallengeDetector.cs`).
- **Config schema versioning + backup** (`Config/ConfigStore.cs`, `Config/ConfigMigrator.cs`),
  with atomic writes and corrupt-file recovery.

### Connection robustness

The transport and polling layers are hardened for a long-running, multi-account tray app:

- **Cookies replay verbatim** — the HTTP handler's cookie container is disabled, so one account's
  `Set-Cookie` (Cloudflare rotates edge cookies constantly) can never overwrite or cross-contaminate
  another account's pasted session cookie (`Net/HttpClientSender.cs`).
- **Transport headers stripped at send time** — a captured `Accept-Encoding: … zstd` would make the
  server reply with compression .NET can't decode; `Host`/`Content-Length`/`Connection` and friends
  are derived from the actual request instead of replayed.
- **Transient retry with jitter** — one quick in-cycle retry for timeouts, DNS/connect faults and
  408/5xx (never for 429 or challenges) (`Net/RetryingHttpSender.cs`).
- **Staggered polling** — accounts refresh a few seconds apart, never as one burst, at a 3-minute
  default cadence (§11); cycles are serialized so a manual refresh can't double-fetch
  (`Polling/RefreshCoordinator.cs`, `Polling/PollingLoop.cs`).
- **Backoff that names its cause** — 429 and challenge backoffs keep reporting "rate limited" vs
  "security check" with a retry ETA, and a re-pasted login clears the backoff immediately
  (`Polling/BackoffPolicy.cs`, `Polling/AccountRefresher.cs`).
- **Proxy-aware** — the system proxy and authenticated-proxy credentials are used automatically, and
  HTTP 407 is reported as a proxy problem, not a token problem. (Note: .NET caches the system proxy
  config per process, so if you connect/disconnect a VPN or change your proxy while the widget is
  running, restart it to pick up the new settings.)
- **Multi-credential captures** — a request that carries both a `Cookie` and an `Authorization`
  header keeps BOTH (stored as one encrypted envelope, each injected into its own header)
  (`Templating/TemplateEngine.cs`, `Import/CurlAccountImport.cs`).
- **Honest failure states** — the UI keeps the last good numbers through transient failures and
  shows how old they are once stale (2× cadence); an HTML login page classifies as "session
  expired", a marker-less edge block as a challenge, never as a "config problem".

---

## Capturing your token + request (the §0 prerequisite)

For each browser profile signed into a Claude account:

1. Open **claude.ai → Settings → Usage**.
2. Press **F12 → Network** tab, then refresh the usage panel.
3. Find the fetch/XHR whose JSON response contains session %, weekly %, and reset times.
4. **Save it:** right-click the request → *Copy → Copy as cURL* (or *Save all as HAR*).
5. Copy the auth value:
   - the **`Authorization: Bearer …`** header, and/or
   - the **session cookie**. If it's `httpOnly` it won't show in `document.cookie` — copy it from
     **Application → Cookies** or the Network request's **Request Headers**.
6. **Replay the full header set** (including `User-Agent`) — a partial replay is the most common
   cause of an edge challenge (see brief §1 R1).

Normalize a redacted copy into `tests/fixtures/<provider>/request.json` + `response.json`
(see existing fixtures for the shape). The fixture-safety test fails the build if a real secret slips
in.

---

## Filling in the Request Template

In-app: tray → **Settings → Request Template editor**. Or edit
`%APPDATA%\UsageWidget\adapter-config.json` directly. Per source type, set:

| Field | Notes |
|-------|-------|
| `Url` | from your capture (replace the `FILL ME IN`) |
| `Method` | usually `GET` |
| `Headers` | full captured set; put `{{TOKEN}}` where the secret goes |
| `AllowedHosts` | hostnames this template may contact (fail-closed) |
| `Mappings.SessionPct` / `WeeklyPct` | JSONPath to the % (or use `Used`+`Limit`) |
| `Mappings.SessionReset` / `WeeklyReset` | JSONPath to the reset; set `*ResetKind` to `Timestamp` or `DurationSeconds` |
| `Mappings.Identity` | optional JSONPath to email/org for the row label |

A mapping prefixed with `header:` reads a **response header** instead of the body (used by the
Anthropic API fallback). The editor validates before saving (URL parses, host allowlisted, method
supported, mappings valid, `{{TOKEN}}` only in auth fields, no real secret present).

---

## Adding your accounts (one paste each)

No forms, no JSONPath. For each Claude account:

1. Open that account → **claude.ai → Settings → Usage**.
2. **F12 → Network tab → tick `Fetch/XHR`**, then click the **↻ refresh** circle on the Usage panel.
3. Right-click the request named **`usage`** → **Copy → Copy as cURL**.
4. In the widget: left-click the tray icon → **Add account** → paste it into the one box → nickname →
   **Save**.

The app pulls the URL, headers, your cookie, and the number locations out of that paste
automatically. The row turns into two live bars. Repeat for your other accounts.

- A **Re-paste token** button appears on any row whose login has expired — same Copy-as-cURL, paste,
  done.
- Deleting an account wipes its cookie from Windows Credential Manager.
- The **Advanced: endpoint setup** screen is only needed if Claude changes its usage page and the
  bars stop working — the Claude defaults are already baked in.

Credential Manager entries are keyed `UsageWidget/{accountId}/{sourceType}` (stable id, not nickname)
so renaming never orphans a secret.

---

## Known limitations

- **Undocumented endpoints:** Claude/Codex usage APIs are unofficial and change without notice —
  that's why the template is editable without a rebuild.
- **Token expiry:** web session tokens may be short-lived; you may need to re-paste. The reserved
  browser-extension source (no paste, no expiry) is the durable answer.
- **Edge challenges:** Cloudflare may block a server-side replay regardless of a valid token.
- **ToS gray area:** replaying authenticated requests across multiple accounts may conflict with
  provider terms; use a conservative cadence. See brief §1.

## When a provider changes its endpoint

Edit only `adapter-config.json` (or the in-app editor): `Url`, `Method`, `Headers`, `AllowedHosts`,
and the `Mappings.*` JSONPaths. No recompile required.
