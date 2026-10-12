using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Windows.Data.Pdf;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Media.Ocr;
using Windows.Storage.Streams;

namespace FindAnything.Core
{
    /// <summary>A recognised word and where it sits on the page (image pixels).</summary>
    public sealed class OcrWordBox
    {
        public int Page;
        public int Line;
        public float X, Y, W, H;
        public string Text;
    }

    /// <summary>The text Windows' OCR found in an image or a scanned PDF.</summary>
    public sealed class OcrText
    {
        public readonly List<OcrWordBox> Words = new List<OcrWordBox>();
        public int ImageWidth, ImageHeight;   // of page 0, in the pixels the boxes use

        /// <summary>Lines of text: words joined by spaces, lines by newlines, pages by a blank line.</summary>
        public string Text
        {
            get
            {
                StringBuilder sb = new StringBuilder();
                int page = -1, line = -1;
                foreach (OcrWordBox w in Words)
                {
                    if (w.Page != page) { if (sb.Length > 0) sb.Append("\n\n"); page = w.Page; line = w.Line; }
                    else if (w.Line != line) { sb.Append('\n'); line = w.Line; }
                    else sb.Append(' ');
                    sb.Append(w.Text);
                }
                return sb.ToString();
            }
        }
    }

    /// <summary>
    /// Text in pictures and scanned PDFs, using the OCR engine built into Windows 10/11 (Windows.Media.Ocr). It runs
    /// on this PC with no internet. Results are cached per file (path + size + date) so each image is read once.
    /// </summary>
    public static class Ocr
    {
        public const int MaxPdfPages = 40;

        /// <summary>
        /// Keep what OCR read in %LOCALAPPDATA%\FindAnything\ocr so each picture is read once. Off by default: the cache
        /// is the text of your pictures, stored on disk.
        /// </summary>
        public static bool UseCache;

        private const string CacheTag = "FAOCR1";

        private static readonly string[] ImageExts = { ".jpg", ".jpeg", ".jfif", ".png", ".bmp", ".gif", ".tif", ".tiff", ".heic", ".heif", ".webp", ".jxr" };

        /// <summary>File masks for every picture type OCR reads: "*.jpg;*.jpeg;...".</summary>
        public static string PictureMasks
        {
            get
            {
                string[] m = new string[ImageExts.Length];
                for (int i = 0; i < m.Length; i++) m[i] = "*" + ImageExts[i];
                return string.Join(";", m);
            }
        }

        public static bool IsImage(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            return Array.IndexOf(ImageExts, ext) >= 0;
        }

        private static bool? available;

        /// <summary>True when Windows has an OCR language installed for this user.</summary>
        public static bool Available
        {
            get
            {
                if (available == null)
                {
                    try { available = OcrEngine.TryCreateFromUserProfileLanguages() != null; }
                    catch (Exception) { available = false; }
                }
                return available.Value;
            }
        }

        public static string CacheFolder
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FindAnything", "ocr"); }
        }

        public static bool HasCache { get { return Directory.Exists(CacheFolder); } }

        public static void ClearCache()
        {
            try { if (Directory.Exists(CacheFolder)) Directory.Delete(CacheFolder, true); } catch (Exception) { }
        }

        /// <summary>OCR text of an image file (cached).</summary>
        public static OcrText ReadImage(string ioPath)
        {
            OcrText cached = FromCache(ioPath);
            if (cached != null) return cached;
            OcrText t = new OcrText();
            double scale;
            SoftwareBitmap bmp = DecodeImage(File.ReadAllBytes(ioPath), out scale);
            using (bmp) Recognize(bmp, 0, t);
            // Boxes back in the picture's own pixels, so the viewer can draw them on the original.
            if (scale != 1)
            {
                foreach (OcrWordBox wb in t.Words) { wb.X = (float)(wb.X / scale); wb.Y = (float)(wb.Y / scale); wb.W = (float)(wb.W / scale); wb.H = (float)(wb.H / scale); }
                t.ImageWidth = (int)Math.Round(t.ImageWidth / scale); t.ImageHeight = (int)Math.Round(t.ImageHeight / scale);
            }
            ToCache(ioPath, t);
            return t;
        }

