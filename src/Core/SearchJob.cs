using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Text;
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
        public bool Loose;                      // ignore spacing/punctuation, O=0 and I=l=1
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
        public bool Ocr;                        // read the text in pictures and scanned PDFs (Windows OCR)
        public bool SecretsMode;                // list keys, serials, passwords... instead of searching for Text
        public SecretOptions Secrets = new SecretOptions();
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
        public long OcrFiles;        // pictures and scanned PDF pages read with OCR (or from its cache)
        public volatile string Current = "";

        public const int MaxErrorsKept = 2000;

        public SearchJob(SearchSettings settings)
        {
            set = settings;
            if (!set.SecretsMode && !set.SearchContents && !set.SearchNames)
                throw new ArgumentException("Nothing to search: turn on Inside files, File names or Pictures (OCR).");
            if (set.SecretsMode) { }
            else if (set.SearchContents)
                scanner = new Scanner(set.Text, set.CaseSensitive, set.Wildcards, set.WholeWord, set.Utf16, set.Loose);
            if (set.SearchNames && !set.SecretsMode)
                names = new NameMatcher(set.Text, set.CaseSensitive, set.Wildcards, set.WholeWord, set.Loose);
            include = FileMask.Split(set.IncludeMasks);
            exclude = FileMask.Split(set.ExcludeMasks);
            List<string> dirs = new List<string>();
            foreach (string s in (set.SkipFolders ?? "").Split(';', '|'))
                if (s.Trim().Length > 0) dirs.Add(s.Trim());
            skipDirs = dirs.ToArray();
        }

        public Scanner Scanner { get { return scanner; } }

        /// <summary>Files searched at once (the CPU use slider). Can change while the search runs.</summary>
        public volatile int Workers;
        private int working;
        private readonly object gate = new object();

        public void Run(CancellationToken ct)
        {
            // Up to every CPU thread may take files, but only Workers of them work at a time (the CPU use slider, which
            // can change mid-search); the others wait without using the CPU. The work runs at below-normal priority.
            if (Workers < 1) Workers = Math.Max(1, set.Threads);
            ParallelOptions po = new ParallelOptions();
            po.MaxDegreeOfParallelism = Math.Max(Math.Max(1, set.Threads), Environment.ProcessorCount);
            po.CancellationToken = ct;
            try
            {
                Parallel.ForEach<FileInfo, byte[]>(
                    Partitioner.Create(Walk(ct), EnumerablePartitionerOptions.NoBuffering),
                    po,
                    delegate { return scanner != null ? scanner.NewBuffer() : null; },
                    delegate(FileInfo fi, ParallelLoopState state, byte[] buf)
                    {
                        lock (gate)
                        {
                            while (working >= Workers && !ct.IsCancellationRequested) Monitor.Wait(gate, 250);
                            working++;
                        }
                        ThreadPriority old = Thread.CurrentThread.Priority;
                        Thread.CurrentThread.Priority = ThreadPriority.BelowNormal;
                        try { ProcessFile(fi, buf, ct); }
                        finally
                        {
                            Thread.CurrentThread.Priority = old;
                            lock (gate) { working--; Monitor.PulseAll(gate); }
                        }
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

            if (set.SecretsMode) { bool keyFile = FindCredentialFile(hit); FindSecrets(fi, hit, ct, keyFile); return; }
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

        /// <summary>Searches one file's contents, extracting document or picture text first when that applies.</summary>
        public void SearchContents(FileHit hit, byte[] buf, CancellationToken ct)
        {
            string source;
            byte[] text = ExtractedText(hit, ct, out source);
            if (text != null)
            {
                Interlocked.Add(ref BytesRead, hit.Size);
                hit.Source = source;
                using (MemoryStream ms = new MemoryStream(text, false))
                    scanner.Scan(ms, buf, hit, ct, false);
                return;
            }

            using (FileStream fs = new FileStream(hit.IoPath, FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
            {
                long n = scanner.Scan(fs, buf, hit, ct, true);
                Interlocked.Add(ref BytesRead, n);
            }
        }

        /// <summary>
        /// The readable text of a document (PDF, Office), a picture (OCR) or a scanned PDF (OCR), as UTF-8; or null
        /// when the file should be searched as raw bytes.
        /// </summary>
        private byte[] ExtractedText(FileHit hit, CancellationToken ct, out string source)
        {
            source = "";
            string path = hit.IoPath;
            if (set.Ocr && Ocr.IsImage(path))
            {
                try
                {
                    OcrText t = Ocr.ReadImage(path);
                    Interlocked.Increment(ref OcrFiles);
                    source = "OCR";
                    return Encoding.UTF8.GetBytes(t.Text);
                }
                catch (Exception ex)
                {
                    AddError(hit.Path + ": picture not readable (" + ex.Message + "); searched as raw bytes");
                    return null;
                }
            }
            if (set.LookInsideDocuments && DocText.KindOf(path) != null)
            {
                string note;
                byte[] text = DocText.Extract(path, ct, out note);
                if (text != null && set.Ocr && DocText.KindOf(path) == "PDF" && IsBlank(text))
                {
                    try
                    {
                        OcrText t = Ocr.ReadPdf(path, ct);
                        Interlocked.Increment(ref OcrFiles);
                        source = "PDF OCR";
                        return Encoding.UTF8.GetBytes(t.Text);
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex) { note = "scanned PDF could not be read (" + ex.Message + ")"; }
                }
                if (note != null) AddError(hit.Path + ": " + note);
                if (text != null) { source = DocText.KindOf(path); return text; }
            }
            return null;
        }

        private static bool IsBlank(byte[] text)
        {
            foreach (byte b in text) if (b > 32) return false;
            return true;
        }

        /// <summary>Sensitive data: the file itself is a credential (an SSH key, .env, a vault...).</summary>
        /// Returns true for key files, whose contents need no second "private key" row.
        private bool FindCredentialFile(FileHit file)
        {
            if (!set.Secrets.CredentialFiles) return false;
            int severity;
            string label = CredentialFiles.Check(file.Path, out severity);
            if (label == null) return false;
            bool keyFile = label.EndsWith("private key");
            string filter = (set.Text ?? "").Trim();
            if (filter.Length > 0 && file.Path.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 && label.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) return keyFile;
            SecretFinding f = new SecretFinding();
            f.Kind = SecretKind.CredentialFile;
            f.Label = label;
            f.Value = file.Name;
            f.Line = file.Name + "   in " + file.Folder;
            f.ValueStart = 0;
            f.Severity = severity;
            SecretAdvice.Rate(f, file.Path);
            FileHit h = new FileHit();
            h.IoPath = file.IoPath; h.Path = file.Path; h.Name = file.Name; h.Folder = file.Folder;
            h.Size = file.Size; h.Modified = file.Modified; h.Source = "";
            h.Secret = f;
            h.ContentHits = 1;
            h.Preview = f.Line;
            Interlocked.Increment(ref HitCount);
            Hits.Enqueue(h);
            return keyFile;
        }

        /// <summary>Keys mode: one result row per key, serial, password or token found in the file.</summary>
        private void FindSecrets(FileInfo fi, FileHit file, CancellationToken ct, bool keyFile)
        {
            if (set.MaxFileBytes > 0 && file.Size > set.MaxFileBytes) { Interlocked.Increment(ref SkippedLarge); return; }
            if (set.SkipCloudOnly && IsCloudOnly(fi)) { Interlocked.Increment(ref SkippedCloud); return; }
            List<SecretFinding> found;
            string source;
            try
            {
                byte[] text = ExtractedText(file, ct, out source);
                if (text != null)
                {
                    using (MemoryStream ms = new MemoryStream(text, false)) found = SecretScanner.Scan(ms, set.Secrets, Encoding.UTF8, ct);
                }
                else
                {
                    using (FileStream fs = new FileStream(file.IoPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.SequentialScan))
                    {
                        byte[] head = new byte[4096];
                        int n = fs.Read(head, 0, head.Length);
                        if (!SecretScanner.LooksLikeText(head, n)) return;   // programs, pictures, archives: no
                        fs.Position = 0;
                        found = SecretScanner.Scan(fs, set.Secrets, null, ct);
                    }
                }
                Interlocked.Add(ref BytesRead, file.Size);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { AddError(file.Path + ": " + ex.Message); return; }

            string filter = (set.Text ?? "").Trim();
            foreach (SecretFinding f in found)
            {
                if (keyFile && f.Kind == SecretKind.PrivateKey) continue;   // already listed as the key file
                if (filter.Length > 0 && f.Line.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0 &&
                    file.Name.IndexOf(filter, StringComparison.OrdinalIgnoreCase) < 0) continue;
                FileHit h = new FileHit();
                h.IoPath = file.IoPath; h.Path = file.Path; h.Name = file.Name; h.Folder = file.Folder;
                h.Size = file.Size; h.Modified = file.Modified; h.Source = source;
                h.Secret = f;
                SecretAdvice.Rate(f, file.Path);
                h.ContentHits = 1;
                h.FirstLine = f.LineNumber;
                h.Preview = f.Line;
                Interlocked.Increment(ref HitCount);
                Hits.Enqueue(h);
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
