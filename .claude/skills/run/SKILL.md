---
name: run
description: Launch WatchBack's ASP.NET Core dev server against an isolated scratch database, log in past onboarding, and drive it with a headless browser (Playwright) to see or screenshot a change. Use when asked to run, start, or screenshot the app, or to confirm a UI change works for real rather than just passing tests.
---

# Running WatchBack for manual/visual verification

WatchBack ships one process: `WatchBack.Api` serves both the API and the
built frontend (`wwwroot/`). There is no separate frontend dev server to run
for visual checks — build once, run the host, drive it with a browser.

## 1. Build (includes the frontend)

```bash
dotnet build -c Release
```

`WatchBack.Api.csproj` runs `npm run build` automatically before the C#
build in Release config, regenerating `wwwroot/css/app.bundle.css` and
`wwwroot/js/app.js` from `frontend/src`. **This matters even for
CSS-only/HTML-only changes to `wwwroot/index.html`**: Tailwind is JIT —
a utility class you add (e.g. `overflow-x-auto`) will not exist in the
committed `app.bundle.css` until you rebuild. Don't skip this step and
conclude a class "should work" — verify the built CSS actually contains it:

```bash
grep -o '\.overflow-x-auto{[^}]*}' src/WatchBack.Api/wwwroot/css/app.bundle.css
```

## 2. Launch against an ISOLATED database — never the real one

By default the app writes to `~/.config/WatchBack/watchback.db` (see
`Program.cs`, `WATCHBACK_DATABASE_PATH`) — **that's the user's real local
dev data**, including their real login. Always override it to a scratch
path so a throwaway verification run can't touch it:

```bash
scratch=/tmp/claude-*/*/*/scratchpad   # or wherever your scratchpad is
mkdir -p "$scratch/wbdata"
cd src/WatchBack.Api
WATCHBACK_DATABASE_PATH="$scratch/wbdata/watchback.db" \
ASPNETCORE_URLS=http://localhost:8485 \
nohup dotnet bin/Release/net10.0/WatchBack.Api.dll > "$scratch/wb.log" 2>&1 &
sleep 3
curl -s -o /dev/null -w "%{http_code}\n" http://localhost:8485/
```

Gotchas:
- **Run from `src/WatchBack.Api/`** (or set content root explicitly). Running
  the DLL from elsewhere makes ASP.NET fail to resolve `WebRootPath` and every
  static asset 404s — you'll see `The WebRootPath was not found` in the log.
- Check `ps aux | grep WatchBack.Api.dll` before reusing a port — a stray
  instance from a previous run (esp. one still pointed at the *old* build
  output before you rebuilt) will silently keep serving stale assets and
  `curl` will still return 200. Kill it and relaunch after every rebuild you
  want reflected.
- There may be an unrelated **system-level instance** already running (e.g.
  a systemd service under a different Linux user, port not 8485). Don't
  touch it — `ps aux` shows the owning user; only kill PIDs you started.

## 3. First run: capture the generated credentials

A fresh database has no account. On first startup the app prints a boxed
"Initial Credentials" block to stdout/log — grab it instead of guessing:

```bash
grep -n -A3 "Initial Credentials" "$scratch/wb.log"
```

## 4. Log in and get past onboarding

The login flow for a **brand-new** account has extra steps a returning-user
flow doesn't:

1. **Login form** — fill username/password from the log, submit.
2. **"Set Up Account" step** (first login only) — the generated credentials
   are temporary; the app immediately demands a new permanent
   username/password (`#setup-username`, `#setup-password`). Set your own
   known password here — you'll need it if the script runs again against the
   same scratch DB.
3. **5-step onboarding wizard** (first login only) — a modal walking through
   provider setup. For UI verification unrelated to onboarding, don't fill it
   out: click the `Skip, I'll set this up later` link to drop straight into
   the main app.
4. Only *after* that do the normal app chrome and the gear-icon settings
   panel exist in the DOM/visible.

Re-running the same script against a **DB that already has an account**
skips straight past steps 2–3 into the login form only — use the
username/password *you* set in step 2, not the generated ones (they're
invalidated).

## 5. Drive it with Playwright — no npm install needed

There's no `playwright` in `frontend/node_modules`. Don't add one. The
.NET Playwright package used by `tests/WatchBack.Api.Tests` already ships a
full playwright-core + bundled Node + downloaded browsers in the NuGet cache
— reuse it directly instead of `npm install`ing a second copy:

```bash
PW_DIR=$(find ~/.nuget/packages/microsoft.playwright -maxdepth 1 -iname '1.*' | sort -V | tail -1)/.playwright
NODE="$PW_DIR/node/linux-x64/node"   # adjust arch if not x64
PLAYWRIGHT_PKG="$PW_DIR/package/index.js"
```

Write a small script requiring `PLAYWRIGHT_PKG` and run it with `$NODE`:

```js
const { chromium } = require(process.env.PLAYWRIGHT_PKG);
(async () => {
  const browser = await chromium.launch({ args: ['--no-sandbox'] });
  const page = await (await browser.newContext({ viewport: { width: 375, height: 667 } })).newPage();
  await page.goto('http://localhost:8485/');
  // ...login steps from §4...
  await page.screenshot({ path: 'out.png' });
  await browser.close();
})();
```

Notes specific to this app's markup:
- The settings/gear button has **no `aria-label`**, only `:title` — select
  it by icon instead: `button:has(ion-icon[name="settings-sharp"])`.
- Config panel tabs have stable ids: `#tab-settings`, `#tab-diagnostics`,
  `#tab-mappings`, `#tab-apikeys` (`aria-controls` gives the matching
  `#panel-*`).
- For mobile-viewport layout bugs, 375×667 (iPhone SE-ish) is narrow enough
  to reproduce overflow/clipping issues that don't show at desktop widths.

## 6. Confirm the bug is real, not just the fix

When verifying a layout fix, it's worth reproducing the *broken* state first
so the "fixed" screenshot is actually meaningful:

```bash
git stash push -- <the changed file>
dotnet build -c Release   # or npm run build if only frontend files changed
# kill + relaunch the server, take the "before" screenshot
git stash pop
dotnet build -c Release
# relaunch, take the "after" screenshot
```

A locator that times out waiting to become "visible" (rather than erroring
"not found") is itself good evidence of an overflow/clipping bug — Playwright
still finds the element in the DOM but the layout hides it.

## 7. Clean up

```bash
pkill -f "bin/Release/net10.0/WatchBack.Api.dll"   # only kills processes YOU started this way
rm -rf "$scratch/wbdata"
```

Double-check `ps aux | grep -i watchback` afterward — confirm only your own
scratch instances died and any pre-existing system service is still running.
