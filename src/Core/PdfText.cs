using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using System.Threading;

namespace FindAnything.Core
{
    /// <summary>
    /// Pulls the text out of a PDF: page content streams run through a small text interpreter that tracks
    /// fonts and positions, so words split across drawing commands come back joined and lines come back as
    /// lines. Scanned pages (pictures of text) have no text to find.
    /// </summary>
    public static class PdfText
    {
        public static string Extract(byte[] data, CancellationToken ct, out string note)
        {
            note = null;
            if (data.Length < 8) return null;
            PdfDoc doc = new PdfDoc(data);
            try
            {
                doc.Load(ct);
                if (doc.Locked) { note = doc.Note; return ""; }
                StringBuilder sb = new StringBuilder();
                PdfTextWriter w = new PdfTextWriter(doc, sb, ct);
                int pages = w.WriteAllPages();
                w.WriteInfo();
                if (pages > 0 && sb.ToString().Trim().Length == 0) note = "PDF has no text layer (scanned pages?)";
                return sb.ToString();
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                note = "PDF could not be parsed (" + ex.GetType().Name + "); searched as raw bytes";
                return null;
            }
        }
    }

    internal struct Mat
    {
        public double A, B, C, D, E, F;

        public Mat(double a, double b, double c, double d, double e, double f)
        {
            A = a; B = b; C = c; D = d; E = e; F = f;
        }

        public static readonly Mat Identity = new Mat(1, 0, 0, 1, 0, 0);

        /// <summary>this x m (apply this first, then m).</summary>
        public Mat Times(Mat m)
        {
            return new Mat(
                A * m.A + B * m.C, A * m.B + B * m.D,
                C * m.A + D * m.C, C * m.B + D * m.D,
                E * m.A + F * m.C + m.E, E * m.B + F * m.D + m.F);
        }

        public Mat Translated(double tx, double ty)
        {
            return new Mat(A, B, C, D, tx * A + ty * C + E, tx * B + ty * D + F);
        }
    }

    internal sealed class PdfFont
    {
        public bool TwoByte;
        public Dictionary<int, string> ToUnicode;
        public string[] Simple;          // code -> text for one-byte fonts
        public bool Ucs2;                // two-byte codes are Unicode (Uni*-UCS2 CMaps)
        public int FirstChar;
        public double[] Widths;
        public double MissingWidth = 500;
        public Dictionary<int, double> CidWidths;
        public double DefaultCidWidth = 1000;

        public string Text(int code)
        {
            string s;
            if (ToUnicode != null && ToUnicode.TryGetValue(code, out s)) return s;
            if (!TwoByte) return Simple != null ? Simple[code & 255] : ((char)code).ToString();
            if (Ucs2) return ((char)code).ToString();
            return "";
        }

        public double Width(int code)
        {
            if (TwoByte)
            {
                double w;
                if (CidWidths != null && CidWidths.TryGetValue(code, out w)) return w;
                return DefaultCidWidth;
            }
            int i = code - FirstChar;
            if (Widths != null && i >= 0 && i < Widths.Length && Widths[i] > 0) return Widths[i];
            return MissingWidth;
        }
    }

    internal sealed class PdfTextWriter
    {
        private readonly PdfDoc doc;
        private readonly StringBuilder sb;
        private readonly CancellationToken ct;
        private readonly Dictionary<object, PdfFont> fontCache = new Dictionary<object, PdfFont>();
        private static readonly PdfFont DefaultFont = MakeDefaultFont();

        // Text state
        private PdfFont font;
        private double size = 1, charSpace, wordSpace, hScale = 1, leading;
        private Mat tm, tlm, ctm;
        private readonly Stack<Mat> ctmStack = new Stack<Mat>();
        private bool haveLast;
        private double lastX, lastY, lastH;

        public PdfTextWriter(PdfDoc doc, StringBuilder sb, CancellationToken ct)
        {
            this.doc = doc; this.sb = sb; this.ct = ct;
        }

