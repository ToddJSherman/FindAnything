using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FindAnything.Core;
using FindAnything.Ui;

namespace FindAnything.App
{
    /// <summary>The Deleted files tab: find deleted files on an NTFS drive (and in its Recycle Bin), preview and recover them.</summary>
    internal sealed partial class MainForm
    {
        private readonly Chip delRecycle = new Chip("Recycle Bin", "\uE74D", true);
        private readonly Chip delOnlyGood = new Chip("Only recoverable", "\uE73E", true);

        private sealed class DeletedScan
        {
            public volatile NtfsVolume Volume;
            public volatile string Phase = "Opening the drive...";
            public List<FileHit> Found;
            public string Error;
            public string Root, Folder;
            public int Erased;
        }

        private DeletedScan delScan;
        private Task delTask;
        private string recoveryFolder;     // where recovered files go, asked once per session
        private string deletedRoot;        // the drive the listed results came from
        private readonly string previewDir = Path.Combine(Path.GetTempPath(), "FindAnything-preview-" + Process.GetCurrentProcess().Id);

        private static bool IsDeletedRow(FileHit h) { return h != null && (h.Source == "DELETED" || h.Source == "RECYCLE BIN"); }

        private void SetUpDeletedChips()
        {
            Tip(delRecycle, "Also list what is in this drive's Recycle Bin (it is still there, just hidden).");
            Tip(delOnlyGood, "Hide files whose data has been overwritten or erased: only ones worth recovering are listed.");
            delOnlyGood.CheckedChanged += delegate { if (mode == 4 && allDeleted.Count > 0) ShowDeleted(); };
        }

        // ------------------------------------------------------------------ the search

        private readonly List<FileHit> allDeleted = new List<FileHit>();

