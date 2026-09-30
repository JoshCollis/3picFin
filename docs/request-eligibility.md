# Request eligibility and opt-in 4K

Implementation for [COL-10](/COL/issues/COL-10), copied approved plan revision 5. Branch `feat/request-eligibility-4k-opt-in`, worktree `.worktrees/col-10`, base `f1dd9e738278cc043d150ddfec2f64f67cfcfca9` (current main at start). The delivery receipt records the exact final commit and patch hash.

## Result

Details, request options and fresh submission use one Seerr eligibility policy. Movie variants with pending, processing, partial or available status cannot be requested again. Requests by any user reserve their variant; no requester identity is returned. Declined/completed requests do not reserve missing media; failed requests do, matching Seerr's existing-request retry semantics. Seerr's named `UNKNOWN=1` means untracked/missing when the complete detail record supports it; malformed, unsupported or incomplete eligibility data fails closed. An absent mediaInfo record is Seerr's untracked-title representation.

TV eligibility uses individual media seasons and request seasons, independently for standard/4K. Series-wide availability does not hide newly added seasons. Only missing, unreserved seasons are shown. A mixed eligible/ineligible submission is rejected as a whole with no upstream POST. Nonexistent season numbers still return 400; stale reserved choices return 409. Changing variants removes ineligible checked choices.

`Enable4kRequests` is an administrator checkbox, false for new and legacy configuration. It persists through the existing save/read-back flow and Jellyfin XML serialization. The server rechecks the live setting after awaited reads and rejects crafted 4K submissions. Opting in preserves mapped Seerr permissions and does not affect historical requests.

Visible local movies suppress standard requests even when Seerr is stale. Visible local 4K variants also suppress 4K requests, using Seerr's Jellyfin scanner rule (video width >2000). Unknown local video metadata fails closed. The existing provider-ID, root, ancestor and visibility checks still apply; local membership is revalidated immediately before POST. A series ID alone never proves season availability.

A bounded set of process-wide locks serializes same-title submissions across users. Existing cache invalidation, mapped permissions, upstream quota enforcement, cancellation, single-submit locking, personal read-back and stale-dialog protection remain. Old-user post responses cannot populate a switched user's UI. The modal retains ElegantFin styling; the details synopsis uses the existing bounded upstream response rather than the former 500-character card limit.

## Focused evidence

Final focused results: 178 backend tests and 99 frontend/configuration tests passed; both Chromium matrices passed. The delivery archive contains exact logs and 56 before/after PNGs at 390/1280px. Both browser suites also check 320px overflow. Cases include movie, TV, missing/broken artwork, long title/40 seasons, pending and processing synthetic Avengers: Doomsday, default-off controls, additional TV seasons and an opted-in missing 4K upgrade. Screenshots are synthetic representations of each revision's DTOs; backend tests independently exercise the real controller/client policy. Mobile pending and desktop TV form screenshots were visually inspected.

From the approved checkout root:

```sh
.qa-tools/run-dotnet.sh test .worktrees/col-10/tests/Rowan.Jellyfin.Plugin.Tests/Rowan.Jellyfin.Plugin.Tests.csproj --no-restore --filter 'FullyQualifiedName~RequestEligibilityTests|FullyQualifiedName~SeerrRequestCreationTests|FullyQualifiedName~TitleDetailsTests|FullyQualifiedName~SeerrOptionsTests|FullyQualifiedName~PluginConfigurationTests|FullyQualifiedName~DiscoveryControllerTests|FullyQualifiedName~SeerrReadCacheTests|FullyQualifiedName~TitleLibrary' --verbosity minimal
node --test .worktrees/col-10/tests/Rowan.Jellyfin.Plugin.Tests/discovery-ui.test.cjs .worktrees/col-10/tests/Rowan.Jellyfin.Plugin.Tests/configPage.smoke.test.cjs
export PYTHONPATH="$PWD/.worktrees/col-8/.qa-tools/python"
export ELEGANTFIN_CSS_DIR="$PWD/.qa-tools/elegantfin"
export MODAL_SCREENSHOT_DIR="$PWD/.worktrees/col-10/.qa-evidence/screenshots"
export ELIGIBILITY_SCREENSHOT_DIR="$PWD/.worktrees/col-10/.qa-evidence/eligibility-screenshots"
MODAL_BASELINE=1 MODAL_BASE_REF=f1dd9e7 .qa-tools/run-browser.sh python3 .worktrees/col-10/tests/Rowan.Jellyfin.Plugin.Tests/modal-refresh.browser.py
.qa-tools/run-browser.sh python3 .worktrees/col-10/tests/Rowan.Jellyfin.Plugin.Tests/modal-refresh.browser.py
ELIGIBILITY_BASELINE=1 .qa-tools/run-browser.sh python3 .worktrees/col-10/tests/Rowan.Jellyfin.Plugin.Tests/request-eligibility.browser.py
.qa-tools/run-browser.sh python3 .worktrees/col-10/tests/Rowan.Jellyfin.Plugin.Tests/request-eligibility.browser.py
git -C .worktrees/col-10 diff --check
```

