# Continuous catalog rails

Discovery's eight catalog/request lists and the plugin-owned Seerr Search rail
load bounded API pages automatically near the loaded end. Off-screen cards are
windowed with inert spacers, with focused/modal-return cards retained. Result
pager markup, handlers and styles are removed; loading, Retry and end states
remain explicit. Native sorting, permissions and request actions are unchanged.

The shared controller uses native scroll/resize events without dependencies,
one in-flight page per rail, generation checks and transport cancellation where
supported. Catalog identity deduplication preserves server order; requests use
request IDs. Cached records and offsets support reverse scrolling and same-session
return navigation without fetching entire catalogs. Request enrichment is limited
to three concurrent metadata reads across the mounted lists.

## Verification

Use an approved temporary directory for builds and tests:

```sh
dotnet test RowanJellyfin.slnx -c Release
node --test tests/Rowan.Jellyfin.Plugin.Tests/*.test.cjs
python3 -m unittest discover -s tests -p 'test_*.py'
python3 tests/continuous-scroll.browser.py
```

The browser probe extracts assets from the built candidate DLL, serves deterministic
bounded DTOs over loopback, traverses nine 400-record lists and measures live DOM
counts at phone/desktop widths and web-TV CSS layout. It checks absent result pagers, error retry,
keyboard/modal focus, stale searches, teardown and cached return navigation.
`FIN_CANDIDATE_DLL`, `FIN_BROWSER_EXECUTABLE` and `FIN_THEME_CSS_DIR` allow existing
local builds, browsers and captured styles without downloads. Screenshots and
receipts belong outside Git. Fixture evidence is not signed-in runtime acceptance.

The native Guide is a separate validated module transform; catalog acceptance
does not attest its search/category controls. Feature-composition tests require
both the current Guide/channel-search resources and catalog-scrolling resources.
