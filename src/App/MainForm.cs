using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FindAnything.Core;

namespace FindAnything.App
{
    internal sealed class MainForm : Form
    {
        public string StartFolder;

        private readonly AppSettings settings = AppSettings.Load();

        private readonly ComboBox lookIn = new ComboBox();
        private readonly Button browse = new Button();
        private readonly ComboBox findBox = new ComboBox();
        private readonly Button searchButton = new Button();
        private readonly Button stopButton = new Button();
        private readonly ComboBox types = new ComboBox();
        private readonly TextBox excludeTypes = new TextBox();

        private readonly CheckBox matchCase = new CheckBox();
        private readonly CheckBox wildcards = new CheckBox();
        private readonly CheckBox wholeWord = new CheckBox();
        private readonly CheckBox inContents = new CheckBox();
        private readonly CheckBox inNames = new CheckBox();
        private readonly CheckBox insideDocs = new CheckBox();
        private readonly CheckBox utf16 = new CheckBox();
        private readonly TextBox skipFolders = new TextBox();
        private readonly NumericUpDown maxMb = new NumericUpDown();
        private readonly NumericUpDown threads = new NumericUpDown();

        private readonly ListView list = new ListView();
        private readonly StatusStrip status = new StatusStrip();
        private readonly ToolStripStatusLabel statusText = new ToolStripStatusLabel();
        private readonly ToolStripStatusLabel problemsLink = new ToolStripStatusLabel();
        private readonly ToolStripProgressBar progress = new ToolStripProgressBar();
        private readonly ToolTip tips = new ToolTip();
        private readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();

        private readonly List<FileHit> hits = new List<FileHit>();
        private readonly List<string> problems = new List<string>();
        private SearchJob job;
        private SearchSettings jobSettings;
        private CancellationTokenSource cts;
        private Task jobTask;
        private Stopwatch clock = new Stopwatch();
        private int sortColumn = -1;
        private bool sortDescending;
        private bool sortDirty;

        private static readonly string[] TypePresets =
        {
            "(all files)",
            "*.txt",
            "*.txt;*.log;*.csv;*.md;*.ini;*.cfg;*.json;*.xml;*.yaml;*.yml",
            "*.pdf;*.docx;*.doc;*.xlsx;*.xls;*.pptx;*.rtf;*.odt",
            "*.cs;*.ps1;*.py;*.js;*.ts;*.html;*.css;*.c;*.cpp;*.h;*.java;*.sql;*.bat;*.cmd",
            "*.eml;*.msg;*.mbox",
        };

        private const string AllDrivesItem = "(all drives)";

        public MainForm()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = "Find Anything";
            Font = new Font("Segoe UI", 9.75F);
            ClientSize = new Size(1180, 720);
            MinimumSize = new Size(820, 480);
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            BuildList();
            Controls.Add(list);
            Controls.Add(BuildTop());
            BuildStatus();
            Controls.Add(status);

            tick.Interval = 150;
            tick.Tick += OnTick;
            DragEnter += delegate(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += OnDragDrop;
            ResumeLayout(false);
            PerformLayout();
            LoadSettings();
        }

        // ------------------------------------------------------------------ layout

        private Control BuildTop()
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Top;
            t.AutoSize = true;
            t.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            t.Padding = new Padding(10, 10, 10, 4);
            t.ColumnCount = 4;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            // Row 0: where
            lookIn.Dock = DockStyle.Fill;
            lookIn.Margin = new Padding(0, 3, 6, 3);
            lookIn.DropDown += delegate { FillLookIn(); };
            tips.SetToolTip(lookIn, "A folder or drive. Several at once: separate them with ;   (for example  C:\\;D:\\)");
            Button(browse, "Browse...", delegate { OnBrowse(); });
            t.Controls.Add(Label("Look in:"), 0, 0);
            t.Controls.Add(lookIn, 1, 0);
            t.Controls.Add(browse, 2, 0);

