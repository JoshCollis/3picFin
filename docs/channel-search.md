# Channel findability

The existing `NativeGuidePagingEnabled` opt-in now includes a **Find channels**
field and one native guide toolbar. Search names or channel numbers, not programme
titles. Case, whitespace and decomposable diacritics are folded; e.g. `France2`
matches `France 2` and `France 24`. Different stations with the same name retain
their separate IDs, logos and channel numbers. Clear removes the query while retaining
the selected assigned category. All categories is the default; see
[`native-guide-paging.md`](native-guide-paging.md#categories-and-rollout-boundary) for its authenticated source and bounds.

The existing, independent `GlobalSearchEnabled` opt-in also enables a compact
**Channels** section alongside the existing native media and Seerr sections. It
uses the same matcher. Results open **Channel details**, using the native details
route, not a custom player. **Search in guide** carries the query into the native
guide. No new flag silently enables Home, discovery or Seerr.

After a complete Channels result is available, scoped CSS hides only a duplicate
native poster group containing exclusively TV channels. Native Movie/Series and
mixed groups are not hidden or reparented. Failed/incomplete channel searches do
not hide the native fallback. V2's existing native Search request cap of 50 is
unchanged; broad terms can fill that native result set with other matches, so
refining a term may still be necessary for native media results.

## Why not just send SearchTerm?

Actual 12.3 probes establish that `/LiveTv/Channels` ignores `SearchTerm`.
`/Search/Hints` does match channels and numbers, but its limit is applied before
its offset: BBC returned five at limit five, none at offset five, but nineteen at
limit fifty. Its total is capped, and its matching can include broader candidates
(e.g. France2 returned six France stations). It is not a complete paging/count
contract. `/Items` filters names but is not the native Live TV visibility/order
contract used here. Name/number matching continues to use the native channel API;
the separate category endpoint supplies only assigned-group membership.

## Bounded authorized metadata

- Each active consumer uses its own API-client/user-bound metadata snapshot from
  `getLiveTvChannels`, with the real current `UserId` and native guide filter/order
  parameters. `SearchTerm` is never sent to that endpoint.
- Reads are sequential, **500 metadata rows at a time**, at most **10,000 channels**.
  No programmes/current programme/media-source fields are requested for indexing.
  Only ID/name/number/type/server ID/primary-image tag are retained.
- The single completed snapshot expires after **60 seconds**. Filter/order changes
  replace its scope. It is reused across typing; main Search also retains it across
  the native input/URL update gap, while immediately removing old actions.
- Guide reads remain serialized with latest-intent coalescing. A metadata scan is
  one bounded operation containing sequential native reads; its result callback
   cannot commit for an obsolete query. At most 50 result rows/programme scopes
  render. Main Search renders at most eight compact station results, gives the
  full matching count and links to the continuous native guide.
- Pause, route exit, API-client switch, logout and account changes invalidate the
  snapshot/actions. Pending metadata pages recheck identity/scope before and after
  every native read. Live TV denial is enforced by the native endpoint, not a
  caller-supplied user override. Main Search additionally checks the native user's
  Live TV grant before reading.
- Incomplete/changing/oversized snapshots fail unavailable, not as a misleading
  complete empty result. A short-lived snapshot can lag an operational catalogue
  change until expiry or route reload. No-match issues zero programme RPCs.
- Native transport is **not aborted**. An already-started request can delay the
  latest intent; no global/cross-instance limit or new timeout is claimed.

## UI and scope

The toolbar is generated inside the structurally validated native guide, with scoped
CSS inheriting the existing font, palette and theme surfaces. Controls have at
least 44px hit targets, a visible outline, labels and dashed disabled borders;
state is not colour-only. TV programme navigation remains native. Programme
rendering, playback, details and recording action markup are not replaced.

Native Android TV is unchanged. Browser TV checks do not prove Android or Wolf
performance. Channel curation, provider profiles, guide tasks and production
rollout are separate operations; this code does not mutate them.
