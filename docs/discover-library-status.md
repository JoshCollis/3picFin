# Discover headings and library membership

Implementation for [COL-8](/COL/issues/COL-8), from approved plan revision 4. Branch `feat/discover-library-status`; isolated worktree `.worktrees/col-8`; base `641bff4dc4b1846f420483a194d20e9ea083b469`. The uploaded delivery report records the exact final commit and changed-file list.

The redundant heading is removed while the region retains its accessible name. The search heading follows rendered cards, including pagination, clear-input, loading, empty/error and stale-response transitions. Other rails remain in place.

`TitleDetails.LibraryStatus` distinguishes `present`, `absent`, `unknown` and `unavailable`. Only a verified accessible ID enables **View in library**. Seerr request status remains separate from membership. Duplicate copies are sorted by ID, with an accessible Seerr hint preferred; a stale hint cannot veto a valid copy. Every candidate must pass fresh user/root/item/type/provider/ancestor checks. Navigation re-fetches details and checks identity, route, mount generation, matching ID and cancellation before calling `Emby.Page.showItem`.

The resolver makes one targeted query per detail lookup: signed-in user, exact TMDb provider identity, Movie/Series type, visible physical library ancestors, non-virtual items, at most 33 results, no total count. More than 32 matches produces uncertainty; it never claims absence or navigates from a truncated set. An empty completed query establishes absence in the accessible library only. This removes whole-library item/root count cutoffs. Existing bounded request-card metadata hydration is unchanged; it now uses targeted queries rather than whole-library scans.

## Dependency verification

Jellyfin.Controller and Jellyfin.Database.Implementations 12.1.0 declare source commit `ee91c75e777da41a9c4f4855e70adc604fbf2ef8` in their NuGet metadata. Public source inspection verified:

- `InternalItemsQuery.HasAnyProviderId` accepts exact provider/value pairs. The database helper emits a provider `EXISTS` predicate.
- `BaseItemRepository.GetItemList` translates filters before `ApplyQueryPaging` applies `Take(Limit)`.
- `LibraryManager` translates CollectionFolder membership to `PhysicalFolderIds`. The resolver uses those physical IDs for ancestry, and rechecks the current visible collections before returning an ID.
- Provider DTO fields are explicitly requested. Alternate versions are included and presentation-key grouping disabled so hidden primary copies cannot mask accessible alternatives.

