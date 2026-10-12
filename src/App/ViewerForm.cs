using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FindAnything.Core;

namespace FindAnything.App
{
    internal sealed class ViewOptions
    {
        public string Text = "";
        public bool CaseSensitive;
        public bool Wildcards = true;
        public bool WholeWord;
        public bool Utf16 = true;
    }

    /// <summary>Random-access bytes: the file itself, or text pulled out of a PDF / Office document.</summary>
    internal sealed class ByteSource : IDisposable
    {
        private readonly FileStream fs;
        private readonly byte[] mem;
        public readonly long Length;

        public ByteSource(string ioPath)
        {
            fs = new FileStream(ioPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, FileOptions.RandomAccess);
            Length = fs.Length;
        }

        public ByteSource(byte[] data)
        {
            mem = data;
            Length = data.Length;
        }

        public int Read(long offset, byte[] buf, int index, int count)
        {
            if (offset >= Length || count <= 0) return 0;
            if (mem != null)
            {
                int n = (int)Math.Min(count, Length - offset);
                Buffer.BlockCopy(mem, (int)offset, buf, index, n);
                return n;
            }
            lock (fs)
            {
                fs.Position = offset;
                int got = 0;
                while (got < count)
                {
                    int n = fs.Read(buf, index + got, count - got);
                    if (n <= 0) break;
                    got += n;
                }
                return got;
            }
        }

        public void Dispose()
        {
            if (fs != null) fs.Dispose();
        }
    }

    /// <summary>
    /// Shows one window of a file at a time (1 MB; small files whole), so a 25 GB file opens instantly and
    /// never fills memory. Matches are highlighted; Next / Previous search the whole file in the background.
    /// </summary>
    internal sealed class ViewerForm : Form
    {
        private const int WindowBytes = 1 << 20;
        private const int WholeFileBytes = 4 << 20;
        private const int MaxHighlights = 5000;

        private readonly FileHit hit;
        private readonly ViewOptions opts;
        private ByteSource source;
        private bool extracted;     // source is UTF-8 text pulled out of a document

        private readonly TextBox findText = new TextBox();
        private readonly CheckBox matchCase = new CheckBox();
        private readonly Button prevButton = new Button();
        private readonly Button nextButton = new Button();
        private readonly Label findStatus = new Label();
        private readonly ComboBox encodingBox = new ComboBox();
        private readonly CheckBox wrap = new CheckBox();
        private readonly RichTextBox text = new RichTextBox();
        private readonly HScrollBar position = new HScrollBar();
        private readonly Label windowInfo = new Label();
        private readonly Button firstPage = new Button(), prevPage = new Button(), nextPage = new Button(), lastPage = new Button();

        private BytePattern pattern;
        private long windowStart, windowEnd;
        private byte[] windowBytes = new byte[0];
        private TextForm windowForm;
        private readonly List<long[]> windowMatches = new List<long[]>();   // absolute {start, end, charStart, charLen}
        private long curStart = -1, curEnd = -1;
        private CancellationTokenSource searchCts;
        private bool scrolling;

        public ViewerForm(FileHit hit, ViewOptions opts)
        {
            this.hit = hit;
            this.opts = opts;

            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = hit.Name + "  -  " + (hit.Folder ?? Path.GetDirectoryName(hit.Path));
            Font = new Font("Segoe UI", 9.75F);
            ClientSize = new Size(1100, 720);
            MinimumSize = new Size(640, 360);
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            text.Dock = DockStyle.Fill;
            text.ReadOnly = true;
            text.BackColor = SystemColors.Window;
            text.HideSelection = false;
            text.DetectUrls = false;
            text.WordWrap = true;
            text.Font = new Font("Consolas", 10.5F);
            text.BorderStyle = BorderStyle.None;
            text.ScrollBars = RichTextBoxScrollBars.Both;
            Controls.Add(text);
            Controls.Add(BuildTop());
            Controls.Add(BuildBottom());
            ResumeLayout(false);
            PerformLayout();

            findText.Text = opts.Text;
            matchCase.Checked = opts.CaseSensitive;
        }

