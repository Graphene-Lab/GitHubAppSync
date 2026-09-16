using System;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using GitHubAppSync;

namespace GitHubAppSync.Verify
{
    /// <summary>
    /// Exercises the update pipeline against a local fake source, with no network involved.
    /// Run with: dotnet run --project tests/Verify
    /// Exits 0 when every scenario passes.
    /// </summary>
    internal static class Program
    {
        private static int passed;
        private static int failed;

        private static int Main()
        {
            var root = Path.Combine(Path.GetTempPath(), "GitHubAppSync-verify-" + Guid.NewGuid().ToString("N").Substring(0, 8));
            Directory.CreateDirectory(root);

            try
            {
                NoManifestLeavesInstallAlone(root);
                OlderBuildIsNotApplied(root);
                EqualBuildIsNotApplied(root);
                NewerBuildIsApplied(root);
                TamperedPayloadIsRejected(root);
                LocalConfigurationSurvivesUpdate(root);
                PruneRemovesOnlyFilesAbsentUpstream(root);
                ManifestWireFormatMatchesTheCiHelper(root);
            }
            catch (Exception ex)
            {
                Console.WriteLine("UNEXPECTED: " + ex);
                failed++;
            }
            finally
            {
                try { Directory.Delete(root, true); } catch { /* temp */ }
            }

            Console.WriteLine();
            Console.WriteLine("passed " + passed + "   failed " + failed);
            return failed == 0 ? 0 : 1;
        }

        // ---- scenarios -------------------------------------------------------

        private static void NoManifestLeavesInstallAlone(string root)
        {
            var install = MakeInstall(root, "none", ("app.dll", "v1"));
            var source = new FakeSource(null, null);

            var result = Update.CheckAndUpdate(source, AlwaysReady, Options(install, "1.0.0.0"));

            Assert(result.Status == UpdateStatus.NoManifest, "no manifest -> NoManifest", result);
            Assert(Read(Path.Combine(install, "app.dll")) == "v1", "no manifest -> install untouched");
        }

        private static void OlderBuildIsNotApplied(string root)
        {
            var install = MakeInstall(root, "older", ("app.dll", "current-build"));
            var payload = MakePayload(root, "older", ("app.dll", "OLD-BUILD"));

            // The running app is newer than what the channel advertises.
            var result = Update.CheckAndUpdate(
                new FakeSource(payload.Manifest, payload.Zip), AlwaysReady,
                Options(install, "1.26.09.20"));

            Assert(result.Status == UpdateStatus.AlreadyUpToDate, "older remote -> AlreadyUpToDate", result);
            Assert(Read(Path.Combine(install, "app.dll")) == "current-build",
                "older remote -> no downgrade written");
        }

        private static void EqualBuildIsNotApplied(string root)
        {
            var install = MakeInstall(root, "equal", ("app.dll", "same"));
            var payload = MakePayload(root, "equal", ("app.dll", "same"));

            var result = Update.CheckAndUpdate(
                new FakeSource(payload.Manifest, payload.Zip), AlwaysReady,
                Options(install, "1.26.09.15"));

            Assert(result.Status == UpdateStatus.AlreadyUpToDate, "equal version -> AlreadyUpToDate", result);
        }

        private static void NewerBuildIsApplied(string root)
        {
            var install = MakeInstall(root, "newer", ("app.dll", "old-content"), ("keep.dll", "unchanged"));
            var payload = MakePayload(root, "newer", ("app.dll", "new-content"), ("keep.dll", "unchanged"), ("added.dll", "brand-new"));

            var result = Update.CheckAndUpdate(
                new FakeSource(payload.Manifest, payload.Zip), AlwaysReady,
                Options(install, "1.26.09.10"));

            Assert(result.Status == UpdateStatus.Updated, "newer remote -> Updated", result);
            Assert(Read(Path.Combine(install, "app.dll")) == "new-content", "changed file replaced");
            Assert(Read(Path.Combine(install, "keep.dll")) == "unchanged", "identical file left alone");
            Assert(Read(Path.Combine(install, "added.dll")) == "brand-new", "new file added");
            Assert(result.AvailableVersion == "1.26.9.15", "result reports the published version", result.AvailableVersion);
        }