        public int WriteAllPages()
        {
            List<Dictionary<string, object>> pages = CollectPages();
            foreach (Dictionary<string, object> page in pages)
            {
                ct.ThrowIfCancellationRequested();
                if (sb.Length >= DocText.MaxChars) break;
                Dictionary<string, object> res = Inherited(page, "Resources");
                object contents = doc.Get(page, "Contents");
                List<object> parts = contents as List<object>;
                List<byte> all = new List<byte>();
                if (parts == null) parts = new List<object> { contents };
                foreach (object part in parts)
                {
                    PdfStream s = doc.Resolve(part) as PdfStream;
                    if (s == null) continue;
                    byte[] bytes = doc.Decode(s);
                    if (bytes == null) continue;
                    all.AddRange(bytes);
                    all.Add(10);
                }
                ctm = Mat.Identity;
                ctmStack.Clear();
                haveLast = false;
                Run(all.ToArray(), res, 0);
                WriteAnnotations(page);
                NewLine();
                sb.Append('\n');
            }
            return pages.Count;
        }

        /// <summary>Title, author, subject and keywords from the document information dictionary.</summary>
        public void WriteInfo()
        {
            int num, gen;
            Dictionary<string, object> info = doc.Info(out num, out gen);
            if (info == null) return;
            foreach (string key in new[] { "Title", "Author", "Subject", "Keywords" })
            {
                byte[] s = doc.Get(info, key) as byte[];
                if (s == null) continue;
                string t = doc.TextString(s, num, gen).Trim();
                if (t.Length > 0) sb.Append(t).Append('\n');
            }
        }

        private void WriteAnnotations(Dictionary<string, object> page)
        {
            object annotsObj;
            if (!page.TryGetValue("Annots", out annotsObj)) return;
            List<object> annots = doc.ArrayOf(annotsObj);
            if (annots == null) return;
            foreach (object a in annots)
            {
                PdfRef r = a as PdfRef;
                Dictionary<string, object> ad = doc.DictOf(a);
                if (ad == null) continue;
                foreach (string key in new[] { "Contents", "V" })
                {
                    byte[] s = doc.Get(ad, key) as byte[];
                    if (s == null) continue;
                    string t = doc.TextString(s, r != null ? r.Num : 0, r != null ? r.Gen : 0).Trim();
                    if (t.Length > 0) { NewLine(); sb.Append(t).Append('\n'); }
                }
            }
        }

        private List<Dictionary<string, object>> CollectPages()
        {
            List<Dictionary<string, object>> pages = new List<Dictionary<string, object>>();
            HashSet<object> seen = new HashSet<object>();
            Dictionary<string, object> root = doc.Root;
            if (root != null) Walk(doc.DictOf(doc.Get(root, "Pages")), pages, seen, 0);

            if (pages.Count == 0)
            {
                // No usable page tree: take every page object in object-number order.
                List<KeyValuePair<int, object>> all = new List<KeyValuePair<int, object>>(doc.AllObjects);
                all.Sort(delegate(KeyValuePair<int, object> x, KeyValuePair<int, object> y) { return x.Key.CompareTo(y.Key); });
                foreach (KeyValuePair<int, object> kv in all)
                {
                    Dictionary<string, object> dct = kv.Value as Dictionary<string, object>;
                    if (dct != null && doc.NameOf(doc.Get(dct, "Type")) == "Page") pages.Add(dct);
                }
            }
            return pages;
        }

        private void Walk(Dictionary<string, object> node, List<Dictionary<string, object>> pages, HashSet<object> seen, int depth)
        {
            if (node == null || depth > 64 || !seen.Add(node)) return;
            List<object> kids = doc.ArrayOf(doc.Get(node, "Kids"));
            if (kids != null && doc.NameOf(doc.Get(node, "Type")) != "Page")
            {
                foreach (object k in kids) Walk(doc.DictOf(k), pages, seen, depth + 1);
            }
            else if (doc.Get(node, "Contents") != null || doc.NameOf(doc.Get(node, "Type")) == "Page")
            {
                pages.Add(node);
            }
        }

        private Dictionary<string, object> Inherited(Dictionary<string, object> node, string key)
        {
            for (int i = 0; i < 64 && node != null; i++)
            {
                Dictionary<string, object> v = doc.DictOf(doc.Get(node, key));
                if (v != null) return v;
                node = doc.DictOf(doc.Get(node, "Parent"));
            }
            return null;
        }

