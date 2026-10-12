using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;
using System.Windows.Forms;
using FindAnything.Ui;

namespace FindAnything.App
{
    /// <summary>
    /// Picture thumbnails from Windows' own thumbnail service (the one Explorer uses): fast, cached by Windows, upright,
    /// and any format Windows has a codec for. Loaded on one background thread; finished images are kept in a small
    /// memory cache and the requester is told to repaint.
    /// </summary>
    internal static class Thumbs
    {
        private sealed class Request
        {
            public string Path;
            public int Size;
            public Control Owner;
        }

        private const int MaxCached = 400;
        private static readonly Dictionary<string, Bitmap> cache = new Dictionary<string, Bitmap>();
        private static readonly LinkedList<string> order = new LinkedList<string>();
        private static readonly HashSet<string> failed = new HashSet<string>();
        private static readonly HashSet<string> pending = new HashSet<string>();
        private static readonly Stack<Request> queue = new Stack<Request>();   // newest first: what is on screen now
        private static readonly object gate = new object();
        private static Thread worker;
        private static readonly AutoResetEvent wake = new AutoResetEvent(false);

        private static string Key(string path, int size) { return size + "|" + path; }

        /// <summary>The thumbnail if it is ready; otherwise null, and it is fetched and 'owner' repainted when ready.</summary>
        public static Bitmap Get(string path, int size, Control owner)
        {
            string key = Key(path, size);
            lock (gate)
            {
                Bitmap b;
                if (cache.TryGetValue(key, out b)) return b;
                if (failed.Contains(key) || pending.Contains(key)) return null;
                pending.Add(key);
                Request r = new Request();
                r.Path = path; r.Size = size; r.Owner = owner;
                queue.Push(r);
                if (worker == null)
                {
                    worker = new Thread(Work);
                    worker.IsBackground = true;
                    worker.SetApartmentState(ApartmentState.STA);   // shell interfaces want an STA
                    worker.Start();
                }
            }
            wake.Set();
            return null;
        }

        public static bool Failed(string path, int size)
        {
            lock (gate) return failed.Contains(Key(path, size));
        }

        private static void Work()
        {
            while (true)
            {
                Request r = null;
                lock (gate) if (queue.Count > 0) r = queue.Pop();
                if (r == null) { wake.WaitOne(); continue; }
                string key = Key(r.Path, r.Size);
                Bitmap b = null;
                try { b = Load(r.Path, r.Size); } catch (Exception) { b = null; }
                lock (gate)
                {
                    pending.Remove(key);
                    if (b == null) failed.Add(key);
                    else
                    {
                        cache[key] = b;
                        order.AddLast(key);
                        while (order.Count > MaxCached)
                        {
                            string old = order.First.Value;
                            order.RemoveFirst();
                            Bitmap ob;
                            if (cache.TryGetValue(old, out ob)) { cache.Remove(old); ob.Dispose(); }
                        }
                    }
                }
                Control owner = r.Owner;
                try { if (owner.IsHandleCreated && !owner.IsDisposed) owner.BeginInvoke((MethodInvoker)delegate { owner.Invalidate(); }); }
                catch (Exception) { }
            }
        }

        /// <summary>A thumbnail no larger than size x size, proportions kept (Windows does the fitting).</summary>
        private static Bitmap Load(string path, int size)
        {
            Guid iid = typeof(IShellItemImageFactory).GUID;
            IShellItemImageFactory f;
            if (SHCreateItemFromParsingName(path, IntPtr.Zero, ref iid, out f) != 0 || f == null) return null;
            IntPtr hbmp = IntPtr.Zero;
            try
            {
                SIZE sz = new SIZE(size, size);
                // RESIZETOFIT keeps the aspect ratio; THUMBNAILONLY avoids falling back to a file-type icon.
                if (f.GetImage(sz, SIIGBF_RESIZETOFIT | SIIGBF_THUMBNAILONLY, out hbmp) != 0 || hbmp == IntPtr.Zero) return null;
                return FromHBitmap(hbmp);
            }
            finally
            {
                if (hbmp != IntPtr.Zero) DeleteObject(hbmp);
                Marshal.ReleaseComObject(f);
            }
        }

