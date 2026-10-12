using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FindAnything.Core
{
    /// <summary>
    /// Finds pictures that look like a sample picture, or that match a description, by comparing CLIP fingerprints.
    /// Every picture under the folders is read once (or taken from the opt-in cache) and scored; the window shows the
    /// best scores first.
    /// </summary>
    public sealed class PictureSearch
    {
        public readonly ConcurrentQueue<FileHit> Hits = new ConcurrentQueue<FileHit>();
        public readonly ConcurrentQueue<string> Errors = new ConcurrentQueue<string>();
        public long Pictures, Done, ErrorCount;
        public volatile string Current = "";
        public string Error;   // set by the caller when the search could not run at all

        /// <summary>Scores one picture: sets hit.Score (and hit.Weak when it does not look alike at all).</summary>
        public delegate void ScoreFn(string ioPath, FileHit hit);

        private ScoreFn score;
        private readonly List<string> roots;
        private readonly string[] skipDirs;
        private readonly HashSet<string> skipFiles = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // the sample pictures themselves

        public PictureSearch(List<string> roots, string skipFolders, IEnumerable<string> samples)
        {
            this.roots = roots;
            List<string> dirs = new List<string>();
            foreach (string s in (skipFolders ?? "").Split(';', '|')) if (s.Trim().Length > 0) dirs.Add(s.Trim());
            skipDirs = dirs.ToArray();
            if (samples != null) foreach (string s in samples) skipFiles.Add(LongPath.Strip(Path.GetFullPath(s)));
        }

        /// <summary>
        /// Scores every picture under the folders: its fingerprint (from embed) against each query fingerprint, keeping
        /// the closest. With several samples a picture only has to look like one of them.
        /// </summary>
        public void Run(Func<string, float[]> embed, List<float[]> queries, int threads, CancellationToken ct)
        {
            Run(delegate(string p, FileHit h) { h.Score = Closest(queries, embed(p)); }, threads, ct);
        }

        private static float Closest(List<float[]> queries, float[] e)
        {
            float best = -1;
            foreach (float[] q in queries) best = Math.Max(best, Clip.Similarity(q, e));
            return best;
        }

        /// <summary>
        /// Sample pictures and words together: a picture must look like a sample (DINOv2, the measured cut-off); those
        /// that do are scored by how well they match the words (CLIP). Pictures that don't look alike are marked Weak and
        /// keep their look score; the words model is only run on look-alikes, which keeps the extra CPU small.
        /// </summary>
        public static ScoreFn LookThenWords(Clip clip, List<float[]> looks, float[] words)
        {
            return delegate(string p, FileHit h)
            {
                float look = Closest(looks, clip.EmbedLook(p));
                h.Look = look;
                if (look < Clip.LookAlike) { h.Weak = true; h.Score = look - 1; return; }
                h.Score = Clip.Similarity(words, clip.EmbedImage(p));
            };
        }

        /// <summary>Scores every picture under the folders with the given scorer.</summary>
        public void Run(ScoreFn score, int threads, CancellationToken ct)
        {
            this.score = score;
            List<FileInfo> files = new List<FileInfo>();
            Stack<DirectoryInfo> stack = new Stack<DirectoryInfo>();
            foreach (string r in roots)
            {
                string root = LongPath.Add(r);
                if (File.Exists(root)) files.Add(new FileInfo(root));
                else if (Directory.Exists(root)) stack.Push(new DirectoryInfo(root));
            }
            while (stack.Count > 0)
            {
                ct.ThrowIfCancellationRequested();
                DirectoryInfo dir = stack.Pop();
                Current = LongPath.Strip(dir.FullName);
                FileSystemInfo[] entries;
                try { entries = dir.GetFileSystemInfos(); }
                catch (Exception ex) { Note(LongPath.Strip(dir.FullName) + ": " + ex.Message); continue; }
                foreach (FileSystemInfo e in entries)
                {
                    DirectoryInfo sub = e as DirectoryInfo;
                    if (sub != null)
                    {
                        if ((sub.Attributes & FileAttributes.ReparsePoint) == 0 && !FileMask.AnyMatch(sub.Name, skipDirs)) stack.Push(sub);
                        continue;
                    }
                    FileInfo f = e as FileInfo;
                    if (f != null && Ocr.IsImage(f.Name) && !SearchJob.IsCloudOnly(f)) files.Add(f);
                }
            }
            Interlocked.Exchange(ref Pictures, files.Count);

            // One picture per thread (the model runs single-threaded inside each), on below-normal-priority threads so
            // the rest of the PC stays smooth. Only the first Workers threads take pictures; the rest wait, so the CPU
            // budget can be turned up or down while the search runs.
            if (Workers < 1) Workers = Math.Max(1, threads);   // the window may already have set it (the slider)
            int next = -1;
            Thread[] pool = new Thread[Math.Max(1, Environment.ProcessorCount)];
            for (int i = 0; i < pool.Length; i++)
            {
                int id = i;
                pool[i] = new Thread(delegate()
                {
                    while (!ct.IsCancellationRequested)
                    {
                        if (Thread.VolatileRead(ref next) >= files.Count - 1) return;   // every picture is taken
                        if (id >= Workers) { Thread.Sleep(150); continue; }
                        int n = Interlocked.Increment(ref next);
                        if (n >= files.Count) return;
                        Score(files[n]);
                    }
                });
                pool[i].IsBackground = true;
                pool[i].Priority = ThreadPriority.BelowNormal;
                pool[i].Start();
            }
            foreach (Thread t in pool) t.Join();
        }

        /// <summary>How many pictures are read at once (1 = lightest). Can change while the search runs.</summary>
        public volatile int Workers;

        private void Score(FileInfo f)
        {
            string path = LongPath.Strip(f.FullName);
            try
            {
                if (skipFiles.Contains(path)) return;
                FileHit h = new FileHit();
                h.IoPath = f.FullName; h.Path = path; h.Name = f.Name; h.Folder = LongPath.Strip(f.DirectoryName);
                h.Size = f.Length; h.Modified = f.LastWriteTime;
                h.Source = "SIMILAR";
                score(f.FullName, h);
                Hits.Enqueue(h);
            }
            catch (Exception ex) { Note(path + ": " + ex.Message); }
            finally { Interlocked.Increment(ref Done); }
        }

        private void Note(string s)
        {
            Interlocked.Increment(ref ErrorCount);
            if (Errors.Count < SearchJob.MaxErrorsKept) Errors.Enqueue(s);
        }
    }
}
