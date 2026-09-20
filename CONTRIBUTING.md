# Contributing to GitHubAppSync

This library updates an application from the releases of a repository. The README covers
*how to use it*; this file covers *how to test, extend, and release it* without breaking the
guarantees it exists to provide.

## Design invariants (do not break)

These are the reasons the library exists. A change that violates any of them re-introduces a
bug the rewrite removed:

1. **Never apply an older or equal version.** The update is decided by a real
   `Version` compare (`UpdateManifest.ParseVersion` strips any `-prerelease` suffix), not by
   a "files differ" hash. An older build on the source must never downgrade a newer install.
2. **SHA-256 is verified in the pipeline, after the download returns** — not inside the
   transport. `Update` hashes the downloaded zip and compares it to `manifest.sha256`
   before anything is written. A custom `IUpdateSource` is therefore *not* trusted to hash
   for you, and cannot skip the check by forgetting to.
3. **Read through the stable redirect** `…/releases/latest/download/<asset>`, never the
   REST API. The redirect is served by GitHub's web tier and does not consume the
   unauthenticated 60 req/h/IP limit, which a fleet behind a shared NAT would exhaust.
4. **No DNS pinning.** Do not rewrite the host to a resolved IP — it breaks TLS and is
   wrong behind a CDN. (The old updater did this; it is gone on purpose.)
5. **Zip entries live at the archive root** (no wrapping folder). The client extracts and
   compares relative paths against the install directory; a wrapping folder makes every file
   look changed or missing.
6. **`ExcludedFileNames` (default `appsettings.json`) is never overwritten** — local config
   survives an update.
7. **Restart only when the caller says it is safe** (`isReadyForReboot`). The default
   restart is platform-appropriate: relaunch the exe on Windows, exit cleanly on Linux/macOS
   so the supervisor (systemd/launchd) restarts it.

## Testing

The regression net is a verifier that runs the whole pipeline against a local **fake**
`IUpdateSource` — no network, no real release:

```
dotnet run --project tests/Verify
```

It exits non-zero on any failure. It covers: no-manifest, anti-downgrade (older not applied),
equal-build not applied, newer-build applied with the correct file delta, tampered payload
rejected with nothing written, `appsettings.json` preserved across an update, pruning on/off,
and the **JSON wire-format contract** between `tools/New-UpdateAsset.ps1` and the client
parser.

**Run it before every change to the library.** When you change behaviour, add a case to
`tests/Verify/Program.cs` rather than relying on manual checks — the wire-format and
integrity cases are the ones that silently regress.

## Extending

### A new update source (e.g. a private server, a CDN, a file share)

Implement `IUpdateSource` and pass it to the same `Update` methods:

```csharp
public interface IUpdateSource
{
    Task<UpdateManifest> GetLatestManifestAsync(CancellationToken cancellationToken = default);
    Task DownloadAssetAsync(UpdateManifest manifest, string destinationFile, CancellationToken cancellationToken = default);
}
```

Return a manifest whose `Version` is comparable and whose `Sha256` is the lowercase hex of
the asset. The pipeline verifies the hash after your `DownloadAssetAsync` returns, so the
integrity guarantee holds regardless of the source. Do not bypass the pipeline by writing
files yourself.

### A new channel

A channel is just an asset-name prefix (`portable`, `win-x64`, `beta`, …). Produce
`<channel>.zip` + `<channel>-manifest.json` with `tools/New-UpdateAsset.ps1 -Channel <name>`
and pass the same name as the `channel` argument. No code change is needed — the channel is
data.

## netstandard2.1 gotchas

The library targets `.NET Standard 2.1` (so it works across .NET Framework/Core/MAUI). A few
APIs that exist in modern .NET are **not** in netstandard — these bit us:

- `FileInfo.MoveTo(dest, overwrite)` and 3-arg `File.Move(..., overwrite)` are **not**
  available (they are .NET Core 3.0+). Use 2-arg `File.Move(src, dst)` to a unique parked
  path.
- `Convert.ToHexString` / `Convert.FromHexString` are **not** available. Hash formatting is
  done with a `StringBuilder` + `b.ToString("x2")`.
- The `tests/**` tree must be excluded from the parent `GitHubAppSync.csproj` glob
  (`<Compile Remove="tests/**" />` etc.), or the test project's assembly attributes collide
  with the library's (CS0579 duplicate attributes).

## Release flow

The package version comes from the **git tag**, not the runner clock:

```
git tag v1.26.09.15 && git push origin v1.26.09.15
```

`publish.yml` runs only on a `v*` tag push: it packs `GitHubAppSync.<ver>.nupkg` and pushes
to NuGet using the `NUGET_API_KEY` secret (it fails fast with `::error::` if the secret is
missing). A plain `master` push runs nothing — it is a pure code sync.

After pushing, confirm the package is live (indexing lags a few minutes):

```
curl -fsS https://api.nuget.org/v3-flatcontainer/githubappsync/index.json
```

Consumers that fall back to NuGet (e.g. CloudClient's `PackageReference GitHubAppSync 1.*`
on public CI) need the new version published **before** their release runs.

## License

Andrea Bruno License 1.4 — see [LICENSE.md](LICENSE.md). It applies to this project and to
all forks and derivative works.
