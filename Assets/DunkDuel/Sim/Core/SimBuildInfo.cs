using System;
using System.Collections.Generic;
using DunkDuel.Sim.Data;

namespace DunkDuel.Sim
{
    /// <summary>
    /// Build identity of the simulation. The netcode handshake refuses peers whose <see cref="Version"/> differs:
    /// a hash of the sim source code (generated into SimBuildInfo.g.cs) combined with the data-table hash.
    /// </summary>
    public static partial class SimBuildInfo
    {
        /// <summary>Wire protocol of the netcode (packets, handshake). Bump on any format change.</summary>
        public const int ProtocolVersion = 1;

        public static ulong Version(SimTables tables)
        {
            unchecked
            {
                ulong h = 14695981039346656037UL;
                for (int i = 0; i < 8; i++)
                {
                    h ^= (byte)(CodeHash >> (8 * i));
                    h *= 1099511628211UL;
                }
                for (int i = 0; i < 8; i++)
                {
                    h ^= (byte)(tables.DataHash >> (8 * i));
                    h *= 1099511628211UL;
                }
                return h;
            }
        }

        /// <summary>
        /// Hash of the sim sources: (relative path, text) pairs sorted by ordinal path, CR stripped, generated files
        /// (*.g.cs) excluded. Used by the Editor build step and Tools/SimTool to regenerate <see cref="CodeHash"/>.
        /// </summary>
        public static ulong ComputeCodeHash(IList<KeyValuePair<string, string>> files)
        {
            var sorted = new List<KeyValuePair<string, string>>(files);
            sorted.Sort((a, b) => string.CompareOrdinal(a.Key, b.Key));
            ulong h = 14695981039346656037UL;
            for (int i = 0; i < sorted.Count; i++)
            {
                string path = sorted[i].Key.Replace('\\', '/');
                if (path.EndsWith(".g.cs", StringComparison.Ordinal)) continue;
                h = TableBinder.HashText(path, h);
                h = TableBinder.HashText(sorted[i].Value, h);
            }
            return h;
        }
    }
}
