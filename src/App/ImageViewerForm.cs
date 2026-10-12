using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using FindAnything.Core;
using FindAnything.Ui;

namespace FindAnything.App
{
    /// <summary>
    /// A picture found by OCR: the photo with the matching words boxed in amber, and the text Windows read from it
    /// alongside (copyable). Next / Previous step through the boxes.
    /// </summary>
    internal sealed class ImageViewerForm : Form
    {
        private readonly FileHit hit;
        private readonly ViewOptions opts;
        private Bitmap image;
        private OcrText ocr;
        private readonly List<RectangleF> boxes = new List<RectangleF>();   // image pixels, one per match
        private int current = -1;

        private readonly Backdrop topBar = new Backdrop();
        private readonly SkinField findField = new SkinField(FieldStyle.Normal, Theme.GSearch);
        private readonly SkinButton prevButton = new SkinButton("Previous", Theme.GPrev, ButtonKind.Secondary);
        private readonly SkinButton nextButton = new SkinButton("Next", Theme.GNext, ButtonKind.Secondary);
        private readonly SkinButton copyButton = new SkinButton("Copy text", Theme.GCopy, ButtonKind.Secondary);
        private readonly SkinButton moreButton = new SkinButton("", "", ButtonKind.Ghost);
        private readonly Label status = new Label();
        private readonly PictureCanvas canvas;
        private readonly RichTextBox textBox = new RichTextBox();
        private readonly Panel side = new Panel();

        public ImageViewerForm(FileHit hit, ViewOptions opts)
        {
            this.hit = hit;
            this.opts = opts;
            canvas = new PictureCanvas(this);

            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            Text = hit.Name + "  -  " + hit.Folder;
            Font = Theme.Ui(14);
            BackColor = Theme.Bg;
            ClientSize = new Size(Theme.S(1180), Theme.S(760));
            MinimumSize = new Size(Theme.S(720), Theme.S(420));
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            textBox.Dock = DockStyle.Fill;
            textBox.ReadOnly = true;
            textBox.BorderStyle = BorderStyle.None;
            textBox.BackColor = Theme.Bg2;
            textBox.ForeColor = Color.FromArgb(214, 223, 233);
            textBox.Font = Theme.Mono(13);
            textBox.DetectUrls = false;
            side.Dock = DockStyle.Right;
            side.Width = Theme.S(360);
            side.BackColor = Theme.Bg2;
            side.Padding = new Padding(Theme.S(18), Theme.S(44), Theme.S(10), Theme.S(12));
            side.Controls.Add(textBox);
            side.Paint += delegate(object s, PaintEventArgs e)
            {
                using (Pen p = new Pen(Theme.Line)) e.Graphics.DrawLine(p, 0, 0, 0, side.Height);
                TextRenderer.DrawText(e.Graphics, "T E X T   I N   T H E   P I C T U R E",
                    Theme.UiSemibold(11.5f), new Rectangle(Theme.S(18), Theme.S(14), side.Width, Theme.S(20)), Theme.Faint, Theme.Left);
            };
            canvas.Dock = DockStyle.Fill;

            BuildTop();
            Controls.Add(canvas);
            Controls.Add(side);
            Controls.Add(topBar);
            ResumeLayout(false);
            findField.Text = opts.Text;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this, Theme.Bg2);
            Theme.DarkScrollbars(textBox);
        }