        // ------------------------------------------------------------------ content stream interpreter

        private void Run(byte[] content, Dictionary<string, object> res, int depth)
        {
            PdfLexer lx = new PdfLexer(content, 0, content.Length);
            List<object> ops = new List<object>();
            int count = 0;
            while (true)
            {
                object t = lx.ParseValue(false, 0);
                if (t == null) break;
                if ((++count & 0xFFFF) == 0) ct.ThrowIfCancellationRequested();
                PdfOp op = t as PdfOp;
                if (op == null || PdfLexer.IsClose(t)) { if (op == null) ops.Add(t); continue; }

                switch (op.V)
                {
                    case "q": ctmStack.Push(ctm); break;
                    case "Q": if (ctmStack.Count > 0) ctm = ctmStack.Pop(); break;
                    case "cm": if (ops.Count >= 6) ctm = MatOf(ops, ops.Count - 6).Times(ctm); break;
                    case "BT": tm = tlm = Mat.Identity; break;
                    case "Tf":
                        if (ops.Count >= 2)
                        {
                            PdfName fn = ops[ops.Count - 2] as PdfName;
                            font = fn != null ? FontFor(res, fn.V) : null;
                            size = Num(ops, ops.Count - 1);
                        }
                        break;
                    case "Tc": if (ops.Count >= 1) charSpace = Num(ops, ops.Count - 1); break;
                    case "Tw": if (ops.Count >= 1) wordSpace = Num(ops, ops.Count - 1); break;
                    case "Tz": if (ops.Count >= 1) hScale = Num(ops, ops.Count - 1) / 100.0; break;
                    case "TL": if (ops.Count >= 1) leading = Num(ops, ops.Count - 1); break;
                    case "Td": if (ops.Count >= 2) MoveLine(Num(ops, ops.Count - 2), Num(ops, ops.Count - 1)); break;
                    case "TD":
                        if (ops.Count >= 2)
                        {
                            leading = -Num(ops, ops.Count - 1);
                            MoveLine(Num(ops, ops.Count - 2), Num(ops, ops.Count - 1));
                        }
                        break;
                    case "Tm": if (ops.Count >= 6) tm = tlm = MatOf(ops, ops.Count - 6); break;
                    case "T*": MoveLine(0, -leading); break;
                    case "Tj": if (ops.Count >= 1) Show(ops[ops.Count - 1] as byte[]); break;
                    case "'":
                        MoveLine(0, -leading);
                        if (ops.Count >= 1) Show(ops[ops.Count - 1] as byte[]);
                        break;
                    case "\"":
                        if (ops.Count >= 3)
                        {
                            wordSpace = Num(ops, ops.Count - 3);
                            charSpace = Num(ops, ops.Count - 2);
                            MoveLine(0, -leading);
                            Show(ops[ops.Count - 1] as byte[]);
                        }
                        break;
                    case "TJ":
                        {
                            List<object> arr = ops.Count >= 1 ? ops[ops.Count - 1] as List<object> : null;
                            if (arr == null) break;
                            foreach (object e in arr)
                            {
                                byte[] s = e as byte[];
                                if (s != null) Show(s);
                                else if (e is double) tm = tm.Translated(-(double)e / 1000.0 * size * hScale, 0);
                            }
                            break;
                        }
                    case "Do":
                        if (ops.Count >= 1 && depth < 8)
                        {
                            PdfName xn = ops[ops.Count - 1] as PdfName;
                            PdfStream xs = xn == null ? null : doc.Resolve(doc.Get(doc.DictOf(doc.Get(res, "XObject")), xn.V)) as PdfStream;
                            if (xs != null && doc.NameOf(doc.Get(xs.Dict, "Subtype")) == "Form")
                            {
                                byte[] form = doc.Decode(xs);
                                if (form != null)
                                {
                                    Dictionary<string, object> formRes = doc.DictOf(doc.Get(xs.Dict, "Resources")) ?? res;
                                    List<object> m = doc.ArrayOf(doc.Get(xs.Dict, "Matrix"));
                                    Mat saveCtm = ctm, saveTm = tm, saveTlm = tlm;
                                    PdfFont saveFont = font;
                                    double saveSize = size;
                                    if (m != null && m.Count >= 6) ctm = MatOf(m, 0).Times(ctm);
                                    Run(form, formRes, depth + 1);
                                    ctm = saveCtm; tm = saveTm; tlm = saveTlm; font = saveFont; size = saveSize;
                                }
                            }
                        }
                        break;
                    case "ID":
                        SkipInlineImage(lx);
                        break;
                }
                ops.Clear();
            }
        }

