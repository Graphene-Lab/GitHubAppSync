namespace GitHubAppSync
{
    /// <summary>Outcome of an update check.</summary>
    public enum UpdateStatus
    {
        /// <summary>Files were replaced; a restart has been scheduled when the app becomes ready.</summary>
        Updated,

        /// <summary>The published build is not newer than the running one; nothing was changed.</summary>
        AlreadyUpToDate,

        /// <summary>The channel has no published manifest yet.</summary>
        NoManifest,

        /// <summary>A payload was downloaded but its SHA-256 did not match the manifest.</summary>
        IntegrityFailure,

        /// <summary>The manifest or the local installation could not be processed.</summary>
        Error
    }

    /// <summary>The result of <see cref="Update.CheckAndUpdate(IUpdateSource, System.Func{bool}, UpdateOptions)"/>.</summary>
    public sealed class UpdateResult
    {
        /// <summary>How the check ended.</summary>
        public UpdateStatus Status { get; }

        /// <summary>Human-readable detail; on error, the reason.</summary>
        public string Message { get; }

        /// <summary>Version the running application was at before the check.</summary>
        public string CurrentVersion { get; }

        /// <summary>Version the channel advertised, when a manifest was found.</summary>
        public string AvailableVersion { get; }

        /// <summary>True only when files were actually replaced.</summary>
        public bool Updated => Status == UpdateStatus.Updated;

        internal UpdateResult(UpdateStatus status, string message, string currentVersion, string availableVersion)
        {
            Status = status;
            Message = message;
            CurrentVersion = currentVersion;
            AvailableVersion = availableVersion;
        }

        /// <summary>Status and message on one line.</summary>
        public override string ToString() => Status + ": " + Message;
    }
}
