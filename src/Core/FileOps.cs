using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace FindAnything.Core
{
    /// <summary>Copies or moves result files into one folder. Never overwrites: a name already there gets " (2)", " (3)"...</summary>
    public static class FileOps
    {
        public static string UniqueName(string folder, string name)
        {
            string p = Path.Combine(folder, name);
            if (!File.Exists(LongPath.Add(p)) && !Directory.Exists(LongPath.Add(p))) return p;
            string stem = Path.GetFileNameWithoutExtension(name), ext = Path.GetExtension(name);
            for (int i = 2; ; i++)
            {
                p = Path.Combine(folder, stem + " (" + i + ")" + ext);
                if (!File.Exists(LongPath.Add(p)) && !Directory.Exists(LongPath.Add(p))) return p;
            }
        }

        /// <summary>Copies (or moves) the files; on a move, each hit's path is updated to where it went.</summary>
        public static string CopyOrMove(List<FileHit> items, string dest, bool move, Action<string, double> report, CancellationToken ct, List<string> problems)
        {
            int done = 0, failed = 0, skipped = 0;
            long total = 0, bytes = 0;
            foreach (FileHit h in items) total += Math.Max(1, h.Size);
            string fullDest = Path.GetFullPath(dest);
            for (int i = 0; i < items.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                FileHit h = items[i];
                report((move ? "Moving " : "Copying ") + (i + 1).ToString("N0") + " of " + items.Count.ToString("N0") + ":  " + h.Name, (double)bytes / Math.Max(1, total));
                try
                {
                    if (move && string.Equals(Path.GetDirectoryName(h.Path), fullDest.TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) { skipped++; continue; }
                    string target = UniqueName(fullDest, h.Name);
                    if (move)
                    {
                        File.Move(h.IoPath, LongPath.Add(target));
                        h.Path = target; h.IoPath = LongPath.Add(target); h.Folder = fullDest; h.Name = Path.GetFileName(target);
                    }
                    else File.Copy(h.IoPath, LongPath.Add(target), false);
                    done++;
                }
                catch (Exception ex)
                {
                    failed++;
                    lock (problems) problems.Add(h.Path + ": not " + (move ? "moved" : "copied") + " (" + ex.Message + ")");
                }
                bytes += Math.Max(1, h.Size);
            }
            string what = done == 1 ? "1 file" : done.ToString("N0") + " files";
            string line = (move ? "Moved " : "Copied ") + what + " to " + fullDest + ".";
            if (skipped > 0) line += "  " + skipped + " already there.";
            if (failed > 0) line += "  " + failed + " could not be " + (move ? "moved" : "copied") + " (see notes).";
            return line;
        }
    }
}
