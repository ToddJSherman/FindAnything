using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace FindAnything.Ui
{
    /// <summary>
    /// The look: colours, fonts, glyphs and DPI scale. The control artwork itself is PNG sprites cut from
    /// design\ui\sprites.html (tools\make-skin.ps1) and drawn 9-sliced, so corners and glows stay crisp at any size.
    /// </summary>
    internal static class Theme
    {
        public static readonly Color Bg = C(0x0B1118);
        public static readonly Color Bg2 = C(0x0F1720);
        public static readonly Color Surface = C(0x141E29);
        public static readonly Color Surface2 = C(0x1A2633);
        public static readonly Color FindFill = C(0x16212E);
        public static readonly Color Line = C(0x243445);
        public static readonly Color Line2 = C(0x2E4257);
        public static readonly Color RowLine = C(0x18232F);
        public static readonly Color RowHover = C(0x111A24);
        public static readonly Color Text = C(0xE8EEF5);
        public static readonly Color Dim = C(0x8A9AAD);
        public static readonly Color Faint = C(0x5B6B7E);
        public static readonly Color Amber = C(0xFFB020);
        public static readonly Color AmberHi = C(0xFFC54D);
        public static readonly Color AmberInk = C(0x1A1206);
        public static readonly Color Green = C(0x5BD69A);

        public static readonly float Scale = ReadScale();

        public static int S(float cssPx) { return (int)Math.Round(cssPx * Scale); }

        private static float ReadScale()
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return g.DpiX / 96f;
        }

        private static Color C(int rgb) { return Color.FromArgb(255, (rgb >> 16) & 255, (rgb >> 8) & 255, rgb & 255); }

        public static Color Alpha(Color c, int a) { return Color.FromArgb(a, c); }

        private static readonly Dictionary<string, Font> fonts = new Dictionary<string, Font>();

        private static string uiFamily, displayFamily, monoFamily, iconFamily;

        private static string Pick(params string[] names)
        {
            using (System.Drawing.Text.InstalledFontCollection all = new System.Drawing.Text.InstalledFontCollection())
            {
                HashSet<string> have = new HashSet<string>();
                foreach (FontFamily f in all.Families) have.Add(f.Name);
                foreach (string n in names) if (have.Contains(n)) return n;
            }
            return "Segoe UI";
        }

        private static Font Get(string family, float cssPx, FontStyle style)
        {
            string key = family + "|" + cssPx + "|" + (int)style;
            Font f;
            if (!fonts.TryGetValue(key, out f))
            {
                f = new Font(family, cssPx * 0.75f, style, GraphicsUnit.Point);
                fonts[key] = f;
            }
            return f;
        }

        /// <summary>Body text. Sizes are CSS pixels at 100% (the mockup's numbers).</summary>
        public static Font Ui(float px, FontStyle style = FontStyle.Regular)
        {
            if (uiFamily == null) uiFamily = Pick("Segoe UI Variable Text", "Segoe UI");
            return Get(uiFamily, px, style);
        }

        public static Font UiSemibold(float px)
        {
            if (uiFamily == null) uiFamily = Pick("Segoe UI Variable Text", "Segoe UI");
            string semi = Pick("Segoe UI Variable Text Semibold", "Segoe UI Semibold");
            return semi == "Segoe UI" ? Get(uiFamily, px, FontStyle.Bold) : Get(semi, px, FontStyle.Regular);
        }

        public static Font Display(float px)
        {
            if (displayFamily == null) displayFamily = Pick("Segoe UI Variable Display Semibold", "Segoe UI Semibold", "Segoe UI");
            return Get(displayFamily, px, displayFamily == "Segoe UI" ? FontStyle.Bold : FontStyle.Regular);
        }

        public static Font Mono(float px, FontStyle style = FontStyle.Regular)
        {
            if (monoFamily == null) monoFamily = Pick("Cascadia Mono", "Consolas");
            return Get(monoFamily, px, style);
        }

        public static Font Icons(float px)
        {
            if (iconFamily == null) iconFamily = Pick("Segoe Fluent Icons", "Segoe MDL2 Assets");
            return Get(iconFamily, px, FontStyle.Regular);
        }

        // Segoe Fluent Icons / MDL2 code points
        public const string GSearch = "", GFolder = "", GOpenFolder = "", GInfo = "";
        public const string GDown = "", GUp = "", GPage = "", GRename = "", GPdf = "";
        public const string GFont = "", GFilter = "", GStop = "", GCheck = "", GPrev = "";
        public const string GNext = "", GLeft = "", GRight = "", GWrap = "", GOpen = "";
        public const string GCopy = "", GSave = "", GEncoding = "", GFirst = "", GLast = "";

        public const TextFormatFlags Left = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        public const TextFormatFlags Center = TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;
        public const TextFormatFlags Right = TextFormatFlags.Right | TextFormatFlags.VerticalCenter | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix;

        public static int TextWidth(string s, Font f)
        {
            return TextRenderer.MeasureText(s, f, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPadding | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix).Width;
        }

        /// <summary>
        /// How far the pen moves after drawing s. A lone measurement includes overhang padding (a word measures
        /// wider alone than inside a sentence); measuring s+"|" minus "|" cancels it, so runs of text line up.
        /// </summary>
        public static int Advance(string s, Font f)
        {
            if (string.IsNullOrEmpty(s)) return 0;
            return TextWidth(s + "|", f) - TextWidth("|", f);
        }

        public static GraphicsPath Round(RectangleF r, float radius)
        {
            GraphicsPath p = new GraphicsPath();
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            if (d <= 0.5f) { p.AddRectangle(r); return p; }
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        public static void FillRound(Graphics g, Color c, RectangleF r, float radius)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Round(r, radius))
            using (SolidBrush b = new SolidBrush(c)) g.FillPath(b, p);
            g.SmoothingMode = old;
        }

        public static void StrokeRound(Graphics g, Color c, RectangleF r, float radius, float width)
        {
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (GraphicsPath p = Round(r, radius))
            using (Pen pen = new Pen(c, width)) g.DrawPath(pen, p);
            g.SmoothingMode = old;
        }

        /// <summary>Dark title bar whose caption colour matches the window top (Windows 11; ignored elsewhere).</summary>
        public static void DarkTitleBar(Form f, Color caption)
        {
            try
            {
                int on = 1;
                DwmSetWindowAttribute(f.Handle, 20, ref on, 4);           // DWMWA_USE_IMMERSIVE_DARK_MODE
                int cap = caption.R | (caption.G << 8) | (caption.B << 16);
                DwmSetWindowAttribute(f.Handle, 35, ref cap, 4);          // DWMWA_CAPTION_COLOR
                int txt = Text.R | (Text.G << 8) | (Text.B << 16);
                DwmSetWindowAttribute(f.Handle, 36, ref txt, 4);          // DWMWA_TEXT_COLOR
                int border = Line.R | (Line.G << 8) | (Line.B << 16);
                DwmSetWindowAttribute(f.Handle, 34, ref border, 4);       // DWMWA_BORDER_COLOR
            }
            catch (Exception) { }
        }

        /// <summary>Dark native scrollbars for list-like controls.</summary>
        public static void DarkScrollbars(Control c)
        {
            try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch (Exception) { }
        }

        [DllImport("dwmapi.dll")]
        private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        private static extern int SetWindowTheme(IntPtr hwnd, string app, string idList);
    }

    /// <summary>
    /// Small UI animations (hover fades, the sliding tab, rows and thumbnails fading in) on one shared UI-thread timer.
    /// Off when Windows' "Animation effects" setting is off, so motion-sensitive users get none.
    /// </summary>
    internal static class Anim
    {
        [DllImport("user32.dll")] private static extern bool SystemParametersInfo(int action, int param, out bool value, int winIni);

        public static readonly bool On = Enabled();
        private static readonly List<Func<bool>> steps = new List<Func<bool>>();
        private static Timer timer;
        private static readonly System.Diagnostics.Stopwatch clock = System.Diagnostics.Stopwatch.StartNew();

        private static bool Enabled()
        {
            bool on;
            try { return !SystemParametersInfo(0x1042 /* SPI_GETCLIENTAREAANIMATION */, 0, out on, 0) || on; }
            catch (Exception) { return true; }
        }

        /// <summary>Milliseconds since the app started: the time base for every animation.</summary>
        public static long Now { get { return clock.ElapsedMilliseconds; } }

        /// <summary>Calls step about 60 times a second until it returns false.</summary>
        public static void Run(Func<bool> step)
        {
            if (!On) { step(); return; }
            if (steps.Contains(step)) return;
            steps.Add(step);
            if (timer == null)
            {
                timer = new Timer();
                timer.Interval = 15;
                timer.Tick += delegate
                {
                    for (int i = steps.Count - 1; i >= 0; i--)
                    {
                        bool more;
                        try { more = steps[i](); } catch (Exception) { more = false; }
                        if (!more) steps.RemoveAt(i);
                    }
                    if (steps.Count == 0) timer.Stop();
                };
            }
            timer.Start();
        }

        /// <summary>0..1 progress of an animation that started at a time from Now and lasts ms.</summary>
        public static float Progress(long start, int ms)
        {
            if (!On) return 1f;
            return Math.Max(0f, Math.Min(1f, (Now - start) / (float)ms));
        }

        /// <summary>Ease-out cubic: quick start, soft landing.</summary>
        public static float Ease(float t) { t = 1 - t; return 1 - t * t * t; }

        public static Color Mix(Color a, Color b, float t)
        {
            t = Math.Max(0f, Math.Min(1f, t));
            return Color.FromArgb((int)(a.A + (b.A - a.A) * t), (int)(a.R + (b.R - a.R) * t), (int)(a.G + (b.G - a.G) * t), (int)(a.B + (b.B - a.B) * t));
        }

        /// <summary>Draws an image at an opacity (0..1).</summary>
        public static void DrawImage(Graphics g, Image img, Rectangle dst, float alpha)
        {
            if (alpha >= 0.999f) { g.DrawImage(img, dst); return; }
            if (alpha <= 0.001f) return;
            using (ImageAttributes ia = new ImageAttributes())
            {
                ColorMatrix cm = new ColorMatrix();
                cm.Matrix33 = alpha;
                ia.SetColorMatrix(cm);
                g.DrawImage(img, dst, 0, 0, img.Width, img.Height, GraphicsUnit.Pixel, ia);
            }
        }
    }

    /// <summary>Embedded PNG sprites, drawn 9-sliced.</summary>
    internal static class Skin
    {
        private sealed class Sprite
        {
            public Bitmap Image;
            public int Pad;      // CSS px of shadow/glow around the visible shape
            public int Radius;   // CSS px
        }

        private static readonly Dictionary<string, Sprite> sprites = new Dictionary<string, Sprite>();
        private const int SpriteScale = 2;   // sprites are rendered at 2x

        // name -> pad, radius (CSS px); must match design\ui\sprites.html and tools\make-skin.ps1
        private static readonly Dictionary<string, int[]> table = new Dictionary<string, int[]>
        {
            { "find", new[] { 6, 12 } }, { "find-on", new[] { 6, 12 } },
            { "field", new[] { 5, 12 } }, { "field-hov", new[] { 5, 12 } }, { "field-on", new[] { 5, 12 } },
            { "mini", new[] { 5, 9 } }, { "mini-on", new[] { 5, 9 } },
            { "pri", new[] { 20, 12 } }, { "pri-hov", new[] { 20, 12 } }, { "pri-down", new[] { 20, 12 } }, { "pri-off", new[] { 20, 12 } },
            { "sec", new[] { 2, 12 } }, { "sec-hov", new[] { 2, 12 } }, { "sec-down", new[] { 2, 12 } },
            { "chip", new[] { 2, 16 } }, { "chip-hov", new[] { 2, 16 } }, { "chip-on", new[] { 2, 16 } }, { "chip-onh", new[] { 2, 16 } },
            { "hero", new[] { 0, 0 } },
        };

        private static Sprite Get(string name)
        {
            Sprite s;
            if (sprites.TryGetValue(name, out s)) return s;
            s = new Sprite();
            int[] t = table[name];
            s.Pad = t[0];
            s.Radius = t[1];
            using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("skin." + name + ".png"))
                s.Image = st == null ? null : new Bitmap(st);
            sprites[name] = s;
            return s;
        }

        /// <summary>How far (device px) a sprite's shadow reaches beyond the shape on each side.</summary>
        public static int Pad(string name) { return Theme.S(Get(name).Pad); }

        /// <summary>Draws the sprite so its visible shape (not its shadow) fills 'shape'.</summary>
        public static void Draw(Graphics g, string name, Rectangle shape) { Draw(g, name, shape, 1f); }

        /// <summary>Draws the sprite at an opacity (0..1): how hover states fade in and out.</summary>
        public static void Draw(Graphics g, string name, Rectangle shape, float alpha)
        {
            if (alpha <= 0.001f) return;
            Sprite s = Get(name);
            if (s.Image == null) { Theme.FillRound(g, Theme.Surface, shape, Theme.S(12)); return; }
            int pad = Theme.S(s.Pad);
            Rectangle dst = Rectangle.Inflate(shape, pad, pad);
            int srcC = (s.Pad + s.Radius) * SpriteScale;
            int dstC = Theme.S(s.Pad + s.Radius);
            srcC = Math.Min(srcC, Math.Min(s.Image.Width, s.Image.Height) / 2);
            dstC = Math.Min(dstC, Math.Min(dst.Width, dst.Height) / 2);

            InterpolationMode oldI = g.InterpolationMode;
            PixelOffsetMode oldP = g.PixelOffsetMode;
            g.InterpolationMode = InterpolationMode.HighQualityBicubic;
            g.PixelOffsetMode = PixelOffsetMode.Half;
            using (ImageAttributes ia = new ImageAttributes())
            {
                ia.SetWrapMode(WrapMode.TileFlipXY);   // no seams at slice edges
                if (alpha < 0.999f) { ColorMatrix cm = new ColorMatrix(); cm.Matrix33 = alpha; ia.SetColorMatrix(cm); }
                int sw = s.Image.Width, sh = s.Image.Height;
                int[] sx = { 0, srcC, sw - srcC, sw };
                int[] sy = { 0, srcC, sh - srcC, sh };
                int[] dx = { dst.Left, dst.Left + dstC, dst.Right - dstC, dst.Right };
                int[] dy = { dst.Top, dst.Top + dstC, dst.Bottom - dstC, dst.Bottom };
                for (int row = 0; row < 3; row++)
                    for (int col = 0; col < 3; col++)
                    {
                        Rectangle d = Rectangle.FromLTRB(dx[col], dy[row], dx[col + 1], dy[row + 1]);
                        if (d.Width <= 0 || d.Height <= 0) continue;
                        g.DrawImage(s.Image, d, sx[col], sy[row], sx[col + 1] - sx[col], sy[row + 1] - sy[row], GraphicsUnit.Pixel, ia);
                    }
            }
            g.InterpolationMode = oldI;
            g.PixelOffsetMode = oldP;
        }

        private static Image appIcon;
        private static readonly Dictionary<string, Image> art = new Dictionary<string, Image>();

        /// <summary>An illustration embedded as art.NAME.png (src\App\art), or null.</summary>
        public static Image Art(string name)
        {
            Image img;
            if (art.TryGetValue(name, out img)) return img;
            try
            {
                using (Stream st = Assembly.GetExecutingAssembly().GetManifestResourceStream("art." + name + ".png"))
                    img = st == null ? null : new Bitmap(st);
            }
            catch (Exception) { img = null; }
            art[name] = img;
            return img;
        }

        /// <summary>
        /// The biggest image in the embedded app.ico. Its frames are PNG-compressed, which System.Drawing.Icon cannot
        /// turn into a bitmap, so the PNG bytes are read straight out of the icon directory.
        /// </summary>
        public static Image AppIcon()
        {
            if (appIcon != null) return appIcon;
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                using (BinaryReader r = new BinaryReader(s))
                {
                    byte[] all = r.ReadBytes((int)s.Length);
                    int count = BitConverter.ToUInt16(all, 4);
                    int best = -1, bestSize = 0;
                    for (int i = 0; i < count; i++)
                    {
                        int w = all[6 + 16 * i];
                        if (w == 0) w = 256;
                        if (w > bestSize) { bestSize = w; best = i; }
                    }
                    int len = BitConverter.ToInt32(all, 6 + 16 * best + 8);
                    int off = BitConverter.ToInt32(all, 6 + 16 * best + 12);
                    appIcon = new Bitmap(new MemoryStream(all, off, len));
                }
            }
            catch (Exception) { appIcon = null; }
            return appIcon;
        }

        /// <summary>The hero background: the mockup's glow gradient, scaled to the width, solid below.</summary>
        public static void DrawHero(Graphics g, Rectangle area, Rectangle clip)
        {
            using (SolidBrush b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, clip);
            Sprite s = Get("hero");
            if (s.Image == null) return;
            float k = area.Width / (float)s.Image.Width;
            int h = (int)(s.Image.Height * k);
            InterpolationMode old = g.InterpolationMode;
            g.InterpolationMode = InterpolationMode.HighQualityBilinear;
            g.DrawImage(s.Image, new Rectangle(area.Left, area.Top, area.Width, h));
            g.InterpolationMode = old;
        }
    }

    /// <summary>A container that paints a background its skinned children can show through their shadows.</summary>
    internal interface IBackdrop
    {
        void PaintBackdrop(Graphics g, Rectangle clipInSelf);
    }

    internal class Backdrop : Panel, IBackdrop
    {
        public bool Hero;
        public Color Fill = Theme.Bg;
        public Color BottomLine = Color.Empty;

        public Backdrop()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
        }

        public void PaintBackdrop(Graphics g, Rectangle clip)
        {
            if (Hero) Skin.DrawHero(g, ClientRectangle, clip);
            else using (SolidBrush b = new SolidBrush(Fill)) g.FillRectangle(b, clip);
            if (BottomLine != Color.Empty)
                using (Pen p = new Pen(BottomLine)) g.DrawLine(p, 0, Height - 1, Width, Height - 1);
        }

        protected override void OnPaintBackground(PaintEventArgs e) { PaintBackdrop(e.Graphics, e.ClipRectangle); }
    }

    /// <summary>Base for skinned controls: paints whatever the parent backdrop has behind it.</summary>
    internal class SkinControl : Control
    {
        protected bool hover, down;
        protected float hot;              // hover, faded: 0 = resting, 1 = fully hovered
        private Func<bool> hotStep;

        public SkinControl()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint |
                     ControlStyles.ResizeRedraw | ControlStyles.SupportsTransparentBackColor, true);
            BackColor = Color.Transparent;
            ForeColor = Theme.Text;
        }

        protected override void OnPaintBackground(PaintEventArgs e)
        {
            IBackdrop b = Parent as IBackdrop;
            if (b != null)
            {
                GraphicsState st = e.Graphics.Save();
                e.Graphics.TranslateTransform(-Left, -Top);
                Rectangle clip = e.ClipRectangle;
                clip.Offset(Left, Top);
                b.PaintBackdrop(e.Graphics, clip);
                e.Graphics.Restore(st);
            }
            else
            {
                using (SolidBrush br = new SolidBrush(Parent != null ? Parent.BackColor : Theme.Bg)) e.Graphics.FillRectangle(br, ClientRectangle);
            }
        }

        protected override void OnMouseEnter(EventArgs e) { hover = true; FadeHover(); base.OnMouseEnter(e); }
        protected override void OnMouseLeave(EventArgs e) { hover = false; down = false; FadeHover(); base.OnMouseLeave(e); }

        /// <summary>Moves "hot" toward the hover state: about 110 ms in, 180 ms out.</summary>
        protected void FadeHover()
        {
            if (hotStep == null)
                hotStep = delegate
                {
                    if (IsDisposed) return false;
                    float target = hover ? 1f : 0f;
                    hot = hover ? Math.Min(target, hot + 0.14f) : Math.Max(target, hot - 0.085f);
                    if (!Anim.On) hot = target;
                    Invalidate();
                    return hot != target;
                };
            Anim.Run(hotStep);
        }
        protected override void OnMouseDown(MouseEventArgs e) { if (e.Button == MouseButtons.Left) { down = true; Invalidate(); } base.OnMouseDown(e); }
        protected override void OnMouseUp(MouseEventArgs e) { down = false; Invalidate(); base.OnMouseUp(e); }
        protected override void OnEnabledChanged(EventArgs e) { Invalidate(); base.OnEnabledChanged(e); }

        protected static void Glyph(Graphics g, string glyph, float px, Color c, Rectangle r, TextFormatFlags f)
        {
            TextRenderer.DrawText(g, glyph, Theme.Icons(px), r, c, f);
        }
    }

    internal enum ButtonKind { Primary, Secondary, Ghost }

    internal sealed class SkinButton : SkinControl, IButtonControl
    {
        public ButtonKind Kind = ButtonKind.Secondary;
        public string Icon = "";
        public float FontPx = 15;
        public Image Picture;             // an illustration instead of a glyph (it tilts a little on hover)
        public int PictureCss = 34;
        public bool Accent;               // a secondary button with an amber edge, for the action that matters here

        public SkinButton(string text, string icon, ButtonKind kind)
        {
            Text = text;
            Icon = icon ?? "";
            Kind = kind;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.Selectable, true);
            if (kind == ButtonKind.Primary) FontPx = 16;
        }

        /// <summary>Width that fits the label, plus the sprite padding.</summary>
        public int PreferredWidth(int heightCss)
        {
            int w = Theme.TextWidth(Text, Theme.UiSemibold(FontPx));
            if (Picture != null) w += Theme.S(PictureCss + 8);
            else if (Icon.Length > 0) w += Theme.S(FontPx + 9);
            return w + Theme.S(Kind == ButtonKind.Primary ? 52 : 40) + 2 * ShadowPad;
        }

        public int ShadowPad { get { return Kind == ButtonKind.Ghost ? 0 : Skin.Pad(Kind == ButtonKind.Primary ? "pri" : "sec"); } }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int pad = ShadowPad;
            Rectangle shape = Rectangle.Inflate(ClientRectangle, -pad, -pad);
            Color fg;
            if (Kind == ButtonKind.Primary)
            {
                if (!Enabled || down) Skin.Draw(g, !Enabled ? "pri-off" : "pri-down", shape);
                else { Skin.Draw(g, "pri", shape); Skin.Draw(g, "pri-hov", shape, hot); }
                fg = Enabled ? Theme.AmberInk : Theme.Faint;
            }
            else if (Kind == ButtonKind.Secondary)
            {
                if (down) Skin.Draw(g, "sec-down", shape);
                else { Skin.Draw(g, "sec", shape); Skin.Draw(g, "sec-hov", shape, hot); }
                if (Accent)
                {
                    Theme.FillRound(g, Theme.Alpha(Theme.Amber, (int)(14 + 16 * hot)), shape, Theme.S(12));
                    Theme.StrokeRound(g, Theme.Alpha(Theme.Amber, (int)(120 + 100 * hot)), Rectangle.Inflate(shape, -1, -1), Theme.S(11), Theme.S(1.3f));
                }
                fg = Enabled ? (Accent ? Anim.Mix(Theme.Text, Theme.AmberHi, hot) : Theme.Text) : Theme.Faint;
            }
            else
            {
                if (hot > 0) Theme.FillRound(g, Theme.Alpha(Theme.Surface2, (int)(200 * hot)), shape, Theme.S(8));
                fg = Anim.Mix(Theme.Dim, Theme.Text, hot);
            }
            if (Focused && ShowFocusCues && Kind != ButtonKind.Ghost)
                Theme.StrokeRound(g, Theme.Alpha(Theme.AmberHi, 160), Rectangle.Inflate(shape, -Theme.S(3), -Theme.S(3)), Theme.S(9), Theme.S(1));

            Font f = Theme.UiSemibold(FontPx);
            int tw = Theme.TextWidth(Text, f);
            int iw = Picture != null ? Theme.S(PictureCss) : Icon.Length > 0 ? Theme.S(FontPx) : 0;
            int gap = iw > 0 && Text.Length > 0 ? Theme.S(Picture != null ? 8 : 9) : 0;
            int x = shape.Left + (shape.Width - tw - iw - gap) / 2;
            if (Picture != null)
            {
                // the Polaroid lifts and straightens a little under the pointer
                GraphicsState st = g.Save();
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                float cx = x + iw / 2f, cy = shape.Top + shape.Height / 2f - Theme.S(2) * hot;
                g.TranslateTransform(cx, cy);
                g.RotateTransform(5f * hot);
                float sc = 1f + 0.08f * hot;
                g.ScaleTransform(sc, sc);
                g.DrawImage(Picture, new RectangleF(-iw / 2f, -iw / 2f, iw, iw));
                g.Restore(st);
            }
            else if (iw > 0) Glyph(g, Icon, FontPx, fg, new Rectangle(x, shape.Top, iw, shape.Height), Theme.Center);
            TextRenderer.DrawText(g, Text, f, new Rectangle(x + iw + gap, shape.Top, tw + 2, shape.Height), fg, Theme.Left);
        }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space || e.KeyCode == Keys.Enter) { e.Handled = true; PerformClick(); }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        // IButtonControl, so a form's AcceptButton / CancelButton can point here.
        public DialogResult DialogResult { get; set; }
        public void NotifyDefault(bool value) { }
        public void PerformClick() { if (Enabled) OnClick(EventArgs.Empty); }

        protected override void OnClick(EventArgs e)
        {
            base.OnClick(e);
            if (DialogResult != DialogResult.None)
            {
                Form f = FindForm();
                if (f != null) f.DialogResult = DialogResult;
            }
        }
    }

    /// <summary>A pill toggle ("chip"). Toggle = false makes it a button that looks like a chip (menus).</summary>
    internal sealed class Chip : SkinControl
    {
        private bool on;
        public bool Toggle = true;
        public string Prefix = "";       // short mono text such as "Aa", or a glyph when PrefixIsIcon
        public bool PrefixIsIcon;
        public string Suffix = "";       // glyph after the text (chevrons)
        public event EventHandler CheckedChanged;

        public Chip(string text, string prefix, bool prefixIsIcon)
        {
            Text = text;
            Prefix = prefix ?? "";
            PrefixIsIcon = prefixIsIcon;
            Cursor = Cursors.Hand;
        }

        [DefaultValue(false)]
        public bool Checked
        {
            get { return on; }
            set
            {
                if (on == value) return;
                on = value;
                Invalidate();
                if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty);
            }
        }

        public int PreferredWidth()
        {
            int w = Theme.TextWidth(Text, Theme.Ui(13, FontStyle.Regular)) + Theme.S(26);
            if (Prefix.Length > 0) w += (PrefixIsIcon ? Theme.S(14) : Theme.TextWidth(Prefix, Theme.Mono(12, FontStyle.Bold))) + Theme.S(7);
            if (Suffix.Length > 0) w += Theme.S(17);
            return w + 2 * Skin.Pad("chip");
        }

        protected override void OnClick(EventArgs e)
        {
            if (Toggle) Checked = !Checked;
            base.OnClick(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int pad = Skin.Pad("chip");
            Rectangle shape = Rectangle.Inflate(ClientRectangle, -pad, -pad);
            bool lit = on && Toggle;
            Skin.Draw(g, lit ? "chip-on" : "chip", shape);
            Skin.Draw(g, lit ? "chip-onh" : "chip-hov", shape, hot);
            Color fg = lit ? Theme.AmberHi : (Toggle ? Anim.Mix(Theme.Dim, Theme.Text, hot) : Theme.Text);
            Color pre = lit ? Theme.Amber : Theme.Faint;
            Font f = Theme.Ui(13, FontStyle.Regular);
            int x = shape.Left + Theme.S(13);
            if (Prefix.Length > 0)
            {
                int pw;
                if (PrefixIsIcon)
                {
                    pw = Theme.S(14);
                    Glyph(g, Prefix, 13, lit ? Theme.AmberHi : fg, new Rectangle(x, shape.Top, pw, shape.Height), Theme.Center);
                }
                else
                {
                    Font pf = Theme.Mono(12, FontStyle.Bold);
                    pw = Theme.TextWidth(Prefix, pf);
                    TextRenderer.DrawText(g, Prefix, pf, new Rectangle(x, shape.Top, pw + 2, shape.Height), pre, Theme.Left);
                }
                x += pw + Theme.S(7);
            }
            int tw = Theme.TextWidth(Text, f);
            TextRenderer.DrawText(g, Text, f, new Rectangle(x, shape.Top, tw + 2, shape.Height), fg, Theme.Left);
            if (Suffix.Length > 0)
                Glyph(g, Suffix, 10, fg, new Rectangle(x + tw + Theme.S(6), shape.Top, Theme.S(12), shape.Height), Theme.Center);
        }
    }

    /// <summary>A pill-shaped segmented switch (the window's mode tabs).</summary>
    internal sealed class Tabs : SkinControl
    {
        private readonly string[] items;
        private readonly string[] icons;
        private int selected;
        private int hoverIndex = -1;
        private Rectangle[] rects = new Rectangle[0];
        private RectangleF pill, pillFrom;   // the selected pill, and where it slides from
        private long slideStart = -1000;
        public event EventHandler SelectedChanged;

        public Tabs(string[] items, string[] icons)
        {
            this.items = items;
            this.icons = icons;
            Cursor = Cursors.Hand;
        }

        public int SelectedIndex
        {
            get { return selected; }
            set
            {
                if (selected == value) return;
                selected = value;
                pillFrom = pill;
                slideStart = Anim.Now;
                Anim.Run(delegate { Invalidate(); return !IsDisposed && Anim.Progress(slideStart, 240) < 1f; });
                Invalidate();
                if (SelectedChanged != null) SelectedChanged(this, EventArgs.Empty);
            }
        }

        private int ItemWidth(int i)
        {
            return Theme.TextWidth(items[i], Theme.UiSemibold(13)) + Theme.S(icons[i].Length > 0 ? 44 : 28);
        }

        public int PreferredWidth()
        {
            int w = Theme.S(8);
            for (int i = 0; i < items.Length; i++) w += ItemWidth(i);
            return w;
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle outer = Rectangle.Inflate(ClientRectangle, -1, -1);
            Theme.FillRound(g, Theme.Surface, outer, outer.Height / 2f);
            Theme.StrokeRound(g, Theme.Line, outer, outer.Height / 2f, 1);
            rects = new Rectangle[items.Length];
            int x0 = outer.Left + Theme.S(4);
            for (int i = 0; i < items.Length; i++)
            {
                rects[i] = new Rectangle(x0, outer.Top + Theme.S(4), ItemWidth(i), outer.Height - Theme.S(8));
                x0 += rects[i].Width;
            }
            // the selected pill slides from the old tab to the new one
            RectangleF target = rects[selected];
            float t = Anim.Ease(Anim.Progress(slideStart, 240));
            pill = pillFrom.Width <= 0 || t >= 1f ? target : new RectangleF(pillFrom.X + (target.X - pillFrom.X) * t, target.Y, pillFrom.Width + (target.Width - pillFrom.Width) * t, target.Height);
            for (int i = 0; i < items.Length; i++)
                if (i == hoverIndex && i != selected) Theme.FillRound(g, Theme.Surface2, rects[i], rects[i].Height / 2f);
            Rectangle pr = Rectangle.Round(pill);
            Theme.FillRound(g, Color.FromArgb(0x2A, 0x24, 0x16), pr, pr.Height / 2f);
            Theme.StrokeRound(g, Theme.Alpha(Theme.Amber, 115), pr, pr.Height / 2f, 1);
            int x = outer.Left + Theme.S(4);
            for (int i = 0; i < items.Length; i++)
            {
                int w = ItemWidth(i);
                Rectangle r = rects[i];
                bool on = i == selected;
                Color fg = on ? Anim.Mix(Theme.Dim, Theme.AmberHi, t) : (i == hoverIndex ? Theme.Text : Theme.Dim);
                int tx = r.Left + Theme.S(14);
                if (icons[i].Length > 0)
                {
                    Glyph(g, icons[i], 13, fg, new Rectangle(tx, r.Top, Theme.S(14), r.Height), Theme.Center);
                    tx += Theme.S(20);
                }
                TextRenderer.DrawText(g, items[i], Theme.UiSemibold(13), new Rectangle(tx, r.Top, r.Right - tx, r.Height), fg, Theme.Left);
                x += w;
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            int h = -1;
            for (int i = 0; i < rects.Length; i++) if (rects[i].Contains(e.Location)) h = i;
            if (h != hoverIndex) { hoverIndex = h; Invalidate(); }
        }

        protected override void OnMouseLeave(EventArgs e) { hoverIndex = -1; base.OnMouseLeave(e); }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            for (int i = 0; i < rects.Length; i++) if (rects[i].Contains(e.Location)) SelectedIndex = i;
        }
    }

    internal enum FieldStyle { Find, Normal, Mini }

    /// <summary>A skinned text field: sprite frame, optional icon, a borderless TextBox, optional drop-down arrow or key hint.</summary>
    internal sealed class SkinField : SkinControl
    {
        public readonly TextBox Box = new TextBox();
        public readonly FieldStyle Style;
        public string Icon = "";
        public string KeyHint = "";
        public bool DropDown;
        public event EventHandler DropDownClicked;
        private Rectangle dropRect;

        public SkinField(FieldStyle style, string icon)
        {
            Style = style;
            Icon = icon ?? "";
            Box.BorderStyle = BorderStyle.None;
            Box.BackColor = style == FieldStyle.Find ? Theme.FindFill : Theme.Surface;
            Box.ForeColor = Theme.Text;
            Box.Font = style == FieldStyle.Find ? Theme.Ui(19, FontStyle.Regular) : style == FieldStyle.Mini ? Theme.Ui(13) : Theme.Ui(15);
            Box.GotFocus += delegate { Invalidate(); };
            Box.LostFocus += delegate { Invalidate(); };
            Box.MouseEnter += delegate { hover = true; Invalidate(); };
            Box.MouseLeave += delegate { hover = ClientRectangle.Contains(PointToClient(Cursor.Position)); Invalidate(); };
            Controls.Add(Box);
            Cursor = Cursors.IBeam;
        }

        public override string Text
        {
            get { return Box.Text; }
            set { Box.Text = value; }
        }

        /// <summary>Grey hint text shown while the box is empty.</summary>
        public void SetPlaceholder(string text)
        {
            if (Box.IsHandleCreated) SendMessage(Box.Handle, 0x1501, (IntPtr)1, text);
            else Box.HandleCreated += delegate { SendMessage(Box.Handle, 0x1501, (IntPtr)1, text); };
        }

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern IntPtr SendMessage(IntPtr h, int msg, IntPtr w, string l);

        private string Sprite
        {
            get
            {
                bool focus = Box.Focused;
                switch (Style)
                {
                    case FieldStyle.Find: return focus ? "find-on" : "find";
                    case FieldStyle.Mini: return focus ? "mini-on" : "mini";
                    default: return focus ? "field-on" : hover ? "field-hov" : "field";
                }
            }
        }

        public int ShadowPad { get { return Skin.Pad(Style == FieldStyle.Find ? "find" : Style == FieldStyle.Mini ? "mini" : "field"); } }

        protected override void OnLayout(LayoutEventArgs e)
        {
            base.OnLayout(e);
            int pad = ShadowPad;
            Rectangle shape = Rectangle.Inflate(ClientRectangle, -pad, -pad);
            int left = shape.Left + Theme.S(Style == FieldStyle.Mini ? 11 : 16);
            if (Icon.Length > 0) left += Theme.S(Style == FieldStyle.Find ? 32 : 29);
            int right = shape.Right - Theme.S(Style == FieldStyle.Mini ? 10 : 14);
            if (DropDown) { dropRect = new Rectangle(shape.Right - Theme.S(38), shape.Top, Theme.S(38), shape.Height); right = dropRect.Left; }
            if (KeyHint.Length > 0) right -= Theme.TextWidth(KeyHint, Theme.Mono(12)) + Theme.S(28);
            int h = Box.PreferredHeight;
            Box.SetBounds(left, shape.Top + (shape.Height - h) / 2 + Theme.S(1), Math.Max(10, right - left), h);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int pad = ShadowPad;
            Rectangle shape = Rectangle.Inflate(ClientRectangle, -pad, -pad);
            Skin.Draw(g, Sprite, shape);
            if (Icon.Length > 0)
            {
                bool find = Style == FieldStyle.Find;
                Glyph(g, Icon, find ? 20 : 17, find ? Theme.AmberHi : Theme.Dim,
                    new Rectangle(shape.Left + Theme.S(16), shape.Top, Theme.S(find ? 22 : 19), shape.Height), Theme.Center);
            }
            if (KeyHint.Length > 0)
            {
                Font kf = Theme.Mono(12);
                int kw = Theme.TextWidth(KeyHint, kf) + Theme.S(14);
                Rectangle k = new Rectangle(shape.Right - Theme.S(14) - kw - (DropDown ? Theme.S(38) : 0), shape.Top + (shape.Height - Theme.S(22)) / 2, kw, Theme.S(22));
                Theme.StrokeRound(g, Theme.Line2, k, Theme.S(6), 1);
                TextRenderer.DrawText(g, KeyHint, kf, k, Theme.Faint, Theme.Center);
            }
            if (DropDown)
                Glyph(g, Theme.GDown, 12, hover ? Theme.Text : Theme.Faint, dropRect, Theme.Center);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (DropDown && dropRect.Contains(e.Location)) { if (DropDownClicked != null) DropDownClicked(this, EventArgs.Empty); }
            else Box.Focus();
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = DropDown && dropRect.Contains(e.Location) ? Cursors.Hand : Cursors.IBeam;
        }

        /// <summary>Opens a menu under the field.</summary>
        public void ShowMenu(ContextMenuStrip menu)
        {
            int pad = ShadowPad;
            menu.MinimumSize = new Size(Width - 2 * pad, 0);
            menu.Show(this, new Point(pad, Height - pad + Theme.S(4)));
        }
    }

    /// <summary>A rounded checkbox with an amber check (installer options).</summary>
    internal sealed class SkinCheck : SkinControl
    {
        private bool on;
        public event EventHandler CheckedChanged;

        public SkinCheck(string text, bool isChecked)
        {
            Text = text;
            on = isChecked;
            Cursor = Cursors.Hand;
            SetStyle(ControlStyles.Selectable, true);
        }

        public bool Checked
        {
            get { return on; }
            set { if (on == value) return; on = value; Invalidate(); if (CheckedChanged != null) CheckedChanged(this, EventArgs.Empty); }
        }

        public int PreferredWidth() { return Theme.S(34) + Theme.TextWidth(Text, Theme.Ui(14)) + Theme.S(4); }

        protected override void OnClick(EventArgs e) { if (Enabled) Checked = !Checked; base.OnClick(e); }

        protected override void OnKeyDown(KeyEventArgs e)
        {
            if (e.KeyCode == Keys.Space) { e.Handled = true; if (Enabled) Checked = !Checked; }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int box = Theme.S(22);
            Rectangle b = new Rectangle(Theme.S(1), (Height - box) / 2, box, box);
            if (on)
            {
                Theme.FillRound(g, Enabled ? Theme.Amber : Theme.Line2, b, Theme.S(6));
                Glyph(g, Theme.GCheck, 13, Theme.AmberInk, b, Theme.Center);
            }
            else
            {
                Theme.FillRound(g, Theme.Surface, b, Theme.S(6));
                Theme.StrokeRound(g, hover ? Theme.Dim : Theme.Line2, b, Theme.S(6), Theme.S(1.2f));
            }
            if (Focused && ShowFocusCues) Theme.StrokeRound(g, Theme.Alpha(Theme.AmberHi, 150), Rectangle.Inflate(b, Theme.S(3), Theme.S(3)), Theme.S(8), 1);
            TextRenderer.DrawText(g, Text, Theme.Ui(14), new Rectangle(b.Right + Theme.S(11), 0, Width - b.Right, Height),
                Enabled ? Theme.Text : Theme.Faint, Theme.Left);
        }
    }

    /// <summary>Thin amber position slider (the viewer's "where in the file").</summary>
    internal sealed class Slider : SkinControl
    {
        private double value;
        private bool dragging;
        public event EventHandler Scrolling;     // while dragging (Value updates live)
        public event EventHandler Committed;     // on release / click

        public Slider() { Cursor = Cursors.Hand; }

        public double Value
        {
            get { return value; }
            set { this.value = Math.Max(0, Math.Min(1, value)); Invalidate(); }
        }

        private Rectangle Track
        {
            get { int r = Theme.S(8); return new Rectangle(r, Height / 2 - Theme.S(2), Math.Max(1, Width - 2 * r), Theme.S(4)); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            Rectangle t = Track;
            Color line = Enabled ? Theme.Line : Theme.RowLine;
            Theme.FillRound(g, line, t, t.Height / 2f);
            if (!Enabled) return;
            int x = t.Left + (int)(t.Width * value);
            Theme.FillRound(g, Theme.Alpha(Theme.Amber, 170), new Rectangle(t.Left, t.Top, Math.Max(t.Height, x - t.Left), t.Height), t.Height / 2f);
            int d = Theme.S(hover || dragging ? 16 : 13);
            Rectangle knob = new Rectangle(x - d / 2, Height / 2 - d / 2, d, d);
            if (hover || dragging) Theme.FillRound(g, Theme.Alpha(Theme.Amber, 50), Rectangle.Inflate(knob, Theme.S(5), Theme.S(5)), d);
            Theme.FillRound(g, Theme.AmberHi, knob, d / 2f);
        }

        private void SetFromX(int x)
        {
            Rectangle t = Track;
            Value = (x - t.Left) / (double)t.Width;
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            base.OnMouseDown(e);
            if (!Enabled || e.Button != MouseButtons.Left) return;
            dragging = true;
            SetFromX(e.X);
            if (Scrolling != null) Scrolling(this, EventArgs.Empty);
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            if (!dragging) return;
            SetFromX(e.X);
            if (Scrolling != null) Scrolling(this, EventArgs.Empty);
        }

        protected override void OnMouseUp(MouseEventArgs e)
        {
            base.OnMouseUp(e);
            if (!dragging) return;
            dragging = false;
            Invalidate();
            if (Committed != null) Committed(this, EventArgs.Empty);
        }
    }

    /// <summary>Dark context menus to match.</summary>
    internal sealed class DarkMenuRenderer : ToolStripProfessionalRenderer
    {
        public DarkMenuRenderer() : base(new Colors()) { RoundedEdges = true; }

        public static ContextMenuStrip Menu()
        {
            ContextMenuStrip m = new ContextMenuStrip();
            m.Renderer = new DarkMenuRenderer();
            m.BackColor = Theme.Surface;
            m.ForeColor = Theme.Text;
            m.Font = Theme.Ui(14);
            m.ShowImageMargin = false;
            m.Padding = new Padding(Theme.S(4));
            return m;
        }

        protected override void OnRenderItemText(ToolStripItemTextRenderEventArgs e)
        {
            e.TextColor = e.Item.Enabled ? (e.Item.Selected ? Theme.AmberHi : Theme.Text) : Theme.Faint;
            base.OnRenderItemText(e);
        }

        protected override void OnRenderArrow(ToolStripArrowRenderEventArgs e)
        {
            e.ArrowColor = Theme.Dim;
            base.OnRenderArrow(e);
        }

        private sealed class Colors : ProfessionalColorTable
        {
            public override Color ToolStripDropDownBackground { get { return Theme.Surface; } }
            public override Color MenuBorder { get { return Theme.Line2; } }
            public override Color MenuItemBorder { get { return Theme.Line2; } }
            public override Color MenuItemSelected { get { return Theme.Surface2; } }
            public override Color MenuItemSelectedGradientBegin { get { return Theme.Surface2; } }
            public override Color MenuItemSelectedGradientEnd { get { return Theme.Surface2; } }
            public override Color ImageMarginGradientBegin { get { return Theme.Surface; } }
            public override Color ImageMarginGradientMiddle { get { return Theme.Surface; } }
            public override Color ImageMarginGradientEnd { get { return Theme.Surface; } }
            public override Color SeparatorDark { get { return Theme.Line; } }
            public override Color SeparatorLight { get { return Theme.Surface; } }
        }
    }
}
