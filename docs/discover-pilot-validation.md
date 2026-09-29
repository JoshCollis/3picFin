# Discover pilot checkpoint (COL-4)

## Scope and checkout

Josh's saved answer on COL-4 directs use of clean `main`, superseding the missing feature-branch instruction. Base: `af068b5d24499536241cbb28fa806fc6b8125caa`. Frontend changes only; no backend fields, live requests, production access, push, merge, or deployment.

Search now leads the Discover content area at full width. Separate Trending Movies and Trending Shows rails split the existing authenticated `3picFin/HomeDiscover/Discover` response by media type; unavailable/empty states remain independent of search and other feeds. Both request lists open the existing details/request controller using media type and TMDb identity. Missing or unsupported media statuses remain unknown. Known media states and request-list states replace personal-attachment wording. Existing native card classes and theme variables remain in use. Long synopsis text is fully retained in a keyboard-focusable scroll region.

## Focused checks

- `node --check src/Rowan.Jellyfin.Plugin/Web/discovery.js` — passed.
- `node --check tests/Rowan.Jellyfin.Plugin.Tests/discovery-pilot.browser.cjs` — passed.
- `node --test tests/Rowan.Jellyfin.Plugin.Tests/discovery-ui.test.cjs tests/Rowan.Jellyfin.Plugin.Tests/discovery-host-contract.test.cjs tests/Rowan.Jellyfin.Plugin.Tests/native-home-rows.test.cjs tests/Rowan.Jellyfin.Plugin.Tests/home-adapter.test.cjs` — 101 passed, 0 failed.
- `git diff --check` — passed.

Coverage includes separate trending types, independent loading/empty/errors, teardown and stale responses, both request lists including Type fallback, known/unknown status mapping, permissions, POST/read-back reconciliation, duplicate protection, standard/4K, season selection, search, and related home contracts. Unit DOM fixtures are not visual or browser verification.

## Browser environment and unverified acceptance

Local setup: `npm install --prefix .qa-tools --no-audit --no-fund playwright` and `PLAYWRIGHT_BROWSERS_PATH="$PWD/.qa-tools/browsers" .qa-tools/node_modules/.bin/playwright install chromium` succeeded. No system packages or .NET SDK installed. Python venv setup failed because ensurepip is absent; the Node fixture avoids that dependency.

`PLAYWRIGHT_BROWSERS_PATH="$PWD/.qa-tools/browsers" node tests/Rowan.Jellyfin.Plugin.Tests/discovery-pilot.browser.cjs` failed before creating a browser: `libglib-2.0.so.0` missing. `ldd` reports 20 missing libraries: GLib/GObject/GIO, NSPR/NSS/NSSUtil, ATK/ATK bridge/AT-SPI, DBus, X11/Xcomposite/Xdamage/Xext/Xfixes/Xrandr/XCB, GBM, xkbcommon, ALSA.

The fixture is prepared to capture before/after rails and movie/TV/missing-data dialogs at 390px and 1280px, and check 320px overflow, long titles, 60 seasons, keyboard focus/dismissal, stale responses, search and synthetic request submission. **It has not run, and no screenshots exist yet.** It uses synthetic host CSS, not an ElegantFin reference. Full visual acceptance, actual browser behaviors and home appearance remain unverified.

## Integration and review gaps for CEO

- Trending uses the only existing trending endpoint, which requires `HomeEnabled` and `DiscoverRowEnabled`; it can be unavailable while ordinary Discover still works. No configuration changed.
- Apple-originals semantics are not exposed by existing allowlisted routes/DTOs. No Apple rail was invented or substituted with streaming-provider availability.
- `PersonalRequest`, `SharedRequest`, and `TitleDetails` expose no requester name. No requester identity is inferred. Adding identity requires a separately approved backend contract.
- The copied plan names no authorized public ElegantFin source/revision. No public reference fetched, no private captures accessed, and no claim about Josh's customized installation. CEO must provide/approve the exact public reference for the theme comparison; record revision and SHA-256 on use.
- COL-4 checkout returned `executionPolicy: null` and `executionState: null`. CEO must configure the native Modal Reviewer gate before completion is submitted. Implementation must not be marked done or handed directly around that gate.

Next: resolve browser runtime and reference/review configuration, run and inspect browser evidence, correct findings, upload screenshots, then submit through the normal issue completion route. This checkpoint is not implementation completion.
