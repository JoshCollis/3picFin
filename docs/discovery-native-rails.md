# Discovery card parity evidence

The Discovery catalog, personal requests, all requests, recommendations, and fragment Search results share a native-shape portrait card. These are Seerr identities, **not** Jellyfin items: no native resume/menu actions or media `data-id` are attached. Poster/title Details stays keyboard accessible; availability and request options are checked in the existing authenticated dialog. Downloads and Calendar expose title-wide Arr records, not poster/card DTOs, so remain separate text rows.

## Reproducible gates

- `node --test tests/Rowan.Jellyfin.Plugin.Tests/*.test.cjs` — 192 passed.
- `dotnet test tests/Rowan.Jellyfin.Plugin.Tests/Rowan.Jellyfin.Plugin.Tests.csproj -c Release` — 344 passed.
- `ELEGANTFIN_CSS=/private/captured/elegant-source-0.css python tests/rendered-elegant-discovery.py` — mobile/desktop computed card boxes/title backings and borders, missing/mixed 2:3 art, hover Details, keyboard/dialog, pagination and no page overflow. This test also injects a late `!important` filled `.cardBox` rule to catch theme override regressions. Captured theme CSS stays private.

## Signed-in hosted acceptance

A disposable Jellyfin 12.1 server ran the Release DLL from this worktree. Two freshly authenticated disposable accounts were seeded via browser credentials; captured ElegantFin styles were loaded; synthetic Seerr DTO responses (including no-art and mixed posters) were fulfilled through browser routing. These are **hosted DOM/CSS** measurements, not proof of live Seerr/CDN connectivity. No live server/configuration was modified.

Across both users at 390 and 1280 px, all six rendered rails (personal, all, recommendations, movies, TV, fragment Search) computed `.cardBox` and `.cardText` backgrounds `rgba(0, 0, 0, 0)`, `.cardBox` border width `0px`, and no `.cardFooter`. Native Home `.cardBox` at the same widths had the same transparent background and zero border. Decoded and missing posters held the same 2:3 frame (within 2 px), with no viewport overflow; the movies rail scrolled horizontally. No resume/menu action was present. Poster Details opened, movie Request reached the existing options dialog with a permitted submit, and title-wide Downloads/Calendar rows rendered independently. No browser page errors were recorded. The disposable containers were removed by the probe's `finally` cleanup.

Private evidence under `/home/josh/.hermes/cache/scratch/discovery-native-evidence/`:
- `hosted.log` — per-user/viewport computed geometry and acceptance results (no credentials).
- `alice-390-discovery.png`, `alice-1280-discovery.png`, `bob-390-discovery.png`, `bob-1280-discovery.png` — hosted mixed/no-art rails.
- Matching `*-downloads.png` and `*-calendar.png` — title-wide sections.
- `rendered.log`, `node.log` — fixture and Node gate output.

The browser probe is private scratch (`discovery-native-hosted-probe.py`) because it relies on local disposable-server setup and captured proprietary theme assets; the repository contains the repeatable fixture and behavioral tests, not host credentials or CSS copies.
