# 3pic Fin

A Jellyfin 12.1 plugin for Home rows, discovery and media requests. It uses Seerr for requests and Radarr/Sonarr for read-only download and release activity. Jellyfin remains the source of truth for what a user can play.

## Current status

Install the [latest release](https://github.com/JoshCollis/3picFin/releases) on Jellyfin 12.1 with File Transformation 3.0.1 (or a compatible version) for automatic Home integration. The portable Home/3pic Fin switch and Discovery view are enabled on a new installation. Existing saved configuration is respected. The optional featured still-image carousel (up to 10 titles) needs a selected-library allowlist and an administrator's trusted-filesystem opt-in; servers whose image writers are not trusted should leave it off. An unsupported web version or ambiguous native Home structure leaves Jellyfin's Home unchanged.

User-scoped rows, Seerr discovery and requests, title-wide Arr downloads, and an upcoming-releases calendar are independently configurable. Seerr credentials and household-wide requests, downloads and calendar remain opt-in; the latter can reveal titles outside a viewer's Jellyfin libraries. Search augmentation is not automatically mounted yet. Keep existing UI plugins until their household features are verified side by side.

## Packages

The Jellyfin repository URL is:

```text
https://raw.githubusercontent.com/JoshCollis/3picFin/catalog/manifest.json
```

Versioned ZIPs are on the [releases page](https://github.com/JoshCollis/3picFin/releases); the `catalog` branch retains the version list. Add the repository URL above in Jellyfin's Plugins → Repositories, install 3pic Fin, and restart Jellyfin. File Transformation is required for automatic Home mounting; other user-facing integrations are configured in the 3pic Fin admin settings. The Home adapter checks Jellyfin 12.1 and native route/DOM compatibility at runtime rather than a server-specific asset hash. See [Home adapter compatibility](docs/home-adapter-integration.md) and [release packaging](docs/release-packaging.md).

## Build and test

Requires the .NET 10 SDK, Node.js and Python 3. From the repository root:

```sh
dotnet restore RowanJellyfin.slnx
dotnet test RowanJellyfin.slnx -c Release --no-restore
dotnet build RowanJellyfin.slnx -c Release --no-restore
node --test tests/Rowan.Jellyfin.Plugin.Tests/*.test.cjs
python3 -m unittest discover -s tests -p 'test_package_plugin.py'
```

The `tests/disposable-*.py` probes use throwaway Jellyfin instances and require Docker. Browser probes also require Playwright. For the title-details HTTP and browser checks, run `python3 tests/disposable-title-details.py --browser`. The hero's filesystem trust requirements are documented in [docs/static-hero.md](docs/static-hero.md).

Licensed under [MIT](LICENSE).
