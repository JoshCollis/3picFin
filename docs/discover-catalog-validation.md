# Discover catalog follow-up validation

> Historical validation of the original catalog implementation. The subsequent rail-order change removes the overlapping “More to discover” composite. Current Discover order is Search results, My Requests, All Requests (when enabled), Trending, Popular Movies, Popular TV, Upcoming Movies, Upcoming TV. The older composite-specific tests and observations below describe that prior implementation.

Implementation branch: `feat/discover-catalog-rails`. Isolated worktree: `.worktrees/col-6` within the approved 3picFin checkout. Base: `ba17ebd96b3f1e225b83760affbad962320f19fa` (local main at assignment). The uploaded manifest records the final commit and changed files.

Search and its results/pagination now precede every rail. Scoped CSS overrides representative host form/input width caps. Empty submitted queries clear results and invalidate pending searches; errors disable pagination. The five catalog rails are Trending, Popular Movies, Popular TV, Upcoming Movies and Upcoming TV. All retain the existing Home-shaped cards and details/request flows. More to discover deduplicates the current five loaded pages by `(MediaType, TmdbId)`; the populated fixture renders 22 unique titles. It does not accumulate pages indefinitely or fetch titles just to fill a quota.

## Retrieval and compatibility

- Popular Movies/TV retain the existing discovery bundle, mapped-user cache and independent movie/TV/request page parameters. Paging either popular rail refreshes that bundle at the other rails' existing page numbers. Sorting is explicitly `sortBy=popularity.desc`.
- Upcoming feeds use new authenticated read routes `3picFin/Discovery/UpcomingMovies` and `UpcomingTV`, through the existing mapped-user client/cache and bounded timeout. Only the two exact upstream paths are added to the read allowlist. Endpoint identity supplies missing `mediaType`; mismatches are rejected by the existing projection. Status fields and sanitized errors retain the existing response contract.
- Trending preserves the order and media identities of eligible results from exactly the requested upstream page (pages 1–100, at most 20 cards). No page refill or separate movie/TV quota. Existing adult/blocklist/TV-rating eligibility remains unchanged; TV detail verification still shares the ten-second deadline and a maximum of 20 checks. Home retains its previous three-page bound. Sparse Trending can still result from those existing eligibility rules; this is not proof of the cause on Josh's installation.
- Each new feed has independent page/generation state. Failure does not replace another feed. More to discover uses successful feeds and discloses partial failure; sparse successful feeds show honest empty states.

