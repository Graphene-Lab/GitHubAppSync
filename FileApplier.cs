using System;
using System.Collections.Generic;
using System.IO;

namespace GitHubAppSync
{
    /// <summary>
    /// Moves staged files into the installation, replacing what is already there.
    /// </summary>
    /// <remarks>
    /// A file that is in use cannot be overwritten in place on Windows, so the existing copy is
    /// moved out of the way rather than renamed inside the application folder. The previous updater
    /// renamed it to "_name" in place; because its own directory scanner skipped names starting with
    /// '_', those backups were invisible to it and accumulated on every update forever. Parking them
    /// in the system temp directory keeps the installation clean and lets the OS collect them.
    /// </remarks>
    internal static class FileApplier
    {
        /// <summary>
        /// Copies the listed relative paths from <paramref name="staged"/> into <paramref name="target"/>,
        /// replacing existing files. Returns the number of files replaced.
        /// </summary>
        internal static int ApplyChanged(DirectoryInfo staged, DirectoryInfo target, IEnumerable<string> relativePaths)
        {
            var replaced = 0;
            // target path -> parked original, so a mid-way failure can be rolled back.
            var applied = new List<KeyValuePair<string, string>>();

            try
            {
                foreach (var relativePath in relativePaths)
                {
                    var sourceFile = new FileInfo(Combine(staged.FullName, relativePath));
                    if (!sourceFile.Exists)
                        continue;

                    var targetFile = new FileInfo(Combine(target.FullName, relativePath));
                    targetFile.Directory?.Create();

                    var parked = targetFile.Exists ? Park(targetFile) : null;

                    sourceFile.CopyTo(targetFile.FullName, true);

                    if (parked != null)
                        applied.Add(new KeyValuePair<string, string>(targetFile.FullName, parked));
                    replaced++;
                }

                return replaced;
            }
            catch
            {
                // A copy failed partway (disk full, a file that cannot be replaced). Restore every
                // file already moved in from its parked original so the install is never left a mix
                // of new and old binaries, then rethrow so the caller reports the failure.
                foreach (var pair in applied)
                {
                    try { File.Copy(pair.Value, pair.Key, true); }
                    catch { /* best effort — the original is still parked in temp */ }
                }

                throw;
            }
        }

        /// <summary>
        /// Deletes local files that the published payload no longer contains.
        /// Excluded file names are never removed, so local configuration survives pruning.
        /// </summary>
        internal static int Prune(DirectoryInfo target, IEnumerable<string> relativePaths, ISet<string> excludedFileNames)
        {
            var removed = 0;

            foreach (var relativePath in relativePaths)
            {
                if (excludedFileNames != null &&
                    excludedFileNames.Contains(Path.GetFileName(relativePath)))
                    continue;

                var file = new FileInfo(Combine(target.FullName, relativePath));
                if (!file.Exists)
                    continue;

                try
                {
                    file.Delete();
                    removed++;
                }
                catch (IOException)
                {
                    // Locked file: leave it. A stale file is preferable to a failed update.
                }
                catch (UnauthorizedAccessException)
                {
                    // Read-only or access-denied file: leave it, same as a locked file.
                }
            }

            return removed;
        }

        private static string Combine(string root, string relativePath)
        {
            // Defense-in-depth against path traversal: the relative paths come from the snapshot
            // (always relative, no ".."), but canonicalize and assert the result stays under the
            // target so a future caller feeding raw zip-entry names cannot escape the install dir.
            var combined = Path.GetFullPath(
                Path.Combine(root, relativePath.Replace('/', Path.DirectorySeparatorChar)));
            var rootFull = Path.GetFullPath(root);
            var rootPrefix = rootFull.EndsWith(Path.DirectorySeparatorChar)
                ? rootFull
                : rootFull + Path.DirectorySeparatorChar;

            if (!combined.StartsWith(rootPrefix, StringComparison.Ordinal) &&
                !string.Equals(combined, rootFull, StringComparison.Ordinal))
                throw new IOException("Refusing to touch a path outside the target directory: " + relativePath);

            return combined;
        }

        /// <summary>
        /// Moves an existing file out of the installation so the new one can take its place, and
        /// returns the path it was parked at (so the caller can roll the move back). Falls back to
        /// leaving it in place under a ".old" backup name only when it cannot be moved to temp at
        /// all, which is rarer than deleting a locked file succeeding on Windows.
        /// </summary>
        private static string Park(FileInfo existing)
        {
            var parked = Path.Combine(
                Path.GetTempPath(),
                "GitHubAppSync-parked",
                DateTime.UtcNow.ToString("yyyyMMddHHmmssfff") + "-" + Guid.NewGuid().ToString("N").Substring(0, 8) + "-" + existing.Name);

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(parked));
                // The parked path is unique, so no overwrite semantics are needed — which matters,
                // because netstandard2.1 does not offer File.Move with overwrite.
                File.Move(existing.FullName, parked);
                return parked;
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }

            var fallback = existing.FullName + ".old";
            try
            {
                if (File.Exists(fallback))
                    File.Delete(fallback);
                File.Move(existing.FullName, fallback);
            }
            catch (Exception ex)
            {
                throw new IOException(
                    "Could not move the existing file \"" + existing.FullName + "\" out of the way to replace it.", ex);
            }

            return fallback;
        }
    }
}