Existing local .NET SDK, cached dependencies, Python Playwright and Chromium were reused. Initial restore used the existing local package cache. No SDK install, broad backend suite or production access. Initial fixture/compilation failures and the browser's aborted CSS import were corrected before the final passing checks.

## Public provenance

Seerr v3.4.1 resolves to commit `69f73a6f1486fdb51b8ddae9a94a8dfb629f461c`. Relevant official sources, with SHA-256:

- `server/entity/MediaRequest.ts`: `f9c58d4778de7fd353b675504ad348480e4e9b739685260b75ac0f4c1f07f2fd` (request retry/season reservation).
- `server/entity/Media.ts`: `79a9f78776ae2398a57221bec9ad8b23b0535cfa347661b835faffd48fbb3ceb` (detail loads cross-user requests and media seasons).
- `server/routes/movie.ts`: `3aaf84c13ff45f2f1f61a8ff69fc0ea896412e6cfbd4dc0e3102bad72fdc8312`.
- `server/routes/tv.ts`: `88875db416ad27b10fcced2cea81f0b92fc0cc785e865d8afb901242cb385916`.
- `server/lib/scanners/jellyfin/index.ts`: `5973cce49cbb23b770f21fb195ca8225abcabfaf8d2905155a5f0a4a062d751e` (local variant classification).

Sources: [Seerr request policy](https://github.com/seerr-team/seerr/blob/69f73a6f1486fdb51b8ddae9a94a8dfb629f461c/server/entity/MediaRequest.ts), [Jellyfin scanner](https://github.com/seerr-team/seerr/blob/69f73a6f1486fdb51b8ddae9a94a8dfb629f461c/server/lib/scanners/jellyfin/index.ts).

Public ElegantFin references retain the pins in [modal-refresh.md](modal-refresh.md#public-reference-provenance): main revision `9d43fa9b898c74055237133b7a7b8b8c5543f0ce`, SHA-256 `779aa801b912d21089d488ddf5a426fc8c02970ec953566ceee416e0d8bce643`; jf12 adapter revision `afdd0e8109266979fb76136374db5192ed2e574a`, SHA-256 `525ac149903b4d2b8d55bdab36c1efaf8e27fc635ffa16afe0a0534705026841`. Browser scripts verify hashes and intercept external traffic.

## Limits and next owner

No household payload or Josh's hosted installation was inspected. Synthetic API/browser boundaries and library adapters do not prove his customized theme, running Jellyfin dependency injection, live Seerr version/quotas, or exact Doomsday payload. Public-theme rendering is not hosted-installation proof. Unknown or incomplete upstream records intentionally disable requests.

The plugin cannot make Seerr GET plus POST atomic against other Seerr clients/processes or a concurrent library scan. Local locks prevent overlapping plugin writers; fresh reads and Seerr's final duplicate guard cover known state. An externally changing selection may still receive upstream filtering/409/202 after validation; read-back remains authoritative and the UI never automatically retries.

Modal Reviewer independently verifies this commit under [COL-11](/COL/issues/COL-11). CEO owns final Josh handoff, release checks, PR/merge/package and public-catalog validation after review. None of those release actions is part of this implementation delivery.