        private static void SkipInlineImage(PdfLexer lx)
        {
            // Data starts after one whitespace byte and ends at whitespace + "EI" + whitespace/end.
            int p = lx.Pos + 1;
            byte[] d = lx.D;
            while (p + 1 < lx.End)
            {
                if (d[p] == 'E' && d[p + 1] == 'I' && PdfLexer.IsWhite(d[p - 1])
                    && (p + 2 >= lx.End || PdfLexer.IsWhite(d[p + 2])))
                    break;
                p++;
            }
            lx.Pos = p;
        }

        private void MoveLine(double tx, double ty)
        {
            tlm = tlm.Translated(tx, ty);
            tm = tlm;
        }

        private void Show(byte[] s)
        {
            if (s == null) return;
            PdfFont f = font ?? DefaultFont;

            Mat trm = tm.Times(ctm);
            double h = Math.Abs(size) * Math.Sqrt(trm.C * trm.C + trm.D * trm.D);
            if (h < 0.01) h = Math.Abs(size) > 0.01 ? Math.Abs(size) : 1;

            if (haveLast)
            {
                double refH = Math.Max(h, lastH);
                if (Math.Abs(trm.F - lastY) > 0.6 * refH) NewLine();
                else
                {
                    double gap = trm.E - lastX;
                    if (gap > 0.2 * refH || gap < -3 * refH) Space();
                }
            }

            int step = f.TwoByte ? 2 : 1;
            for (int i = 0; i + step <= s.Length; i += step)
            {
                int code = step == 2 ? (s[i] << 8) | s[i + 1] : s[i];
                string text = f.Text(code);
                for (int k = 0; k < text.Length; k++)
                {
                    char c = text[k];
                    if (c == '\0') continue;
                    sb.Append(c < 0x20 && c != '\t' ? ' ' : c);
                }
                double adv = f.Width(code) / 1000.0 * size + charSpace;
                if (step == 1 && code == 32) adv += wordSpace;
                tm = tm.Translated(adv * hScale, 0);
            }

            Mat end = tm.Times(ctm);
            lastX = end.E;
            lastY = trm.F;
            lastH = h;
            haveLast = true;
        }

        private void NewLine()
        {
            while (sb.Length > 0 && sb[sb.Length - 1] == ' ') sb.Length--;
            if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
        }

        private void Space()
        {
            if (sb.Length > 0 && sb[sb.Length - 1] != ' ' && sb[sb.Length - 1] != '\n') sb.Append(' ');
        }

        private static double Num(List<object> ops, int i)
        {
            return i >= 0 && i < ops.Count && ops[i] is double ? (double)ops[i] : 0;
        }

        private Mat MatOf(List<object> ops, int i)
        {
            return new Mat(NumR(ops, i), NumR(ops, i + 1), NumR(ops, i + 2), NumR(ops, i + 3), NumR(ops, i + 4), NumR(ops, i + 5));
        }

        private double NumR(List<object> ops, int i)
        {
            return doc.NumOf(ops[i], 0);
        }

        // ------------------------------------------------------------------ fonts

        private PdfFont FontFor(Dictionary<string, object> res, string name)
        {
            Dictionary<string, object> fonts = doc.DictOf(doc.Get(res, "Font"));
            Dictionary<string, object> fd = doc.DictOf(doc.Get(fonts, name));
            if (fd == null) return null;
            PdfFont f;
            if (fontCache.TryGetValue(fd, out f)) return f;
            f = BuildFont(fd);
            fontCache[fd] = f;
            return f;
        }

