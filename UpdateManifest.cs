using System;

namespace GitHubAppSync
{
    /// <summary>
    /// Describes the newest published build of one update channel.
    /// Continuous integration produces this as the release asset "&lt;channel&gt;-manifest.json".
    /// </summary>
    /// <remarks>
    /// The manifest is deliberately tiny (a few hundred bytes) so a client can decide whether an
    /// update exists without downloading the payload. The payload itself is a single zip asset,
    /// because GitHub release assets are flat and cannot mirror a nested application tree.
    /// </remarks>
    public sealed class UpdateManifest
    {
        /// <summary>The update channel this manifest belongs to (for example "portable" or "win-x64").</summary>
        public string Channel { get; set; }

        /// <summary>
        /// The published version, as a dotted number, optionally with a prerelease suffix
        /// (for example "1.26.09.15" or "1.26.09.15-beta").
        /// </summary>
        public string Version { get; set; }

        /// <summary>Release asset name holding the payload, resolved against the release download URL.</summary>
        public string Asset { get; set; }

        /// <summary>Lowercase hexadecimal SHA-256 of the payload asset.</summary>
        public string Sha256 { get; set; }

        /// <summary>When CI produced the manifest.</summary>
        public DateTime GeneratedAtUtc { get; set; }

        /// <summary>
        /// The version stripped of any prerelease suffix, ready for numeric comparison.
        /// Returns null when <see cref="Version"/> is missing or not a valid dotted number.
        /// </summary>
        public System.Version ParseVersion()
        {
            if (string.IsNullOrWhiteSpace(Version))
                return null;

            var core = Version.Trim();
            var separator = core.IndexOf('-');
            if (separator > 0)
                core = core.Substring(0, separator);

            return System.Version.TryParse(core, out var parsed) ? parsed : null;
        }

        /// <summary>
        /// Reads a manifest from its JSON form. Case-insensitive, comments and trailing commas
        /// tolerated, so a hand-written manifest behaves like a generated one.
        /// </summary>
        /// <exception cref="FormatException">The JSON is malformed.</exception>
        public static UpdateManifest FromJson(string json) => ManifestJson.Deserialize(json);

        /// <summary>Writes the manifest as indented camelCase JSON, the form CI publishes.</summary>
        public string ToJson() => ManifestJson.Serialize(this);
    }
}