        private Control BuildTop()
        {
            TableLayoutPanel t = new TableLayoutPanel();
            t.Dock = DockStyle.Top;
            t.AutoSize = true;
            t.Padding = new Padding(8, 8, 8, 4);
            t.ColumnCount = 9;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 40F));
            for (int i = 0; i < 4; i++) t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 60F));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            findText.Dock = DockStyle.Fill;
            findText.Margin = new Padding(0, 3, 6, 3);
            findText.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter)
                {
                    e.SuppressKeyPress = true;
                    if (e.Shift) GoPrevious(); else GoNext();
                }
            };
            findText.TextChanged += delegate { pattern = null; };
            matchCase.Text = "Match case";
            matchCase.AutoSize = true;
            matchCase.Margin = new Padding(0, 5, 8, 0);
            matchCase.CheckedChanged += delegate { pattern = null; RefreshHighlights(); };
            Btn(prevButton, "< Previous", delegate { GoPrevious(); });
            Btn(nextButton, "Next >", delegate { GoNext(); });
            findStatus.AutoSize = true;
            findStatus.Anchor = AnchorStyles.Left;
            findStatus.Margin = new Padding(6, 6, 6, 0);
            findStatus.ForeColor = SystemColors.GrayText;

            encodingBox.DropDownStyle = ComboBoxStyle.DropDownList;
            encodingBox.Items.AddRange(new object[] { "Auto", "UTF-8", "UTF-16 LE", "Windows-1252" });
            encodingBox.SelectedIndex = 0;
            encodingBox.Width = 120;
            encodingBox.Margin = new Padding(0, 3, 8, 3);
            encodingBox.SelectedIndexChanged += delegate { pattern = null; LoadWindow(windowStart, curStart, false); };
            wrap.Text = "Wrap";
            wrap.AutoSize = true;
            wrap.Checked = true;
            wrap.Margin = new Padding(0, 5, 0, 0);
            wrap.CheckedChanged += delegate { text.WordWrap = wrap.Checked; ScrollToCurrent(); };

            Label find = new Label();
            find.Text = "Find:";
            find.AutoSize = true;
            find.Anchor = AnchorStyles.Left;
            find.Margin = new Padding(0, 6, 6, 0);

            t.Controls.Add(find, 0, 0);
            t.Controls.Add(findText, 1, 0);
            t.Controls.Add(matchCase, 2, 0);
            t.Controls.Add(prevButton, 3, 0);
            t.Controls.Add(nextButton, 4, 0);
            t.Controls.Add(new Label { AutoSize = true, Margin = new Padding(0) }, 5, 0);
            t.Controls.Add(findStatus, 6, 0);
            t.Controls.Add(encodingBox, 7, 0);
            t.Controls.Add(wrap, 8, 0);

            ContextMenuStrip more = new ContextMenuStrip();
            more.Items.Add("Open with its usual program", null, delegate { OpenDefault(); });
            more.Items.Add("Show in folder", null, delegate { Shell.ShowInFolder(hit.Path); });
            more.Items.Add("Copy path", null, delegate { Clipboard.SetText(hit.Path); });
            text.ContextMenuStrip = BuildTextMenu(more);
            return t;
        }

        private ContextMenuStrip BuildTextMenu(ContextMenuStrip more)
        {
            ContextMenuStrip m = new ContextMenuStrip();
            ToolStripMenuItem copy = new ToolStripMenuItem("Copy", null, delegate { if (text.SelectionLength > 0) text.Copy(); });
            copy.ShortcutKeyDisplayString = "Ctrl+C";
            m.Items.Add(copy);
            m.Items.Add(new ToolStripSeparator());
            while (more.Items.Count > 0) m.Items.Add(more.Items[0]);
            return m;
        }

        private Control BuildBottom()
        {
            TableLayoutPanel b = new TableLayoutPanel();
            b.Dock = DockStyle.Bottom;
            b.AutoSize = true;
            b.Padding = new Padding(8, 4, 8, 6);
            b.ColumnCount = 6;
            b.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100F));
            for (int i = 0; i < 5; i++) b.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            position.Anchor = AnchorStyles.Left | AnchorStyles.Right;
            position.Height = SystemInformation.HorizontalScrollBarHeight;
            position.Minimum = 0;
            position.Maximum = 10000 + 499;
            position.LargeChange = 500;
            position.SmallChange = 10;
            position.Margin = new Padding(0, 6, 8, 0);
            position.Scroll += OnPositionScroll;
            windowInfo.AutoSize = true;
            windowInfo.Anchor = AnchorStyles.Left;
            windowInfo.Margin = new Padding(0, 6, 8, 0);
            windowInfo.ForeColor = SystemColors.GrayText;
            Btn(firstPage, "Start", delegate { LoadWindow(0, -1, false); });
            Btn(prevPage, "< Page", delegate { LoadWindow(windowStart - (WindowBytes - 4096), -1, false); });
            Btn(nextPage, "Page >", delegate { LoadWindow(windowEnd - 4096, -1, false); });
            Btn(lastPage, "End", delegate { LoadWindow(long.MaxValue, -1, false); });
            b.Controls.Add(position, 0, 0);
            b.Controls.Add(windowInfo, 1, 0);
            b.Controls.Add(firstPage, 2, 0);
            b.Controls.Add(prevPage, 3, 0);
            b.Controls.Add(nextPage, 4, 0);
            b.Controls.Add(lastPage, 5, 0);
            return b;
        }

        private static void Btn(Button b, string label, EventHandler click)
        {
            b.Text = label;
            b.AutoSize = true;
            b.AutoSizeMode = AutoSizeMode.GrowAndShrink;
            b.Padding = new Padding(6, 1, 6, 1);
            b.Margin = new Padding(0, 2, 6, 2);
            b.Click += click;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            string kind = hit.Source;
            if (string.IsNullOrEmpty(kind))
            {
                Open(new ByteSource(hit.IoPath ?? hit.Path), false);
                return;
            }

            // A document: pull its text out first (in the background, PDFs can take a moment).
            text.Text = "Reading the text out of this " + kind + "...";
            SetBusy(true);
            string path = hit.IoPath ?? hit.Path;
            Task<byte[]> t = Task.Factory.StartNew(delegate
            {
                string note;
                byte[] b = DocText.Extract(path, CancellationToken.None, out note);
                return b ?? Encoding.UTF8.GetBytes("(No text could be read from this file" + (note != null ? ": " + note : "") + ")");
            });
            t.ContinueWith(delegate(Task<byte[]> done)
            {
                if (IsDisposed) return;
                SetBusy(false);
                if (done.IsFaulted) { text.Text = "Could not read this file: " + done.Exception.InnerException.Message; return; }
                Open(new ByteSource(done.Result), true);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private void Open(ByteSource src, bool isExtracted)
        {
            source = src;
            extracted = isExtracted;
            bool small = source.Length <= WholeFileBytes;
            position.Enabled = firstPage.Enabled = prevPage.Enabled = nextPage.Enabled = lastPage.Enabled = !small;

            long at = hit.FirstOffset;
            if (at >= 0 && at < source.Length && !string.IsNullOrEmpty(findText.Text))
            {
                curStart = at;
                LoadWindow(at - WindowBytes / 4, at, true);
            }
            else
            {
                LoadWindow(0, -1, false);
                if (!string.IsNullOrEmpty(findText.Text) && windowMatches.Count > 0) SelectMatch(0);
                else if (!string.IsNullOrEmpty(findText.Text)) GoNext();
            }
            text.Focus();
        }

        protected override void OnFormClosed(FormClosedEventArgs e)
        {
            if (searchCts != null) searchCts.Cancel();
            if (source != null) source.Dispose();
            base.OnFormClosed(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.F3: GoNext(); return true;
                case Keys.Shift | Keys.F3: GoPrevious(); return true;
                case Keys.Control | Keys.F: findText.Focus(); findText.SelectAll(); return true;
                case Keys.Escape:
                    if (searchCts != null) { searchCts.Cancel(); return true; }
                    Close();
                    return true;
                case Keys.Control | Keys.Home: if (position.Enabled) LoadWindow(0, -1, false); return position.Enabled;
                case Keys.Control | Keys.End: if (position.Enabled) LoadWindow(long.MaxValue, -1, false); return position.Enabled;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ------------------------------------------------------------------ text form and pattern

        private TextForm FormForWindow()
        {
            if (extracted) return TextForm.Bytes8;
            switch (encodingBox.SelectedIndex)
            {
                case 1: case 3: return TextForm.Bytes8;
                case 2: return TextForm.Utf16;
            }
            if (hit.FirstOffset >= 0 && hit.FirstForm == TextForm.Utf16) return TextForm.Utf16;
            byte[] bom = new byte[2];
            return source.Read(0, bom, 0, 2) == 2 && bom[0] == 0xFF && bom[1] == 0xFE ? TextForm.Utf16 : TextForm.Bytes8;
        }

        private BytePattern Pattern()
        {
            if (pattern != null && pattern.Form == windowForm) return pattern;
            pattern = null;
            if (string.IsNullOrEmpty(findText.Text)) return null;
            try
            {
                pattern = BytePattern.Compile(findText.Text, matchCase.Checked, opts.Wildcards, opts.WholeWord, windowForm);
            }
            catch (ArgumentException ex) { findStatus.Text = ex.Message; }
            return pattern;
        }

        // ------------------------------------------------------------------ window

        /// <summary>Shows the window starting near 'start'. focus = absolute offset of a match to select, or -1.</summary>
        private void LoadWindow(long start, long focus, bool focusIsMatch)
        {
            if (source == null) return;
            long len = source.Length;
            int size = len <= WholeFileBytes ? (int)len : WindowBytes;
            if (start > len - size) start = len - size;
            if (start < 0) start = 0;

            windowForm = FormForWindow();
            if (windowForm == TextForm.Utf16)
            {
                // Keep two-byte characters whole: same byte parity as the match (or even).
                long parity = focus >= 0 ? (focus & 1) : 0;
                if ((start & 1) != parity) start = start > 0 ? start - 1 : start + 1;
            }

            byte[] buf = new byte[size];
            int n = source.Read(start, buf, 0, size);
            int skip = 0;
            if (windowForm == TextForm.Bytes8 && start > 0 && Utf8Mode())
                while (skip < 3 && skip < n && (buf[skip] & 0xC0) == 0x80) skip++;   // don't start mid-character
            if (skip > 0)
            {
                byte[] t = new byte[n - skip];
                Buffer.BlockCopy(buf, skip, t, 0, n - skip);
                buf = t;
                n -= skip;
                start += skip;
            }
            else if (n < buf.Length)
            {
                Array.Resize(ref buf, n);
            }

            windowStart = start;
            windowEnd = start + n;
            windowBytes = buf;

            string shown = DisplayText(buf);
            Native.Redraw(text, false);
            text.Text = shown;
            Native.Redraw(text, true);

            RefreshHighlights();

            if (focus >= 0)
            {
                int idx = windowMatches.FindIndex(delegate(long[] m) { return m[0] == focus; });
                if (idx >= 0) SelectMatch(idx);
                else if (focusIsMatch) { curStart = curEnd = -1; }
            }
            else
            {
                text.Select(0, 0);
                text.ScrollToCaret();
            }
            UpdatePositionInfo();
        }

        private bool Utf8Mode()
        {
            return extracted || encodingBox.SelectedIndex == 0 || encodingBox.SelectedIndex == 1;
        }

        private Encoding WindowEncoding()
        {
            if (windowForm == TextForm.Utf16) return Encoding.Unicode;
            if (extracted || encodingBox.SelectedIndex == 1) return Encoding.UTF8;
            if (encodingBox.SelectedIndex == 3) return Encoding.GetEncoding(1252);
            // Auto: UTF-8 unless the bytes clearly are not.
            string probe = Encoding.UTF8.GetString(windowBytes, 0, Math.Min(windowBytes.Length, 65536));
            int bad = 0;
            foreach (char c in probe) if (c == '�') bad++;
            return bad > 2 + probe.Length / 50 ? Encoding.GetEncoding(1252) : Encoding.UTF8;
        }

        private Encoding shownEncoding;

        private string DisplayText(byte[] buf)
        {
            shownEncoding = WindowEncoding();
            string s = shownEncoding.GetString(buf, 0, windowForm == TextForm.Utf16 ? buf.Length & ~1 : buf.Length);
            // One displayed character per decoded character, so match positions line up:
            // CR LF -> space LF, lone CR -> LF, other control characters -> a middle dot.
            char[] c = s.ToCharArray();
            for (int i = 0; i < c.Length; i++)
            {
                char ch = c[i];
                if (ch == '\r') c[i] = i + 1 < c.Length && c[i + 1] == '\n' ? ' ' : '\n';
                else if (ch < 0x20 && ch != '\n' && ch != '\t') c[i] = '·';
                else if (ch == '\u007F') c[i] = '·';
            }
            return new string(c);
        }

        /// <summary>Finds every match in the window and paints it.</summary>
        private void RefreshHighlights()
        {
            windowMatches.Clear();
            BytePattern p = Pattern();
            if (p != null && windowBytes.Length > 0)
            {
                int from = 0, end;
                long charPos = 0;
                int lastByte = 0;
                Encoding enc = shownEncoding ?? Encoding.UTF8;
                while (windowMatches.Count < MaxHighlights)
                {
                    int st = p.Find(windowBytes, from, windowBytes.Length, windowBytes.Length, out end);
                    if (st < 0) break;
                    long cs, cl;
                    if (windowForm == TextForm.Utf16) { cs = st / 2; cl = (end - st + 1) / 2; }
                    else if (enc.IsSingleByte) { cs = st; cl = end - st; }
                    else
                    {
                        charPos += enc.GetCharCount(windowBytes, lastByte, st - lastByte);
                        cs = charPos;
                        cl = enc.GetCharCount(windowBytes, st, end - st);
                        lastByte = st;
                    }
                    windowMatches.Add(new long[] { windowStart + st, windowStart + end, cs, cl });
                    from = Math.Max(end, st + 1);
                }
            }

            Native.Redraw(text, false);
            text.SelectAll();
            text.SelectionBackColor = SystemColors.Window;
            foreach (long[] m in windowMatches)
            {
                text.Select((int)m[2], (int)m[3]);
                text.SelectionBackColor = Color.FromArgb(255, 236, 120);
            }
            int cur = CurrentIndex();
            if (cur >= 0) PaintMatch(cur, true);
            else text.Select(0, 0);
            Native.Redraw(text, true);
            UpdateFindStatus();
        }

        private int CurrentIndex()
        {
            if (curStart < 0) return -1;
            return windowMatches.FindIndex(delegate(long[] m) { return m[0] == curStart; });
        }

        private void PaintMatch(int idx, bool current)
        {
            long[] m = windowMatches[idx];
            text.Select((int)m[2], (int)m[3]);
            text.SelectionBackColor = current ? Color.FromArgb(255, 150, 50) : Color.FromArgb(255, 236, 120);
            text.Select((int)m[2], 0);   // caret at the match; a live selection would paint over the highlight
        }

        private void SelectMatch(int idx)
        {
            int old = CurrentIndex();
            if (old >= 0 && old != idx) PaintMatch(old, false);
            long[] m = windowMatches[idx];
            curStart = m[0];
            curEnd = m[1];
            PaintMatch(idx, true);
            ScrollToCurrent();
            UpdateFindStatus();
        }

        private void ScrollToCurrent()
        {
            int idx = CurrentIndex();
            if (idx < 0) return;
            long[] m = windowMatches[idx];
            text.Select((int)m[2], 0);
            Native.CenterLine(text, (int)m[2]);
        }

        private void UpdateFindStatus()
        {
            if (Pattern() == null) { if (string.IsNullOrEmpty(findText.Text)) findStatus.Text = ""; return; }
            int idx = CurrentIndex();
            string where = windowMatches.Count == 0 ? "No matches on this page" :
                windowMatches.Count + (windowMatches.Count >= MaxHighlights ? "+" : "") + " on this page";
            if (idx >= 0) where = "Match " + (idx + 1) + " of " + where.Replace(" on this page", " here");
            findStatus.Text = where;
        }

        private void UpdatePositionInfo()
        {
            long len = source.Length;
            windowInfo.Text = len <= WholeFileBytes
                ? Fmt.Size(len) + (extracted ? " of text" : "")
                : string.Format("{0} - {1} of {2}  ({3:0.0}%)", Fmt.Size(windowStart), Fmt.Size(windowEnd), Fmt.Size(len),
                    len == 0 ? 0 : 100.0 * windowStart / len);
            if (!scrolling && len > 0)
                position.Value = (int)Math.Min(10000, 10000.0 * windowStart / Math.Max(1, len - WindowBytes));
        }

        private void OnPositionScroll(object sender, ScrollEventArgs e)
        {
            if (e.Type == ScrollEventType.EndScroll) return;   // ThumbPosition already loaded the page
            scrolling = e.Type == ScrollEventType.ThumbTrack;
            long len = source == null ? 0 : source.Length;
            if (len == 0) return;
            long target = (long)((len - WindowBytes) * (Math.Min(10000, e.NewValue) / 10000.0));
            if (e.Type == ScrollEventType.ThumbTrack)
            {
                windowInfo.Text = "Go to " + Fmt.Size(target) + " of " + Fmt.Size(len);
                return;
            }
            LoadWindow(target, -1, false);
        }

        // ------------------------------------------------------------------ next / previous

        private void GoNext()
        {
            if (source == null || searchCts != null || Pattern() == null) return;
            int idx = CurrentIndex();
            long after = curStart >= 0 ? curStart : windowStart - 1;
            for (int i = 0; i < windowMatches.Count; i++)
                if (windowMatches[i][0] > after && (idx < 0 || i > idx)) { SelectMatch(i); return; }

            // Not on this page: search the rest of the file.
            long from = Math.Max(after + 1, Math.Max(windowStart, windowEnd - pattern.MaxLength));
            RunSearch(true, from);
        }

        private void GoPrevious()
        {
            if (source == null || searchCts != null || Pattern() == null) return;
            long before = curStart >= 0 ? curStart : windowEnd;
            for (int i = windowMatches.Count - 1; i >= 0; i--)
                if (windowMatches[i][0] < before) { SelectMatch(i); return; }
            RunSearch(false, Math.Min(before, windowStart + pattern.MaxLength));
        }

        private void RunSearch(bool forward, long from)
        {
            BytePattern p = pattern;
            ByteSource src = source;
            CancellationTokenSource cts = new CancellationTokenSource();
            searchCts = cts;
            SetBusy(true);
            findStatus.Text = "Searching the rest of the file...  (Esc to stop)";
            long total = src.Length;
            Progress<double> progress = new Progress<double>(delegate(double f)
            {
                if (searchCts == cts) findStatus.Text = string.Format("Searching... {0:0}%  (Esc to stop)", f * 100);
            });
            IProgress<double> report = progress;

            Task<long> t = Task.Factory.StartNew(delegate
            {
                return forward ? FindForward(src, p, from, cts.Token, report) : FindBackward(src, p, from, cts.Token, report);
            }, cts.Token, TaskCreationOptions.LongRunning, TaskScheduler.Default);

            t.ContinueWith(delegate(Task<long> done)
            {
                searchCts = null;
                cts.Dispose();
                if (IsDisposed) return;
                SetBusy(false);
                if (done.IsCanceled) { findStatus.Text = "Stopped."; return; }
                if (done.IsFaulted) { findStatus.Text = "Read error: " + done.Exception.InnerException.Message; return; }
                long at = done.Result;
                if (at < 0)
                {
                    findStatus.Text = forward ? "No more matches after this point." : "No matches before this point.";
                    System.Media.SystemSounds.Beep.Play();
                    return;
                }
                curStart = at;
                LoadWindow(at - WindowBytes / 4, at, true);
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        private static long FindForward(ByteSource src, BytePattern p, long from, CancellationToken ct, IProgress<double> progress)
        {
            const int chunk = 4 << 20;
            int keep = p.MaxLength + 2;
            byte[] buf = new byte[chunk + keep + 2];
            long pos = Math.Max(0, from);
            long len = src.Length;
            int tick = 0;
            while (pos < len)
            {
                ct.ThrowIfCancellationRequested();
                long readAt = Math.Max(0, pos - 2);           // two bytes before, for whole-word checks
                int lead = (int)(pos - readAt);
                int n = src.Read(readAt, buf, 0, buf.Length);
                if (n <= lead) break;
                bool eof = readAt + n >= len;
                int limit = eof ? n : n - keep;
                int end;
                int st = p.Find(buf, lead, limit, n, out end);
                if (st >= 0) return readAt + st;
                if (eof) break;
                pos = readAt + limit;
                if ((++tick & 3) == 0) progress.Report((double)pos / len);
            }
            return -1;
        }

        /// <summary>The last match that starts before 'before'.</summary>
        private static long FindBackward(ByteSource src, BytePattern p, long before, CancellationToken ct, IProgress<double> progress)
        {
            const int chunk = 4 << 20;
            int keep = p.MaxLength + 2;
            byte[] buf = new byte[chunk + keep + 2];
            long end = Math.Min(before, src.Length);
            int tick = 0;
            while (end > 0)
            {
                ct.ThrowIfCancellationRequested();
                long start = Math.Max(0, end - chunk);
                long readAt = Math.Max(0, start - 2);
                int lead = (int)(start - readAt);
                int n = src.Read(readAt, buf, 0, (int)Math.Min(buf.Length, end - readAt + keep));
                int limit = (int)(end - readAt);
                long last = -1;
                int from = lead, e;
                while (true)
                {
                    int st = p.Find(buf, from, limit, n, out e);
                    if (st < 0) break;
                    last = readAt + st;
                    from = st + 1;
                }
                if (last >= 0) return last;
                end = start;
                if ((++tick & 3) == 0) progress.Report(1.0 - (double)end / Math.Max(1, src.Length));
            }
            return -1;
        }

        private void SetBusy(bool busy)
        {
            prevButton.Enabled = nextButton.Enabled = !busy;
            UseWaitCursor = busy;
        }

        private void OpenDefault()
        {
            if (hit.Size >= Shell.BigFileBytes)
            {
                DialogResult r = MessageBox.Show(this, hit.Name + " is " + Fmt.Size(hit.Size) +
                    ".\n\nIts usual program may load the whole file and freeze. Open it anyway?", "Big file",
                    MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (r != DialogResult.Yes) return;
            }
            try { Shell.OpenDefault(hit.Path); }
            catch (Exception ex) { MessageBox.Show(this, ex.Message, "Find Anything"); }
        }
    }
}
