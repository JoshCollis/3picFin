# 3pic Fin

3pic Fin is a modular Jellyfin 12.1 plugin for Home rows and discovery. This repository is a **staging implementation**, not a drop-in replacement for existing Home or search plugins. The optional Home adapter's distribution compatibility pins are unset, so it does not inject into an unverified production Home bundle. No live installation or full feature parity is claimed.

The backend includes user-visible Recently Added, native Home rows, a gated still-image hero, and Seerr/Arr-backed discovery, calendar, requests and downloads. Admin configuration defaults sensitive modules off. Shared Downloads and Calendar deliberately expose household-level title information to signed-in users when enabled, including titles outside an individual's library or parental visibility; review the admin warnings before enabling. The hero filesystem option is for trusted Jellyfin internal metadata only, not user-writable shares. Routes retain their `Rowan/Home` and `3picFin` technical prefixes for compatibility.

## Verify locally

Requires .NET 10, Node.js and Python 3. Run from the repository root:

```sh
dotnet restore RowanJellyfin.slnx
dotnet test RowanJellyfin.slnx -c Release --no-restore
dotnet build RowanJellyfin.slnx -c Release --no-restore
node --test tests/Rowan.Jellyfin.Plugin.Tests/*.test.cjs
python3 -m unittest discover -s tests -p 'test_package_plugin.py'
```

Disposable Jellyfin probes under `tests/disposable-*.py` require Docker; some browser probes also require Playwright. They create throwaway servers and do not target a configured instance. See [Home adapter limitations](docs/home-adapter-integration.md), [hero trust requirements](docs/static-hero.md) and [staged packaging](docs/release-packaging.md). Package validation produces local artifacts only; the release workflow requires separate repository setup and a reviewed tag. Do not install or publish as a production-ready Home replacement without distribution-specific pins and signed-in compatibility testing.

MIT licensed; see [LICENSE](LICENSE).
