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

        private Func<string, float[]> embed;
        private List<float[]> queries;
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
            this.embed = embed;
            this.queries = queries;
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

            ParallelOptions po = new ParallelOptions();
            po.MaxDegreeOfParallelism = Math.Max(1, threads);
            po.CancellationToken = ct;
            try
            {
                Parallel.ForEach(files, po, delegate(FileInfo f)
                {
                    string path = LongPath.Strip(f.FullName);
                    try
                    {
                        if (skipFiles.Contains(path)) return;
                        float[] e = embed(f.FullName);
                        FileHit h = new FileHit();
                        h.IoPath = f.FullName; h.Path = path; h.Name = f.Name; h.Folder = LongPath.Strip(f.DirectoryName);
                        h.Size = f.Length; h.Modified = f.LastWriteTime;
                        h.Source = "SIMILAR";
                        float best = -1;
                        foreach (float[] q in queries) best = Math.Max(best, Clip.Similarity(q, e));
                        h.Score = best;
                        Hits.Enqueue(h);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { Note(path + ": " + ex.Message); }
                    finally { Interlocked.Increment(ref Done); }
                });
            }
            catch (OperationCanceledException) { }
            catch (AggregateException ae) { ae.Handle(delegate(Exception x) { return x is OperationCanceledException; }); }
        }

        private void Note(string s)
        {
            Interlocked.Increment(ref ErrorCount);
            if (Errors.Count < SearchJob.MaxErrorsKept) Errors.Enqueue(s);
        }
    }
}