        /// <summary>OCR text of a PDF's pages, rendered by Windows' own PDF renderer (cached).</summary>
        public static OcrText ReadPdf(string ioPath, CancellationToken ct)
        {
            OcrText cached = FromCache(ioPath);
            if (cached != null) return cached;
            OcrText t = new OcrText();
            IRandomAccessStream stream = ToStream(File.ReadAllBytes(ioPath));
            PdfDocument doc = Wait(PdfDocument.LoadFromStreamAsync(stream));
            uint pages = Math.Min(doc.PageCount, (uint)MaxPdfPages);
            for (uint p = 0; p < pages; p++)
            {
                ct.ThrowIfCancellationRequested();
                using (PdfPage page = doc.GetPage(p))
                {
                    InMemoryRandomAccessStream img = new InMemoryRandomAccessStream();
                    PdfPageRenderOptions o = new PdfPageRenderOptions();
                    // About 200 dpi for a letter-size page: plenty for OCR, bounded memory.
                    o.DestinationWidth = (uint)Math.Min(2200, Math.Max(800, page.Size.Width * 200 / 72));
                    Wait(page.RenderToStreamAsync(img, o));
                    BitmapDecoder dec = Wait(BitmapDecoder.CreateAsync(img));
                    using (SoftwareBitmap bmp = Wait(dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied)))
                        Recognize(bmp, (int)p, t);
                }
            }
            ToCache(ioPath, t);
            return t;
        }

        private static void Recognize(SoftwareBitmap bmp, int page, OcrText t)
        {
            OcrEngine engine = OcrEngine.TryCreateFromUserProfileLanguages();
            if (engine == null) throw new InvalidOperationException("Windows has no OCR language installed");
            if (page == 0) { t.ImageWidth = bmp.PixelWidth; t.ImageHeight = bmp.PixelHeight; }
            OcrResult r = Wait(engine.RecognizeAsync(bmp));
            int lineNo = 0;
            foreach (OcrLine line in r.Lines)
            {
                foreach (OcrWord w in line.Words)
                {
                    OcrWordBox b = new OcrWordBox();
                    b.Page = page;
                    b.Line = lineNo;
                    b.X = (float)w.BoundingRect.X; b.Y = (float)w.BoundingRect.Y;
                    b.W = (float)w.BoundingRect.Width; b.H = (float)w.BoundingRect.Height;
                    b.Text = w.Text;
                    t.Words.Add(b);
                }
                lineNo++;
            }
        }

        /// <summary>Decodes an image with Windows' codecs, upright (EXIF) and no larger than the OCR limit.</summary>
        public static SoftwareBitmap DecodeImage(byte[] bytes, out double scale)
        {
            BitmapDecoder dec = Wait(BitmapDecoder.CreateAsync(ToStream(bytes)));
            uint max = OcrEngine.MaxImageDimension;
            uint w = dec.OrientedPixelWidth, h = dec.OrientedPixelHeight;
            BitmapTransform tr = new BitmapTransform();
            double k = 1;
            if (w > max || h > max) k = (double)max / Math.Max(w, h);
            if (k != 1)
            {
                tr.ScaledWidth = (uint)(dec.PixelWidth * k);
                tr.ScaledHeight = (uint)(dec.PixelHeight * k);
                tr.InterpolationMode = BitmapInterpolationMode.Fant;   // only ever shrinks: enlarging made OCR misread
            }
            scale = k;
            return Wait(dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, tr,
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage));
        }

        /// <summary>
        /// The picture as a GDI+ bitmap, turned upright the same way OCR saw it (EXIF), and readable even in formats
        /// GDI+ lacks (HEIC, WebP) because Windows' own codecs decode it.
        /// </summary>
        public static System.Drawing.Bitmap LoadUpright(string ioPath)
        {
            BitmapDecoder dec = Wait(BitmapDecoder.CreateAsync(ToStream(File.ReadAllBytes(ioPath))));
            using (SoftwareBitmap sb = Wait(dec.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied, new BitmapTransform(),
                ExifOrientationMode.RespectExifOrientation, ColorManagementMode.DoNotColorManage)))
            {
                InMemoryRandomAccessStream png = new InMemoryRandomAccessStream();
                BitmapEncoder enc = Wait(BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, png));
                enc.SetSoftwareBitmap(sb);
                Wait(enc.FlushAsync());
                uint size = (uint)png.Size;
                DataReader dr = new DataReader(png.GetInputStreamAt(0));
                Wait(dr.LoadAsync(size));
                byte[] bytes = new byte[size];
                dr.ReadBytes(bytes);
                return new System.Drawing.Bitmap(new MemoryStream(bytes));
            }
        }

        private static IRandomAccessStream ToStream(byte[] bytes)
        {
            InMemoryRandomAccessStream s = new InMemoryRandomAccessStream();
            DataWriter dw = new DataWriter(s);
            dw.WriteBytes(bytes);
            Wait(dw.StoreAsync());
            dw.DetachStream();
            s.Seek(0);
            return s;
        }

        // ------------------------------------------------------------------ waiting on WinRT calls
        // (the usual AsTask() helpers need the Windows SDK's union metadata; these need nothing)

        private static T Wait<T>(IAsyncOperation<T> op)
        {
            SpinUntilDone(op);
            if (op.Status != AsyncStatus.Completed) throw Failure(op);
            return op.GetResults();
        }

        private static T Wait<T, P>(IAsyncOperationWithProgress<T, P> op)
        {
            SpinUntilDone(op);
            if (op.Status != AsyncStatus.Completed) throw Failure(op);
            return op.GetResults();
        }

        private static void Wait(IAsyncAction op)
        {
            SpinUntilDone(op);
            if (op.Status != AsyncStatus.Completed) throw Failure(op);
        }

        private static void SpinUntilDone(IAsyncInfo op)
        {
            int spins = 0;
            while (op.Status == AsyncStatus.Started) Thread.Sleep(spins++ < 50 ? 1 : 5);
        }

        private static Exception Failure(IAsyncInfo op)
        {
            return op.ErrorCode != null ? new IOException("Windows could not read the image: " + op.ErrorCode.Message, op.ErrorCode)
                                        : new IOException("Windows could not read the image (" + op.Status + ")");
        }

        // ------------------------------------------------------------------ cache

        private static string CachePath(string ioPath)
        {
            FileInfo fi = new FileInfo(ioPath);
            string key = LongPath.Strip(fi.FullName).ToLowerInvariant() + "|" + fi.Length + "|" + fi.LastWriteTimeUtc.Ticks;
            using (SHA1 sha = SHA1.Create())
            {
                byte[] h = sha.ComputeHash(Encoding.UTF8.GetBytes(key));
                StringBuilder sb = new StringBuilder();
                foreach (byte b in h) sb.Append(b.ToString("x2"));
                return Path.Combine(CacheFolder, sb.ToString(0, 2), sb.ToString() + ".txt");
            }
        }

        private static OcrText FromCache(string ioPath)
        {
            if (!UseCache) return null;
            try
            {
                string p = CachePath(ioPath);
                if (!File.Exists(p)) return null;
                string[] lines = File.ReadAllLines(p, Encoding.UTF8);
                if (lines.Length == 0 || !lines[0].StartsWith(CacheTag)) return null;
                OcrText t = new OcrText();
                string[] head = lines[0].Split(' ');
                if (head.Length >= 3) { t.ImageWidth = int.Parse(head[1]); t.ImageHeight = int.Parse(head[2]); }
                for (int i = 1; i < lines.Length; i++)
                {
                    string[] f = lines[i].Split(new[] { '\t' }, 7);
                    if (f.Length < 7) continue;
                    OcrWordBox w = new OcrWordBox();
                    w.Page = int.Parse(f[0]); w.Line = int.Parse(f[1]);
                    w.X = float.Parse(f[2], CultureInfo.InvariantCulture); w.Y = float.Parse(f[3], CultureInfo.InvariantCulture);
                    w.W = float.Parse(f[4], CultureInfo.InvariantCulture); w.H = float.Parse(f[5], CultureInfo.InvariantCulture);
                    w.Text = f[6];
                    t.Words.Add(w);
                }
                return t;
            }
            catch (Exception) { return null; }
        }

        private static void ToCache(string ioPath, OcrText t)
        {
            if (!UseCache) return;
            try
            {
                string p = CachePath(ioPath);
                Directory.CreateDirectory(Path.GetDirectoryName(p));
                StringBuilder sb = new StringBuilder();
                sb.Append(CacheTag).Append(' ').Append(t.ImageWidth).Append(' ').Append(t.ImageHeight).Append('\n');
                foreach (OcrWordBox w in t.Words)
                    sb.Append(w.Page).Append('\t').Append(w.Line).Append('\t')
                      .Append(w.X.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(w.Y.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(w.W.ToString(CultureInfo.InvariantCulture)).Append('\t').Append(w.H.ToString(CultureInfo.InvariantCulture)).Append('\t')
                      .Append(w.Text.Replace('\t', ' ').Replace('\n', ' ')).Append('\n');
                File.WriteAllText(p, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }
    }
}
