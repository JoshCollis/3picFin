# Discover pilot review handoff (COL-4)

## Implementation

Branch: `feat/request-modal-refresh` in the assigned 3picFin checkout. Full implementation base: `af068b5d24499536241cbb28fa806fc6b8125caa`; original required revision `e069ff14e601eb22f138df352ce96b5f6929b3d0` is an ancestor. Backend checkpoint `63071e1ee24fdb63fe297138d0839c0219316f18` is preserved. The uploaded review manifest records the final head and file hashes.

Search leads Discover at full content width. Trending Movies and Trending Shows use the authenticated `3picFin/Discovery/Trending` contract independently of Home flags. Home poster classes, 2:3 artwork, rail spacing and theme variables are retained. Both request lists open the same details modal using TMDb ID and media type. Known media/request states are explicit; missing, unsupported or failed lookups stay unknown. Request-list modals show only the server-authorized `RequesterDisplayName` as text; absent names show Unknown, with no nested-user/email fallback. Full synopsis text stays accessible in a keyboard-focusable scroll region.

The existing request controller retains permission checks, standard/4K controls, TV season selection, duplicate protection, POST/read-back reconciliation, cancellation and stale-response invalidation. This continuation changes only discovery.js, related fixtures and this report. Backend changes were separately implemented and validated in the authorized sequential handoff.

## Exact focused commands and results

- `node --test tests/Rowan.Jellyfin.Plugin.Tests/discovery-ui.test.cjs tests/Rowan.Jellyfin.Plugin.Tests/discovery-host-contract.test.cjs tests/Rowan.Jellyfin.Plugin.Tests/native-home-rows.test.cjs tests/Rowan.Jellyfin.Plugin.Tests/home-adapter.test.cjs` — 102 passed, 0 failed.
- `.qa-tools/run-browser.sh node tests/Rowan.Jellyfin.Plugin.Tests/discovery-pilot.browser.cjs` — synthetic host browser assertions at 320, 390 and 1280px.
- `FIN_PUBLIC_THEME=1 .qa-tools/run-browser.sh node tests/Rowan.Jellyfin.Plugin.Tests/discovery-pilot.browser.cjs` — same assertions with both approved public CSS references, hash-checked before use.
- `node --check src/Rowan.Jellyfin.Plugin/Web/discovery.js` and `node --check tests/Rowan.Jellyfin.Plugin.Tests/discovery-pilot.browser.cjs` — syntax validation.
- `git diff --check` — whitespace validation.

The browser output files in the uploaded evidence bundle record final results. Chromium 153.0.8010.12 uses workspace-local Playwright, libraries and fonts through the CEO-provisioned wrapper. All external requests are aborted except locally fulfilled synthetic poster responses; no live service calls. Public CSS imports/fonts are blocked. Initial public CSS insertion awaited blocked imports and failed; inserting the same verified bytes as a style element resolved the fixture-only problem.

Browser coverage: full-width search and typed rails; both request-list identities; authorized/missing requester text and HTML-like text escaping; all supported media statuses plus unknown/error; request modal overflow; 60 seasons and long titles; 4K TV POST/read-back; duplicate-submit disabling; synopsis retention; focus trapping/restoration; Escape/close/cancel; aborted and stale details; search; independent failed/empty trending feeds. Home is rendered with the actual Home row component and a synthetic native-shaped host; before/after screenshots are byte-identical when Discover CSS is added. Unit tests additionally cover unauthorized shared lists, standard requests, permissions, read-back mismatches, teardown and invalidation.

CEO's backend-validation issue document reports successful C# compilation and 184 focused tests, 0 failed/skipped, using checksum-verified .NET 10.0.401. Those results are inherited evidence, not a frontend rerun. They include disabled-Home/enabled-Discover and requester privacy coverage. No broad backend suite was run here.

## Visual evidence and reference provenance

The evidence bundle contains before/after rails and movie, TV and missing-data modals at 390px and 1280px, with base files obtained through `git show af068b5:...`. It also includes requester dialogs, many-season controls, and Home regression captures at 320/390/1280px. Both synthetic-host and public-theme variants are provided. Synthetic artwork and names only.

Approved public source inputs (unchanged downloaded bytes; no private capture):

- `lscambo13/ElegantFin`, revision `9d43fa9b898c74055237133b7a7b8b8c5543f0ce`, `Theme/ElegantFin-jellyfin-theme-build-latest-minified.css`; SHA-256 `779aa801b912d21089d488ddf5a426fc8c02970ec953566ceee416e0d8bce643`.
- `mihaif7/elegantfin-jf12`, revision `afdd0e8109266979fb76136374db5192ed2e574a`, `Theme/ElegantFin-jf12-modern-latest.css`; SHA-256 `525ac149903b4d2b8d55bdab36c1efaf8e27fc635ffa16afe0a0534705026841`.

Sources: https://github.com/lscambo13/ElegantFin/blob/9d43fa9b898c74055237133b7a7b8b8c5543f0ce/Theme/ElegantFin-jellyfin-theme-build-latest-minified.css and https://github.com/mihaif7/elegantfin-jf12/blob/afdd0e8109266979fb76136374db5192ed2e574a/Theme/ElegantFin-jf12-modern-latest.css .

## Boundaries and remaining review

- Apple originals are unsupported by existing contracts. Provider availability is not originals provenance; no Apple rail/integration was invented. CEO already accepted this bounded limitation in the handoff.
- Public-reference synthetic rendering does not establish parity with Josh's customized hosted installation or an authenticated Jellyfin client. Private/production layout and real Seerr operations remain unverified and out of this run's access scope. The host shell/native baseline is synthetic; Home pixel checks prove CSS isolation in that fixture, not whole-client visual equivalence.
- Independent findings belong to the configured native Modal Reviewer stage and existing COL-5 report path. Submit through the ordinary completion route; implementation is not independently approved until that gate finishes.
- No push, merge, publication or deployment. CEO coordinates Josh's final handoff.
