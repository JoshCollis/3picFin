# Request modal refresh

The details dialog now groups artwork with title, media type, year and season count. The entire overview returned by TitleDetails remains readable in the dialog's single scroll area (the server still caps it at 500 characters). Missing or failed art collapses its column. Confirmation keeps the already-loaded title/artwork/metadata while fetching fresh RequestOptions. Season checkboxes and standard/4K selection remain authoritative there; the count in details is informational.

Styles are confined to the two existing dialogs and use ElegantFin `textColor`, `dimTextColor`, `drawerColor`, `borderColor`, `smallRadius`, `ef12-surfaceRadius`, `btnSubmitColor` and `accentColor`, with existing Jellyfin/fallback values. Font is inherited. Focus wraps through visible enabled controls; Escape/Close restores the invoking control, except during an in-flight submission when dismissal remains locked. POST/read-back, permissions, disposal and duplicate safeguards are preserved.

## Public reference provenance

Downloaded on 2026-09-29 from the two upstream projects named by `native-home-card-parity.browser.py`. No private captures, household data or production services were used. These public bytes happen to match the hashes already documented by that older fixture.

| Local filename | Public source revision and path | SHA-256 |
| --- | --- | --- |
| `elegant-source-0.css` | [lscambo13/ElegantFin, 9d43fa9b898c74055237133b7a7b8b8c5543f0ce](https://github.com/lscambo13/ElegantFin/blob/9d43fa9b898c74055237133b7a7b8b8c5543f0ce/Theme/ElegantFin-jellyfin-theme-build-latest-minified.css) | `779aa801b912d21089d488ddf5a426fc8c02970ec953566ceee416e0d8bce643` |
| `elegant-source-1.css` | [mihaif7/elegantfin-jf12, afdd0e8109266979fb76136374db5192ed2e574a](https://github.com/mihaif7/elegantfin-jf12/blob/afdd0e8109266979fb76136374db5192ed2e574a/Theme/ElegantFin-jf12-modern-latest.css) | `525ac149903b4d2b8d55bdab36c1efaf8e27fc635ffa16afe0a0534705026841` |

The modal fixture verifies these hashes and injects both stylesheets unchanged. All browser traffic is intercepted: original geometric SVG artwork, synthetic API data, empty external font-import responses. The fixture uses a local Arial-compatible fallback, not the remote Inter font. Public reference rendering does **not** prove Josh's customized hosted installation, signed-in integration, stock host typography or theme overrides.

## Reproduce focused verification

Run from the repository root. Workspace tooling used Python 3.13, Playwright 1.63.0, Chromium 153.0.8010.12 (1243) and Node 24.21.0. `.modal-tools/` and `.modal-evidence/` are local verification outputs, excluded from the delivery patch.

```sh
export PLAYWRIGHT_BROWSERS_PATH="$PWD/.modal-tools/browsers"
export ELEGANTFIN_CSS_DIR="$PWD/.modal-tools/theme"
export LD_LIBRARY_PATH="$PWD/.modal-tools/sysroot/usr/lib/x86_64-linux-gnu"
export FONTCONFIG_FILE="$PWD/.modal-tools/fonts.conf"
export MODAL_SCREENSHOT_DIR="$PWD/.modal-evidence"

node --test tests/Rowan.Jellyfin.Plugin.Tests/discovery-ui.test.cjs tests/Rowan.Jellyfin.Plugin.Tests/discovery-host-contract.test.cjs tests/Rowan.Jellyfin.Plugin.Tests/home-adapter.test.cjs tests/Rowan.Jellyfin.Plugin.Tests/global-search-addon.test.cjs
MODAL_BASELINE=1 .modal-tools/venv/bin/python3 tests/Rowan.Jellyfin.Plugin.Tests/modal-refresh.browser.py
.modal-tools/venv/bin/python3 tests/Rowan.Jellyfin.Plugin.Tests/modal-refresh.browser.py
.modal-tools/venv/bin/python3 tests/Rowan.Jellyfin.Plugin.Tests/discovery-details.browser.py
.modal-tools/venv/bin/python3 tests/Rowan.Jellyfin.Plugin.Tests/discovery-requests.browser.py
.modal-tools/venv/bin/python3 tests/Rowan.Jellyfin.Plugin.Tests/search-rail.browser.py
git diff --check
```

On a normally provisioned browser host, the LD_LIBRARY_PATH/FONTCONFIG_FILE overrides are unnecessary. In this minimal execution environment, `python3 -m venv` lacked ensurepip; pip was bootstrapped in that venv, then `python3 -m pip install playwright` and `python3 -m playwright install chromium` installed only local tooling. Missing Chromium shared libraries were downloaded with `apt-get download` and extracted with `dpkg-deb -x` into `.modal-tools/sysroot`; no system packages or .NET SDK were installed. The run evidence records commands/results and setup limitations.

## Coverage and evidence

- Four Node suites: 91 passing tests for discovery, host contracts, Home and global Search lifecycle, permissions, normal/4K options, uncertain outcomes, personal read-back and stale-user/route handling.
- `modal-refresh.browser.py`: movie, TV, missing data, failed art, long/unbroken titles and 40 seasons at 320/390/1280px. Checks dialog/document overflow, full overview text, minimum 4.5:1 text contrast against a conservative composited background, focus wrapping/restoration, Escape/Close, fresh options, explicit season choice, standard movie and 4K TV POST payloads, in-flight dismissal lock, duplicate prevention, read-back and stale options after cancellation. Screenshots at 390/1280px before/after, plus confirmation views. Baseline loads assets directly from `e069ff14e601eb22f138df352ce96b5f6929b3d0`; its long-title overflow is recorded, not required to pass.
- `discovery-details.browser.py`: keyboard open/dismissal, available/partial/blocklisted/unavailable actions, shared discovery/search/recommendation entry points, cancelled and disposed responses. Corrected its pre-existing case-sensitive `Requested` expectation to the actual `requested/tracked` status.
- `discovery-requests.browser.py`: 320/1280px request cards and metadata enrichment. `search-rail.browser.py`: 390/1280/2560px public-theme Search placement, paging, scrolling and disposal. External imports are intercepted.

No backend fields, metadata services or app-wide styling changed. DLL/package build, signed-in hosted Jellyfin, private theme overrides, remote Inter, non-Chromium browsers and screen-reader speech output remain unverified. Independent native review is required before CEO's final handoff; Josh's merge/deployment decision is separate.