        private PdfFont BuildFont(Dictionary<string, object> fd)
        {
            PdfFont f = new PdfFont();
            string subtype = doc.NameOf(doc.Get(fd, "Subtype"));
            PdfStream toUni = doc.Resolve(doc.Get(fd, "ToUnicode")) as PdfStream;
            int codeBytes = 0;
            if (toUni != null)
            {
                byte[] cmap = doc.Decode(toUni);
                if (cmap != null) f.ToUnicode = ParseCMap(cmap, out codeBytes);
            }

            if (subtype == "Type0")
            {
                f.TwoByte = codeBytes != 1;
                string enc = doc.NameOf(doc.Get(fd, "Encoding")) ?? "";
                f.Ucs2 = enc.Contains("UCS2") || enc.Contains("UTF16");
                List<object> desc = doc.ArrayOf(doc.Get(fd, "DescendantFonts"));
                Dictionary<string, object> cid = desc != null && desc.Count > 0 ? doc.DictOf(desc[0]) : null;
                if (cid != null)
                {
                    f.DefaultCidWidth = doc.NumOf(doc.Get(cid, "DW"), 1000);
                    f.CidWidths = new Dictionary<int, double>();
                    List<object> w = doc.ArrayOf(doc.Get(cid, "W"));
                    if (w != null)
                    {
                        int i = 0;
                        while (i < w.Count)
                        {
                            int first = (int)doc.NumOf(w[i], 0);
                            if (i + 1 >= w.Count) break;
                            List<object> list = doc.ArrayOf(w[i + 1]);
                            if (list != null)
                            {
                                for (int k = 0; k < list.Count; k++) f.CidWidths[first + k] = doc.NumOf(list[k], f.DefaultCidWidth);
                                i += 2;
                            }
                            else
                            {
                                if (i + 2 >= w.Count) break;
                                int last = (int)doc.NumOf(w[i + 1], 0);
                                double wd = doc.NumOf(w[i + 2], f.DefaultCidWidth);
                                for (int c = first; c <= last && c - first < 65536; c++) f.CidWidths[c] = wd;
                                i += 3;
                            }
                        }
                    }
                }
                return f;
            }

            // Simple font: one byte per glyph, base encoding plus /Differences.
            f.FirstChar = (int)doc.NumOf(doc.Get(fd, "FirstChar"), 0);
            List<object> widths = doc.ArrayOf(doc.Get(fd, "Widths"));
            if (widths != null)
            {
                f.Widths = new double[widths.Count];
                for (int i = 0; i < widths.Count; i++) f.Widths[i] = doc.NumOf(widths[i], 0);
            }
            Dictionary<string, object> descr = doc.DictOf(doc.Get(fd, "FontDescriptor"));
            double missing = doc.NumOf(doc.Get(descr, "MissingWidth"), 0);
            if (missing > 0) f.MissingWidth = missing;
            if (subtype == "Type3")
            {
                // Type 3 widths are in glyph space; scale by the font matrix to get thousandths.
                List<object> fm = doc.ArrayOf(doc.Get(fd, "FontMatrix"));
                double scale = fm != null && fm.Count >= 1 ? doc.NumOf(fm[0], 0.001) * 1000 : 1;
                if (f.Widths != null) for (int i = 0; i < f.Widths.Length; i++) f.Widths[i] *= scale;
            }

            object encObj = doc.Get(fd, "Encoding");
            string baseName = doc.NameOf(encObj);
            Dictionary<string, object> encDict = doc.DictOf(encObj);
            if (encDict != null) baseName = doc.NameOf(doc.Get(encDict, "BaseEncoding"));
            f.Simple = BaseEncoding(baseName);
            if (encDict != null)
            {
                List<object> diffs = doc.ArrayOf(doc.Get(encDict, "Differences"));
                if (diffs != null)
                {
                    int code = 0;
                    foreach (object o in diffs)
                    {
                        object v = doc.Resolve(o);
                        if (v is double) code = (int)(double)v;
                        else
                        {
                            PdfName gn = v as PdfName;
                            if (gn != null && code >= 0 && code < 256)
                            {
                                string u = GlyphNames.ToText(gn.V);
                                if (u != null) f.Simple[code] = u;
                                code++;
                            }
                        }
                    }
                }
            }
            return f;
        }

        private static PdfFont MakeDefaultFont()
        {
            PdfFont f = new PdfFont();
            f.Simple = BaseEncoding(null);
            return f;
        }