Public API references: [movies](https://docs.seerr.dev/api/discover-movies/), [upcoming movies](https://docs.seerr.dev/api/upcoming-movies/), [upcoming TV](https://docs.seerr.dev/api/discover-upcoming-tv-shows/), [mixed Trending](https://docs.seerr.dev/api/trending-movies-and-tv/).

Public source checked at Seerr tag `v3.4.1`: `server/routes/discover.ts` forwards `query.sortBy`; `server/api/themoviedb/index.ts` defines and defaults `popularity.desc` for both movie and TV discovery. SHA-256 respectively: `c8aee81f7fbd09ede5c655fafdf6b142acfda1aef793a78455098db6943f5e54`, `448785e69782e082b544fe30af681404f377a0200d8a2454e76e3754fcf96071`. Sources: `https://raw.githubusercontent.com/seerr-team/seerr/v3.4.1/server/routes/discover.ts` and `https://raw.githubusercontent.com/seerr-team/seerr/v3.4.1/server/api/themoviedb/index.ts`.

## Reproduction and obtained results

Run from the isolated worktree. `.qa-tools` points to the pre-existing workspace-local tools; no SDK/browser was installed. Set an evidence directory outside tracked sources. Actual logs are in the uploaded evidence bundle.

```sh
export FIN_EVIDENCE_DIR="$PAPERCLIP_RUN_SCRATCH_DIR/evidence"
export FIN_PUBLIC_THEME=1
export PLAYWRIGHT_BROWSERS_PATH="$PWD/.qa-tools/browsers"
export LD_LIBRARY_PATH="$PWD/.qa-tools/runtime/root/usr/lib/x86_64-linux-gnu"
export FONTCONFIG_FILE="$PWD/.qa-tools/runtime/fonts.conf"
node --test tests/Rowan.Jellyfin.Plugin.Tests/discovery-ui.test.cjs tests/Rowan.Jellyfin.Plugin.Tests/discovery-host-contract.test.cjs
node tests/Rowan.Jellyfin.Plugin.Tests/discovery-catalog.browser.cjs
node tests/Rowan.Jellyfin.Plugin.Tests/discovery-pilot.browser.cjs
export DOTNET_ROOT="$PWD/.qa-tools/dotnet"
export DOTNET_CLI_HOME="$PWD/.qa-tools/dotnet-home"
export NUGET_PACKAGES="$PWD/.qa-tools/nuget"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
.qa-tools/dotnet/dotnet restore tests/Rowan.Jellyfin.Plugin.Tests/Rowan.Jellyfin.Plugin.Tests.csproj --source "$PWD/.qa-tools/nuget"
.qa-tools/dotnet/dotnet test tests/Rowan.Jellyfin.Plugin.Tests/Rowan.Jellyfin.Plugin.Tests.csproj --filter 'FullyQualifiedName~DiscoveryControllerTests|FullyQualifiedName~HomeDiscoverTests|FullyQualifiedName~SeerrClientTests|FullyQualifiedName~SeerrReadCacheTests|FullyQualifiedName~SeerrRequestCreationTests' --no-restore
node --check src/Rowan.Jellyfin.Plugin/Web/discovery.js
git diff --check
```

- Node: 59 tests passed, zero failures.
- Focused .NET: 128 tests passed, zero failed/skipped. Local-cache restore succeeded. The initial `--no-restore` attempt correctly failed because the new worktree had no assets file; no SDK install or remote restore was used.
- Catalog browser: passed at 320/390/1280px. Content/search-row widths were 298.90625/364.28125/1195.53125px, and input widths 205.71875/271.09375/1102.34375px. Search plus button fills the content area within 2px. DOM/visible result order and zero document overflow asserted under late host form/input caps. Mixed identity/order, TV-only page, bounded calls, all five pagers, 22 deduplicated suggestions, keyboard search, pagination, stale/cleared searches, empty/error and isolated feed failures passed.
- Existing pilot browser: passed at 320/390/1280px for movie/long-title Home parity, request lists, known/unknown statuses, permission-sensitive actions, 4K TV POST/read-back, duplicate-submit disabling, 60 seasons, full synopsis, focus trap/restoration, dismissal and stale detail responses. Home screenshots remain byte-identical after Discover CSS is added.
- Before/after rails and movie/TV/missing-data modals captured at 390 and 1280px against this task's exact base. Additional catalog screenshots, many-season controls and title parity evidence included. Initial browser failure was an obsolete lowercase split-Trending empty-state expectation; corrected to match the combined heading and rerun successfully.

## Public theme and integration limits

The unchanged public CSS inputs are hash-checked by the browser harness:

| Source/revision | SHA-256 |
| --- | --- |
| [lscambo13/ElegantFin, 9d43fa9b898c74055237133b7a7b8b8c5543f0ce](https://github.com/lscambo13/ElegantFin/blob/9d43fa9b898c74055237133b7a7b8b8c5543f0ce/Theme/ElegantFin-jellyfin-theme-build-latest-minified.css) | `779aa801b912d21089d488ddf5a426fc8c02970ec953566ceee416e0d8bce643` |
| [mihaif7/elegantfin-jf12, afdd0e8109266979fb76136374db5192ed2e574a](https://github.com/mihaif7/elegantfin-jf12/blob/afdd0e8109266979fb76136374db5192ed2e574a/Theme/ElegantFin-jf12-modern-latest.css) | `525ac149903b4d2b8d55bdab36c1efaf8e27fc635ffa16afe0a0534705026841` |

Synthetic titles/artwork and API responses only; browser network requests are blocked or fulfilled locally. No private captures, household data, live requests/downloads or production infrastructure. Public-reference rendering does not prove Josh's customized hosted layout. Live Seerr/Jellyseerr version compatibility, hosted authentication/host CSS and real provider operations remain unverified. No push, merge, publication, deployment or release is included. Independent review and Josh's release decision remain separate.