Source: [Jellyfin at the dependency revision](https://github.com/jellyfin/jellyfin/tree/ee91c75e777da41a9c4f4855e70adc604fbf2ef8). The evidence package includes exact source-file and NuGet SHA-256 hashes. `TitleLibraryQueryTests` executes the dependency's actual provider predicate against an in-memory SQLite schema, verifies SQL EXISTS/LIMIT, and combines that with production query-shape assertions. `TitleLibraryAccessTests` invokes the actual resolver adapter with synthetic Jellyfin subclasses/managers.

## Focused verification

- 42 backend tests passed: TitleDetails, TitleLibraryQuery, TitleLibraryAccess and DiscoveryController. Synthetic Skywalker has 3,600 unrelated same-name records across 12 roots; movie and series, missing/stale hints, duplicates, wrong provider/type, virtual/hidden items, inaccessible parents, revoked identity/root/item access, overflow and lookup failures are covered.
- 87 Node tests passed: discovery UI, host navigation and host contract. A prior browser assertion for obsolete `requested/tracked` wording was corrected to `Requested · Processing`.
- `discover-library.browser.py`: 320/390/1280px; initial/loading/nonempty/empty/error/clear/pagination/stale search, movie/TV from discovery/search/request cards, focus/dismissal, missing data, full synopsis and long titles/40 seasons. Before/after screenshots at 390/1280px.
- `library-host.browser.py`: actual shipped host plus fragment at 390/1280px; keyboard movie/series navigation; fresh lookup; revoked ID, stale user/route and dismissed in-flight navigation never call the native navigation boundary.
- Existing `discovery-details.browser.py`: 320/390/1280px passed.
- Existing `modal-refresh.browser.py`: public-theme geometry/contrast, artwork/missing data, full synopsis, focus, request permissions, seasons/4K, POST/read-back, cancellation, duplicate submission and stale options passed.
- `git diff --check` passed.

From the approved checkout root, the exact focused commands (output paths may be changed) are:

```sh
.qa-tools/run-dotnet.sh test .worktrees/col-8/tests/Rowan.Jellyfin.Plugin.Tests/Rowan.Jellyfin.Plugin.Tests.csproj --filter 'FullyQualifiedName~TitleDetailsTests|FullyQualifiedName~TitleLibrary|FullyQualifiedName~DiscoveryControllerTests' --no-restore --verbosity minimal
node --test .worktrees/col-8/tests/Rowan.Jellyfin.Plugin.Tests/discovery-ui.test.cjs .worktrees/col-8/tests/Rowan.Jellyfin.Plugin.Tests/home-tab-host.test.cjs .worktrees/col-8/tests/Rowan.Jellyfin.Plugin.Tests/discovery-host-contract.test.cjs
export PYTHONPATH="$PWD/.worktrees/col-8/.qa-tools/python"
export ELEGANTFIN_CSS_DIR="$PWD/.qa-tools/elegantfin"
export DISCOVER_SCREENSHOT_DIR="$PWD/.worktrees/col-8/.qa-tools/evidence/screenshots"
export MODAL_SCREENSHOT_DIR="$PWD/.worktrees/col-8/.qa-tools/evidence/request-screenshots"
DISCOVER_BASELINE=1 .qa-tools/run-browser.sh python3 .worktrees/col-8/tests/Rowan.Jellyfin.Plugin.Tests/discover-library.browser.py
.qa-tools/run-browser.sh python3 .worktrees/col-8/tests/Rowan.Jellyfin.Plugin.Tests/discover-library.browser.py
.qa-tools/run-browser.sh python3 .worktrees/col-8/tests/Rowan.Jellyfin.Plugin.Tests/library-host.browser.py
.qa-tools/run-browser.sh python3 .worktrees/col-8/tests/Rowan.Jellyfin.Plugin.Tests/discovery-details.browser.py
.qa-tools/run-browser.sh python3 .worktrees/col-8/tests/Rowan.Jellyfin.Plugin.Tests/modal-refresh.browser.py
git -C .worktrees/col-8 diff --check
```

Existing local .NET SDK/Chromium were reused. Python Playwright 1.63.0 was installed only into this worktree's ignored `.qa-tools/python` directory; SQLite 10.0.11 is a test-only package. No SDK/system package installation or broad backend suite was used.

## Evidence limits

All data is synthetic. Browser API and `Emby.Page.showItem` boundaries are mocked; the shipped host and modal code run in Chromium. SQLite verifies the actual provider predicate in a minimal schema, not an entire running Jellyfin server/database. These checks do not prove Josh's library layout, exact Skywalker root cause, customized hosted theme, live Seerr permissions or end-to-end server routing.

Public ElegantFin references are the existing pinned files documented in [modal-refresh.md](modal-refresh.md#public-reference-provenance): upstream `9d43fa9b898c74055237133b7a7b8b8c5543f0ce` (SHA-256 `779aa801b912d21089d488ddf5a426fc8c02970ec953566ceee416e0d8bce643`) and jf12 adapter `afdd0e8109266979fb76136374db5192ed2e574a` (SHA-256 `525ac149903b4d2b8d55bdab36c1efaf8e27fc635ffa16afe0a0534705026841`). Browser traffic is intercepted; no private capture or household data was used. Existing synopsis server length limits remain unchanged.

Next owner: Modal Reviewer independently verifies the delivered commit under [COL-9](/COL/issues/COL-9); CEO coordinates Josh's handoff. No push, merge, publication or deployment is included.