        private static string[] BaseEncoding(string name)
        {
            Encoding enc = Encoding.GetEncoding(name == "MacRomanEncoding" ? 10000 : 1252);
            string[] t = new string[256];
            byte[] one = new byte[1];
            for (int i = 0; i < 256; i++)
            {
                one[0] = (byte)i;
                t[i] = enc.GetString(one);
            }
            if (name == "StandardEncoding")
            {
                t[0x27] = "’"; t[0x60] = "‘";
            }
            return t;
        }

        /// <summary>Reads bfchar/bfrange mappings from a ToUnicode CMap. codeBytes = 1 or 2 from the codespace.</summary>
        private static Dictionary<int, string> ParseCMap(byte[] data, out int codeBytes)
        {
            Dictionary<int, string> map = new Dictionary<int, string>();
            codeBytes = 0;
            PdfLexer lx = new PdfLexer(data, 0, data.Length);
            string mode = null;
            List<object> items = new List<object>();
            while (true)
            {
                object t = lx.ParseValue(false, 0);
                if (t == null) break;
                PdfOp op = t as PdfOp;
                if (op == null) { if (mode != null) items.Add(t); continue; }
                switch (op.V)
                {
                    case "begincodespacerange": case "beginbfchar": case "beginbfrange":
                        mode = op.V; items.Clear(); break;
                    case "endcodespacerange":
                        foreach (object o in items)
                        {
                            byte[] b = o as byte[];
                            if (b != null && b.Length > 0) codeBytes = codeBytes == 0 ? b.Length : Math.Max(codeBytes, b.Length);
                        }
                        mode = null; break;
                    case "endbfchar":
                        for (int i = 0; i + 1 < items.Count; i += 2)
                        {
                            byte[] src = items[i] as byte[];
                            byte[] dst = items[i + 1] as byte[];
                            if (src != null && dst != null) map[Code(src)] = Utf16Be(dst);
                            else if (src != null && items[i + 1] is PdfName)
                            {
                                string g = GlyphNames.ToText(((PdfName)items[i + 1]).V);
                                if (g != null) map[Code(src)] = g;
                            }
                        }
                        if (codeBytes == 0 && items.Count > 0 && items[0] is byte[]) codeBytes = ((byte[])items[0]).Length;
                        mode = null; break;
                    case "endbfrange":
                        for (int i = 0; i + 2 < items.Count; i += 3)
                        {
                            byte[] lo = items[i] as byte[], hi = items[i + 1] as byte[];
                            if (lo == null || hi == null) continue;
                            int a = Code(lo), b = Code(hi);
                            if (b < a || b - a > 65535) continue;
                            byte[] dst = items[i + 2] as byte[];
                            List<object> arr = items[i + 2] as List<object>;
                            if (dst != null && dst.Length >= 2)
                            {
                                string baseText = Utf16Be(dst);
                                char[] chars = baseText.ToCharArray();
                                for (int c = a; c <= b; c++)
                                {
                                    map[c] = new string(chars);
                                    chars[chars.Length - 1]++;
                                }
                            }
                            else if (arr != null)
                            {
                                for (int c = a; c <= b && c - a < arr.Count; c++)
                                {
                                    byte[] one = arr[c - a] as byte[];
                                    if (one != null) map[c] = Utf16Be(one);
                                }
                            }
                        }
                        if (codeBytes == 0 && items.Count > 0 && items[0] is byte[]) codeBytes = ((byte[])items[0]).Length;
                        mode = null; break;
                }
            }
            return map;
        }

        private static int Code(byte[] b)
        {
            int c = 0;
            for (int i = 0; i < b.Length && i < 4; i++) c = (c << 8) | b[i];
            return c;
        }

        private static string Utf16Be(byte[] b)
        {
            if (b.Length == 1) return ((char)b[0]).ToString();
            return Encoding.BigEndianUnicode.GetString(b, 0, b.Length & ~1);
        }
    }

    /// <summary>Adobe glyph names used in /Differences arrays, mapped to text.</summary>
    internal static class GlyphNames
    {
        private static readonly Dictionary<string, string> Names = Build();

