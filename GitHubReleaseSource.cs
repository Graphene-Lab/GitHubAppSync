using System;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace GitHubAppSync
{
    /// <summary>
    /// Update source backed by the releases of a public GitHub repository.
    /// </summary>
    /// <remarks>
    /// Assets are resolved through the stable redirect
    /// <c>https://github.com/{owner}/{repo}/releases/latest/download/{asset}</c>.
    /// That redirect is served by GitHub's web tier, not by the REST API, so a fleet of clients
    /// polling for updates never consumes the unauthenticated API rate limit (60 requests per hour
    /// per IP), which would otherwise be exhausted quickly behind a shared NAT.
    /// The redirect is followed over TLS; unlike the previous private-server updater this does not
    /// rewrite the host to a resolved IP address, which would break certificate validation.
    /// </remarks>
    public sealed class GitHubReleaseSource : IUpdateSource
    {
        private const string ManifestSuffix = "-manifest.json";
        private const string DefaultChannel = "portable";

        private static readonly HttpClient Http = new HttpClient(new HttpClientHandler
        {
            AllowAutoRedirect = true,
            AutomaticDecompression = DecompressionMethods.None
        })
        {
            Timeout = TimeSpan.FromMinutes(10)
        };

        /// <summary>GitHub organisation or user that owns the repository.</summary>
        public string Owner { get; }

        /// <summary>GitHub repository name.</summary>
        public string Repository { get; }

        /// <summary>Update channel; selects the asset names this source looks for.</summary>
        public string Channel { get; }

        /// <summary>Creates a source for the given owner, repository and channel.</summary>
        public GitHubReleaseSource(string owner, string repository, string channel = DefaultChannel)
        {
            if (string.IsNullOrWhiteSpace(owner))
                throw new ArgumentException("Owner is required.", nameof(owner));
            if (string.IsNullOrWhiteSpace(repository))
                throw new ArgumentException("Repository is required.", nameof(repository));

            Owner = owner.Trim();
            Repository = repository.Trim();
            Channel = string.IsNullOrWhiteSpace(channel) ? DefaultChannel : channel.Trim();
        }

        /// <summary>
        /// Creates a source from a compact repository specification: <c>owner/repo</c>,
        /// <c>github:owner/repo</c> or <c>https://github.com/owner/repo</c>.
        /// </summary>
        public static GitHubReleaseSource Parse(string repositorySpec, string channel = DefaultChannel)
        {
            if (string.IsNullOrWhiteSpace(repositorySpec))
                throw new ArgumentException("Repository specification is required.", nameof(repositorySpec));

            var spec = repositorySpec.Trim();

            if (spec.StartsWith("github:", StringComparison.OrdinalIgnoreCase))
                spec = spec.Substring("github:".Length);

            if (Uri.TryCreate(spec, UriKind.Absolute, out var uri))
                spec = uri.AbsolutePath;

            spec = spec.Trim('/');

            var parts = spec.Split('/');
            if (parts.Length < 2 || string.IsNullOrEmpty(parts[0]) || string.IsNullOrEmpty(parts[1]))
                throw new FormatException(
                    "Expected \"owner/repo\", \"github:owner/repo\" or \"https://github.com/owner/repo\" but got \"" +
                    repositorySpec + "\".");

            return new GitHubReleaseSource(parts[0], parts[1], channel);
        }

        /// <summary>URL of the channel manifest asset.</summary>
        public string ManifestUrl => DownloadBase + Channel + ManifestSuffix;

        /// <summary>Builds the download URL of an asset on the newest release of the channel.</summary>
        public string AssetUrl(string assetName) => DownloadBase + assetName;

        private string DownloadBase =>
            "https://github.com/" + Owner + "/" + Repository + "/releases/latest/download/";

        /// <inheritdoc />
        public async Task<UpdateManifest> GetLatestManifestAsync(CancellationToken cancellationToken = default)
        {
            using var response = await Http.GetAsync(ManifestUrl, HttpCompletionOption.ResponseContentRead, cancellationToken)
                .ConfigureAwait(false);

            // A channel that has never been published simply has no manifest.
            if (response.StatusCode == HttpStatusCode.NotFound)
                return null;

            response.EnsureSuccessStatusCode();

            var json = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
            return ManifestJson.Deserialize(json);
        }

        /// <inheritdoc />
        /// <remarks>
        /// This transport only moves bytes. The digest check against the manifest is performed by
        /// <see cref="Update"/> after the download returns, so that integrity is a property of the
        /// update pipeline and not of one transport: a custom <see cref="IUpdateSource"/> cannot
        /// accidentally skip it by forgetting to hash.
        /// </remarks>
        public async Task DownloadAssetAsync(UpdateManifest manifest, string destinationFile, CancellationToken cancellationToken = default)
        {
            if (manifest == null) throw new ArgumentNullException(nameof(manifest));
            if (string.IsNullOrWhiteSpace(manifest.Asset))
                throw new InvalidOperationException("The manifest does not name a payload asset.");

            var directory = Path.GetDirectoryName(Path.GetFullPath(destinationFile));
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using var response = await Http.GetAsync(AssetUrl(manifest.Asset), HttpCompletionOption.ResponseHeadersRead, cancellationToken)
                .ConfigureAwait(false);
            response.EnsureSuccessStatusCode();

            using var source = await response.Content.ReadAsStreamAsync().ConfigureAwait(false);
            using var target = new FileStream(destinationFile, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 16);

            var buffer = new byte[1 << 16];
            while (true)
            {
                var read = await source.ReadAsync(buffer, 0, buffer.Length, cancellationToken).ConfigureAwait(false);
                if (read <= 0)
                    break;

                await target.WriteAsync(buffer, 0, read, cancellationToken).ConfigureAwait(false);
            }

            await target.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
    }
}
