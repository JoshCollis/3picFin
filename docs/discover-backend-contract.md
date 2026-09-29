# Discover backend handoff

Branch: `feat/request-modal-refresh`. Full pilot diff base: `af068b5d24499536241cbb28fa806fc6b8125caa`.
Backend checkpoint/base: `0aa22a290fe0ef85760076a755251c7a53fb6d99`.
The issue work products record the exact resulting head. The existing checkpoint is preserved.

## Trending contract

`GET 3picFin/Discovery/Trending?page=1` returns the existing `SourceResult<HomeDiscoverItem>` envelope:
`Items`, `Error`, `Page`, `TotalPages`, `TotalResults`. Each item has `TmdbId`, `MediaType` (`movie` or `tv`), `Title`, nullable `PosterPath`, and nullable `Date`. These are candidates, without a playback or availability claim. Consumers should retain existing PascalCase/camelCase handling.

Requires an authenticated, existing, unambiguous Jellyfin identity, `DiscoveryPageEnabled`, and the existing enabled/mapped Seerr integration. Home and all Home row flags are independent. Invalid identity returns 403; disabled/missing page configuration returns 404; page outside 1–100 returns 400. Upstream and mapping failures retain typed `Error` values in the normal envelope and must not be displayed as an empty successful feed.

The endpoint reuses the same Home filtering/read implementation: adult and blocklist exclusions, verified safe US TV ratings, 20 candidates, at most three catalog pages and 20 TV detail reads, 10-second overall deadline, mapped reads/cache, and `private, no-store`. Existing Home routes keep their original Home/row gates.

Frontend next action: switch Discover's trending URL from `3picFin/HomeDiscover/Discover` to `3picFin/Discovery/Trending`, retaining its media-type split, loading/error handling and stale-response protection. Do not change Home's URL.

## Requester contract

`GET 3picFin/Discovery` → `Requests.Items[]` and `GET 3picFin/SharedRequests` → `Items[]` add nullable `RequesterDisplayName` (camelCase under camelCase serialization). Existing status, media identity, season and request fields are unchanged. No title-detail or request-write contract changed.

Only an explicit upstream `requestedBy.displayName` is eligible, with a positive numeric requester ID. Personal output requires that ID to match the current server-resolved Seerr mapping. Another user's name is withheld even if the personal upstream response includes their record. The shared route allows cross-user labels only behind its existing authenticated-user and administrator `SharedRequestsEnabled` opt-in checks; its existing global read is unchanged.

Labels are trimmed, 1–100 UTF-16 code units, and reject `@`, control and Unicode format characters. Missing, malformed, oversized and email-like labels return null. No fallback to username, email, numeric ID or guessed identity; no raw upstream object, token or account ID is added to a response. Labels are untrusted text: frontend must render with textContent, associate with the correct request record, and show unknown/omit when null. A server with no explicit safe displayName will intentionally show no requester label. No live server compatibility was tested.

Frontend next action: carry the field from each request item into its modal context. Do not infer requesters from catalog/title-only details or from the current viewer. Preserve request permissions and actions.

## Apple originals

Unsupported by the current repository contracts. `SeerrClient.IsAllowedReadPath` exposes search and generic trending/movie/TV discovery; the DTOs contain no verified original-production provenance. TV detail reads exist for ratings, not an originals catalog. No existing Apple-originals feed or provenance predicate was found in source/tests. This is a repository capability finding, not a claim about everything upstream Seerr/TMDb could support. Provider availability alone cannot establish originals. No Apple rail, provider query or integration was added.

## Actual verification

- `git diff --check`: passed.
- `node --test tests/Rowan.Jellyfin.Plugin.Tests/discovery-ui.test.cjs tests/Rowan.Jellyfin.Plugin.Tests/native-home-rows.test.cjs`: **69 passed, 0 failed**. Existing synthetic frontend/Home compatibility only; does not execute C#.
- `dotnet test tests/Rowan.Jellyfin.Plugin.Tests/Rowan.Jellyfin.Plugin.Tests.csproj -c Release --filter 'FullyQualifiedName~Discovery|FullyQualifiedName~HomeDiscoverTests|FullyQualifiedName~MyRequestsRowTests'`: **exit 127, dotnet: command not found**. No C# compilation or test pass is claimed.
- Added synthetic C# regressions for disabled Home/enabled Discover, disabled Discover/enabled Home, unauthenticated/missing/conflicting/unknown identity, invalid pages, common content filtering, authorized cross-user names, personal mapping isolation, missing identity, no email/username fallback, and unsafe/oversized labels. Existing mapping/cache/deadline/request permission tests remain part of the focused command above.

## Remaining validation and sequential ownership

CEO owns provisioning/authorizing a bounded remedy: place an approved .NET 10 SDK in `.qa-tools/dotnet`, with CLI home and NuGet packages confined beneath `.qa-tools`, then run the exact focused command above using that binary. No system packages, downloads, live Seerr/Jellyfin requests or credentials were used by this backend assignment. Restoring packages or acquiring an SDK requires resolving the current no-download constraint first; none was attempted. Backend Engineer can resume if compiled checks reveal failures.

CEO hands the checkout to Frontend Engineer for URL/label integration and synthetic browser acceptance/screenshots. The configured native Modal Reviewer stage follows completed frontend evidence and must independently inspect the full diff and run checks. This backend handoff is not pilot completion or approval. No push, merge, publication or deployment.
