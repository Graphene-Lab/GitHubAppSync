using System;
using System.Collections.Generic;
using System.Reflection;

namespace GitHubAppSync
{
    /// <summary>
    /// Thrown when a downloaded payload does not match the digest published in the manifest.
    /// </summary>
    public sealed class UpdateIntegrityException : Exception
    {
        /// <summary>Creates the exception with the mismatch description.</summary>
        public UpdateIntegrityException(string message) : base(message) { }

        /// <summary>Creates the exception wrapping a cause.</summary>
        public UpdateIntegrityException(string message, Exception inner) : base(message, inner) { }
    }

    /// <summary>Controls where the updater acts, what it leaves alone, and when it restarts.</summary>
    public sealed class UpdateOptions
    {
        /// <summary>
        /// The installation directory to update. Defaults to the directory the executable lives in,
        /// which is the portable layout the updater was designed for.
        /// </summary>
        public string TargetDirectory { get; set; } = AppDomain.CurrentDomain.BaseDirectory;

        /// <summary>
        /// The version to compare the published version against. Defaults to the entry assembly's
        /// version, so an application built with the same version scheme as its releases needs no
        /// configuration. Set it explicitly when the running version comes from somewhere else.
        /// </summary>
        public Version CurrentVersion { get; set; } = ReadEntryAssemblyVersion();

        /// <summary>
        /// File names never overwritten, whatever the update contains. Local configuration must
        /// survive an update, so <c>appsettings.json</c> is excluded by default.
        /// Comparison is by file name, case-insensitive, at any depth.
        /// </summary>
        public ISet<string> ExcludedFileNames { get; } =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "appsettings.json" };

        /// <summary>
        /// When true, files present in the installation but absent from the published payload are
        /// deleted. The previous updater never removed anything, so files deleted upstream
        /// accumulated forever. Off by default because pruning an unknown installation is the one
        /// irreversible action the updater can take.
        /// </summary>
        public bool PruneRemovedFiles { get; set; } = false;

        /// <summary>Delay before the first automatic check.</summary>
        public TimeSpan FirstCheckDelay { get; set; } = TimeSpan.FromHours(1);

        /// <summary>Interval between automatic checks.</summary>
        public TimeSpan CheckInterval { get; set; } = TimeSpan.FromHours(25);

        /// <summary>
        /// When true (default) a restart is scheduled after a successful update, and is performed
        /// only once <c>isReadyForReboot</c> reports the application can restart.
        /// </summary>
        public bool RestartAfterUpdate { get; set; } = true;

        /// <summary>
        /// Overrides how the application restarts. When null the platform default is used:
        /// relaunch the executable on Windows, exit cleanly on Linux and macOS so the service
        /// supervisor restarts it.
        /// </summary>
        public Action RestartAction { get; set; }

        private static Version ReadEntryAssemblyVersion()
        {
            try
            {
                return Assembly.GetEntryAssembly()?.GetName()?.Version;
            }
            catch
            {
                // Some hosts expose no entry assembly; the caller can supply CurrentVersion.
                return null;
            }
        }
    }
}
