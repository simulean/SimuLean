namespace SimuLean.Unity
{
    /// <summary>
    /// The prebuilt runners attached to this SimuLean release: <c>SimuLeanRunner-&lt;version&gt;-&lt;rid&gt;.zip</c>, each holding
    /// <c>SimuLeanRunner/&lt;rid&gt;/simulean[.exe]</c> for the project root. <see cref="ExternalRunner"/> downloads the one of this
    /// machine on first use, but only while the project's engine is the release's own (<see cref="EngineFingerprint"/>):
    /// a changed engine is built here instead, so a download never brings a runner that would be refused. The release
    /// export checks both values against the exported tree (its version is the one of <c>CITATION.cff</c>), so they are set
    /// when a release is made.
    /// </summary>
    public static class RunnerRelease
    {
        /// <summary>The release these runners belong to (the tag is <c>v</c> + it).</summary>
        public const string Version = "1.3.0";

        /// <summary>The engine fingerprint of the release (<see cref="SimuLean.Serialization.EngineFingerprint"/>).</summary>
        public const string EngineFingerprint = "bf7b2fbeed343573";

        /// <summary>The public repository whose releases carry the runners.</summary>
        public const string Repository = "https://github.com/simulean/SimuLean";

        /// <summary>The platforms a release ships a runner for (others build it with a .NET 10 SDK).</summary>
        public static readonly string[] Rids = { "win-x64", "osx-arm64", "linux-x64" };

        /// <summary>The archive's name for <paramref name="rid"/>.</summary>
        public static string ArchiveName(string rid) => $"SimuLeanRunner-{Version}-{rid}.zip";

        /// <summary>Where the archive for <paramref name="rid"/> is downloaded from.</summary>
        public static string Url(string rid) => $"{Repository}/releases/download/v{Version}/{ArchiveName(rid)}";
    }
}
