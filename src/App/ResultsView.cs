using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.Windows.Forms;
using FindAnything.Core;
using FindAnything.Ui;

namespace FindAnything.App
{
    /// <summary>
    /// The results list, drawn to the mockup: type badge, name over folder, the first match with the hit highlighted,
    /// hit count pill, line, size, date. Virtual (only visible rows are drawn), multi-select, keyboard, own scrollbar.
    /// </summary>
    internal sealed class ResultsView : Control
    {
        public List<FileHit> Items = new List<FileHit>();
        public Func<string, int[]> FindSpan;          // preview -> {start, length} of the match, or null
        public event EventHandler ItemActivate;
        public event Action<int> SortRequested;
        public int SortColumn = -1;
        public bool SortDescending;
        public string EmptyTitle = "Type what to find and press Enter";
        public string EmptyText = "Searches inside every file: text, PDF, Word, Excel, logs, any size.";

        private readonly HashSet<int> selected = new HashSet<int>();
        private int focus = -1, anchor = -1, hoverRow = -1;
        private int top;                 // first visible pixel (scroll offset)
        private bool dragThumb;
        private int dragStartY, dragStartTop;
        private bool hoverBar;

        private static readonly string[] Headers = { "File", "First match", "Hits", "Line", "Size", "Modified" };
        private int[] colX = new int[7];

        public ResultsView()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
            BackColor = Theme.Bg;
            TabStop = true;
        }

        private int HeaderH { get { return Theme.S(34); } }
        private int RowH { get { return Theme.S(52); } }
        private int ViewH { get { return Math.Max(0, Height - HeaderH); } }
        private int ContentH { get { return Items.Count * RowH; } }
        private int MaxTop { get { return Math.Max(0, ContentH - ViewH); } }

        public void ResetSelection()
        {
            selected.Clear();
            focus = anchor = -1;
            top = 0;
            Invalidate();
        }

        public void ItemsChanged()
        {
            top = Math.Min(top, MaxTop);
            Invalidate();
        }

        public IList<FileHit> SelectedItems
        {
            get
            {
                List<int> idx = new List<int>(selected);
                idx.Sort();
                List<FileHit> l = new List<FileHit>();
                foreach (int i in idx) if (i < Items.Count) l.Add(Items[i]);
                return l;
            }
        }

        public FileHit FocusedItem { get { return focus >= 0 && focus < Items.Count ? Items[focus] : (selected.Count > 0 ? SelectedItems[0] : null); } }

        public void Select(FileHit h)
        {
            int i = Items.IndexOf(h);
            selected.Clear();
            if (i >= 0) { selected.Add(i); focus = anchor = i; EnsureVisible(i); }
            Invalidate();
        }

        public void SelectFirst()
        {
            if (Items.Count == 0 || selected.Count > 0) return;
            selected.Add(0);
            focus = anchor = 0;
            Invalidate();
        }

        public void SelectAll()
        {
            for (int i = 0; i < Items.Count; i++) selected.Add(i);
            Invalidate();
        }

        // ------------------------------------------------------------------ layout

        private void LayoutColumns()
        {
            int pad = Theme.S(22);
            int w = Width - pad;
            int fileW = Math.Max(Theme.S(220), Math.Min(Theme.S(330), (int)(Width * 0.27)));
            int hitsW = Theme.S(60), lineW = Theme.S(80), sizeW = Theme.S(80), dateW = Theme.S(128);
            colX[0] = pad;
            colX[1] = colX[0] + fileW + Theme.S(14);
            colX[6] = w;
            colX[5] = colX[6] - dateW;
            colX[4] = colX[5] - sizeW - Theme.S(10);
            colX[3] = colX[4] - lineW - Theme.S(10);
            colX[2] = colX[3] - hitsW - Theme.S(10);
            if (colX[2] < colX[1] + Theme.S(120)) colX[2] = colX[1] + Theme.S(120);
        }

