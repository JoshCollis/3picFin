# Staged Jellyfin plugin catalog

This repository targets **Jellyfin 12.1 / net10.0**. Plugin `3pic Fin` (`bd36ab75-0f4a-49b6-92ef-3a93da040c7a`) has catalog ABI `12.1.0.0`. Packages are **staging only**: Home compatibility pins remain unset. No workflow installs on a server or approves live replacement.

## Publishing

The public `catalog` branch holds the cumulative [manifest](https://raw.githubusercontent.com/JoshCollis/3picFin/catalog/manifest.json). GitHub Actions has write permission to update it and publish prereleases. The branch must remain public and writable by the workflow token. Do not replace its history or use the source-tree `manifest.json` as the live catalog.

For another staging build, create and push `vN.N.N.N` at the **current default-branch HEAD**, then dispatch `Publish tagged Jellyfin catalog` from that default branch with the tag. The workflow checks the current default branch SHA via GitHub API (not only the dispatch ref), builds the DLL with the four-component version, tests, validates ZIP/meta/assembly, and reads the current catalog branch. It creates a **draft** release with ZIP and SHA256SUMS, downloads the draft ZIP and byte-compares it, then commits a cumulative manifest to `catalog` with a non-force push. It reads the exact catalog file back through the GitHub API, publishes the release as a **prerelease**, and downloads/validates the public manifest and ZIP. The stable URL is the raw catalog URL above; release-tag assets are not catalog URLs. The catalog checksum is MD5 of the complete ZIP, while SHA256SUMS is additional integrity evidence.

The workflow is serialized per repository. A retry with the same tag and same byte-identical ZIP and manifest is safe before publication; it rejects changed release assets or a conflicting published version. If a catalog push fails, the draft release remains and the catalog is unchanged; resolve branch permission/concurrent writer issues and rerun. **Recovery window:** after catalog push and before the draft is published, the catalog row temporarily points to a ZIP on a draft release that public clients cannot download. If publication or its final readback fails, rerun the same workflow promptly or manually remove only the new row from `catalog` after verifying the release is still draft; never discard older versions. An already published release with a matching catalog should be verified using the public URLs rather than rerun (the draft gate deliberately refuses it). Do not register the repository URL in Jellyfin until the workflow's final public readback succeeds. CDN propagation may delay the raw URL; rerun a public readback independently before installation.

## Local reproduction

```sh
VERSION=0.0.0.1
URL="https://example.invalid/releases/download/v$VERSION/3pic-fin_$VERSION.zip"
dotnet restore RowanJellyfin.slnx
dotnet test RowanJellyfin.slnx -c Release --no-restore
dotnet build RowanJellyfin.slnx -c Release --no-restore -p:Version="$VERSION"
node --test tests/Rowan.Jellyfin.Plugin.Tests/*.test.cjs
python3 -m unittest discover -s tests -p 'test_package_plugin.py'
python3 scripts/package_plugin.py package --dll src/Rowan.Jellyfin.Plugin/bin/Release/net10.0/Rowan.Jellyfin.Plugin.dll \
  --manifest manifest.json --output dist --version "$VERSION" --source-url "$URL" \
  --epoch 0 --changelog 'Local staging fixture only'
python3 scripts/package_plugin.py validate --package "dist/3pic-fin_$VERSION.zip" \
  --manifest dist/manifest.json --version "$VERSION" --source-url "$URL"
dotnet run --project tools/VerifyAssembly -- \
  src/Rowan.Jellyfin.Plugin/bin/Release/net10.0/Rowan.Jellyfin.Plugin.dll "dist/3pic-fin_$VERSION.zip" "$VERSION"
(cd dist && sha256sum -c SHA256SUMS)
```

The actual release uses `catalog`'s current manifest as package input rather than `manifest.json` from source. The package ZIP contains DLL and meta.json at root with fixed member times/modes; identical DLL, version, changelog and epoch reproduce the ZIP. Source builds are not asserted bit-for-bit reproducible. Test actual activation and signed-in Home/Favorites/browser behavior in a disposable Jellyfin 12.1 runtime, then establish distribution-specific Home pins before any live rollout.

Sources: [Jellyfin plugin repository format](https://jellyfin.org/posts/plugin-updates/), [JPRM manifest implementation](https://github.com/oddstr13/jellyfin-plugin-repository-manager/blob/master/jprm/__init__.py).
