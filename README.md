# 3pic Fin

A Jellyfin 12.1 plugin for Home rows, discovery and media requests. It uses Seerr for requests and Radarr/Sonarr for read-only download and release activity. Jellyfin remains the source of truth for what a user can play.

## Current status

The [v0.1.0.0 release](https://github.com/JoshCollis/3picFin/releases/tag/v0.1.0.0) is a staging build. Its Home adapter has no production web-bundle compatibility pins, so installing the ZIP will not add the 3pic Fin Home switch to an unverified Jellyfin web build. Search augmentation is likewise not enabled for an unverified distribution. The current package is for disposable testing, not replacing installed UI plugins.

The server includes user-scoped Home rows, a still-image hero, Seerr discovery and requests, title-wide Arr downloads, and an upcoming-releases calendar. Most modules are independently configurable and default off. Household requests, downloads, calendar and upcoming rows can show titles outside a viewer's libraries when an administrator enables them. Read the disclosures in the plugin settings before enabling those feeds.

## Packages

The Jellyfin repository URL is:

```text
https://raw.githubusercontent.com/JoshCollis/3picFin/catalog/manifest.json
```

Versioned ZIPs are on the [releases page](https://github.com/JoshCollis/3picFin/releases). The `catalog` branch retains the version list. Adding the repository to a live server does not make this staging build ready for Home replacement; test it on a disposable Jellyfin 12.1 instance first. The optional Home injection uses the File Transformation plugin and is disabled without verified compatibility pins for the exact web distribution. See [Home adapter compatibility](docs/home-adapter-integration.md) and [release packaging](docs/release-packaging.md).

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
