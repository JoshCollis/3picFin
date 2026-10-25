# Jellyfin 12.3 compatibility

## Supported releases and upstream pins

The portable Home, global 3pic Fin navigation and Search adapters accept stable **12.1.x and 12.3.x** servers. Unsupported minors (including unverified 12.2), future majors, previews and malformed versions still fail closed. Existing structural, route, identity and authorization checks remain in place; this is not a blanket Jellyfin 12 version bypass.

The compatibility review used:

- [Jellyfin server v12.3](https://github.com/jellyfin/jellyfin/tree/06de749d435e2d0be0c5119a41f2300fdd7143e5), commit `06de749d435e2d0be0c5119a41f2300fdd7143e5`.
- [Jellyfin web v12.3](https://github.com/jellyfin/jellyfin-web/tree/c06cb17b274c10f9015412305edb0afdc010ae7a), commit `c06cb17b274c10f9015412305edb0afdc010ae7a`.
- Official runtime `jellyfin/jellyfin:12.3@sha256:2038fe038ab3876a80f6e7b7e0aef6607b032c7dc0c9b3ad3296d0b2eba3bbb5`, reporting `12.3.0`.
- Official 12.1 runtime digest `sha256:78d3ea1207d1322471fcac39a614f004f2ccf7e878f95ab2977d752f07e4dd7e`, reporting `12.1.0`.

The 12.3 Controller/Model NuGet metadata points to the server commit above and targets .NET 10. The plugin deliberately **ships against Controller/Model 12.1.0** and retains package `targetAbi: 12.1.0.0`: this is the minimum compatible ABI, not an exact server-version selector. The unchanged minimum-ABI DLL was exercised on both runtimes. No server API or target-framework upgrade was necessary. CI additionally restores and runs the .NET suite against 12.3.0 using `-p:JellyfinApiVersion=12.3.0`; do not publish that override build with minimum-ABI metadata.

The pinned web keeps the direct `#homeTab[data-index="0"]` and `#favoritesTab[data-index="1"]` panes and native `.sections` in `src/apps/legacy/controllers/home.html`, Search's `#searchPage` and `#searchTextInput`, and `window.Emby.Page.showItem`. The runtime proves the toolbar/mobile-drawer seams and actual Home chunk timing as well. React-owned DOM remains an unsupported integration seam, not a stable upstream extension API. File Transformation 3.0.1's 12.1 build activated and automatically injected this DLL on 12.3 through the existing reflection boundary; no second injector or web-root modifications were introduced.

## Reproduce

Requires .NET 10, Node, Python with Playwright/Chromium, Docker and ffmpeg. Set `TMPDIR` to a writable private scratch directory. Build normally, without an API-version override, before disposable tests:

```sh
dotnet restore RowanJellyfin.slnx
dotnet test RowanJellyfin.slnx -c Release --no-restore
dotnet build RowanJellyfin.slnx -c Release --no-restore
node --test tests/Rowan.Jellyfin.Plugin.Tests/*.test.cjs
python3 -m unittest discover -s tests -p 'test_*.py'
python3 tests/Rowan.Jellyfin.Plugin.Tests/native-search.browser.py
python3 tests/disposable-version-compat.py --output "$TMPDIR/fin-12.3"
python3 tests/disposable-version-compat.py --image jellyfin/jellyfin:12.1 --output "$TMPDIR/fin-12.1"
```

The disposable test stages the built DLL unchanged, binds only a random loopback port, provisions synthetic media and random throwaway accounts, installs only File Transformation, and removes its container and private configuration on exit. It writes screenshots and a server log to the requested evidence directory; it does not contact or configure a live Jellyfin instance.

## Verified scope

- .NET: **413 passed** against 12.1 API packages and **413 passed** against 12.3 API packages.
- Node: **224 passed**, including added supported-version and fail-closed negatives for Home/global navigation.
- Python package/release tests: **6 passed**.
- Stock signed-in Chromium at **390px and 1280px**, on both 12.1 and 12.3: automatic Home mount, actual authenticated user identity, native sections preserved, decoded metadata-backed hero JPEG, hero/native-row separation, native Open, Home → Fin → Favorites → Search → Fin → Home, persistent toolbar/mobile navigation, native movie Search results alongside a browser-local Seerr candidate fixture, and logout teardown. No page errors in these cases.
- Admin page: real checkbox toggles and save/readback in both directions at both widths on both runtimes, retaining the hero's real library ID.
- Hero-only mobile: Discovery disabled with configuration readback, decoded hero, no empty Fin panel/global entry, native Home preserved.
- Anonymous requests return **401**, userless API keys **403** for Mode, Hero, hero image, Recently Added and Search. A second real user with no allowed libraries receives no hero slides and **404** for another user's image; its admin configuration request returns **403**.

## Remaining acceptance limits

This is stock-distribution compatibility proof, not live deployment or every-feature parity. No live server, user credentials, plugin configuration, release or catalog was modified during verification. Search candidate DTOs are fulfilled locally in the browser; live Seerr/Arr connectivity, request writes, CDN behavior and representative customized themes are not proved by that fixture. New 12.3 HSS/Plugin Pages coexistence, base-path proxy, Live TV population and household performance acceptance remain separate gates.

The broader existing Chromium scripts were also attempted. Four fail in both this revision and the exact pre-change `895b048` tree: `deferred-discovery` (request-count assertion), `discovery-carousels` (obsolete Movies heading), `discovery-details` (request-dialog fixture/readback), and `fin-url-cls` (old 65dvh expectation versus the current 86dvh reservation). These failures are not represented as passing or fixed by the version update. Additional theme/parity scripts require private captured ElegantFin styles unavailable to this checkout. The focused signed-in stock checks above do not substitute for those customized-distribution gates.

## Release notes

Suggested next version after 0.1.8.9: **0.1.8.10**.

> Add stable Jellyfin 12.3 support to automatic Home integration, persistent 3pic Fin navigation and Search augmentation. Retain Jellyfin 12.1 support and minimum ABI 12.1.0.0. Preserve existing native-DOM, authentication and fail-closed compatibility checks. Add dual-version API CI coverage and an unchanged-DLL disposable signed-in browser/authentication regression probe.

Publishing the catalog can cause auto-update on registered servers. A local package is not authorization to publish or restart a server; review deployment exposure separately.