        private static void TamperedPayloadIsRejected(string root)
        {
            var install = MakeInstall(root, "tamper", ("app.dll", "good"));
            var payload = MakePayload(root, "tamper", ("app.dll", "malicious"));

            // Simulate a substituted or truncated download: the manifest digest no longer matches.
            payload.Manifest.Sha256 = new string('a', 64);

            var result = Update.CheckAndUpdate(
                new FakeSource(payload.Manifest, payload.Zip), AlwaysReady,
                Options(install, "1.0.0.0"));

            Assert(result.Status == UpdateStatus.IntegrityFailure, "bad digest -> IntegrityFailure", result);
            Assert(Read(Path.Combine(install, "app.dll")) == "good", "bad digest -> nothing written to install");
        }

        private static void LocalConfigurationSurvivesUpdate(string root)
        {
            var install = MakeInstall(root, "config", ("app.dll", "old"), ("appsettings.json", "{\"Local\":true}"));
            var payload = MakePayload(root, "config", ("app.dll", "new"), ("appsettings.json", "{\"Local\":false}"));

            var result = Update.CheckAndUpdate(
                new FakeSource(payload.Manifest, payload.Zip), AlwaysReady,
                Options(install, "1.0.0.0"));

            Assert(result.Status == UpdateStatus.Updated, "config scenario -> Updated", result);
            Assert(Read(Path.Combine(install, "app.dll")) == "new", "config scenario -> binary updated");
            Assert(Read(Path.Combine(install, "appsettings.json")) == "{\"Local\":true}",
                "appsettings.json keeps the local value");
        }

        private static void PruneRemovesOnlyFilesAbsentUpstream(string root)
        {
            var keep = MakeInstall(root, "prune-off", ("app.dll", "old"), ("orphan.txt", "leftover"));
            var payload = MakePayload(root, "prune", ("app.dll", "new"));

            var off = Update.CheckAndUpdate(
                new FakeSource(payload.Manifest, payload.Zip), AlwaysReady,
                Options(keep, "1.0.0.0"));
            Assert(off.Status == UpdateStatus.Updated, "prune off -> Updated", off);
            Assert(File.Exists(Path.Combine(keep, "orphan.txt")), "prune off -> orphan kept (default is non-destructive)");

            var on = MakeInstall(root, "prune-on", ("app.dll", "old"), ("orphan.txt", "leftover"));
            var pruneOptions = Options(on, "1.0.0.0");
            pruneOptions.PruneRemovedFiles = true;

            var result = Update.CheckAndUpdate(
                new FakeSource(payload.Manifest, payload.Zip), AlwaysReady, pruneOptions);

            Assert(result.Status == UpdateStatus.Updated, "prune on -> Updated", result);
            Assert(!File.Exists(Path.Combine(on, "orphan.txt")), "prune on -> orphan removed");
            Assert(Read(Path.Combine(on, "app.dll")) == "new", "prune on -> updated file still applied");
        }

        // ---- helpers ---------------------------------------------------------

        /// <summary>
        /// Guards the contract between the CI helper (which emits camelCase JSON from PowerShell)
        /// and the client parser. If a property name drifts, this fails instead of a silent
        /// null version at the anti-downgrade check.
        /// </summary>
        private static void ManifestWireFormatMatchesTheCiHelper(string root)
        {
            const string emittedByCiHelper = @"{
  ""channel"": ""portable"",
  ""version"": ""1.26.09.15"",
  ""asset"": ""portable.zip"",
  ""sha256"": ""0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"",
  ""generatedAtUtc"": ""2026-09-15T22:30:00Z""
}";

            var manifest = UpdateManifest.FromJson(emittedByCiHelper);

