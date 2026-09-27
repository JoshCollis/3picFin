# Optional Home adapter — distribution compatibility gate

The embedded discovery stylesheet URL must be built through Jellyfin's base-path-aware API client:

<!-- discovery-host-css-contract -->
```js
function discoveryStylesheetUrl(ApiClient) {
    return ApiClient.getUrl('3picFin/Web/discovery.css');
}
```

The packaged adapter is injected only when File Transformation sees the exact tested **pre-transform** `index.html` body, with both Rowan flags enabled. It preserves HSS and Plugin Pages tags and never transforms the HSS chunk. Production pins in `HomeAdapterRegistration.cs`, `home-adapter.js`, and `home-tab-host.js` remain `null`: no production distribution was verified.

A distribution-specific browser manifest names the tested Home chunk filename and SHA-256 of its **delivered** body. The client requires one Resource Timing script entry for a Home chunk at the exact same-origin URL resolved against `document.baseURI`, then re-fetches that URL with `cache: no-store` and hashes its response. Missing, ambiguous, redirected, mismatched or blocked responses fail closed. It separately checks the final native DOM for unique direct-child `homeTab` and `favoritesTab` with their expected indexes and a direct `.sections`, signed-in `ApiClient` identity, visible Home, and non-Favorites route. It tears down on route/user/pane changes, including pending async loads, and does not move HSS `.sections`.

**This is a compatibility drift check, not cryptographic proof of active browser bytes.** A service worker, cache, or time-of-check/time-of-use change can make the refetch differ from bytes already executed. No parsed-index re-fetch/hash is used; the server's pre-transform index guard is separate. Do not call `compatibilityVerified` active-byte attestation. A missing production pin keeps the feature inert; never promote a lab pin without testing the actual production-distributed assets.

The optional featured hero is queried only after the distribution-gated host has mounted active Home. The adapter opts the host into a user-scoped `Rowan/Home/Hero` read; `HeroPolicy.Enabled` returns no slides unless Home and the trusted-filesystem image gate are enabled and configured libraries are selected. The host rejects invalid slide identity/image tags and loads `static-hero.js`/`.css` only when slides exist. It inserts its own root before the original direct-child `.sections`, never moves or edits HSS rows or Favorites, and removes the root, stylesheet, image requests and timers on Fin selection, route teardown, logout or user switch. Return to Home re-reads the endpoint. Up to ten slides use the existing still-image carousel. Open is host-owned: it checks the active Home route/session, re-reads `Users/{userId}/Items/{id}`, verifies item ID and Movie/Series type, and calls `Emby.Page.showItem` with the server ID; it never accepts a navigation URL from a slide. This does not disable or replace HSS Media Bar settings/markup; it is an optional top-of-Home component.

The image filesystem trust gate remains **off** where metadata writers are administrator-equivalent. In that state the endpoint returns `[]`, so no blank hero mounts. Production index, Home chunk and host pins remain unset, and File Transformation remains gated: this commit does not activate Home injection on Gill.

Disposable automation (Docker and network access required):

```sh
dotnet test tests/Rowan.Jellyfin.Plugin.Tests/Rowan.Jellyfin.Plugin.Tests.csproj -c Release
node --test tests/Rowan.Jellyfin.Plugin.Tests/*.test.cjs
python3 tests/disposable-home-adapter.py
python3 tests/disposable-home-adapter.py --with-dependencies
python3 tests/disposable-home-adapter.py --with-dependencies --test-index-pin
python3 tests/disposable-home-adapter.py --with-dependencies --test-index-pin --browser
python3 tests/Rowan.Jellyfin.Plugin.Tests/home-tab-host.browser.py
```

The `--browser` run copies source into scratch, pins only that copy to the tested original index and Home chunk, installs File Transformation 3.0.1.0, Plugin Pages 3.0.1.0 and HSS 3.0.2.0, verifies the chunk body hash, completes the wizard with a generated disposable account, and lets the injected adapter mount automatically in signed-in Chromium. It asserts one tab host/panel, untouched direct native sections/Favorites, Fin selection, Favorites and route teardown/remount, and identity invalidation. Separate browser contexts inject wrong refetched body, missing timing and wrong loaded URL and assert no mount. The disposable server restarts and rechecks injection; Docker container, credentials and scratch tree are removed on exit. The final Python command checks 360px and 1280px geometry in an isolated CSS fixture.

No production distribution is pinned. A private disposable clone of the intended web distribution and its File Transformation/HSS/Plugin Pages binaries mounted with lab-only pins, populated three HSS rows, switched Home/Fin/Favorites, and logged in a second synthetic user. File Transformation's exact callback input matched the **origin-served** index body, not the on-disk bytes; a disk-byte pin failed closed. The clone contains no ElegantFin asset and cannot establish live household or theme parity.

### Clone acceptance exceptions (not production waivers)

An extended clone run used two libraries and a strict localhost Nginx `/jellyfin/` prefix (`location /` returns 404). At 320/390/768/1100 CSS pixels, direct access passed three cases and failed at 320; prefixed access failed all four. The 320px document was 471px wide: a native HSS `overflowBackdropCard` extended to the right edge, while 3pic Fin's tabs stayed within 320px. All prefixed cases fetched `/HomeScreen/home-screen-sections.css`, `/HomeScreen/home-screen-sections.js`, and `/PluginPages/inject.js` outside the prefix (404); no 3pic Fin asset/API escaped. The host still mounted, and HSS rows appeared because the transformation and chunk ran, but that is **not** evidence that missing dependency CSS/JS is harmless or that styling/interaction parity passed. The private clone's deliberate acceptance assertion remains red (three pass, five fail).

These are dependency/deployment failures, not grounds to rewrite another plugin's output in 3pic Fin. HSS and Plugin Pages are separately owned GPL-3.0 projects by IAmParadox27. Their `TransformationPatches.IndexHtml` callbacks construct asset URLs from Jellyfin `NetworkConfiguration.BaseUrl`; the proxy's `X-Forwarded-Prefix` alone does not set that value. Our `HomeAdapterRegistration.TransformIndex` only appends its own script to the exact pre-transform index and must leave dependency tags and the HSS Home chunk untouched. A 3pic Fin post-transform rewrite would depend on callback ordering and defeat the exact-index guard, and a global CSS overflow mask could hide HSS cards. No 3pic Fin-owned code fix is justified by this run.

Resolve the prefix at the deployment boundary: test Jellyfin's supported configured base URL `/jellyfin` with a matching proxy mapping so dependency callbacks emit prefixed routes; alternatively route those three root paths explicitly through the proxy to the same Jellyfin origin as a reversible compatibility shim (limit paths and test cache/auth behavior). Do not assume the live proxy currently rejects them. Resolve the 320px card in HSS/Jellyfin's own responsive scroller/card styling, preserving horizontal card access rather than hiding overflow on the document. Rerun all eight browser cases with the actual ElegantFin asset, all dependency requests successful under the intended routing, no document overflow at 320px, and the existing identity, keyboard, row pagination, and Favorites assertions before treating either exception as closed or setting production pins.

The repeatable repository browser probe still uses empty libraries and a simulated identity invalidation, not a second user's real authentication. The private distribution clone supplements rather than replaces it. Production asset delivery (including service workers) must be measured before any production pin. The prior shutdown race regression remains covered by `HomeAdapterRegistrationTests`.