        public static string ToText(string name)
        {
            string s;
            if (Names.TryGetValue(name, out s)) return s;
            int dot = name.IndexOf('.');
            if (dot > 0) return ToText(name.Substring(0, dot));   // "a.sc", "one.oldstyle"
            if (name.StartsWith("uni") && name.Length >= 7 && (name.Length - 3) % 4 == 0)
            {
                StringBuilder sb = new StringBuilder();
                for (int i = 3; i + 4 <= name.Length; i += 4)
                {
                    int v;
                    if (!int.TryParse(name.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)) return null;
                    sb.Append((char)v);
                }
                return sb.ToString();
            }
            if (name.StartsWith("u") && name.Length >= 5 && name.Length <= 7)
            {
                int v;
                if (int.TryParse(name.Substring(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v) && v <= 0x10FFFF)
                    return char.ConvertFromUtf32(v);
            }
            if (name.Length == 1) return name;
            return null;
        }

        private static Dictionary<string, string> Build()
        {
            Dictionary<string, string> d = new Dictionary<string, string>();
            string[] digits = { "zero", "one", "two", "three", "four", "five", "six", "seven", "eight", "nine" };
            for (int i = 0; i < 10; i++) d[digits[i]] = ((char)('0' + i)).ToString();
            string pairs =
                "space: |exclam:!|quotedbl:\"|numbersign:#|dollar:$|percent:%|ampersand:&|quotesingle:'|" +
                "parenleft:(|parenright:)|asterisk:*|plus:+|comma:,|hyphen:-|minus:-|period:.|slash:/|colon::|" +
                "semicolon:;|less:<|equal:=|greater:>|question:?|at:@|bracketleft:[|backslash:\\|bracketright:]|" +
                "asciicircum:^|underscore:_|grave:`|braceleft:{|braceright:}|asciitilde:~|" +
                "quoteleft:‘|quoteright:’|quotedblleft:“|quotedblright:”|quotesinglbase:‚|" +
                "quotedblbase:„|endash:–|emdash:—|bullet:•|ellipsis:…|fi:fi|fl:fl|ff:ff|" +
                "ffi:ffi|ffl:ffl|copyright:©|registered:®|trademark:™|degree:°|section:§|" +
                "paragraph:¶|dagger:†|daggerdbl:‡|nbspace: |sfthyphen:­|periodcentered:·|" +
                "multiply:×|divide:÷|plusminus:±|cent:¢|sterling:£|yen:¥|Euro:€|" +
                "euro:€|guillemotleft:«|guillemotright:»|exclamdown:¡|questiondown:¿|" +
                "eacute:é|egrave:è|ecircumflex:ê|edieresis:ë|aacute:á|agrave:à|" +
                "acircumflex:â|adieresis:ä|atilde:ã|aring:å|ccedilla:ç|iacute:í|" +
                "igrave:ì|icircumflex:î|idieresis:ï|ntilde:ñ|oacute:ó|ograve:ò|" +
                "ocircumflex:ô|odieresis:ö|otilde:õ|oslash:ø|uacute:ú|ugrave:ù|" +
                "ucircumflex:û|udieresis:ü|yacute:ý|ydieresis:ÿ|germandbls:ß|" +
                "Eacute:É|Egrave:È|Aacute:Á|Agrave:À|Adieresis:Ä|Odieresis:Ö|" +
                "Udieresis:Ü|Ccedilla:Ç|Ntilde:Ñ|Oslash:Ø|Aring:Å|AE:Æ|ae:æ|" +
                "OE:Œ|oe:œ|dotlessi:ı|onehalf:½|onequarter:¼|threequarters:¾|" +
                "mu:µ|currency:¤|brokenbar:¦|dieresis:¨|macron:¯|acute:´|cedilla:¸|" +
                "ordfeminine:ª|ordmasculine:º|logicalnot:¬|florin:ƒ|perthousand:‰|" +
                "guilsinglleft:‹|guilsinglright:›|circumflex:ˆ|tilde:˜|tab:\t";
            foreach (string pair in pairs.Split('|'))
            {
                int colon = pair.IndexOf(':');
                if (colon > 0) d[pair.Substring(0, colon)] = pair.Substring(colon + 1);
            }
            d["bar"] = "|";   // not in the list above because '|' separates it
            return d;
        }
    }
}