            // Row 1: what
            findBox.Dock = DockStyle.Fill;
            findBox.Margin = new Padding(0, 3, 6, 3);
            findBox.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter && !findBox.DroppedDown) { e.SuppressKeyPress = true; StartSearch(); }
            };
            tips.SetToolTip(findBox, "Text to find.  *  = any characters on the same line,  ?  = any one character.");
            Button(searchButton, "Search", delegate { StartSearch(); });
            Button(stopButton, "Stop", delegate { StopSearch(); });
            stopButton.Enabled = false;
            t.Controls.Add(Label("Find:"), 0, 1);
            t.Controls.Add(findBox, 1, 1);
            t.Controls.Add(searchButton, 2, 1);
            t.Controls.Add(stopButton, 3, 1);

            // Row 2: file types
            TableLayoutPanel typeRow = new TableLayoutPanel();
            typeRow.Dock = DockStyle.Fill;
            typeRow.AutoSize = true;
            typeRow.Margin = new Padding(0);
            typeRow.ColumnCount = 3;
            typeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 62F));
            typeRow.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            typeRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 38F));
            types.Dock = DockStyle.Fill;
            types.Margin = new Padding(0, 3, 6, 3);
            types.Items.AddRange(TypePresets);
            tips.SetToolTip(types, "Only search these file types, separated by ;   Empty or (all files) = everything.\n" +
                                   "*.txt finds TXT files only: a DOC containing the text is not counted.");
            excludeTypes.Dock = DockStyle.Fill;
            excludeTypes.Margin = new Padding(0, 3, 6, 3);
            tips.SetToolTip(excludeTypes, "Never search these file types, for example  *.exe;*.dll;*.iso");
            typeRow.Controls.Add(types, 0, 0);
            typeRow.Controls.Add(Label("Except:"), 1, 0);
            typeRow.Controls.Add(excludeTypes, 2, 0);
            t.Controls.Add(Label("File types:"), 0, 2);
            t.Controls.Add(typeRow, 1, 2);
            t.SetColumnSpan(typeRow, 3);

            // Row 3: how to match
            FlowLayoutPanel opts = Flow();
            opts.Controls.Add(Check(matchCase, "Match case", "Upper and lower case must match exactly."));
            opts.Controls.Add(Check(wildcards, "Wildcards  * ?", "Off: * and ? are searched for as plain characters."));
            opts.Controls.Add(Check(wholeWord, "Whole word", "\"Govee\" does not match inside \"Govees\"."));
            opts.Controls.Add(Separator());
            opts.Controls.Add(Check(inContents, "Inside files", "Search the text inside each file."));
            opts.Controls.Add(Check(inNames, "File names", "Also list files whose name matches."));
            opts.Controls.Add(Separator());
            opts.Controls.Add(Check(insideDocs, "Read PDF and Office text",
                "Pull the real text out of PDF, DOCX, XLSX, PPTX and OpenDocument files before searching.\n" +
                "Their text is compressed, so a plain byte search would miss it."));
            opts.Controls.Add(Check(utf16, "Unicode (UTF-16) text",
                "Also find text stored two bytes per character: Notepad \"Unicode\" files, old .doc files, programs."));
            t.Controls.Add(opts, 1, 3);
            t.SetColumnSpan(opts, 3);

            // Row 4: limits
            FlowLayoutPanel limits = Flow();
            limits.Controls.Add(Label("Skip folders:"));
            skipFolders.Width = 300;
            skipFolders.Margin = new Padding(0, 3, 14, 3);
            tips.SetToolTip(skipFolders, "Folder names to skip, separated by ;   Wildcards work:  node_modules;.git;*cache*");
            limits.Controls.Add(skipFolders);
            limits.Controls.Add(Label("Skip files over"));
            maxMb.Minimum = 0;
            maxMb.Maximum = 100000000;
            maxMb.Width = 90;
            maxMb.Margin = new Padding(0);   // NumericUpDown margins scale wrongly at high DPI; spacing comes from labels
            maxMb.ThousandsSeparator = true;
            tips.SetToolTip(maxMb, "0 = no limit. Big files are streamed in 4 MB pieces, so they never fill memory; this only saves time.");
            limits.Controls.Add(maxMb);
            limits.Controls.Add(Label("MB  (0 = no limit)"));
            limits.Controls.Add(Label("   Threads"));
            threads.Minimum = 1;
            threads.Maximum = 32;
            threads.Width = 56;
            threads.Margin = new Padding(0);
            tips.SetToolTip(threads, "Files searched at the same time. 4-8 suits SSDs; use 1-2 for spinning drives.");
            limits.Controls.Add(threads);
            t.Controls.Add(limits, 1, 4);
            t.SetColumnSpan(limits, 3);

            t.RowCount = 5;
            for (int i = 0; i < 5; i++) t.RowStyles.Add(new RowStyle(SizeType.AutoSize));
            return t;
        }

        private static Label Label(string text)
        {
            Label l = new Label();
            l.Text = text;
            l.AutoSize = true;
            l.Anchor = AnchorStyles.Left;
            l.Margin = new Padding(0, 6, 6, 0);
            return l;
        }

        private static void Button(Button b, string text, EventHandler click)
        {
            b.Text = text;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(10, 2, 10, 2);
            b.Margin = new Padding(0, 2, 6, 2);
            b.Click += click;
        }

        private CheckBox Check(CheckBox c, string text, string tip)
        {
            c.Text = text;
            c.AutoSize = true;
            c.Margin = new Padding(0, 4, 14, 2);
            tips.SetToolTip(c, tip);
            return c;
        }

        private static FlowLayoutPanel Flow()
        {
            FlowLayoutPanel p = new FlowLayoutPanel();
            p.AutoSize = true;
            p.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            p.Dock = DockStyle.Fill;
            p.WrapContents = true;
            p.Margin = new Padding(0, 2, 0, 2);
            return p;
        }

        private static Control Separator()
        {
            // Plain space between option groups. (A drawn divider gets its size scaled twice at 200%.)
            Label l = new Label();
            l.AutoSize = true;
            l.Text = "";
            l.Margin = new Padding(0, 0, 18, 0);
            return l;
        }

        private void BuildList()
        {
            list.Dock = DockStyle.Fill;
            list.View = View.Details;
            list.FullRowSelect = true;
            list.GridLines = false;
            list.HideSelection = false;
            list.VirtualMode = true;
            list.Columns.Add("Name", 230);
            list.Columns.Add("Folder", 330);
            list.Columns.Add("Hits", 60, HorizontalAlignment.Right);
            list.Columns.Add("Line", 70, HorizontalAlignment.Right);
            list.Columns.Add("Found in", 110);
            list.Columns.Add("First match", 420);
            list.Columns.Add("Size", 85, HorizontalAlignment.Right);
            list.Columns.Add("Modified", 140);
            list.RetrieveVirtualItem += OnRetrieveItem;
            list.ColumnClick += OnColumnClick;
            list.DoubleClick += delegate { OpenViewer(); };
            list.KeyDown += OnListKeyDown;
            list.ContextMenuStrip = BuildMenu();
        }

        protected override void OnLoad(EventArgs e)
        {
            base.OnLoad(e);
            // Column widths are not scaled by AutoScaleMode, so scale them here.
            float k;
            using (Graphics g = CreateGraphics()) k = g.DpiX / 96f;   // DeviceDpi stays 96 without an app.config
            foreach (ColumnHeader c in list.Columns) c.Width = (int)(c.Width * k);
        }

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            ToolStripMenuItem view = new ToolStripMenuItem("Open in viewer (highlights matches)", null, delegate { OpenViewer(); });
            view.ShortcutKeyDisplayString = "Enter";
            view.Font = new Font(m.Font, FontStyle.Bold);
            ToolStripMenuItem def = new ToolStripMenuItem("Open with its usual program", null, delegate { OpenDefault(); });
            ToolStripMenuItem npp = new ToolStripMenuItem("Open in Notepad++ at the match", null, delegate { OpenNotepadPlusPlus(); });
            ToolStripMenuItem folder = new ToolStripMenuItem("Show in folder", null, delegate { ForSelected(delegate(FileHit h) { Shell.ShowInFolder(h.Path); }); });
            ToolStripMenuItem copy = new ToolStripMenuItem("Copy path", null, delegate { CopyPaths(); });
            copy.ShortcutKeyDisplayString = "Ctrl+C";
            ToolStripMenuItem export = new ToolStripMenuItem("Save the list as CSV...", null, delegate { ExportCsv(); });
            m.Items.AddRange(new ToolStripItem[] { view, def, npp, new ToolStripSeparator(), folder, copy, new ToolStripSeparator(), export });
            m.Opening += delegate(object s, System.ComponentModel.CancelEventArgs e)
            {
                bool any = list.SelectedIndices.Count > 0;
                view.Enabled = def.Enabled = folder.Enabled = copy.Enabled = any;
                npp.Visible = Shell.NotepadPlusPlus != null;
                npp.Enabled = any;
                export.Enabled = hits.Count > 0;
            };
            return m;
        }

        private void BuildStatus()
        {
            statusText.Spring = true;
            statusText.TextAlign = ContentAlignment.MiddleLeft;
            statusText.Text = "Pick a folder or drive, type what to find, press Enter.";
            problemsLink.IsLink = true;
            problemsLink.Visible = false;
            problemsLink.Click += delegate { ShowProblems(); };
            progress.Style = ProgressBarStyle.Marquee;
            progress.Visible = false;
            ToolStripStatusLabel about = new ToolStripStatusLabel("About");
            about.IsLink = true;
            about.Margin = new Padding(12, 3, 0, 2);
            about.Click += delegate { ShowAbout(); };
            status.Items.AddRange(new ToolStripItem[] { statusText, problemsLink, progress, about });
        }

        private void ShowAbout()
        {
            Form f = new Form();
            f.Text = "About " + AppInfo.Name;
            f.Font = Font;
            f.AutoScaleMode = AutoScaleMode.Dpi;
            f.AutoScaleDimensions = new SizeF(96F, 96F);
            f.FormBorderStyle = FormBorderStyle.FixedDialog;
            f.MaximizeBox = f.MinimizeBox = false;
            f.ShowInTaskbar = false;
            f.StartPosition = FormStartPosition.CenterParent;
            f.AutoSize = true;
            f.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            f.Padding = new Padding(20);

            FlowLayoutPanel p = new FlowLayoutPanel();
            p.FlowDirection = FlowDirection.TopDown;
            p.AutoSize = true;
            p.WrapContents = false;
            p.Padding = new Padding(14, 10, 14, 8);
            Label title = new Label();
            title.Text = AppInfo.Name + "  " + AppInfo.Version;
            title.Font = new Font(Font.FontFamily, 14F, FontStyle.Bold);
            title.AutoSize = true;
            Label body = new Label();
            body.Text = "Find text inside any file: plain text, PDF, Office, Unicode, and files of any size.\n\n" +
                        "Created by " + AppInfo.Author + ".\nFree and open source under the " + AppInfo.License + ".";
            body.AutoSize = true;
            body.Margin = new Padding(0, 10, 0, 10);
            LinkLabel link = new LinkLabel();
            link.Text = AppInfo.Url;
            link.AutoSize = true;
            link.LinkClicked += delegate { try { Shell.OpenDefault(AppInfo.Url); } catch (Exception) { } };
            Button ok = new Button();
            ok.Text = "OK";
            ok.AutoSize = true;
            ok.DialogResult = DialogResult.OK;
            ok.Margin = new Padding(0, 16, 0, 0);
            f.AcceptButton = ok;
            p.Controls.AddRange(new Control[] { title, body, link, ok });
            f.Controls.Add(p);
            f.ShowDialog(this);
        }

        // ------------------------------------------------------------------ settings

        private void LoadSettings()
        {
            foreach (string s in settings.GetList("Recent.LookIn")) lookIn.Items.Add(s);
            foreach (string s in settings.GetList("Recent.Find")) findBox.Items.Add(s);
            lookIn.Text = settings.Get("LookIn", Environment.GetFolderPath(Environment.SpecialFolder.UserProfile));
            findBox.Text = settings.Get("Find", "");
            types.Text = settings.Get("Types", TypePresets[0]);
            excludeTypes.Text = settings.Get("ExcludeTypes", "");
            matchCase.Checked = settings.GetBool("MatchCase", false);
            wildcards.Checked = settings.GetBool("Wildcards", true);
            wholeWord.Checked = settings.GetBool("WholeWord", false);
            inContents.Checked = settings.GetBool("InContents", true);
            inNames.Checked = settings.GetBool("InNames", true);
            insideDocs.Checked = settings.GetBool("InsideDocs", true);
            utf16.Checked = settings.GetBool("Utf16", true);
            skipFolders.Text = settings.Get("SkipFolders", "System Volume Information;$Recycle.Bin");
            maxMb.Value = Math.Min(maxMb.Maximum, Math.Max(0, settings.GetLong("MaxMB", 0)));
            threads.Value = Math.Min(threads.Maximum, Math.Max(1, settings.GetLong("Threads", Math.Min(8, Environment.ProcessorCount / 2))));
        }

        private void SaveSettings()
        {
            settings.Set("LookIn", lookIn.Text);
            settings.Set("Find", findBox.Text);
            settings.Set("Types", types.Text);
            settings.Set("ExcludeTypes", excludeTypes.Text);
            settings.Set("MatchCase", matchCase.Checked);
            settings.Set("Wildcards", wildcards.Checked);
            settings.Set("WholeWord", wholeWord.Checked);
            settings.Set("InContents", inContents.Checked);
            settings.Set("InNames", inNames.Checked);
            settings.Set("InsideDocs", insideDocs.Checked);
            settings.Set("Utf16", utf16.Checked);
            settings.Set("SkipFolders", skipFolders.Text);
            settings.Set("MaxMB", (long)maxMb.Value);
            settings.Set("Threads", (long)threads.Value);
            settings.SetList("Recent.LookIn", Recent(lookIn));
            settings.SetList("Recent.Find", Recent(findBox));
            settings.Save();
        }

        private static List<string> Recent(ComboBox c)
        {
            List<string> l = new List<string>();
            foreach (object o in c.Items)
            {
                string s = o.ToString();
                if (s != AllDrivesItem && !l.Contains(s) && s.Length > 0) l.Add(s);
            }
            return l;
        }

        private static void Remember(ComboBox c, string value)
        {
            if (string.IsNullOrEmpty(value)) return;
            int i = c.Items.IndexOf(value);
            if (i >= 0) c.Items.RemoveAt(i);
            c.Items.Insert(0, value);
            while (c.Items.Count > 20) c.Items.RemoveAt(c.Items.Count - 1);
            c.Text = value;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (StartFolder != null) lookIn.Text = StartFolder;
            findBox.Focus();
            findBox.SelectAll();
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (cts != null) cts.Cancel();
            SaveSettings();
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && job != null) { StopSearch(); return true; }
            if (keyData == (Keys.Control | Keys.F)) { findBox.Focus(); findBox.SelectAll(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ------------------------------------------------------------------ look in

        private void FillLookIn()
        {
            List<string> recent = Recent(lookIn);
            string text = lookIn.Text;
            lookIn.Items.Clear();
            foreach (string r in recent) lookIn.Items.Add(r);
            lookIn.Items.Add(AllDrivesItem);
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    if (!lookIn.Items.Contains(d.Name)) lookIn.Items.Add(d.Name);
                }
                catch (Exception) { }
            }
            lookIn.Text = text;
        }

        private void OnBrowse()
        {
            string start = lookIn.Text.Split(';')[0].Trim();
            string picked = FolderPicker.Pick(this, start);
            if (picked != null) lookIn.Text = picked;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files != null && files.Length > 0) lookIn.Text = string.Join(";", files);
        }

        private List<string> Roots()
        {
            List<string> roots = new List<string>();
            string text = lookIn.Text.Trim();
            if (text == AllDrivesItem)
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable)) roots.Add(d.Name);
                    }
                    catch (Exception) { }
                }
                return roots;
            }
            foreach (string part in text.Split(';'))
            {
                string p = part.Trim().Trim('"');
                if (p.Length == 0) continue;
                if (p.Length == 2 && p[1] == ':') p += "\\";
                roots.Add(p);
            }
            return roots;
        }

        // ------------------------------------------------------------------ search

        private void StartSearch()
        {
            if (job != null) return;
            SearchSettings s = new SearchSettings();
            s.Roots = Roots();
            s.Text = findBox.Text;
            s.CaseSensitive = matchCase.Checked;
            s.Wildcards = wildcards.Checked;
            s.WholeWord = wholeWord.Checked;
            s.SearchContents = inContents.Checked;
            s.SearchNames = inNames.Checked;
            s.IncludeMasks = types.Text == TypePresets[0] ? "" : types.Text;
            s.ExcludeMasks = excludeTypes.Text;
            s.SkipFolders = skipFolders.Text;
            s.LookInsideDocuments = insideDocs.Checked;
            s.Utf16 = utf16.Checked;
            s.MaxFileBytes = (long)maxMb.Value * 1024 * 1024;
            s.Threads = (int)threads.Value;

            if (s.Roots.Count == 0) { Warn("Pick a folder or drive to look in."); lookIn.Focus(); return; }
            foreach (string r in s.Roots)
                if (!Directory.Exists(LongPath.Add(r)) && !File.Exists(LongPath.Add(r))) { Warn("Can't find \"" + r + "\"."); lookIn.Focus(); return; }

            SearchJob newJob;
            try { newJob = new SearchJob(s); }
            catch (ArgumentException ex) { Warn(ex.Message); findBox.Focus(); return; }

            Remember(findBox, findBox.Text);
            if (lookIn.Text != AllDrivesItem) Remember(lookIn, lookIn.Text);
            SaveSettings();

            hits.Clear();
            problems.Clear();
            list.VirtualListSize = 0;
            list.Invalidate();
            sortColumn = -1;
            UpdateSortArrows();

            job = newJob;
            jobSettings = s;
            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            SearchJob running = job;
            jobTask = Task.Factory.StartNew(delegate { running.Run(token); }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            clock = Stopwatch.StartNew();
            searchButton.Enabled = false;
            stopButton.Enabled = true;
            progress.Visible = true;
            problemsLink.Visible = false;
            tick.Start();
        }

        private void StopSearch()
        {
            if (cts != null) cts.Cancel();
            statusText.Text = "Stopping...";
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (job == null) { tick.Stop(); return; }
            bool done = jobTask.IsCompleted;

            int before = hits.Count;
            FileHit h;
            while (job.Hits.TryDequeue(out h)) hits.Add(h);
            string p;
            while (job.Errors.TryDequeue(out p)) problems.Add(p);
            if (hits.Count != before || (sortDirty && done))
            {
                if (sortColumn >= 0) { SortHits(); sortDirty = false; }
                list.VirtualListSize = hits.Count;
                list.Invalidate();
            }

            long errors = Interlocked.Read(ref job.ErrorCount);
            problemsLink.Visible = errors > 0;
            problemsLink.Text = errors == 1 ? "1 note" : errors.ToString("N0") + " notes";

            string counts = string.Format("{0:N0} found  |  {1:N0} files searched  |  {2} read",
                hits.Count, Interlocked.Read(ref job.FilesSearched), Fmt.Size(Interlocked.Read(ref job.BytesRead)));
            long seen = Interlocked.Read(ref job.FilesSeen), searched = Interlocked.Read(ref job.FilesSearched);
            if (seen != searched) counts += string.Format("  |  {0:N0} other types skipped", seen - searched);
            long big = Interlocked.Read(ref job.SkippedLarge), cloud = Interlocked.Read(ref job.SkippedCloud);
            if (big > 0) counts += string.Format("  |  {0:N0} too big", big);
            if (cloud > 0) counts += string.Format("  |  {0:N0} online-only (not downloaded)", cloud);

            if (!done)
            {
                statusText.Text = counts + "  |  " + job.Current;
                return;
            }

            tick.Stop();
            if (sortColumn < 0)
            {
                // Results arrive in whatever order the threads finish; settle them into path order.
                hits.Sort(delegate(FileHit a, FileHit b) { return string.Compare(a.Path, b.Path, StringComparison.CurrentCultureIgnoreCase); });
                list.Invalidate();
            }
            bool stopped = cts.IsCancellationRequested;
            statusText.Text = (stopped ? "Stopped after " : "Done in ") + clock.Elapsed.TotalSeconds.ToString("0.0") + " s.  " + counts;
            progress.Visible = false;
            searchButton.Enabled = true;
            stopButton.Enabled = false;
            job = null;
            cts.Dispose();
            cts = null;
            if (hits.Count > 0 && list.SelectedIndices.Count == 0)
            {
                list.SelectedIndices.Add(0);
                list.FocusedItem = list.Items[0];
            }
        }

        // ------------------------------------------------------------------ list

        private void OnRetrieveItem(object sender, RetrieveVirtualItemEventArgs e)
        {
            FileHit h = e.ItemIndex < hits.Count ? hits[e.ItemIndex] : null;
            if (h == null) { e.Item = new ListViewItem(new[] { "", "", "", "", "", "", "", "" }); return; }
            e.Item = new ListViewItem(new[]
            {
                h.Name,
                h.Folder,
                h.HitsText,
                h.ContentHits > 0 ? h.FirstLine.ToString("N0") : "",
                h.MatchText,
                h.Preview ?? "",
                Fmt.Size(h.Size),
                h.Modified == DateTime.MinValue ? "" : h.Modified.ToString("yyyy-MM-dd HH:mm")
            });
        }

        private void OnColumnClick(object sender, ColumnClickEventArgs e)
        {
            if (sortColumn == e.Column) sortDescending = !sortDescending;
            else { sortColumn = e.Column; sortDescending = e.Column == 2 || e.Column == 6 || e.Column == 7; }
            FileHit selected = Selected();
            SortHits();
            UpdateSortArrows();
            list.Invalidate();
            if (selected != null)
            {
                list.SelectedIndices.Clear();
                int i = hits.IndexOf(selected);
                if (i >= 0) { list.SelectedIndices.Add(i); list.EnsureVisible(i); }
            }
            if (job != null) sortDirty = true;
        }

        private void SortHits()
        {
            int col = sortColumn;
            int dir = sortDescending ? -1 : 1;
            hits.Sort(delegate(FileHit a, FileHit b)
            {
                int r;
                switch (col)
                {
                    case 0: r = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); break;
                    case 1: r = string.Compare(a.Folder, b.Folder, StringComparison.CurrentCultureIgnoreCase); break;
                    case 2: r = a.ContentHits.CompareTo(b.ContentHits); break;
                    case 3: r = a.FirstLine.CompareTo(b.FirstLine); break;
                    case 4: r = string.Compare(a.MatchText, b.MatchText, StringComparison.Ordinal); break;
                    case 5: r = string.Compare(a.Preview ?? "", b.Preview ?? "", StringComparison.CurrentCultureIgnoreCase); break;
                    case 6: r = a.Size.CompareTo(b.Size); break;
                    default: r = a.Modified.CompareTo(b.Modified); break;
                }
                if (r == 0) r = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
                return r * dir;
            });
        }

        private void UpdateSortArrows()
        {
            string[] names = { "Name", "Folder", "Hits", "Line", "Found in", "First match", "Size", "Modified" };
            for (int i = 0; i < names.Length; i++)
                list.Columns[i].Text = names[i] + (i == sortColumn ? (sortDescending ? "  \u25BC" : "  \u25B2") : "");
        }

        private void OnListKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; OpenViewer(); }
            else if (e.KeyCode == Keys.C && e.Control) { e.SuppressKeyPress = true; CopyPaths(); }
            else if (e.KeyCode == Keys.A && e.Control)
            {
                e.SuppressKeyPress = true;
                for (int i = 0; i < hits.Count; i++) list.SelectedIndices.Add(i);
            }
        }

        private FileHit Selected()
        {
            if (list.SelectedIndices.Count == 0) return null;
            int i = list.SelectedIndices[0];
            return i < hits.Count ? hits[i] : null;
        }

        private void ForSelected(Action<FileHit> action)
        {
            foreach (int i in list.SelectedIndices)
            {
                if (i >= hits.Count) continue;
                try { action(hits[i]); }
                catch (Exception ex) { Warn(ex.Message); return; }
            }
        }

        private ViewOptions CurrentViewOptions()
        {
            ViewOptions o = new ViewOptions();
            SearchSettings s = jobSettings;
            o.Text = s != null ? s.Text : findBox.Text;
            o.CaseSensitive = s != null ? s.CaseSensitive : matchCase.Checked;
            o.Wildcards = s != null ? s.Wildcards : wildcards.Checked;
            o.WholeWord = s != null ? s.WholeWord : wholeWord.Checked;
            o.Utf16 = s != null ? s.Utf16 : utf16.Checked;
            return o;
        }

        private void OpenViewer()
        {
            FileHit h = Selected();
            if (h == null) return;
            try
            {
                ViewerForm v = new ViewerForm(h, CurrentViewOptions());
                v.Show(this);
            }
            catch (Exception ex) { Warn("Can't open " + h.Name + ": " + ex.Message); }
        }

        /// <summary>Asks before handing a huge file to a program that loads it whole.</summary>
        private bool ConfirmBig(FileHit h, string program)
        {
            if (h.Size < Shell.BigFileBytes) return true;
            DialogResult r = MessageBox.Show(this,
                h.Name + " is " + Fmt.Size(h.Size) + ".\n\n" + program + " loads the whole file into memory and may freeze or crash.\n\n" +
                "Yes: open it in the built-in viewer instead (instant, any size)\nNo: open it in " + program + " anyway",
                "Big file", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes) { new ViewerForm(h, CurrentViewOptions()).Show(this); return false; }
            return r == DialogResult.No;
        }

        private void OpenDefault()
        {
            ForSelected(delegate(FileHit h) { if (ConfirmBig(h, "Its program")) Shell.OpenDefault(h.Path); });
        }

        private void OpenNotepadPlusPlus()
        {
            ForSelected(delegate(FileHit h)
            {
                if (ConfirmBig(h, "Notepad++")) Shell.OpenInNotepadPlusPlus(h.Path, h.Source.Length == 0 ? h.FirstLine : 0);
            });
        }

        private void CopyPaths()
        {
            StringBuilder sb = new StringBuilder();
            foreach (int i in list.SelectedIndices)
                if (i < hits.Count) sb.Append(hits[i].Path).Append("\r\n");
            if (sb.Length > 0) Clipboard.SetText(sb.ToString().TrimEnd());
        }

        private void ExportCsv()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "CSV (*.csv)|*.csv";
                d.FileName = "Find Anything results.csv";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                StringBuilder sb = new StringBuilder();
                sb.Append("Path,Name,Folder,Hits,Line,Found in,First match,Size,Modified\r\n");
                foreach (FileHit h in hits)
                {
                    sb.Append(Csv(h.Path)).Append(',').Append(Csv(h.Name)).Append(',').Append(Csv(h.Folder)).Append(',')
                      .Append(h.HitsText).Append(',').Append(h.ContentHits > 0 ? h.FirstLine.ToString() : "").Append(',')
                      .Append(Csv(h.MatchText)).Append(',').Append(Csv(h.Preview ?? "")).Append(',')
                      .Append(h.Size).Append(',').Append(h.Modified.ToString("yyyy-MM-dd HH:mm:ss")).Append("\r\n");
                }
                try { File.WriteAllText(d.FileName, sb.ToString(), new UTF8Encoding(true)); }
                catch (Exception ex) { Warn(ex.Message); }
            }
        }

        private static string Csv(string s)
        {
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        private void ShowProblems()
        {
            Form f = new Form();
            f.Text = "Notes from the search";
            f.Font = Font;
            f.AutoScaleMode = AutoScaleMode.Dpi;
            f.AutoScaleDimensions = new SizeF(96F, 96F);
            f.ClientSize = new Size(900, 420);
            f.StartPosition = FormStartPosition.CenterParent;
            TextBox t = new TextBox();
            t.Multiline = true;
            t.ReadOnly = true;
            t.ScrollBars = ScrollBars.Both;
            t.WordWrap = false;
            t.Dock = DockStyle.Fill;
            t.Font = new Font("Consolas", 9.75F);
            StringBuilder sb = new StringBuilder();
            sb.Append("Files and folders that could not be read (locked by Windows, access denied), and documents with no\r\n");
            sb.Append("searchable text (scanned PDFs, password-protected PDFs).\r\n\r\n");
            foreach (string p in problems) sb.Append(p).Append("\r\n");
            if (problems.Count >= SearchJob.MaxErrorsKept) sb.Append("(only the first " + SearchJob.MaxErrorsKept + " are kept)\r\n");
            t.Text = sb.ToString();
            f.Controls.Add(t);
            f.Show(this);
            t.Select(0, 0);
        }

        private void Warn(string message)
        {
            MessageBox.Show(this, message, "Find Anything", MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }
}
