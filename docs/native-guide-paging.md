# Optional native guide paging and categories

The guide adds full-catalogue channel-name/number search, assigned channel categories and a compact accessible toolbar,
and an optional Channels section under the existing global Search opt-in. See
[`channel-search.md`](channel-search.md) for native API semantics, snapshot bounds,
authorization, cache invalidation and the private-candidate rollout boundary.

`NativeGuidePagingEnabled` is an independent, **default-off** choice. On a compatible
native guide module it changes Jellyfin's existing guide page from 500 to 50 channels.
The guide toolbar gains **First**, **Page / Go**, **Last**, and an authenticated
channel-range/page count. Any of the 38 pages in a 1,879-channel catalogue is one
bounded jump away; sequential Next clicks are not the only navigation route.
Next/Previous remains available. Changing guide settings resets
the channel cursor, and an empty channel page never triggers an unrestricted
programme fetch. The grid remains 24 hours wide,
all native date tabs remain, and Jellyfin still owns rendering, logos, details,
playback, recordings, programme-type filters, favourite sorting and focus.

This is **bounded native pagination**, not continuous-scroll virtualisation. It
trades more page changes for less work per page. It does not change native Android
TV, the lineup, guide retention, country visibility or provider configuration.

## Narrow compatibility boundary

- Served web build: `8ae2bfeb4afd006c3a0d`, server 12.3.0.
- Asset: `14553.6ddb0da23f2698557d33.chunk.js`, 23,549 bytes.
- Native SHA-256: `a613661f2a2bd541c694184c769ab168d7678ee677efcdae5e0eabf8d3acfe0d`.
- File Transformation 3.0.3's relative-file callback is registered independently
  using the existing optional reflection/host-lifecycle pattern.
- Server versions, OS and UA strings do not gate guide inspection. The native
  module identity and **every unique structural seam** must match atomically.
  Harmless source comments and another hexadecimal chunk filename are compatible;
  unsupported, ambiguous and already transformed inputs are unchanged. Other
  scripts, routes and plugins are untouched. No files in Jellyfin's web root are edited.
- The shared native page-size constant changes. Merely rewriting HTTP `Limit`
  would be incorrect: the native Next button would still advance by 500 and skip
  channels. Native total counts and programme channel IDs are never fabricated.
- Settings refresh also resets the page offset. Native settings otherwise retain
  an offset beyond the end of a smaller filtered result. An empty-page guard avoids
  sending `channelIds=''`, which the API interprets as an unrestricted query. These
  avoid the unrestricted-fetch edge case. Auto-refresh and date navigation keep
  their native channel cursor behaviour; settings and route reentry reset it.
- Toolbar controls and request coordination are embedded inside this validated guide
  module. Native channel/programme rendering and action markup are retained.
  Page counts come from the current server response, never a hardcoded country map.

Upstream reviewed source:
`jellyfin-web@c06cb17b274c10f9015412305edb0afdc010ae7a/src/components/guide/guide.js`.
The actual served chunk was separately verified; a server version alone does not
attest a web bundle. Capability acceptance does not certify an untested renderer.

### Module-scoped cache adoption

An independent index callback discovers exactly one compatible `14553.<hex>.chunk.js`
in the configured public web root (maximum 512 KiB). With a unique deferred
runtime/main bootstrap it queues that native module using an ordinary script tag
and `?fin-guide=<transformed-body-SHA256>` before deferred bootstrap runs. No active
instance is replaced. Unsupported/ambiguous files or bootstrap shapes leave the
index unchanged. Other asset URLs, storage, service workers and cache policies are
not changed. A fresh document is necessary; a stale index/service-worker response
that never obtains the new loader is outside this correction.

`--amendments` on the disposable recipe proves original immutable HTTP-cache RED
versus versioned-module GREEN without Playwright routing or cache clearing. CDP
records executed bodies and cache/service-worker flags. This reproduces one
possible missing-controls mechanism, not the owner Mac's uninspected root cause.

