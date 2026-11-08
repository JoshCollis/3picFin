# Continuous native Guide and assigned categories

The independent, default-off Guide option adds full-catalogue channel name/number
search, assigned categories, and continuous vertical channel scrolling. There are
no result-page, page-size, First/Last or Load more controls. The compact toolbar
contains only icon-based channel search/clear and an assigned-category selector.
The count and live loading status sit in the date-header metadata, outside those
two controls. Native date tabs and horizontal panning navigate time; no duplicate
Earlier/Now/Later buttons are added.

The existing configuration key `NativeGuidePagingEnabled` is retained so saved
settings remain compatible. Its administrator-facing label now describes a
continuous native Guide. No credentials, flags or library settings are reset.

## Native renderer boundary

The feature remains an opt-in transformation of the validated native module:

- Module `14553`, capability-pinned native asset SHA-256
  `a613661f2a2bd541c694184c769ab168d7678ee677efcdae5e0eabf8d3acfe0d`.
- Reviewed source: `jellyfin-web@c06cb17b274c10f9015412305edb0afdc010ae7a`,
  `src/components/guide/guide.js`, its template and scroller.
- Every unique transformation seam must match atomically. Unknown, ambiguous,
  disabled or already-transformed input is unchanged; version, OS and user-agent
  labels are not treated as proof of renderer compatibility.
- Existing File Transformation registration and module-versioned index preload
  remain independent of Home/Search. A fresh client document is still needed.
  No native web-root file, framework prototype, global network client or other
  plugin renderer is replaced.

Native channel/programme action markup and item maps remain in charge of details,
playback, recording indicators, favourite ordering and programme-type settings.
Channel name markup is always included alongside the native logo. Current cells
also have a text/time indicator and an edge marker, rather than a colour-only
state. Focus uses a high-contrast outline. The header and timeline retain the same
proportional native day grid.

The labelled Now marker has its own header lane above the time labels. Programme
bodies own a consistent inset fill and current-state edge, separate from native
translated text wrappers; the duration positions and sticky text offsets remain
native. Thin neutral separators avoid doubled borders while keyboard focus stays
strong. Channel names and secondary numbers are centered together beside logos.
Only the controls/metadata gutter is aligned to the measured native header;
the Guide grid is not inset. The selected date uses one container-level indicator
and an accessible pressed state instead of separate weekday/day underlines. Channels without
logos retain the same logo slot with a neutral TV fallback, so names do not shift.
The visible search placeholder is concise; its accessible name still describes
name/number search. Browser geometry checks decode a local SVG logo fixture,
verify logo/name boxes and the missing-logo variant, and reject Now/time-label
intersection. These fixture images are not live channel-logo acceptance.
`--refinement` adds irregular/overlapping programme durations, missing EPG and
native category/current/indicator classes, plus geometry gates for the two-group
toolbar, native header alignment, centered identities, inset bodies, chevron and
selected date. Combine it with `--continuous --toolbar-only` for focused visual,
tail, filtering and lifecycle checks without repeating full-catalogue traversal.

## Bounded scrolling and lifecycle

The controller moves an overscanned native row window (normally 28 channels,
never more than 50). Matching vertical padding on the existing channel column and
programme grid represents omitted rows, retaining full-catalogue scrollbar
geometry. Only this window's channel IDs reach the existing 24-hour programme
query. An empty window never submits an empty channel-ID restriction, which the
native API would interpret as all channels.

The existing vertical scroller stays in charge in both native-scroll and animated
web-TV modes. Its slider is viewport-sized while its scrollable content represents
the catalogue; row windows do not enlarge its viewport. Loading text reserves
stable toolbar space so it cannot resize/reset that scroller on every read.
Home/End and boundary navigation reach the logical first/tail channel without
visible result-page controls. Recycled focus returns to the same programme when
present, otherwise a corresponding visible programme, not the document body.
Horizontal position and ruler alignment survive vertical window changes.

There is one active native read and one latest pending read per instance.
Generation, API-client and user checks prevent obsolete callbacks from reaching
the renderer. A bounded eight-response cache supports nearby reverse traversal;
search/category/settings/date refresh, identity changes and pause invalidate the
relevant state. This does **not** claim transport cancellation: a started native
read can finish before newer work starts. Old actions stay inert until the current
window commits. Errors offer Retry rather than exposing stale actions.

## Search and assigned categories

All categories is the default. Category assignment intersects with name/number
search and native guide settings before counting/windowing. Clear removes search,
not the selected category. Filter changes reset to the first logical channel.
Missing selected categories fail closed instead of silently widening to All.

Categories come from imported M3U `group-title` cache data, not programme genres
or guessed country/name rules. The authenticated categories endpoint retains its
current-user, Live TV permission and standalone channel-visibility checks and
`private, no-store` response. Provider URLs, credentials, cache paths and stream
addresses are never returned. Existing limits remain: eight M3U tuners, 16 MiB
aggregate cache bytes, depth 16, 10,000 channels, 256 groups, 200 characters/name.
Duplicate/conflicting/incomplete joins fail closed; bounded trailing bytes from
native non-truncating cache writes are ignored. See [channel search](channel-search.md)
for the separate sequential 500-record metadata snapshot, 60-second scope/TTL and
10,000-channel ceiling. Metadata search does not preload programme grids.

## Reproduce without production access

Set `TMPDIR` to approved private scratch storage. Retain the public native web
distribution outside Git, then use `FIN_GUIDE_WEB_ROOT`, `FIN_GUIDE_NATIVE_ASSET`
and `FIN_GUIDE_PATCHED_ASSET` for local native-module checks:

```sh
dotnet test RowanJellyfin.slnx -c Release
node --test tests/Rowan.Jellyfin.Plugin.Tests/*.test.cjs
python3 -m unittest discover -s tests -p 'test_*.py'
python3 tests/guide-native.browser.py --asset "$FIN_GUIDE_PATCHED_ASSET" \
  --continuous --output "$TMPDIR/guide-desktop"
python3 tests/guide-native.browser.py --asset "$FIN_GUIDE_PATCHED_ASSET" \
  --continuous --width 390 --output "$TMPDIR/guide-phone"
python3 tests/guide-native.browser.py --asset "$FIN_GUIDE_PATCHED_ASSET" \
  --continuous --layout tv --output "$TMPDIR/guide-web-tv"
python3 tests/disposable-guide-categories.py --output "$TMPDIR/guide-runtime"
```

`FIN_GUIDE_CHROME` can select an existing browser. `--theme` accepts local captured
CSS. `--toolbar-only --continuous` is a short tail/functional smoke, **not** full
catalogue traversal. Full traversal measures distinct channel coverage, tail,
reverse reads, bounded row/cell DOM, search/category intersections/reset,
header/row alignment, title/time clipping, keyboard/focus, native settings/date
and route/identity teardown.
Fixture output is separate from signed-in disposable-runtime receipts. Neither
is production acceptance or native Android TV / physical remote certification.
Keep screenshots, API captures, profiles and private audit reports outside Git.

Unsupported native guide modules retain their original behaviour; do not claim
continuous scrolling for a module that fails the capability gate. Deployment,
plugin replacement/reload and production setting changes require separate approval.
