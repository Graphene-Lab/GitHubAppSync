using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

namespace GitHubAppSync
{
    /// <summary>One entry of a directory snapshot.</summary>
    internal sealed class FileEntry
    {
        internal string RelativePath;
        internal string Sha256;
        internal bool IsDirectory;
    }

    /// <summary>
    /// Snapshots a directory tree with a SHA-256 per file and diffs two snapshots.
    /// </summary>
    /// <remarks>
    /// This replaces the previous updater's hash, which was a rolling XOR seeded by file length:
    /// order-independent and trivially collidable, adequate for "did this file change" but not for
    /// "are these the bytes CI produced". Here the same snapshot drives the delta, and the digest
    /// is one a client can actually trust for a single file.
    /// </remarks>
    internal static class FileStructure
    {
        /// <summary>
        /// Walks <paramref name="root"/> and returns one entry per file and directory.
        /// Hidden entries, entries whose name starts with '_' (the staging backups the apply step
        /// leaves behind) and <paramref name="excludedFileNames"/> are skipped.
        /// </summary>
        internal static List<FileEntry> Create(DirectoryInfo root, ISet<string> excludedFileNames)
        {
            var entries = new List<FileEntry>();
            Walk(root, root.FullName.Length + 1, excludedFileNames, entries);
            return entries;
        }

        private static void Walk(DirectoryInfo directory, int rootLength, ISet<string> excludedFileNames, List<FileEntry> output)
        {
            foreach (var sub in directory.GetDirectories())
            {
                if (IsSkipped(sub.Name, sub.Attributes))
                    continue;

                output.Add(new FileEntry
                {
                    RelativePath = Normalize(sub.FullName.Substring(rootLength)),
                    IsDirectory = true
                });

                Walk(sub, rootLength, excludedFileNames, output);
            }

            foreach (var file in directory.GetFiles())
            {
                if (IsSkipped(file.Name, file.Attributes))
                    continue;
                if (excludedFileNames != null && excludedFileNames.Contains(file.Name))
                    continue;

                output.Add(new FileEntry
                {
                    RelativePath = Normalize(file.FullName.Substring(rootLength)),
                    Sha256 = HashFile(file),
                    IsDirectory = false
                });
            }
        }

        private static bool IsSkipped(string name, FileAttributes attributes)
        {
            if ((attributes & FileAttributes.Hidden) != 0)
                return true;

            // '_' is the prefix the apply step uses to park a file it could not delete while locked.
            return name.StartsWith("_", StringComparison.Ordinal);
        }

        private static string Normalize(string relativePath) => relativePath.Replace('\\', '/');

        private static string HashFile(FileInfo file) => Sha256File(file.FullName);

        /// <summary>SHA-256 of a file, lowercase hex.</summary>
        internal static string Sha256File(string path)
        {
            using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var hash = SHA256.Create();
            return ToHex(hash.ComputeHash(stream));
        }

        private static string ToHex(byte[] bytes)
        {
            var builder = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                builder.Append(b.ToString("x2"));
            return builder.ToString();
        }

        /// <summary>Maps relative path to entry, ignoring directories.</summary>
        internal static Dictionary<string, FileEntry> FilesByPath(List<FileEntry> entries)
        {
            var map = new Dictionary<string, FileEntry>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in entries)
                if (!entry.IsDirectory)
                    map[entry.RelativePath] = entry;
            return map;
        }

        /// <summary>Relative paths of files that are new or whose content differs.</summary>
        internal static List<string> ChangedPaths(
            Dictionary<string, FileEntry> incoming,
            Dictionary<string, FileEntry> current)
        {
            var changed = new List<string>();
            foreach (var pair in incoming)
            {
                if (current.TryGetValue(pair.Key, out var existing) &&
                    string.Equals(existing.Sha256, pair.Value.Sha256, StringComparison.OrdinalIgnoreCase))
                    continue;

                changed.Add(pair.Key);
            }
            return changed;
        }

        /// <summary>Relative paths of files present locally but absent from the incoming payload.</summary>
        internal static List<string> RemovedPaths(
            Dictionary<string, FileEntry> incoming,
            Dictionary<string, FileEntry> current)
        {
            var removed = new List<string>();
            foreach (var pair in current)
                if (!incoming.ContainsKey(pair.Key))
                    removed.Add(pair.Key);
            return removed;
        }
    }
}
