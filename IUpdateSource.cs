using System.Threading;
using System.Threading.Tasks;

namespace GitHubAppSync
{
    /// <summary>
    /// A source that can report the newest published build and download its payload.
    /// </summary>
    /// <remarks>
    /// <see cref="GitHubReleaseSource"/> is the implementation for public GitHub Releases.
    /// A deployment that must keep updates off a public repository implements this interface
    /// against its own host and passes it to <see cref="Update"/> instead — the update logic
    /// (version comparison, integrity check, staged apply, gated restart) is identical.
    /// </remarks>
    public interface IUpdateSource
    {
        /// <summary>
        /// Returns the manifest of the newest build available on the channel,
        /// or null when the channel has nothing published.
        /// </summary>
        Task<UpdateManifest> GetLatestManifestAsync(CancellationToken cancellationToken = default);

        /// <summary>
        /// Downloads the payload named by <paramref name="manifest"/> into <paramref name="destinationFile"/>.
        /// The file must not already exist, or must be overwritten by the implementation.
        /// </summary>
        Task DownloadAssetAsync(UpdateManifest manifest, string destinationFile, CancellationToken cancellationToken = default);
    }
}
