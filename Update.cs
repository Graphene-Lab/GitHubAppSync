using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Threading;
using System.Threading.Tasks;

namespace GitHubAppSync
{
    /// <summary>
    /// Checks a release channel for a newer build and applies it to the running installation.
    /// </summary>
    /// <remarks>
    /// The pipeline is: read the channel manifest, compare versions, download the payload and verify
    /// its SHA-256, extract it to a staging directory, diff it against the installation by file
    /// digest, move only the changed files in, then restart once the host application says it may.
    ///
    /// Two guards the previous updater did not have:
    /// <list type="bullet">
    ///   <item><description>A real version comparison, so an older build sitting on the channel can
    ///   never be applied over a newer installation. The old updater decided on a structure hash alone,
    ///   which meant any difference — including a rollback — counted as an "update".</description></item>
    ///   <item><description>A cryptographic digest of the payload, so a truncated or substituted
    ///   download is rejected before anything is written.</description></item>
    /// </list>
    /// </remarks>
    public static class Update
    {
        private static readonly object Gate = new object();
        private static Timer pollTimer;
        private static Timer restartTimer;
        private static Func<bool> rebootGate;

        /// <summary>
        /// Starts a background timer that keeps the installation up to date from a GitHub repository.
        /// </summary>
        /// <param name="repositorySpec">"owner/repo", "github:owner/repo" or a github.com URL.</param>
        /// <param name="isReadyForReboot">Returns true when the application may restart — idle, no backup running.</param>
        /// <param name="channel">Update channel selecting the published asset names.</param>
        /// <param name="options">Overrides the defaults; may be null.</param>
        public static void MonitoringUpdates(string repositorySpec, Func<bool> isReadyForReboot,
            string channel = "portable", UpdateOptions options = null)
            => MonitoringUpdates(GitHubReleaseSource.Parse(repositorySpec, channel), isReadyForReboot, options);

        /// <summary>Starts a background timer against an arbitrary update source.</summary>
        public static void MonitoringUpdates(IUpdateSource source, Func<bool> isReadyForReboot, UpdateOptions options = null)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            // A debugging session must not have its binaries replaced underneath it.
            if (Debugger.IsAttached)
                return;

            var effective = options ?? new UpdateOptions();

            lock (Gate)
            {
                // Dispose the previous monitor inside the same lock as the new one, so two
                // concurrent calls cannot both install a timer and orphan the first.
                pollTimer?.Dispose();
                pollTimer = new Timer(_ =>
                {
                    try
                    {
                        CheckAndUpdate(source, isReadyForReboot, effective);
                    }
                    catch
                    {
                        // A failed background check must never take the application down; the next
                        // tick retries.
                    }
                }, null, effective.FirstCheckDelay, effective.CheckInterval);
            }
        }

        /// <summary>Stops the background update timer, if one is running.</summary>
        public static void StopMonitoringUpdates()
        {
            lock (Gate)
            {
                pollTimer?.Dispose();
                pollTimer = null;

                // A restart armed by a previous update must stop too, otherwise the process would
                // still restart after monitoring was explicitly stopped.
                restartTimer?.Dispose();
                restartTimer = null;
                rebootGate = null;
            }
        }

        /// <summary>Synchronous form of <see cref="CheckAndUpdateAsync"/>, for parity with call sites that cannot await.</summary>
        public static UpdateResult CheckAndUpdate(string repositorySpec, Func<bool> isReadyForReboot,
            string channel = "portable", UpdateOptions options = null)
            => CheckAndUpdateAsync(GitHubReleaseSource.Parse(repositorySpec, channel), isReadyForReboot, options)
                .GetAwaiter().GetResult();

        /// <summary>Synchronous form of <see cref="CheckAndUpdateAsync"/>.</summary>
        public static UpdateResult CheckAndUpdate(IUpdateSource source, Func<bool> isReadyForReboot, UpdateOptions options = null)
            => CheckAndUpdateAsync(source, isReadyForReboot, options).GetAwaiter().GetResult();