        /// <summary>
        /// HBITMAP to Bitmap, the right way up and keeping alpha. The shell's thumbnail bitmaps store rows top-first even
        /// though their header says bottom-first, so Image.FromHbitmap (which trusts the header) shows them upside down;
        /// copy the rows as stored instead.
        /// </summary>
        private static Bitmap FromHBitmap(IntPtr hbmp)
        {
            DIBSECTION ds;
            int got = GetObjectDib(hbmp, Marshal.SizeOf(typeof(DIBSECTION)), out ds);
            if (got < Marshal.SizeOf(typeof(DIBSECTION)) || ds.dsBm.bmBitsPixel != 32 || ds.dsBm.bmBits == IntPtr.Zero)
                return Image.FromHbitmap(hbmp);
            int w = ds.dsBm.bmWidth, h = Math.Abs(ds.dsBm.bmHeight), stride = ds.dsBm.bmWidthBytes;
            byte[] all = new byte[stride * h];
            Marshal.Copy(ds.dsBm.bmBits, all, 0, all.Length);
            bool anyAlpha = false;
            for (int i = 3; i < all.Length && !anyAlpha; i += 4) if (all[i] != 0) anyAlpha = true;
            if (!anyAlpha) for (int i = 3; i < all.Length; i += 4) all[i] = 255;   // photos: alpha unused, make opaque
            Bitmap result = new Bitmap(w, h, PixelFormat.Format32bppPArgb);
            BitmapData d = result.LockBits(new Rectangle(0, 0, w, h), ImageLockMode.WriteOnly, PixelFormat.Format32bppPArgb);
            try
            {
                for (int y = 0; y < h; y++)
                    Marshal.Copy(all, y * stride, new IntPtr(d.Scan0.ToInt64() + (long)y * d.Stride), Math.Min(w * 4, d.Stride));
            }
            finally { result.UnlockBits(d); }
            return result;
        }
        private static readonly Dictionary<string, string> dims = new Dictionary<string, string>();