        private void BuildTop()
        {
            topBar.Dock = DockStyle.Top;
            topBar.Fill = Theme.Bg2;
            topBar.BottomLine = Theme.Line;
            topBar.Height = Theme.S(70);
            findField.SetPlaceholder("Find in this picture");
            findField.Box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; FindMatches(); if (boxes.Count > 0) Step(e.Shift ? -1 : 1); }
            };
            prevButton.Click += delegate { Step(-1); };
            nextButton.Click += delegate { Step(1); };
            copyButton.Click += delegate { if (ocr != null && ocr.Text.Length > 0) Clipboard.SetText(ocr.Text); };
            moreButton.FontPx = 16;
            moreButton.Click += delegate
            {
                ContextMenuStrip m = DarkMenuRenderer.Menu();
                m.Items.Add("Open with its usual program", null, delegate { try { Shell.OpenDefault(hit.Path); } catch (Exception) { } });
                m.Items.Add("Show in folder", null, delegate { Shell.ShowInFolder(hit.Path); });
                m.Items.Add("Copy path", null, delegate { Clipboard.SetText(hit.Path); });
                m.Show(moreButton, new Point(moreButton.Width - m.PreferredSize.Width, moreButton.Height));
            };
            status.BackColor = Color.Transparent;
            status.ForeColor = Theme.Dim;
            status.Font = Theme.Ui(13);
            status.TextAlign = ContentAlignment.MiddleLeft;
            status.AutoEllipsis = true;
            topBar.Controls.AddRange(new Control[] { findField, prevButton, nextButton, status, copyButton, moreButton });
            topBar.Resize += delegate { LayoutTop(); };
        }

        private void LayoutTop()
        {
            int W = topBar.Width, y = Theme.S(14), h = Theme.S(42), L = Theme.S(18), R = W - Theme.S(14);
            int bp = prevButton.ShadowPad, fp = findField.ShadowPad;
            moreButton.Bounds = new Rectangle(R - Theme.S(40), y, Theme.S(40), h);
            R -= Theme.S(46);
            copyButton.FontPx = prevButton.FontPx = nextButton.FontPx = 14;
            int cw = copyButton.PreferredWidth(42) - 2 * bp - Theme.S(8);
            copyButton.Bounds = Rectangle.Inflate(new Rectangle(R - cw, y, cw, h), bp, bp);
            R -= cw + Theme.S(14);
            int fw = Math.Max(Theme.S(200), Math.Min(Theme.S(360), W / 4));
            findField.Bounds = Rectangle.Inflate(new Rectangle(L, y, fw, h), fp, fp);
            int x = L + fw + Theme.S(10);
            int pw = prevButton.PreferredWidth(42) - 2 * bp - Theme.S(8);
            prevButton.Bounds = Rectangle.Inflate(new Rectangle(x, y, pw, h), bp, bp);
            x += pw + Theme.S(8);
            int nw = nextButton.PreferredWidth(42) - 2 * bp - Theme.S(8);
            nextButton.Bounds = Rectangle.Inflate(new Rectangle(x, y, nw, h), bp, bp);
            x += nw + Theme.S(14);
            status.Bounds = new Rectangle(x, y + (h - Theme.S(24)) / 2, Math.Max(0, R - x), Theme.S(24));
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            status.Text = "Reading the picture...";
            UseWaitCursor = true;
            string path = hit.IoPath ?? hit.Path;
            Task.Factory.StartNew(delegate
            {
                Bitmap b = Ocr.LoadUpright(path);
                OcrText t = Ocr.ReadImage(path);
                return new object[] { b, t };
            }).ContinueWith(delegate(Task<object[]> done)
            {
                UseWaitCursor = false;
                if (IsDisposed) return;
                if (done.IsFaulted) { status.Text = "Could not open the picture: " + done.Exception.InnerException.Message; return; }
                image = (Bitmap)done.Result[0];
                ocr = (OcrText)done.Result[1];
                textBox.Text = ocr.Text.Length > 0 ? ocr.Text : "(no text was recognised in this picture)";
                FindMatches();
                if (boxes.Count > 0) Step(1);
                canvas.Invalidate();
            }, TaskScheduler.FromCurrentSynchronizationContext());
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            switch (keyData)
            {
                case Keys.F3: Step(1); return true;
                case Keys.Shift | Keys.F3: Step(-1); return true;
                case Keys.Escape: Close(); return true;
                case Keys.Control | Keys.F: findField.Box.Focus(); findField.Box.SelectAll(); return true;
            }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        /// <summary>Matches the find text against each recognised line and boxes the words each match covers.</summary>
        private void FindMatches()
        {
            boxes.Clear();
            current = -1;
            if (ocr == null) return;
            textBox.SelectAll();
            textBox.SelectionBackColor = Theme.Bg2;
            textBox.SelectionColor = Color.FromArgb(214, 223, 233);
            string text = findField.Text;
            if (text.Trim().Length == 0) { status.Text = ocr.Words.Count + " words recognised"; canvas.Invalidate(); return; }
            BytePattern p;
            try { p = BytePattern.Make(text, opts.CaseSensitive, opts.Wildcards, opts.WholeWord, TextForm.Bytes8, opts.Loose); }
            catch (ArgumentException ex) { status.Text = ex.Message; return; }

            // Rebuild each line the way OcrText.Text joins it, remembering where each word starts.
            int lineStartInText = 0;
            int i = 0;
            List<OcrWordBox> words = ocr.Words;
            int prevPage = -1;
            while (i < words.Count)
            {
                int j = i;
                StringBuilder line = new StringBuilder();
                List<int> starts = new List<int>();
                while (j < words.Count && words[j].Page == words[i].Page && words[j].Line == words[i].Line)
                {
                    if (line.Length > 0) line.Append(' ');
                    starts.Add(line.Length);
                    line.Append(words[j].Text);
                    j++;
                }
                if (prevPage >= 0) lineStartInText += words[i].Page != prevPage ? 2 : 1;
                prevPage = words[i].Page;
                string s = line.ToString();
                int from = 0;
                while (from < s.Length)
                {
                    int ms, ml;
                    if (!p.FindInString(s.Substring(from), out ms, out ml)) break;
                    ms += from;
                    // words overlapping [ms, ms+ml) become one box
                    RectangleF box = RectangleF.Empty;
                    for (int k = 0; k < starts.Count; k++)
                    {
                        int ws = starts[k], we = ws + words[i + k].Text.Length;
                        if (ws < ms + ml && ms < we)
                        {
                            OcrWordBox w = words[i + k];
                            RectangleF r = new RectangleF(w.X, w.Y, w.W, w.H);
                            box = box.IsEmpty ? r : RectangleF.Union(box, r);
                        }
                    }
                    if (!box.IsEmpty) boxes.Add(box);
                    textBox.Select(lineStartInText + ms, ml);
                    textBox.SelectionBackColor = Color.FromArgb(74, 58, 18);
                    textBox.SelectionColor = Color.FromArgb(255, 210, 122);
                    from = ms + Math.Max(1, ml);
                }
                lineStartInText += s.Length;
                i = j;
            }
            textBox.Select(0, 0);
            status.Text = boxes.Count == 0 ? "Not found in this picture" : boxes.Count + (boxes.Count == 1 ? " match" : " matches") + " in the picture";
            canvas.Invalidate();
        }

        private void Step(int dir)
        {
            if (boxes.Count == 0) return;
            current = current < 0 ? (dir > 0 ? 0 : boxes.Count - 1) : (current + dir + boxes.Count) % boxes.Count;
            status.Text = "Match " + (current + 1) + " of " + boxes.Count;
            canvas.Invalidate();
        }

        /// <summary>Draws the picture fitted to the window, with the match boxes on top.</summary>
        private sealed class PictureCanvas : Control
        {
            private readonly ImageViewerForm owner;

            public PictureCanvas(ImageViewerForm owner)
            {
                this.owner = owner;
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                BackColor = Theme.Bg;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                using (SolidBrush b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);
                Bitmap img = owner.image;
                if (img == null) return;
                int pad = Theme.S(24);
                float k = Math.Min((Width - 2f * pad) / img.Width, (Height - 2f * pad) / img.Height);
                k = Math.Min(k, 4f);
                float w = img.Width * k, h = img.Height * k;
                float x = (Width - w) / 2, y = (Height - h) / 2;
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.DrawImage(img, x, y, w, h);
                g.SmoothingMode = SmoothingMode.AntiAlias;
                for (int i = 0; i < owner.boxes.Count; i++)
                {
                    RectangleF r = owner.boxes[i];
                    RectangleF s = new RectangleF(x + r.X * k - Theme.S(4), y + r.Y * k - Theme.S(3), r.Width * k + Theme.S(8), r.Height * k + Theme.S(6));
                    bool cur = i == owner.current;
                    using (SolidBrush fill = new SolidBrush(Color.FromArgb(cur ? 70 : 40, Theme.Amber))) g.FillRectangle(fill, s);
                    using (Pen pen = new Pen(cur ? Theme.Amber : Color.FromArgb(200, Theme.Amber), Theme.S(cur ? 3 : 2))) g.DrawRectangle(pen, s.X, s.Y, s.Width, s.Height);
                    if (cur)
                        using (Pen glow = new Pen(Color.FromArgb(60, Theme.Amber), Theme.S(8)))
                            g.DrawRectangle(glow, s.X - Theme.S(3), s.Y - Theme.S(3), s.Width + Theme.S(6), s.Height + Theme.S(6));
                }
            }
        }
    }
}