        /// <summary>
        /// Checks the channel once and applies a newer build if one is published.
        /// </summary>
        public static async Task<UpdateResult> CheckAndUpdateAsync(
            IUpdateSource source,
            Func<bool> isReadyForReboot,
            UpdateOptions options = null,
            CancellationToken cancellationToken = default)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));

            var effective = options ?? new UpdateOptions();
            var target = new DirectoryInfo(effective.TargetDirectory);
            if (!target.Exists)
                return Result(UpdateStatus.Error, "The target directory does not exist: " + effective.TargetDirectory, null, null);

            UpdateManifest manifest;
            try
            {
                manifest = await source.GetLatestManifestAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return Result(UpdateStatus.Error, "Could not read the update manifest: " + ex.Message, null, null);
            }

            if (manifest == null)
                return Result(UpdateStatus.NoManifest, "No manifest published on this channel.", VersionText(effective.CurrentVersion), null);

            var available = manifest.ParseVersion();
            if (available == null)
                return Result(UpdateStatus.Error,
                    "The published version \"" + manifest.Version + "\" is not a comparable dotted number.",
                    VersionText(effective.CurrentVersion), manifest.Version);

            var current = effective.CurrentVersion;
            if (current == null)
                return Result(UpdateStatus.Error,
                    "The running version could not be determined; set UpdateOptions.CurrentVersion so " +
                    "the published version can be compared. Refusing to apply a version that cannot be " +
                    "checked against the running one.",
                    null, available.ToString());

            if (available <= current)
                return Result(UpdateStatus.AlreadyUpToDate,
                    "Published " + available + " is not newer than the running " + current + ".",
                    VersionText(current), available.ToString());

            var staging = Path.Combine(Path.GetTempPath(), "GitHubAppSync", Guid.NewGuid().ToString("N"));
            try
            {
                Directory.CreateDirectory(staging);
                var archive = Path.Combine(staging, Path.GetFileName(manifest.Asset ?? "update.zip"));

                try
                {
                    await source.DownloadAssetAsync(manifest, archive, cancellationToken).ConfigureAwait(false);
                }
                catch (UpdateIntegrityException ex)
                {
                    return Result(UpdateStatus.IntegrityFailure, ex.Message, VersionText(current), available.ToString());
                }

                // Verified here rather than inside the transport so every source is covered.
                // A manifest without a digest is rejected: applying an unverified payload would
                // bypass the integrity guarantee, so the digest is required, not optional.
                if (string.IsNullOrWhiteSpace(manifest.Sha256))
                {
                    TryDeleteFile(archive);
                    return Result(UpdateStatus.IntegrityFailure,
                        "The published manifest carries no SHA-256 for asset \"" + manifest.Asset +
                        "\"; refusing to apply an unverified payload.",
                        VersionText(current), available.ToString());
                }

                var actual = FileStructure.Sha256File(archive);
                if (!string.Equals(actual, manifest.Sha256.Trim(), StringComparison.OrdinalIgnoreCase))
                {
                    TryDeleteFile(archive);
                    return Result(UpdateStatus.IntegrityFailure,
                        "SHA-256 mismatch for asset \"" + manifest.Asset + "\": expected " +
                        manifest.Sha256.Trim().ToLowerInvariant() + ", computed " + actual +
                        ". Nothing was written.",
                        VersionText(current), available.ToString());
                }

                var payload = Path.Combine(staging, "payload");
                ZipFile.ExtractToDirectory(archive, payload);

                var incoming = FileStructure.FilesByPath(FileStructure.Create(new DirectoryInfo(payload), effective.ExcludedFileNames));
                var running = FileStructure.FilesByPath(FileStructure.Create(target, effective.ExcludedFileNames));

                var changed = FileStructure.ChangedPaths(incoming, running);
                if (changed.Count == 0)
                    return Result(UpdateStatus.AlreadyUpToDate,
                        "Version " + available + " is published but every file already matches.",
                        VersionText(current), available.ToString());

                var replaced = FileApplier.ApplyChanged(new DirectoryInfo(payload), target, changed);

                if (effective.PruneRemovedFiles)
                {
                    var removed = FileStructure.RemovedPaths(incoming, running);
                    if (removed.Count > 0)
                        FileApplier.Prune(target, removed, effective.ExcludedFileNames);
                }

                if (effective.RestartAfterUpdate && isReadyForReboot != null)
                    ScheduleRestart(isReadyForReboot, effective);

                return Result(UpdateStatus.Updated,
                    replaced + " file(s) updated to " + available + ".",
                    VersionText(current), available.ToString());
            }
            finally
            {
                TryDeleteTree(staging);
            }
        }

        /// <summary>
        /// Polls the readiness callback until the application allows a restart, then restarts it.
        /// </summary>
        private static void ScheduleRestart(Func<bool> ready, UpdateOptions options)
        {
            lock (Gate)
            {
                rebootGate = ready;
                restartTimer?.Dispose();

                Timer timer = null;
                timer = new Timer(_ =>
                {
                    bool ok;
                    try
                    {
                        ok = ready?.Invoke() == true;
                    }
                    catch
                    {
                        ok = false;
                    }

                    if (!ok)
                        return;

                    // Claim the restart under the lock: only act if this timer is still the armed
                    // one, so a concurrent ScheduleRestart/StopMonitoringUpdates is never clobbered.
                    lock (Gate)
                    {
                        if (!ReferenceEquals(restartTimer, timer))
                            return;
                        restartTimer = null;
                        rebootGate = null;
                    }

                    timer.Dispose();

                    try
                    {
                        if (options?.RestartAction != null)
                            options.RestartAction();
                        else
                            ProcessRestarter.Restart();
                    }
                    catch
                    {
                        // If the restart itself fails the files are already updated; the next
                        // launch picks them up.
                    }
                }, null, TimeSpan.FromSeconds(5), TimeSpan.FromMinutes(1));

                restartTimer = timer;
            }
        }

        private static UpdateResult Result(UpdateStatus status, string message, string current, string available)
            => new UpdateResult(status, message, current, available);

        private static string VersionText(Version version) => version?.ToString();

        private static void TryDeleteFile(string path)
        {
            try
            {
                if (File.Exists(path))
                    File.Delete(path);
            }
            catch
            {
                // Best effort: the caller already knows the payload is unusable.
            }
        }

        private static void TryDeleteTree(string path)
        {
            try
            {
                if (Directory.Exists(path))
                    Directory.Delete(path, true);
            }
            catch
            {
                // Best effort on a temp directory.
            }
        }
    }
}
