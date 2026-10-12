using System;

namespace FindAnything.Core
{
    /// <summary>
    /// Paths over 260 characters only work through the \\?\ prefix unless Windows' LongPathsEnabled policy is on
    /// (it is off by default). Searching uses prefixed paths; everything shown to the user is unprefixed.
    /// Call EnableInProcess() first thing in Main.
    /// </summary>
    public static class LongPath
    {
        public static void EnableInProcess()
        {
            AppContext.SetSwitch("Switch.System.IO.UseLegacyPathHandling", false);
            AppContext.SetSwitch("Switch.System.IO.BlockLongPaths", false);
        }

        public static string Add(string path)
        {
            if (string.IsNullOrEmpty(path) || path.StartsWith(@"\\?\")) return path;
            if (path.StartsWith(@"\\")) return @"\\?\UNC\" + path.Substring(2);
            if (path.Length >= 2 && path[1] == ':')
            {
                // "C:" alone means "current folder on C:", which \\?\ does not understand.
                if (path.Length == 2) path += @"\";
                return @"\\?\" + path;
            }
            return path;   // relative path: leave alone
        }

        public static string Strip(string path)
        {
            if (string.IsNullOrEmpty(path)) return path;
            if (path.StartsWith(@"\\?\UNC\")) return @"\\" + path.Substring(8);
            if (path.StartsWith(@"\\?\")) return path.Substring(4);
            return path;
        }
    }
}
