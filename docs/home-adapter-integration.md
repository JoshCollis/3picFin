# Portable Home adapter (Jellyfin 12.1)

The embedded discovery stylesheet URL is resolved through Jellyfin's base-path-aware API client:

<!-- discovery-host-css-contract -->
```js
function discoveryStylesheetUrl(ApiClient) {
    return ApiClient.getUrl('3picFin/Web/discovery.css');
}
```

3pic Fin uses File Transformation's exact `index.html` callback to append base-relative Home and Search loaders independently. It does not rewrite Jellyfin's Home chunk or other plugins' tags. The callback is idempotent and requires a complete unambiguous HTML document; Home requires an enabled Discovery, trusted hero, or selected personal row, while Search requires `GlobalSearchEnabled`. Install File Transformation 3.0.1 or compatible; without it automatic mounting is unavailable. Restart Jellyfin after newly enabling a feature if its callback did not register on startup.

The loader accepts only the 12.1.x server version reported by `System/Info/Public`, a unique same-origin `hometab.<hex>.chunk.js` script Resource Timing entry in the web directory, a unique native direct-child `#homeTab[data-index="0"]` and `#favoritesTab[data-index="1"]`, direct-child Home `.sections`, active Home route/pane, and a current signed-in `ApiClient` identity. There is **no distribution-specific index or chunk hash**. A version match and DOM shape are a compatibility contract, not attestation of executed JavaScript or a guarantee every customized 12.1 web build is compatible. Unsupported versions, ambiguous script timing or DOM, signed-out sessions and route changes leave native UI intact. An optional version probe error prevents mounting and can retry; an explicitly unsupported version is rejected.

The host keeps native `.sections` in place and places a guarded 3pic Fin control beside Favorites in Jellyfin 12.1's visible toolbar (or mobile drawer), with a **visible** structurally checked native tabbar fallback. A hidden legacy `.skinHeader` is never used as the stock fallback. It never changes Home/Favorites tab indices. Fin is **Home-owned URL state** at `#/home?fin=1`, not a dedicated Jellyfin route or a third native tab. The adapter accepts that exact query on the Home pane, restores it on remount and Back/Forward, and rejects Favorites (`?tab=1`) or combined/unknown queries. If the navigation seam disappears, the host falls back to Home and removes the stale Fin flag. Discovery loads only when selected; the hero and optional personal rows remain on Home. Route exit, logout and identity changes clear user-bound UI. The hero remains default-off behind trusted-filesystem and explicit library selection. Household-wide requests, downloads and Calendar remain independent default-off administrator choices. Search is a separate automatically injected, global-query-only Seerr section when enabled; it preserves native results and does not claim native/Seerr duplicate suppression.

The enabled Home hero reserves its responsive 65dvh mobile / min(78dvh, 800px) desktop slot **before the slide and renderer fetches**, then removes it on empty/invalid responses. `python3 tests/Rowan.Jellyfin.Plugin.Tests/fin-url-cls.browser.py` runs a deferred slide reply in Chromium at 360px and 1280px, compares the native row's position before/after render, records layout-shift entries, and saves screenshots in `~/.hermes/cache/scratch/fin-home-{width}.png`. This measures the post-reservation transition only: the asynchronous compatibility/Mode gate can still shift the first paint, and an empty/error reply collapses the reservation. The fixture has no decoded backdrop, no real HSS rows, and no React Router or live Jellyfin; stock/custom signed-in distribution testing and real-user CLS measurement remain release gates.

Disposable smoke (Docker and Chromium required):

```sh
dotnet test tests/Rowan.Jellyfin.Plugin.Tests/Rowan.Jellyfin.Plugin.Tests.csproj -c Release
node --test tests/Rowan.Jellyfin.Plugin.Tests/*.test.cjs
python3 tests/disposable-home-adapter.py --with-dependencies --browser
```

The browser smoke uses the **public DLL unchanged**, installs File Transformation, HSS and Plugin Pages in a throwaway stock Jellyfin 12.1, verifies delivered index tags and automatic signed-in Home/Fin mount, Favorites, route teardown, identity invalidation, missing/duplicate timing negatives and restart. A separate unchanged-public-DLL clone of a customized distribution passed direct 390/768/1100px populated HSS lazy rows, hero image/Open, Home/Fin/Favorites, logout and second-user remount. Direct 320px failed on HSS card overflow; four strict `/jellyfin/` proxy cases failed on dependency root-absolute asset 404s, while 3pic Fin's paths stayed prefixed. Test deployment-specific base paths with matching Jellyfin/proxy settings; address dependency failures at the owning boundary rather than rewriting other plugins' output. ElegantFin and live household performance remain unverified.
