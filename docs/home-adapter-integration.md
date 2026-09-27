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

No production distribution is pinned. Capture signed-in Home response bodies, timing and DOM on the intended distribution; verify the pre-transform callback input, then pin a disposable build and run the full populated lifecycle gate before proposing any production pin.

The browser identity-invalidating case substitutes `getCurrentUserId` to simulate logout; it does not perform a second user's real authentication. This run uses empty libraries and no configured Seerr upstream. Populated HSS rows, fake Seerr results, live user-switch authentication, base-path browser proxy and ElegantFin are still unverified by this repeatable harness. Production asset delivery (including service workers) must be measured before any production pin. The prior shutdown race regression remains covered by `HomeAdapterRegistrationTests`.
