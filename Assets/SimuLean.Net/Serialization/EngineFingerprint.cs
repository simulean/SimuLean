using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;

namespace SimuLean.Serialization
{
    /// <summary>
    /// The engine's fingerprint: a hash of the engine's sources, so the editor can tell whether an
    /// out-of-editor runner was built from the same engine it runs (a stale runner is never used). SHA-256 over every
    /// <c>*.cs</c> under the engine folder, in ordinal order of the path relative to it (with <c>/</c>): the path, a line
    /// feed, the text with every carriage return removed (a checkout with CRLF or LF gives the same value) and a line feed;
    /// the first 16 hexadecimal digits. The runner's build computes the same (<c>Tools/Runner/Fingerprint.targets</c>);
    /// <c>runnertests</c> checks that both agree. The choice rules (<c>Assets/SimuLean.UserLogic</c>) are
    /// compiled into the runner too, so their sources follow the engine's, as <c>UserLogic/&lt;path&gt;</c> (a project without
    /// the folder keeps the engine-only value).
    /// </summary>
    public static class EngineFingerprint
    {
        /// <summary>The fingerprint of the engine sources under <paramref name="engineRoot"/> (e.g. <c>Assets/SimuLean.Net</c>).</summary>
        public static string OfSources(string engineRoot, string userLogicRoot = null)
        {
            if (!Directory.Exists(engineRoot)) throw new DirectoryNotFoundException($"No engine folder '{engineRoot}'.");
            var files = Files(engineRoot, "").OrderBy(f => f.rel, StringComparer.Ordinal).ToList();
            if (userLogicRoot != null && Directory.Exists(userLogicRoot))
                files.AddRange(Files(userLogicRoot, "UserLogic/").OrderBy(f => f.rel, StringComparer.Ordinal));
            using (var sha = SHA256.Create())
            {
                foreach (var (rel, full) in files)
                {
                    string text = File.ReadAllText(full, Encoding.UTF8).Replace("\r", "");
                    byte[] bytes = Encoding.UTF8.GetBytes(rel + "\n" + text + "\n");
                    sha.TransformBlock(bytes, 0, bytes.Length, null, 0);
                }
                sha.TransformFinalBlock(new byte[0], 0, 0);
                return string.Concat(sha.Hash.Take(8).Select(b => b.ToString("x2")));
            }
        }

        private static System.Collections.Generic.IEnumerable<(string rel, string full)> Files(string folder, string prefix)
        {
            string root = Path.GetFullPath(folder).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            return Directory.GetFiles(root, "*.cs", SearchOption.AllDirectories)
                .Select(f => (rel: prefix + f.Substring(root.Length + 1).Replace(Path.DirectorySeparatorChar, '/'), full: f));
        }
    }
}
