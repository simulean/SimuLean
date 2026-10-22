using System;
using System.Collections.Generic;

namespace SimuLean.Data
{
    /// <summary>
    /// Process-wide cache of open <see cref="ProjectDatabase"/> connections,
    /// keyed by absolute file path — several elements referencing the same
    /// project database share one (thread-safe) connection.
    /// </summary>
    public static class ProjectDatabaseRegistry
    {
        private static readonly Dictionary<string, ProjectDatabase> open =
            new Dictionary<string, ProjectDatabase>(StringComparer.OrdinalIgnoreCase);
        private static readonly object sync = new object();

        /// <summary>Returns the open database for <paramref name="path"/>, opening it on first use.</summary>
        public static ProjectDatabase GetOrOpen(string path)
        {
            string full = System.IO.Path.GetFullPath(path);
            lock (sync)
            {
                if (!open.TryGetValue(full, out var db))
                {
                    db = new ProjectDatabase(full);
                    open[full] = db;
                }
                return db;
            }
        }

        /// <summary>Closes and forgets every cached connection.</summary>
        public static void CloseAll()
        {
            lock (sync)
            {
                foreach (var db in open.Values)
                    db.Dispose();
                open.Clear();
            }
        }
    }
}