        private void StartDeletedSearch()
        {
            if (Busy) return;
            if (!RawDisk.IsAdmin) { RestartAsAdmin(); return; }
            string look = lookField.Text.Trim().Trim('"');
            if (look.Length == 0 || look == AllDrivesItem) { Warn("Pick one drive (or a folder on it) to look for deleted files."); return; }
            string root;
            try { root = Path.GetPathRoot(Path.GetFullPath(look)); }
            catch (Exception) { Warn("\"" + look + "\" is not a drive or folder."); return; }
            if (root == null || root.Length < 3 || root[1] != ':') { Warn("Deleted files works on drive letters (C:\\, D:\\ ...)."); return; }
            string folder = look.Length > root.Length ? Path.GetFullPath(look).TrimEnd('\\') : null;

            string text = findField.Text.Trim();
            string[] incl = SplitMasks(types.Text), excl = SplitMasks(excludeTypes.Text);
            Func<string, bool> accept = delegate(string name)
            {
                if (text.Length > 0)
                {
                    bool wild = text.IndexOf('*') >= 0 || text.IndexOf('?') >= 0;
                    if (wild ? !FileMask.AnyMatch(name, new[] { text }) : name.IndexOf(text, StringComparison.OrdinalIgnoreCase) < 0) return false;
                }
                if (incl.Length > 0 && !FileMask.AnyMatch(name, incl)) return false;
                if (excl.Length > 0 && FileMask.AnyMatch(name, excl)) return false;
                return true;
            };

            DeletedScan scan = new DeletedScan();
            scan.Root = root;
            scan.Folder = folder;
            bool recycle = delRecycle.Checked;
            delScan = scan;
            hits.Clear();
            allDeleted.Clear();
            problems.Clear();
            results.SortColumn = -1;
            results.ResetSelection();
            results.EmptyTitle = "Reading the file table of " + root + "...";
            results.EmptyText = "Read-only: nothing on the drive is changed.";
            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            delTask = Task.Factory.StartNew(delegate
            {
                List<FileHit> found = new List<FileHit>();
                try
                {
                    using (NtfsVolume v = new NtfsVolume(RawDisk.OpenVolume(root), root))
                    {
                        scan.Volume = v;
                        scan.Phase = "Reading the file table";
                        List<DeletedFile> files = v.FindDeleted(accept, token);
                        scan.Phase = "Checking which files can still be read";
                        int checkedCount = 0;
                        foreach (DeletedFile f in files)
                        {
                            token.ThrowIfCancellationRequested();
                            if (folder != null && !f.Folder.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;
                            // On an SSD, Windows usually tells the drive to erase freed space (TRIM): such files read as zeros.
                            bool erased = false;
                            if (f.Chance == 2 && f.Resident == null && f.Size > 0 && checkedCount++ < 20000)
                                erased = NtfsVolume.AllZero(v.Head(f, (int)Math.Min(f.Size, 4096)));
                            if (erased) scan.Erased++;
                            FileHit h = new FileHit();
                            h.Path = f.Path; h.Name = f.Name; h.Folder = f.Folder; h.Size = f.Size; h.Modified = f.Modified;
                            h.Source = "DELETED"; h.Tag = f;
                            h.Weak = erased;   // reused here: the data reads as zeros
                            found.Add(h);
                        }
                    }
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { scan.Error = ex.Message; }
                if (recycle && !token.IsCancellationRequested)
                {
                    scan.Phase = "Reading the Recycle Bin";
                    List<string> notes = new List<string>();
                    foreach (RecycledFile r in RecycleBin.List(root, notes))
                    {
                        string name = Path.GetFileName(r.OriginalPath);
                        if (!accept(name)) continue;
                        if (folder != null && !r.OriginalPath.StartsWith(folder, StringComparison.OrdinalIgnoreCase)) continue;
                        FileHit h = new FileHit();
                        h.Path = r.OriginalPath; h.Name = name; h.Folder = Path.GetDirectoryName(r.OriginalPath);
                        h.IoPath = LongPath.Add(r.DataPath); h.Size = r.Size; h.Modified = r.Deleted;
                        h.Source = "RECYCLE BIN"; h.Tag = r;
                        found.Add(h);
                    }
                }
                scan.Found = found;
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            clock = Stopwatch.StartNew();
            status.Running = true;
            status.Notes = 0;
            LayoutHero();
            tick.Start();
        }

        private static string[] SplitMasks(string s)
        {
            List<string> l = new List<string>();
            foreach (string p in (s ?? "").Split(';', ',')) if (p.Trim().Length > 0) l.Add(p.Trim());
            return l.ToArray();
        }

        private void OnDeletedTick()
        {
            DeletedScan scan = delScan;
            bool done = delTask.IsCompleted;
            List<string> parts = new List<string>();
            List<bool> bold = new List<bool>();
            NtfsVolume v = scan.Volume;
            if (!done)
            {
                parts.Add(scan.Phase); bold.Add(true);
                if (v != null && v.Records > 0 && scan.Phase == "Reading the file table")
                {
                    parts.Add((100.0 * Interlocked.Read(ref v.RecordsRead) / v.Records).ToString("0") + "%"); bold.Add(false);
                }
                status.SetParts(parts.ToArray(), bold.ToArray());
                return;
            }
            tick.Stop();
            status.Running = false;
            delScan = null;
            cts.Dispose();
            cts = null;
            deletedRoot = scan.Root;
            if (scan.Error != null) Warn("The drive could not be read: " + scan.Error);
            allDeleted.Clear();
            if (scan.Found != null) allDeleted.AddRange(scan.Found);
            ShowDeleted();
            if (scan.Erased > 0 && hits.Count == 0 && delOnlyGood.Checked)
                results.EmptyText = scan.Erased.ToString("N0") + " deleted files were found, but their data reads as zeros: the SSD has erased it (TRIM). Turn off Only recoverable to see them.";
            LayoutHero();
        }

        private static bool Recoverable(FileHit h)
        {
            DeletedFile f = h.Tag as DeletedFile;
            return f == null || (f.Chance > 0 && !h.Weak);
        }

        private void ShowDeleted()
        {
            hits.Clear();
            int hidden = 0, inBin = 0, good = 0;
            foreach (FileHit h in allDeleted)
            {
                if (delOnlyGood.Checked && !Recoverable(h)) { hidden++; continue; }
                hits.Add(h);
                if (h.Source == "RECYCLE BIN") inBin++; else if (Recoverable(h)) good++;
            }
            // newest first: what was deleted lately is usually what is missed
            hits.Sort(delegate(FileHit a, FileHit b) { int r = b.Modified.CompareTo(a.Modified); return r != 0 ? r : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase); });
            results.EmptyTitle = "No deleted files found";
            results.EmptyText = hidden > 0 ? hidden.ToString("N0") + " were found but can no longer be recovered (turn off Only recoverable to list them)."
                                           : "Nothing deleted with that name on this drive. Try fewer letters, or another drive.";
            results.ResetSelection();
            results.ItemsChanged();
            results.SelectFirst();
            List<string> parts = new List<string>();
            List<bool> bold = new List<bool>();
            parts.Add("Done in " + TimeText(clock.Elapsed.TotalSeconds)); bold.Add(true);
            parts.Add(good.ToString("N0")); bold.Add(true); parts.Add("recoverable"); bold.Add(false);
            parts.Add(inBin.ToString("N0")); bold.Add(true); parts.Add("in the Recycle Bin"); bold.Add(false);
            if (hidden > 0) { parts.Add(hidden.ToString("N0")); bold.Add(true); parts.Add("overwritten or erased (hidden)"); bold.Add(false); }
            status.SetParts(parts.ToArray(), bold.ToArray());
        }

        // ------------------------------------------------------------------ preview

        /// <summary>Copies the deleted file out to a private temporary folder and opens it (pictures in their app, the rest in the viewer).</summary>
        private void PreviewDeleted(FileHit h)
        {
            string path;
            if (h.Source == "RECYCLE BIN") path = LongPath.Strip(h.IoPath);
            else
            {
                DeletedFile f = h.Tag as DeletedFile;
                if (f == null) return;
                if (f.Size > 512L << 20) { Warn("This file is " + Fmt.Size(f.Size) + ": recover it to a folder to look at it."); return; }
                try
                {
                    Directory.CreateDirectory(previewDir);
                    path = Path.Combine(previewDir, f.Record + "-" + f.Name);
                    if (!File.Exists(path))
                    {
                        using (NtfsVolume v = new NtfsVolume(RawDisk.OpenVolume(deletedRoot), deletedRoot))
                        using (FileStream o = File.Create(path))
                            v.Recover(f, o, CancellationToken.None, null);
                    }
                    byte[] head = new byte[(int)Math.Min(4096, new FileInfo(path).Length)];
                    using (FileStream s = File.OpenRead(path)) s.Read(head, 0, head.Length);
                    if (NtfsVolume.AllZero(head))
                    {
                        Warn(f.Name + " reads as zeros: its data has been erased (on an SSD, Windows tells the drive to wipe freed space). It cannot be recovered.");
                        return;
                    }
                }
                catch (Exception ex) { Warn("Can't preview " + h.Name + ": " + ex.Message); return; }
            }
            try
            {
                if (Ocr.IsImage(path)) Shell.OpenDefault(path);
                else
                {
                    FileHit view = new FileHit();
                    view.Path = path; view.IoPath = LongPath.Add(path); view.Name = h.Name; view.Folder = h.Folder; view.Size = new FileInfo(path).Length;
                    new ViewerForm(view, CurrentViewOptions()).Show(this);
                }
            }
            catch (Exception ex) { Warn("Can't open " + h.Name + ": " + ex.Message); }
        }

        private void CleanUpPreviews()
        {
            try { if (Directory.Exists(previewDir)) Directory.Delete(previewDir, true); } catch (Exception) { }
        }

        // ------------------------------------------------------------------ recovery

        /// <summary>Recovers the selected deleted files into a folder - asked once, then remembered for the session.</summary>
        private void RecoverSelected(string folder)
        {
            List<FileHit> items = new List<FileHit>();
            foreach (FileHit h in results.SelectedItems) if (IsDeletedRow(h)) items.Add(h);
            if (items.Count == 0) return;
            while (folder == null)
            {
                folder = FolderPicker.Pick(this, recoveryFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop));
                if (folder == null) return;
                // Writing to the same drive can land on the very clusters still holding other deleted files.
                if (deletedRoot != null && string.Equals(Path.GetPathRoot(folder), deletedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    DialogResult r = MessageBox.Show(this,
                        "That folder is on " + deletedRoot + ", the drive the files were deleted from.\n\nWriting there can overwrite other deleted files before you get them back. " +
                        "A folder on another drive (a USB stick, a second disk) is safer.\n\nYes: pick another folder\nNo: recover to " + folder + " anyway",
                        "Same drive", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
                    if (r == DialogResult.Cancel) return;
                    if (r == DialogResult.Yes) folder = null;
                }
            }
            recoveryFolder = folder;
            List<string> problemsNow = new List<string>();
            string root = deletedRoot, dest = folder;
            ProgressDialog.Run(this, "Recovering " + (items.Count == 1 ? "1 file" : items.Count.ToString("N0") + " files"), folder,
                delegate(Action<string, double> report, CancellationToken ct)
                {
                    int ok = 0, damaged = 0, failed = 0;
                    NtfsVolume v = null;
                    try
                    {
                        for (int i = 0; i < items.Count; i++)
                        {
                            ct.ThrowIfCancellationRequested();
                            FileHit h = items[i];
                            report("Recovering " + (i + 1).ToString("N0") + " of " + items.Count.ToString("N0") + ":  " + h.Name, (double)i / items.Count);
                            string target = FileOps.UniqueName(dest, h.Name);
                            try
                            {
                                if (h.Source == "RECYCLE BIN")
                                {
                                    RecycledFile rf = (RecycledFile)h.Tag;
                                    if (rf.IsFolder) CopyFolder(rf.DataPath, target);
                                    else File.Copy(h.IoPath, LongPath.Add(target), false);
                                }
                                else
                                {
                                    DeletedFile f = (DeletedFile)h.Tag;
                                    if (v == null) v = new NtfsVolume(RawDisk.OpenVolume(root), root);
                                    using (FileStream o = new FileStream(LongPath.Add(target), FileMode.CreateNew, FileAccess.Write))
                                        v.Recover(f, o, ct, null);
                                    if (f.Modified != DateTime.MinValue) File.SetLastWriteTime(LongPath.Add(target), f.Modified);
                                    if (f.Chance < 2 || h.Weak) damaged++;
                                }
                                ok++;
                            }
                            catch (OperationCanceledException) { throw; }
                            catch (Exception ex) { failed++; lock (problemsNow) problemsNow.Add(h.Path + ": not recovered (" + ex.Message + ")"); }
                        }
                    }
                    finally { if (v != null) v.Dispose(); }
                    string line = "Recovered " + (ok == 1 ? "1 file" : ok.ToString("N0") + " files") + " to " + dest + ".";
                    if (damaged > 0) line += "  " + damaged + " had been partly overwritten or erased, so they may not open.";
                    if (failed > 0) line += "  " + failed + " failed (see notes).";
                    return line;
                });
            if (problemsNow.Count > 0) { problems.AddRange(problemsNow); status.Notes += problemsNow.Count; }
        }

        private static void CopyFolder(string from, string to)
        {
            Directory.CreateDirectory(LongPath.Add(to));
            foreach (string f in Directory.GetFiles(LongPath.Add(from))) File.Copy(f, LongPath.Add(Path.Combine(to, Path.GetFileName(f))), false);
            foreach (string d in Directory.GetDirectories(LongPath.Add(from))) CopyFolder(LongPath.Strip(d), Path.Combine(to, Path.GetFileName(d)));
        }
    }
}