            Assert(manifest.Channel == "portable", "wire -> channel", manifest.Channel);
            Assert(manifest.Version == "1.26.09.15", "wire -> version", manifest.Version);
            Assert(manifest.Asset == "portable.zip", "wire -> asset", manifest.Asset);
            Assert(manifest.Sha256.StartsWith("0123456789abcdef"), "wire -> sha256", manifest.Sha256);
            Assert(manifest.GeneratedAtUtc.Year == 2026 && manifest.GeneratedAtUtc.Hour == 22,
                "wire -> generatedAtUtc", manifest.GeneratedAtUtc.ToString("o"));
            Assert(manifest.ParseVersion() == new Version(1, 26, 9, 15),
                "wire -> comparable version", manifest.ParseVersion()?.ToString());

            // A prerelease suffix must still compare on its numeric core.
            var prerelease = UpdateManifest.FromJson(emittedByCiHelper.Replace("1.26.09.15", "1.26.09.15-beta"));
            Assert(prerelease.ParseVersion() == new Version(1, 26, 9, 15),
                "prerelease suffix stripped for comparison", prerelease.ParseVersion()?.ToString());
        }

        private static Func<bool> AlwaysReady => () => true;

        private static UpdateOptions Options(string target, string currentVersion) => new UpdateOptions
        {
            TargetDirectory = target,
            CurrentVersion = Version.Parse(currentVersion),
            RestartAfterUpdate = false
        };

        private static string MakeInstall(string root, string tag, params (string Name, string Content)[] files)
        {
            var dir = Path.Combine(root, "install-" + tag);
            Directory.CreateDirectory(dir);
            Write(dir, files);
            return dir;
        }

        private static (UpdateManifest Manifest, string Zip) MakePayload(
            string root, string tag, params (string Name, string Content)[] files)
        {
            var dir = Path.Combine(root, "payload-" + tag);
            Directory.CreateDirectory(dir);
            Write(dir, files);

            var zip = Path.Combine(root, tag + ".zip");
            if (File.Exists(zip))
                File.Delete(zip);
            ZipFile.CreateFromDirectory(dir, zip);

            var manifest = new UpdateManifest
            {
                Channel = "portable",
                Version = "1.26.09.15",
                Asset = Path.GetFileName(zip),
                Sha256 = Sha256(zip),
                GeneratedAtUtc = DateTime.UtcNow
            };
            return (manifest, zip);
        }

        private static void Write(string dir, (string Name, string Content)[] files)
        {
            foreach (var file in files)
            {
                var path = Path.Combine(dir, file.Name);
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent))
                    Directory.CreateDirectory(parent);
                File.WriteAllText(path, file.Content);
            }
        }

        private static string Read(string path) =>
            File.Exists(path) ? File.ReadAllText(path) : null;

        private static string Sha256(string path)
        {
            using var stream = File.OpenRead(path);
            using var hash = SHA256.Create();
            var bytes = hash.ComputeHash(stream);
            var builder = new System.Text.StringBuilder(bytes.Length * 2);
            foreach (var b in bytes)
                builder.Append(b.ToString("x2"));
            return builder.ToString();
        }

        private static void Assert(bool condition, string label, object evidence = null)
        {
            if (condition)
            {
                passed++;
                Console.WriteLine("  ok   " + label);
            }
            else
            {
                failed++;
                Console.WriteLine("  FAIL " + label + (evidence == null ? "" : "   [" + evidence + "]"));
            }
        }
    }

    /// <summary>Serves a prepared manifest and payload from disk, standing in for GitHub Releases.</summary>
    internal sealed class FakeSource : IUpdateSource
    {
        private readonly UpdateManifest manifest;
        private readonly string zipPath;

        internal FakeSource(UpdateManifest manifest, string zipPath)
        {
            this.manifest = manifest;
            this.zipPath = zipPath;
        }

        public Task<UpdateManifest> GetLatestManifestAsync(CancellationToken cancellationToken = default)
            => Task.FromResult(manifest);

        public Task DownloadAssetAsync(UpdateManifest manifest, string destinationFile, CancellationToken cancellationToken = default)
        {
            File.Copy(zipPath, destinationFile, true);
            return Task.CompletedTask;
        }
    }
}