        /// <summary>"4032 x 3024" from the file header (no full decode); "" when unknown.</summary>
        public static string Dimensions(string path)
        {
            string s;
            if (dims.TryGetValue(path, out s)) return s;
            s = "";
            try
            {
                using (System.IO.FileStream fs = new System.IO.FileStream(path, System.IO.FileMode.Open, System.IO.FileAccess.Read, System.IO.FileShare.ReadWrite))
                using (Image img = Image.FromStream(fs, false, false))
                {
                    int w = img.Width, h = img.Height;
                    foreach (int id in img.PropertyIdList)
                        if (id == 0x0112)   // EXIF orientation 5-8: stored sideways
                        {
                            int o = img.GetPropertyItem(id).Value[0];
                            if (o >= 5 && o <= 8) { int tmp = w; w = h; h = tmp; }
                        }
                    s = w + " \u00D7 " + h;
                }
            }
            catch (Exception) { }
            dims[path] = s;
            return s;
        }
        /// <summary>
        /// Draws an image fitted inside 'box' with its proportions kept and centred: the same footprint whether the
        /// picture is 64 px or 8000 px, never stretched.
        /// </summary>
        public static void DrawFitted(Graphics g, Image img, RectangleF box)
        {
            float k = Math.Min(box.Width / img.Width, box.Height / img.Height);
            float w = img.Width * k, h = img.Height * k;
            RectangleF r = new RectangleF(box.X + (box.Width - w) / 2, box.Y + (box.Height - h) / 2, w, h);
            InterpolationMode oldI = g.InterpolationMode;
            PixelOffsetMode oldP = g.PixelOffsetMode;
            // Tiny pictures (icons) blown up 3x or more stay crisp with square pixels instead of a blur.
            bool blocky = k >= 3;
            g.InterpolationMode = blocky ? InterpolationMode.NearestNeighbor : InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = blocky ? PixelOffsetMode.Half : PixelOffsetMode.HighQuality;
            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);   // no grey fringe at the edges
                g.DrawImage(img, new[] { r.Location, new PointF(r.Right, r.Top), new PointF(r.Left, r.Bottom) },
                    new RectangleF(0, 0, img.Width, img.Height), GraphicsUnit.Pixel, ia);
            }
            g.InterpolationMode = oldI;
            g.PixelOffsetMode = oldP;
        }

        // ------------------------------------------------------------------ interop

        private const int SIIGBF_RESIZETOFIT = 0x00, SIIGBF_THUMBNAILONLY = 0x08;

        [StructLayout(LayoutKind.Sequential)]
        private struct SIZE
        {
            public int cx, cy;
            public SIZE(int x, int y) { cx = x; cy = y; }
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAP
        {
            public int bmType, bmWidth, bmHeight, bmWidthBytes;
            public ushort bmPlanes, bmBitsPixel;
            public IntPtr bmBits;
        }

        [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItemImageFactory
        {
            [PreserveSig] int GetImage(SIZE size, int flags, out IntPtr phbm);
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItemImageFactory item);

        [DllImport("gdi32.dll")]
        private static extern bool DeleteObject(IntPtr h);

        [StructLayout(LayoutKind.Sequential)]
        private struct BITMAPINFOHEADER
        {
            public int biSize, biWidth, biHeight;
            public short biPlanes, biBitCount;
            public int biCompression, biSizeImage, biXPelsPerMeter, biYPelsPerMeter, biClrUsed, biClrImportant;
        }

        [StructLayout(LayoutKind.Sequential)]
        private struct DIBSECTION
        {
            public BITMAP dsBm;
            public BITMAPINFOHEADER dsBmih;
            public int dsBitfield0, dsBitfield1, dsBitfield2;
            public IntPtr dshSection;
            public int dsOffset;
        }

        [DllImport("gdi32.dll", EntryPoint = "GetObject")]
        private static extern int GetObjectDib(IntPtr h, int size, out DIBSECTION ds);
    }

    /// <summary>
    /// The big hover preview: a borderless, click-through, never-focused window. Every picture shows at the same size
    /// (fitted into one fixed box, proportions kept), with its file name and real pixel size underneath.
    /// </summary>
    internal sealed class PreviewPopup : Form
    {
        public static int BoxPx { get { return Theme.S(420); } }   // the picture area, whatever the picture
        private string path;
        private string caption = "", sizeText = "";

        public PreviewPopup()
        {
            FormBorderStyle = FormBorderStyle.None;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.Manual;
            TopMost = true;
            BackColor = Theme.Surface;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            int pad = Theme.S(10);
            ClientSize = new Size(BoxPx + 2 * pad, BoxPx + 2 * pad + Theme.S(44));
        }

        protected override bool ShowWithoutActivation { get { return true; } }

        protected override CreateParams CreateParams
        {
            get
            {
                CreateParams cp = base.CreateParams;
                cp.ExStyle |= 0x08000000 | 0x00000080 | 0x00000020;   // NOACTIVATE | TOOLWINDOW | TRANSPARENT (click-through)
                cp.ClassStyle |= 0x00020000;                           // CS_DROPSHADOW
                return cp;
            }
        }

        public string ShowingPath { get { return Visible ? path : null; } }

        public void ShowFor(string picture, string name, Rectangle anchorScreen)
        {
            path = picture;
            caption = name;
            sizeText = Thumbs.Dimensions(picture);
            // Beside the thumbnail, on whichever side has room; kept on the screen.
            Rectangle wa = Screen.FromRectangle(anchorScreen).WorkingArea;
            int x = anchorScreen.Right + Theme.S(14);
            if (x + Width > wa.Right) x = anchorScreen.Left - Theme.S(14) - Width;
            int y = anchorScreen.Top + anchorScreen.Height / 2 - Height / 2;
            y = Math.Max(wa.Top + Theme.S(8), Math.Min(y, wa.Bottom - Height - Theme.S(8)));
            x = Math.Max(wa.Left + Theme.S(8), x);
            Location = new Point(x, y);
            if (!Visible) Show(); else Invalidate();
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(Theme.Surface)) g.FillRectangle(b, ClientRectangle);
            using (Pen p = new Pen(Theme.Line2)) g.DrawRectangle(p, 0, 0, Width - 1, Height - 1);
            int pad = Theme.S(10);
            Rectangle box = new Rectangle(pad, pad, BoxPx, BoxPx);
            using (SolidBrush b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, box);
            Bitmap image = path == null ? null : Thumbs.Get(path, BoxPx, this);
            if (image != null) Thumbs.DrawFitted(g, image, box);
            else TextRenderer.DrawText(g, path != null && Thumbs.Failed(path, BoxPx) ? "No preview for this picture" : "Loading...", Theme.Ui(13), box, Theme.Faint, Theme.Center);
            TextRenderer.DrawText(g, caption, Theme.UiSemibold(13), new Rectangle(pad + Theme.S(2), box.Bottom + Theme.S(6), BoxPx - Theme.S(100), Theme.S(20)), Theme.Text, Theme.Left | TextFormatFlags.EndEllipsis);
            TextRenderer.DrawText(g, sizeText, Theme.Ui(12), new Rectangle(pad, box.Bottom + Theme.S(6), BoxPx - Theme.S(2), Theme.S(20)), Theme.Dim, Theme.Right);
            TextRenderer.DrawText(g, "Double-click the row to open it", Theme.Ui(11.5f), new Rectangle(pad + Theme.S(2), box.Bottom + Theme.S(24), BoxPx, Theme.S(18)), Theme.Faint, Theme.Left);
        }
    }
}