## What this intentionally does not introduce

No new credential dependency, persistent/shared user cache, DOM observer,
replacement renderer, global network/prototype patch, custom playback route or
cross-user data model. The bounded, short-lived per-consumer channel metadata
snapshot is described in `channel-search.md`. Native API authorization, filters and request parameters
remain native apart from channel page scope. The API client uses a POST body for programme
queries whose channel-ID string is longer than 1,800 characters. A bare URL in a
log therefore does not imply an all-catalogue query.

The module-local controller allows **one active native read and one latest pending
read per guide instance**. New page/date/filter intents invalidate old callbacks
and replace pending work. A generation plus API-client/user check prevents stale
responses reaching the renderer. Pause/destroy drops pending work; resume refreshes.
The old programme grid and channel actions are `inert` while loading, so old actions
cannot target newly requested data. Native focus is restored before rendering where
needed. The native renderer still chooses the committed programme/remote focus.

**This is not transport cancellation.** An already-started native API request runs
to completion before the latest pending read can start, and can delay it. There is
no cross-instance/global transport limit or timeout added. Underlying API failures
leave old actions inert and show an error rather than exposing stale actions.

## Reproduction

Use a private output directory outside Git and an approved `TMPDIR`. First retain
the exact public native chunk above. Then:

```sh
# Set TMPDIR to your approved private scratch directory before running probes.
# Optional: FIN_GUIDE_CHROME=/absolute/path/to/an/installed/chromium
export FIN_GUIDE_NATIVE_ASSET="$EVIDENCE/14553.6ddb0da23f2698557d33.chunk.js"
export FIN_GUIDE_PATCHED_ASSET="$EVIDENCE/guide-categories.js"
dotnet test RowanJellyfin.slnx -c Release
node --test tests/Rowan.Jellyfin.Plugin.Tests/*.test.cjs
python3 -m unittest discover -s tests -p 'test_*.py'
python3 tests/guide-native.browser.py --output "$EVIDENCE/native"
python3 tests/guide-native.browser.py --asset "$FIN_GUIDE_PATCHED_ASSET" \
  --categories --toolbar-only --output "$EVIDENCE/candidate"
python3 tests/guide-native.browser.py --asset "$FIN_GUIDE_PATCHED_ASSET" \
  --categories --toolbar-only --width 390 --output "$EVIDENCE/phone"
python3 tests/guide-native.browser.py --asset "$FIN_GUIDE_PATCHED_ASSET" \
  --categories --toolbar-only --layout tv --output "$EVIDENCE/web-tv"
python3 tests/guide-native.browser.py --asset "$FIN_GUIDE_PATCHED_ASSET" \
  --assert-bounded --functional --layout tv --output "$EVIDENCE/web-tv-lifecycle"
python3 tests/guide-native.browser.py --asset "$FIN_GUIDE_PATCHED_ASSET" \
  --empty-guide --output "$EVIDENCE/empty-guide"
python3 tests/disposable-guide-categories.py --output "$EVIDENCE/hosted-categories"
python3 scripts/benchmark-guide.py --evidence "$EVIDENCE" \
  --asset "$FIN_GUIDE_PATCHED_ASSET" --runs 1 --prefix recheck-v2
```

`--assert-bounded` on the unmodified asset is the behavioural RED case. The browser
probe uses actual served web code but locally fulfilled API responses and a
synthetic identity. It blocks WebSockets and locally acknowledges non-GET activity;
no production writes or playback requests are needed. It must **not** be reported
as a signed-in real-user, Wolf, MPV-bridge or Android TV test.

The benchmark anchors the same UTC guide slot while allowing wall time to advance;
performance clocks, timers and animation frames remain native. It compares the
same first-ten-row vertical distance and full-day horizontal pan, not different
page-length scroll distances. CPU profiles and numeric timing summaries are private
outputs. Frame counts and actual overrun duration matter: a stalled native frame
can overrun the nominal four-second input ramp. Do not describe either case as
60fps, or turn a sparse diagnostic p95 into a production percentile.

