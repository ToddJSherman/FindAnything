using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace FindAnything.Core
{
    public sealed class SearchSettings
    {
        public List<string> Roots = new List<string>();
        public string Text = "";
        public bool CaseSensitive;
        public bool Wildcards = true;
        public bool WholeWord;
        public bool SearchContents = true;
        public bool SearchNames;
        public string IncludeMasks = "";        // empty = every file type
        public string ExcludeMasks = "";
        public string SkipFolders = "System Volume Information;$Recycle.Bin";
        public bool LookInsideDocuments = true; // PDF, DOCX, XLSX, PPTX, ODF
        public bool Utf16 = true;
        public long MaxFileBytes;               // 0 = no limit (content search only)
        public int Threads = 4;
        public bool SkipCloudOnly = true;       // OneDrive "online-only" files would download if read
    }

    /// <summary>
    /// Walks the folders and searches each file on a few worker threads. Results and counters are read by
    /// the window while it runs. Every file is streamed, so no single file can eat the memory.
    /// </summary>
    public sealed class SearchJob
    {
        private readonly SearchSettings set;
        private readonly Scanner scanner;
        private readonly NameMatcher names;
        private readonly string[] include, exclude, skipDirs;

        public readonly ConcurrentQueue<FileHit> Hits = new ConcurrentQueue<FileHit>();
        public readonly ConcurrentQueue<string> Errors = new ConcurrentQueue<string>();

        public long FilesSeen;       // every file found while walking
        public long FilesSearched;   // files that passed the type filter
        public long BytesRead;
        public long HitCount;
        public long ErrorCount;
        public long SkippedLarge;
        public long SkippedCloud;
        public long Folders;
        public volatile string Current = "";

        public const int MaxErrorsKept = 2000;

        public SearchJob(SearchSettings settings)
        {
            set = settings;
            if (!set.SearchContents && !set.SearchNames)
                throw new ArgumentException("Pick at least one of: file contents, file names.");
            if (set.SearchContents)
                scanner = new Scanner(set.Text, set.CaseSensitive, set.Wildcards, set.WholeWord, set.Utf16);
            if (set.SearchNames)
                names = new NameMatcher(set.Text, set.CaseSensitive, set.Wildcards, set.WholeWord);
            include = FileMask.Split(set.IncludeMasks);
            exclude = FileMask.Split(set.ExcludeMasks);
            List<string> dirs = new List<string>();
            foreach (string s in (set.SkipFolders ?? "").Split(';', '|'))
                if (s.Trim().Length > 0) dirs.Add(s.Trim());
            skipDirs = dirs.ToArray();
        }

        public Scanner Scanner { get { return scanner; } }

        public void Run(CancellationToken ct)
        {
            ParallelOptions po = new ParallelOptions();
            po.MaxDegreeOfParallelism = Math.Max(1, set.Threads);
            po.CancellationToken = ct;
            try
            {
                Parallel.ForEach<FileInfo, byte[]>(
                    Partitioner.Create(Walk(ct), EnumerablePartitionerOptions.NoBuffering),
                    po,
                    delegate { return scanner != null ? scanner.NewBuffer() : null; },
                    delegate(FileInfo fi, ParallelLoopState state, byte[] buf)
                    {
                        ProcessFile(fi, buf, ct);
                        return buf;
                    },
                    delegate(byte[] buf) { });
            }
            catch (OperationCanceledException) { }
            catch (AggregateException ae)
            {
                ae.Handle(delegate(Exception e) { return e is OperationCanceledException; });
            }
        }

        private IEnumerable<FileInfo> Walk(CancellationToken ct)
        {
            Stack<DirectoryInfo> stack = new Stack<DirectoryInfo>();
            for (int i = set.Roots.Count - 1; i >= 0; i--)
            {
                string root = LongPath.Add(set.Roots[i]);
                if (File.Exists(root)) { FileInfo single = new FileInfo(root); Interlocked.Increment(ref FilesSeen); yield return single; continue; }
                if (Directory.Exists(root)) stack.Push(new DirectoryInfo(root));
                else AddError(set.Roots[i] + ": folder not found");
            }

            while (stack.Count > 0)
            {
                if (ct.IsCancellationRequested) yield break;
                DirectoryInfo dir = stack.Pop();
                Interlocked.Increment(ref Folders);
                Current = LongPath.Strip(dir.FullName);

                FileSystemInfo[] entries;
                try { entries = dir.GetFileSystemInfos(); }
                catch (Exception ex) { AddError(LongPath.Strip(dir.FullName) + ": " + ex.Message); continue; }

                List<DirectoryInfo> subdirs = new List<DirectoryInfo>();
                foreach (FileSystemInfo e in entries)
                {
                    DirectoryInfo sub = e as DirectoryInfo;
                    if (sub != null)
                    {
                        // Junctions and symlinks are skipped: they loop back ("Application Data") or leave the tree.
                        if ((sub.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        if (FileMask.AnyMatch(sub.Name, skipDirs)) continue;
                        subdirs.Add(sub);
                        continue;
                    }
                    FileInfo f = e as FileInfo;
                    if (f == null) continue;
                    Interlocked.Increment(ref FilesSeen);
                    yield return f;
                }
                // Push in reverse so folders are visited in name order.
                subdirs.Sort(delegate(DirectoryInfo a, DirectoryInfo b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
                for (int i = subdirs.Count - 1; i >= 0; i--) stack.Push(subdirs[i]);
            }
        }

        private void ProcessFile(FileInfo fi, byte[] buf, CancellationToken ct)
        {
            string name = fi.Name;
            if (include.Length > 0 && !FileMask.AnyMatch(name, include)) return;
            if (exclude.Length > 0 && FileMask.AnyMatch(name, exclude)) return;
            Interlocked.Increment(ref FilesSearched);

            FileHit hit = new FileHit();
            hit.IoPath = fi.FullName;
            hit.Path = LongPath.Strip(fi.FullName);
            hit.Name = name;
            hit.Folder = LongPath.Strip(fi.DirectoryName);
            try
            {
                hit.Size = fi.Length;
                hit.Modified = fi.LastWriteTime;
            }
            catch (Exception) { }

            if (names != null && names.IsMatch(name)) hit.NameMatch = true;

            if (scanner != null)
            {
                if (set.MaxFileBytes > 0 && hit.Size > set.MaxFileBytes)
                {
                    Interlocked.Increment(ref SkippedLarge);
                }
                else if (set.SkipCloudOnly && IsCloudOnly(fi))
                {
                    Interlocked.Increment(ref SkippedCloud);
                }
                else
                {
                    try
                    {
                        SearchContents(hit, buf, ct);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        AddError(hit.Path + ": " + ex.Message);
                    }
                }
            }

            if (hit.NameMatch || hit.ContentHits > 0)
            {
                Interlocked.Increment(ref HitCount);
                Hits.Enqueue(hit);
            }
        }

        /// <summary>Searches one file's contents, extracting document text first when that applies.</summary>
        public void SearchContents(FileHit hit, byte[] buf, CancellationToken ct)
        {
            string path = hit.IoPath;
            if (set.LookInsideDocuments && DocText.KindOf(path) != null)
            {
                string note;
                byte[] text = DocText.Extract(path, ct, out note);
                if (note != null) AddError(hit.Path + ": " + note);
                if (text != null)
                {
                    Interlocked.Add(ref BytesRead, hit.Size);
                    hit.Source = DocText.KindOf(path);
                    using (MemoryStream ms = new MemoryStream(text, false))
                        scanner.Scan(ms, buf, hit, ct, false);
                    return;
                }
            }

            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
            {
                long n = scanner.Scan(fs, buf, hit, ct, true);
                Interlocked.Add(ref BytesRead, n);
            }
        }

        public static bool IsCloudOnly(FileInfo fi)
        {
            try
            {
                int a = (int)fi.Attributes;
                const int Offline = 0x1000, RecallOnOpen = 0x40000, RecallOnDataAccess = 0x400000;
                return (a & (Offline | RecallOnOpen | RecallOnDataAccess)) != 0;
            }
            catch (Exception) { return false; }
        }

        private void AddError(string message)
        {
            Interlocked.Increment(ref ErrorCount);
            if (Errors.Count < MaxErrorsKept) Errors.Enqueue(message);
        }
    }
}
