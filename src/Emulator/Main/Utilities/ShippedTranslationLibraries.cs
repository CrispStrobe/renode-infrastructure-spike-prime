//
// Copyright (c) 2026 Brickwright contributors
//
// This file is licensed under the MIT License.
// Full license text is available in 'licenses/MIT.txt'.
//
using System;
using System.Collections.Generic;
using System.IO;

namespace Antmicro.Renode.Utilities
{
    /// <summary>
    /// Optional shipped translation libraries. When a directory holds
    /// <c>translate-*.so</c> files (environment variable <c>RENODE_TLIB_DIRECTORY</c>,
    /// otherwise a <c>tlib</c> directory beside the Renode host), the first CPU that
    /// needs a given library loads that file in place instead of a temporary copy
    /// extracted from the embedded resource. This lets a signed bundle ship tlib as a
    /// separate, signed, replaceable file. tlib keeps global state per loaded image, so
    /// only one CPU at a time may use a shipped file; any further CPU of the same
    /// architecture falls back to the existing per-CPU extraction. Without the
    /// directory, behaviour is unchanged. A shipped file is never deleted.
    /// </summary>
    public static class ShippedTranslationLibraries
    {
        public const string DirectoryVariable = "RENODE_TLIB_DIRECTORY";

        public static string Directory
        {
            get
            {
                var configured = Environment.GetEnvironmentVariable(DirectoryVariable);
                if(!string.IsNullOrEmpty(configured))
                {
                    return configured;
                }
                return Path.Combine(AppContext.BaseDirectory, "tlib");
            }
        }

        /// <summary>Maps an embedded resource name (Antmicro.Renode.translate-arm-m-le.so) to its shipped file name.</summary>
        public static string FileNameFor(string resourceName)
        {
            const string prefix = "Antmicro.Renode.";
            return resourceName.StartsWith(prefix, StringComparison.Ordinal) ? resourceName.Substring(prefix.Length) : resourceName;
        }

        public static bool TryClaim(string resourceName, out string path)
        {
            return TryClaim(Directory, resourceName, out path);
        }

        public static bool TryClaim(string directory, string resourceName, out string path)
        {
            path = null;
            if(string.IsNullOrEmpty(directory))
            {
                return false;
            }
            var candidate = Path.GetFullPath(Path.Combine(directory, FileNameFor(resourceName)));
            if(!File.Exists(candidate))
            {
                return false;
            }
            lock(claimed)
            {
                if(!claimed.Add(candidate))
                {
                    return false;
                }
            }
            path = candidate;
            return true;
        }

        public static bool IsShipped(string path)
        {
            if(path == null)
            {
                return false;
            }
            lock(claimed)
            {
                return claimed.Contains(Path.GetFullPath(path));
            }
        }

        /// <summary>Releases a claim after its CPU unloaded the library, so a later CPU can load it again.</summary>
        public static bool Release(string path)
        {
            if(path == null)
            {
                return false;
            }
            lock(claimed)
            {
                return claimed.Remove(Path.GetFullPath(path));
            }
        }

        private static readonly HashSet<string> claimed = new HashSet<string>();
    }
}