An optional `--dataset` can replay an allowlisted, privately collected read-only snapshot.
A userless administrative capture is not personal permission or
favourite-order proof. Use the same file for both variants. Empty
programme rows and missing guide coverage are data-quality issues, not rendering
failures. The older frozen capture predates the completed French refresh at
`2026-11-02T18:55:20Z`; TF1/France2/beIN1 programme entries are now present according
to the owner's follow-up verification. Private captures must never be added to Git.

The separate disposable-host recipe starts the pinned official 12.3 image on
loopback with File Transformation 3.0.3.0 and this DLL. It uses random, real local
user sessions and synthetic metadata with non-playable stream addresses. It checks
default-off/enable/disable delivery, saved readback, drift fallback and the existing
Home/Search injection. Legacy per-channel policy fields are probed rather than
assumed to restrict Live TV. If unsupported, the receipt records that result and
checks genuine Live TV allow/deny instead. It never uses production sessions.

## Categories and rollout boundary

**All categories** is the default. Assigned categories intersect with channel-name/
number search and native guide settings before counting or paging; changing the
category or search resets page 1. Clear removes the search, not the selected category.
Only the selected page's at most 50 channel IDs reach programme queries.

At the pinned server commit, `src/Jellyfin.LiveTv/TunerHosts/M3uParser.cs` reads
`group-title` into `ChannelInfo.ChannelGroup`. `Guide/GuideManager.cs` copies
`ChannelInfo.Tags` separately, not `ChannelGroup`. Consequently native Tags such as
`Kids` are not evidence of Dispatcharr-assigned groups. The existing M3U tuner cache
retains the parsed `ChannelGroup`, `TunerHostId` and exact channel `Id`;
`GuideManager` stores that same ID as the imported channel's `ExternalId`.

`GET /3picFin/Guide/Categories` uses only caches belonging to currently configured
M3U tuners. It requires authentication, exactly one existing user identity, the
Live TV grant and the guide opt-in. It enumerates that user's native channel
inventory and checks standalone visibility, joining exact external IDs for the
native tuner service. It returns only stable name-derived opaque group IDs, original
group names and visible Jellyfin channel IDs, with `private, no-store`. No caller-
selected user, provider URL, tuner details, cache paths, programme genres or stream
addresses enter the response. No network connection, credential, scan or stream
probe is needed: this is read-only navigation over the existing imported M3U data.

Bounds: eight configured M3U tuners, 16 MiB aggregate cache bytes, JSON depth 16,
10,000 channels, 256 groups and 200 characters per group name. The first complete
JSON array is read; native non-truncating cache writes can leave trailing bytes
from an older larger array, which are ignored within that same byte budget.
Duplicate IDs/properties, conflicting joins, incomplete caches and exceeded bounds
fail closed. Missing categories leave All-channel search available. A selected
category that disappears reports unavailable rather than silently widening to All;
the viewer can explicitly select All to recover. Category metadata is scoped to
the API client/user, expires after 60 seconds and is discarded on pause/route exit.
Group changes follow the native imported cache, not a new provider refresh.

The focused disposable recipe uses 128 synthetic channels, real local sessions,
native APIs and File Transformation on 12.3. It checks group import provenance,
the trailing-remnant shape, anonymous/userless/Live-TV-denied access, 50-channel
programme scopes and rendered desktop/mobile/web-TV intersection. Fixture replay
screenshots and disposable signed-in screenshots are explicitly marked; neither
is evidence of a signed-in production session or native Android TV rendering.

Do not deploy this review candidate from a test command. Integration requires
separate rollout approval, normal plugin replacement/reload procedure, enabling the
option and a fresh client load. Revalidate the delivered asset hash and complete
catalogue on the target client. Turning the option off and freshly reloading the
client restores the native 500-row guide; the original web files are never changed.
