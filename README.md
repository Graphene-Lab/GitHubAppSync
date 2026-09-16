# GitHubAppSync

A small library that lets an application update itself from the **releases of a GitHub repository**.

The application asks the release channel for a small manifest, compares the version, downloads the
payload only when it is newer, checks its SHA-256, replaces the files that changed, and restarts when
the application says it is safe to do so.

It is general-purpose: any application on any channel can use it. The update source is an interface,
so a deployment that must not use a public repository can plug its own source and keep the same logic.

## Why it exists

It replaces an older updater that pulled files from a private HTTP directory server. That design had
three problems this library fixes:

| Old behaviour | Here |
|---|---|
| The update was decided by a **structure hash** alone. Any difference counted as an update, so an older build on the server **silently downgraded** a newer installation. | A real version comparison. An older or equal build is never applied. |
| The file hash was a rolling XOR seeded by file length: order-independent and easy to collide. | SHA-256, checked against the manifest before anything is written. |
| Files removed upstream stayed in the installation forever. | Optional pruning (`PruneRemovedFiles`). |

It also drops the old DNS pinning (rewriting the host to a resolved IP), which breaks TLS and is wrong
behind a CDN.

## Install

```
dotnet add package GitHubAppSync
```

Target: `.NET Standard 2.1`.

## Use it from a client

Start the background updater once, at startup:

```csharp
GitHubAppSync.Update.MonitoringUpdates(
    "Graphene-Lab/MyApp",   // owner/repo, "github:owner/repo", or a github.com URL
    () => myApp.IsIdle && !backupIsRunning,   // when the app may restart
    channel: "portable");
```

Or check once, on demand:

```csharp
var result = GitHubAppSync.Update.CheckAndUpdate(
    "Graphene-Lab/MyApp",
    () => myApp.IsIdle,
    channel: "portable");

if (result.Updated)
    Console.WriteLine($"Updated {result.CurrentVersion} -> {result.AvailableVersion}");
```

`StopMonitoringUpdates()` stops the timer.

### Options

```csharp
new UpdateOptions
{
    TargetDirectory     = AppDomain.CurrentDomain.BaseDirectory,  // what to update
    CurrentVersion      = Assembly.GetEntryAssembly().GetName().Version,
    ExcludedFileNames   = { "appsettings.json" },  // never overwritten (local config)
    PruneRemovedFiles   = false,   // delete files that no longer exist upstream
    FirstCheckDelay     = TimeSpan.FromHours(1),
    CheckInterval       = TimeSpan.FromHours(25),
    RestartAfterUpdate  = true,
    RestartAction       = null     // null = platform default
}
```

`RestartAction` default: on Windows the executable is relaunched; on Linux and macOS the process exits
cleanly so the service supervisor (systemd, launchd) restarts it.

### Result status

`Updated`, `AlreadyUpToDate`, `NoManifest`, `IntegrityFailure`, `Error`.

## Release assets and channels

Each release must carry two assets per channel. The name of the channel is the prefix.

```
portable.zip              portable-manifest.json
win-x64.zip               win-x64-manifest.json
linux-arm64.zip           linux-arm64-manifest.json
```

The manifest is small on purpose: the client reads it first and downloads the payload only when the
version is newer.

```json
{
  "channel": "portable",
  "version": "1.26.09.15",
  "asset": "portable.zip",
  "sha256": "…lowercase hex of the zip…",
  "generatedAtUtc": "2026-09-15T22:30:00Z"
}
```

The zip stores its files at the archive root, with no wrapping folder, because the client extracts it
and compares the relative paths against the installation directory.

### Why `releases/latest/download` and not the API

Assets are read through the stable redirect:

```
https://github.com/<owner>/<repo>/releases/latest/download/<channel>-manifest.json
```

That redirect is served by GitHub's web tier, not by the REST API. A fleet of clients polling for
updates therefore never consumes the unauthenticated API limit (60 requests per hour per IP), which
would be exhausted quickly behind a shared NAT.

## Producing the assets in CI

`tools/New-UpdateAsset.ps1` builds the zip and the manifest from a publish directory:

```powershell
pwsh -File tools/New-UpdateAsset.ps1 `
  -PublishDir ./publish `
  -Channel portable `
  -Version 1.26.09.15 `
  -OutDir ./dist
```

Then attach both files to the release, for example with `softprops/action-gh-release`:

```yaml
- run: pwsh -File tools/New-UpdateAsset.ps1 -PublishDir ./publish -Channel portable -Version '${{ env.VERSION }}' -OutDir ./dist
- uses: softprops/action-gh-release@v2
  with:
    tag_name: v${{ env.VERSION }}
    files: dist/*
```

## Custom update source

A deployment that must keep updates off a public repository implements `IUpdateSource` and passes it to
the same methods:

```csharp
public interface IUpdateSource
{
    Task<UpdateManifest> GetLatestManifestAsync(CancellationToken cancellationToken = default);
    Task DownloadAssetAsync(UpdateManifest manifest, string destinationFile, CancellationToken cancellationToken = default);
}
```

The integrity check runs in the pipeline after the download returns, not inside the transport, so a
custom source cannot skip it by forgetting to hash.

## Verification

A verifier exercises the pipeline against a local fake source, with no network:

```
dotnet run --project tests/Verify
```

It covers: no manifest, older build not applied (anti-downgrade), equal build not applied, newer build
applied with correct delta, tampered payload rejected with nothing written, local `appsettings.json`
preserved across an update, pruning on and off, and the JSON contract between the CI helper and the
client parser. It exits non-zero if any check fails.

## License

Andrea Bruno License 1.4 — see [LICENSE.md](LICENSE.md). It applies to this project and to all forks
and derivative works.