        private int RowAt(int y)
        {
            if (y < HeaderH) return -1;
            int i = (y - HeaderH + top) / RowH;
            return i >= 0 && i < Items.Count ? i : -1;
        }

        private int ColAtHeader(int x)
        {
            for (int c = 0; c < 6; c++) if (x >= colX[c] - Theme.S(6) && x < (c == 5 ? colX[6] : colX[c + 1] - Theme.S(4))) return c;
            return -1;
        }

        public void EnsureVisible(int i)
        {
            int y = i * RowH;
            if (y < top) top = y;
            else if (y + RowH > top + ViewH) top = y + RowH - ViewH;
            top = Math.Max(0, Math.Min(top, MaxTop));
            Invalidate();
        }

        // ------------------------------------------------------------------ painting

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            LayoutColumns();
            using (SolidBrush b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);

            if (Items.Count == 0) PaintEmpty(g);

            // rows
            int first = top / RowH;
            int y = HeaderH - (top % RowH);
            g.SetClip(new Rectangle(0, HeaderH, Width, ViewH));
            for (int i = first; i < Items.Count && y < Height; i++, y += RowH) PaintRow(g, i, new Rectangle(0, y, Width, RowH));
            g.ResetClip();

            PaintHeader(g);
            PaintScrollbar(g);
        }

        private void PaintEmpty(Graphics g)
        {
            Rectangle r = new Rectangle(0, HeaderH, Width, ViewH);
            int cy = r.Top + r.Height / 2 - Theme.S(40);
            TextRenderer.DrawText(g, Theme.GSearch, Theme.Icons(44), new Rectangle(0, cy - Theme.S(34), Width, Theme.S(60)), Theme.Line2, Theme.Center);
            TextRenderer.DrawText(g, EmptyTitle, Theme.UiSemibold(17), new Rectangle(0, cy + Theme.S(34), Width, Theme.S(26)), Theme.Dim, Theme.Center);
            TextRenderer.DrawText(g, EmptyText, Theme.Ui(13), new Rectangle(0, cy + Theme.S(62), Width, Theme.S(22)), Theme.Faint, Theme.Center);
        }

        private void PaintHeader(Graphics g)
        {
            Rectangle h = new Rectangle(0, 0, Width, HeaderH);
            using (SolidBrush b = new SolidBrush(Theme.Bg2)) g.FillRectangle(b, h);
            using (Pen p = new Pen(Theme.Line)) g.DrawLine(p, 0, HeaderH - 1, Width, HeaderH - 1);
            Font f = Theme.UiSemibold(11.5f);
            for (int c = 0; c < 6; c++)
            {
                bool right = c >= 2;
                string label = Headers[c].ToUpperInvariant();
                int x0 = colX[c], x1 = c == 5 ? colX[6] : (c == 1 ? colX[2] - Theme.S(10) : colX[c + 1] - Theme.S(10));
                Color fg = c == SortColumn ? Theme.AmberHi : Theme.Faint;
                Rectangle r = new Rectangle(x0, 0, x1 - x0, HeaderH);
                string text = Spaced(label);
                int tw = Theme.TextWidth(text, f);
                TextRenderer.DrawText(g, text, f, r, fg, right ? Theme.Right : Theme.Left);
                if (c == SortColumn)
                {
                    int gx = right ? r.Right - tw - Theme.S(16) : r.Left + tw + Theme.S(4);
                    TextRenderer.DrawText(g, SortDescending ? Theme.GDown : Theme.GUp, Theme.Icons(9), new Rectangle(gx, 0, Theme.S(12), HeaderH), fg, Theme.Center);
                }
            }
        }

        /// <summary>Letter-spaced caps for column headers (GDI has no letter-spacing).</summary>
        private static string Spaced(string s)
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder(); foreach (char ch in s) { if (sb.Length > 0) sb.Append('\u200A'); sb.Append(ch); } return sb.ToString();
        }

        private void PaintRow(Graphics g, int i, Rectangle r)
        {
            FileHit h = Items[i];
            bool sel = selected.Contains(i);
            if (sel)
            {
                using (LinearGradientBrush b = new LinearGradientBrush(r, Theme.Alpha(Theme.Amber, 34), Theme.Alpha(Theme.Amber, 10), LinearGradientMode.Horizontal))
                    g.FillRectangle(b, r);
                using (SolidBrush b = new SolidBrush(Theme.Amber)) g.FillRectangle(b, r.Left, r.Top, Theme.S(3), r.Height);
            }
            else if (i == hoverRow)
            {
                using (SolidBrush b = new SolidBrush(Theme.RowHover)) g.FillRectangle(b, r);
            }
            using (Pen p = new Pen(Theme.RowLine)) g.DrawLine(p, 0, r.Bottom - 1, Width, r.Bottom - 1);
            if (i == focus && Focused && !sel)
                using (Pen p = new Pen(Theme.Line2)) g.DrawRectangle(p, r.Left + 1, r.Top, r.Width - 3, r.Height - 2);

            // badge + name + folder
            string ext = Badge(h);
            Color bf, bb;
            BadgeColors(ext, out bf, out bb);
            Rectangle badge = new Rectangle(colX[0], r.Top + (r.Height - Theme.S(24)) / 2, Theme.S(42), Theme.S(24));
            Theme.FillRound(g, bb, badge, Theme.S(6));
            TextRenderer.DrawText(g, ext, Theme.UiSemibold(ext.Length > 3 ? 9.5f : 10.5f), badge, bf, Theme.Center);
            int nx = badge.Right + Theme.S(11);
            int nw = colX[1] - Theme.S(14) - nx;
            TextRenderer.DrawText(g, h.Name, Theme.UiSemibold(14), new Rectangle(nx, r.Top + Theme.S(7), nw, Theme.S(21)), Theme.Text, Theme.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, h.Folder ?? "", Theme.Ui(12), new Rectangle(nx, r.Top + Theme.S(27), nw, Theme.S(18)), Theme.Faint, Theme.Left | TextFormatFlags.PathEllipsis);

            // first match, with the hit highlighted
            Rectangle pr = new Rectangle(colX[1], r.Top, colX[2] - Theme.S(14) - colX[1], r.Height);
            if (h.Secret != null) PaintSecret(g, h.Secret, pr);
            else if (h.ContentHits > 0 && !string.IsNullOrEmpty(h.Preview)) PaintPreview(g, h.Preview, pr, FindSpan != null ? FindSpan(h.Preview) : null);
            else if (h.NameMatch) TextRenderer.DrawText(g, "matched by file name", Theme.Ui(13, FontStyle.Italic), pr, Theme.Faint, Theme.Left);

            // hits pill (not for keys or drive matches: each row is one)
            if (h.ContentHits > 0 && h.Secret == null && h.Source != "DISK")
            {
                string n = h.HitsText;
                Font pf = Theme.UiSemibold(12);
                int pw = Math.Max(Theme.S(30), Theme.TextWidth(n, pf) + Theme.S(16));
                Rectangle pill = new Rectangle(colX[3] - Theme.S(10) - pw, r.Top + (r.Height - Theme.S(22)) / 2, pw, Theme.S(22));
                if (sel) Theme.FillRound(g, Theme.Amber, pill, pill.Height / 2f);
                else { Theme.FillRound(g, Theme.Surface2, pill, pill.Height / 2f); Theme.StrokeRound(g, Theme.Line2, pill, pill.Height / 2f, 1); }
                TextRenderer.DrawText(g, n, pf, pill, sel ? Theme.AmberInk : Theme.Text, Theme.Center);
            }

            Font nf = Theme.Ui(12.5f);
            bool disk = h.Source == "DISK";
            if (h.ContentHits > 0 && !disk)
                TextRenderer.DrawText(g, h.FirstLine.ToString("N0"), nf, new Rectangle(colX[3], r.Top, colX[4] - Theme.S(10) - colX[3], r.Height), Theme.Dim, Theme.Right);
            if (!disk) TextRenderer.DrawText(g, Fmt.Size(h.Size), nf, new Rectangle(colX[4], r.Top, colX[5] - Theme.S(10) - colX[4], r.Height), Theme.Dim, Theme.Right);
            if (h.Modified != DateTime.MinValue)
                TextRenderer.DrawText(g, h.Modified.ToString("yyyy-MM-dd  HH:mm"), nf, new Rectangle(colX[5], r.Top, colX[6] - colX[5], r.Height), Theme.Faint, Theme.Right);
        }

        private void PaintSecret(Graphics g, SecretFinding s, Rectangle r)
        {
            string kind = (s.Label ?? SecretDetector.KindName(s.Kind)).ToUpperInvariant();
            Font kf = Theme.UiSemibold(10.5f);
            int kw = Theme.TextWidth(kind, kf) + Theme.S(16);
            Rectangle pill = new Rectangle(r.Left, r.Top + (r.Height - Theme.S(22)) / 2, kw, Theme.S(22));
            Color c = KindColor(s.Kind);
            Theme.FillRound(g, Color.FromArgb(38, c), pill, Theme.S(6));
            TextRenderer.DrawText(g, kind, kf, pill, c, Theme.Center);
            Rectangle rest = new Rectangle(pill.Right + Theme.S(10), r.Top, Math.Max(0, r.Right - pill.Right - Theme.S(10)), r.Height);
            PaintPreview(g, s.Line, rest, new[] { s.ValueStart, s.Value.Length });
        }

        private static Color KindColor(SecretKind k)
        {
            switch (k)
            {
                case SecretKind.Password: case SecretKind.Pin: return Color.FromArgb(255, 138, 138);
                case SecretKind.ApiToken: case SecretKind.PrivateKey: return Color.FromArgb(197, 163, 255);
                case SecretKind.Card: case SecretKind.Ssn: return Color.FromArgb(141, 184, 255);
                default: return Theme.AmberHi;
            }
        }

        private void PaintPreview(Graphics g, string text, Rectangle r, int[] span)
        {
            Font f = Theme.Mono(13);
            if (span == null) { TextRenderer.DrawText(g, text, f, r, Theme.Dim, Theme.Left | TextFormatFlags.EndEllipsis); return; }

            int s = span[0], len = span[1];
            string before = text.Substring(0, s), hit = text.Substring(s, len), after = text.Substring(s + len);
            // Keep the hit in view: trim the start if the text before it would push it off the column.
            int hw = Theme.Advance(hit, f);
            // Trim the start only as far as needed to keep the whole hit (and a little after it) in view.
            int room = r.Width - hw - Theme.S(40);
            while (before.Length > 2 && Theme.Advance(before, f) > room) before = "\u2026" + before.Substring(Math.Min(before.Length - 1, 3));

            int x = r.Left;
            int bw = Theme.Advance(before, f);
            TextRenderer.DrawText(g, before, f, new Rectangle(x, r.Top, Math.Min(bw + 2, r.Right - x), r.Height), Theme.Dim, Theme.Left);
            x += bw;
            if (x >= r.Right) return;
            Rectangle m = new Rectangle(x + Theme.S(1), r.Top + (r.Height - Theme.S(22)) / 2, Math.Min(hw + Theme.S(6), r.Right - x), Theme.S(22));
            Theme.FillRound(g, Theme.Alpha(Theme.Amber, 56), m, Theme.S(4));
            using (SolidBrush ul = new SolidBrush(Theme.Alpha(Theme.Amber, 150))) g.FillRectangle(ul, m.Left + Theme.S(2), m.Bottom - Theme.S(2), m.Width - Theme.S(4), Theme.S(2));
            TextRenderer.DrawText(g, hit, f, new Rectangle(x + Theme.S(4), r.Top, Math.Min(hw + 2, r.Right - x - Theme.S(4)), r.Height), Theme.AmberHi, Theme.Left);
            x += hw + Theme.S(8);
            if (x < r.Right)
                TextRenderer.DrawText(g, after, f, new Rectangle(x, r.Top, r.Right - x, r.Height), Theme.Dim, Theme.Left | TextFormatFlags.EndEllipsis);
        }

        private static string Badge(FileHit h)
        {
            if (h.Source == "DISK") return "DISK";
            string ext = Path.GetExtension(h.Name);
            if (string.IsNullOrEmpty(ext) || ext.Length > 6) return "FILE";
            ext = ext.Substring(1).ToUpperInvariant();
            return ext.Length > 4 ? ext.Substring(0, 4) : ext;
        }

        private static void BadgeColors(string ext, out Color fg, out Color bg)
        {
            int rgb;
            switch (ext)
            {
                case "PDF": rgb = 0xFF7A7A; break;
                case "DOC": case "DOCX": case "DOCM": case "RTF": case "ODT": rgb = 0x8DB8FF; break;
                case "XLS": case "XLSX": case "XLSM": case "CSV": case "ODS": rgb = 0x72D99A; break;
                case "PPT": case "PPTX": case "ODP": rgb = 0xFF9F6B; break;
                case "LOG": case "OUT": case "ERR": rgb = 0x7EE0AE; break;
                case "JSON": case "XML": case "YAML": case "YML": case "INI": case "CFG": case "CONF": rgb = 0xC5A3FF; break;
                case "CS": case "JS": case "TS": case "PY": case "PS1": case "C": case "CPP": case "H": case "JAVA": case "HTML": case "CSS": case "SQL": case "BAT": case "CMD": rgb = 0x6ED3E0; break;
                case "EXE": case "DLL": case "SYS": case "BIN": case "ISO": rgb = 0xE2B86B; break;
                case "JPG": case "JPEG": case "PNG": case "GIF": case "BMP": case "TIF": case "TIFF": case "HEIC": case "WEBP": case "JFIF": rgb = 0xF59AC8; break;
                case "DISK": rgb = 0xFFC54D; break;
                default: rgb = 0xB9C6D4; break;
            }
            fg = Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255);
            bg = Color.FromArgb(40, fg);
        }

        // ------------------------------------------------------------------ scrollbar (thin overlay)

        private Rectangle BarTrack { get { return new Rectangle(Width - Theme.S(12), HeaderH + Theme.S(4), Theme.S(10), ViewH - Theme.S(8)); } }

        private Rectangle Thumb
        {
            get
            {
                Rectangle t = BarTrack;
                if (ContentH <= ViewH || t.Height <= 0) return Rectangle.Empty;
                int h = Math.Max(Theme.S(36), (int)(t.Height * (ViewH / (double)ContentH)));
                int y = t.Top + (int)((t.Height - h) * (top / (double)MaxTop));
                return new Rectangle(t.Left, y, t.Width, h);
            }
        }

        private void PaintScrollbar(Graphics g)
        {
            Rectangle th = Thumb;
            if (th.IsEmpty) return;
            int w = hoverBar || dragThumb ? Theme.S(8) : Theme.S(5);
            Rectangle r = new Rectangle(th.Right - w - Theme.S(2), th.Top, w, th.Height);
            Theme.FillRound(g, hoverBar || dragThumb ? Theme.Dim : Theme.Line2, r, w / 2f);
        }

        // ------------------------------------------------------------------ input

        protected override void OnMouseWheel(MouseEventArgs e)
        {
            top = Math.Max(0, Math.Min(MaxTop, top - e.Delta / 120 * RowH * 3));
            Invalidate();
            base.OnMouseWheel(e);
        }

        private Point lastDown;

        protected override void OnMouseDown(MouseEventArgs e)
        {
            lastDown = e.Location;
            Focus();
            base.OnMouseDown(e);
            Rectangle th = Thumb;
            if (!th.IsEmpty && BarTrack.Contains(e.Location))
            {
                if (th.Contains(e.Location)) { dragThumb = true; dragStartY = e.Y; dragStartTop = top; }
                else { top = Math.Max(0, Math.Min(MaxTop, top + (e.Y < th.Top ? -ViewH : ViewH))); Invalidate(); }
                return;
            }
            if (e.Y < HeaderH)
            {
                int c = ColAtHeader(e.X);
                if (c >= 0 && SortRequested != null) SortRequested(c);
                return;
            }
            int i = RowAt(e.Y);
            if (i < 0) { if ((ModifierKeys & (Keys.Control | Keys.Shift)) == 0) { selected.Clear(); Invalidate(); } return; }
            if (e.Button == MouseButtons.Right && selected.Contains(i)) { focus = i; Invalidate(); return; }
            ClickRow(i, ModifierKeys);
        }

        private void ClickRow(int i, Keys mods)
        {
            if ((mods & Keys.Shift) != 0 && anchor >= 0)
            {
                if ((mods & Keys.Control) == 0) selected.Clear();
                for (int k = Math.Min(anchor, i); k <= Math.Max(anchor, i); k++) selected.Add(k);
            }
            else if ((mods & Keys.Control) != 0)
            {
                if (!selected.Remove(i)) selected.Add(i);
                anchor = i;
            }
            else
            {
                selected.Clear();
                selected.Add(i);
                anchor = i;
            }
            focus = i;
            EnsureVisible(i);
            Invalidate();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (dragThumb)
            {
                Rectangle t = BarTrack, th = Thumb;
                double per = MaxTop / (double)Math.Max(1, t.Height - th.Height);
                top = Math.Max(0, Math.Min(MaxTop, dragStartTop + (int)((e.Y - dragStartY) * per)));
                Invalidate();
                return;
            }
            bool bar = BarTrack.Contains(e.Location) && !Thumb.IsEmpty;
            int row = bar ? -1 : RowAt(e.Y);
            if (bar != hoverBar || row != hoverRow) { hoverBar = bar; hoverRow = row; Invalidate(); }
            Cursor = e.Y < HeaderH && ColAtHeader(e.X) >= 0 ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (dragThumb) { dragThumb = false; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            hoverRow = -1;
            hoverBar = false;
            Invalidate();
        }

        protected override void OnDoubleClick(EventArgs e)
        {
            base.OnDoubleClick(e);
            Point p = lastDown;   // where the click happened, not where the mouse is now
            if (RowAt(p.Y) >= 0 && !BarTrack.Contains(p) && ItemActivate != null) ItemActivate(this, EventArgs.Empty);
        }

        protected override bool IsInputKey(Keys keyData)
        {
            switch (keyData & Keys.KeyCode)
            {
                case Keys.Up: case Keys.Down: case Keys.PageUp: case Keys.PageDown: case Keys.Home: case Keys.End: case Keys.Enter:
                    return true;
            }
            return base.IsInputKey(keyData);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            base.OnKeyDown(e);
            if (e.Handled || Items.Count == 0) return;
            int page = Math.Max(1, ViewH / RowH - 1);
            int to = -1;
            switch (e.KeyCode)
            {
                case Keys.Up: to = Math.Max(0, focus - 1); break;
                case Keys.Down: to = Math.Min(Items.Count - 1, focus + 1); break;
                case Keys.PageUp: to = Math.Max(0, focus - page); break;
                case Keys.PageDown: to = Math.Min(Items.Count - 1, focus + page); break;
                case Keys.Home: to = 0; break;
                case Keys.End: to = Items.Count - 1; break;
                case Keys.Enter: if (ItemActivate != null) ItemActivate(this, EventArgs.Empty); e.Handled = true; return;
                case Keys.A: if (e.Control) { SelectAll(); e.Handled = true; } return;
            }
            if (to < 0) return;
            if (focus < 0) to = 0;
            ClickRow(to, e.Shift ? Keys.Shift : Keys.None);
            e.Handled = true;
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
        protected override void OnResize(EventArgs e) { top = Math.Min(top, MaxTop); base.OnResize(e); }
    }
}
