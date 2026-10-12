using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace FindAnything.Core
{
    /// <summary>
    /// Reads the objects of a PDF held in memory. The file is swept for "n g obj" headers instead of trusting
    /// the cross-reference table, so damaged and incrementally-updated files load too (later definitions win).
    /// </summary>
    internal sealed class PdfDoc
    {
        public const int MaxStreamBytes = 256 << 20;

        private readonly byte[] d;
        private readonly Dictionary<int, object> objs = new Dictionary<int, object>();
        private readonly Dictionary<int, int> gens = new Dictionary<int, int>();
        private readonly List<Dictionary<string, object>> trailers = new List<Dictionary<string, object>>();
        private PdfCrypt crypt;

        public string Note;
        public bool Locked;   // encrypted with a real password: text cannot be read

        public PdfDoc(byte[] data)
        {
            d = data;
        }

        public void Load(CancellationToken ct)
        {
            int i = 0;
            int n = d.Length;
            while (i < n - 3)
            {
                int k = Array.IndexOf<byte>(d, (byte)'o', i, n - 3 - i);
                if (k < 0) break;
                if ((k & 0xFFFFF) == 0) ct.ThrowIfCancellationRequested();
                if (d[k + 1] == 'b' && d[k + 2] == 'j' && k > 0 && PdfLexer.IsWhite(d[k - 1])
                    && (k + 3 >= n || PdfLexer.IsWhite(d[k + 3]) || PdfLexer.IsDelim(d[k + 3])))
                {
                    int num, gen;
                    if (HeaderBefore(k, out num, out gen))
                    {
                        i = ReadIndirect(num, gen, k + 3);
                        continue;
                    }
                }
                i = k + 1;
            }

            // Classic trailers ("trailer << ... >>"); cross-reference streams were collected as objects.
            byte[] kw = Encoding.ASCII.GetBytes("trailer");
            for (int p = IndexOf(d, kw, 0); p >= 0; p = IndexOf(d, kw, p + kw.Length))
            {
                PdfLexer lx = new PdfLexer(d, p + kw.Length, d.Length);
                Dictionary<string, object> t = lx.ParseValue(true, 0) as Dictionary<string, object>;
                if (t != null) trailers.Add(t);
            }

            // Objects packed inside object streams (PDF 1.5+). These are never themselves encrypted strings.
            List<PdfStream> objStreams = new List<PdfStream>();
            foreach (object o in objs.Values)
            {
                PdfStream s = o as PdfStream;
                if (s != null && NameOf(Get(s.Dict, "Type")) == "XRef")
                    trailers.Add(s.Dict);
            }

            SetUpEncryption();
            if (Locked) return;

            foreach (object o in objs.Values)
            {
                PdfStream s = o as PdfStream;
                if (s != null && NameOf(Get(s.Dict, "Type")) == "ObjStm") objStreams.Add(s);
            }
            foreach (PdfStream s in objStreams)
            {
                ct.ThrowIfCancellationRequested();
                byte[] data = Decode(s);
                if (data == null) continue;
                int count = (int)NumOf(Get(s.Dict, "N"), 0);
                int first = (int)NumOf(Get(s.Dict, "First"), 0);
                PdfLexer hdr = new PdfLexer(data, 0, Math.Min(first, data.Length));
                for (int j = 0; j < count; j++)
                {
                    object a = hdr.Next(), b = hdr.Next();
                    if (!(a is double) || !(b is double)) break;
                    int num = (int)(double)a;
                    int off = first + (int)(double)b;
                    if (objs.ContainsKey(num) || off >= data.Length) continue;
                    PdfLexer lx = new PdfLexer(data, off, data.Length);
                    objs[num] = lx.ParseValue(true, 0);
                    gens[num] = 0;
                }
            }
        }

        private bool HeaderBefore(int k, out int num, out int gen)
        {
            num = gen = 0;
            int p = k - 1;
            while (p >= 0 && PdfLexer.IsWhite(d[p])) p--;
            int genEnd = p + 1;
            while (p >= 0 && d[p] >= '0' && d[p] <= '9') p--;
            int genStart = p + 1;
            if (genStart == genEnd || genEnd - genStart > 5) return false;
            if (p < 0 || !PdfLexer.IsWhite(d[p])) return false;
            while (p >= 0 && PdfLexer.IsWhite(d[p])) p--;
            int numEnd = p + 1;
            while (p >= 0 && d[p] >= '0' && d[p] <= '9') p--;
            int numStart = p + 1;
            if (numStart == numEnd || numEnd - numStart > 9) return false;
            if (p >= 0 && !PdfLexer.IsWhite(d[p]) && !PdfLexer.IsDelim(d[p])) return false;
            for (int i = numStart; i < numEnd; i++) num = num * 10 + (d[i] - '0');
            for (int i = genStart; i < genEnd; i++) gen = gen * 10 + (d[i] - '0');
            return true;
        }

        /// <summary>Parses "value [stream ... endstream]" after an obj header. Returns where to keep sweeping.</summary>
        private int ReadIndirect(int num, int gen, int pos)
        {
            PdfLexer lx = new PdfLexer(d, pos, d.Length);
            object value = lx.ParseValue(true, 0);
            Dictionary<string, object> dict = value as Dictionary<string, object>;
            if (dict != null)
            {
                lx.SkipWhite();
                if (lx.Pos + 6 <= d.Length && Matches(d, lx.Pos, "stream"))
                {
                    int start = lx.Pos + 6;
                    if (start < d.Length && d[start] == 13) start++;
                    if (start < d.Length && d[start] == 10) start++;

                    int length = -1;
                    object lenObj = dict.ContainsKey("Length") ? dict["Length"] : null;
                    PdfRef lenRef = lenObj as PdfRef;
                    if (lenRef != null && objs.ContainsKey(lenRef.Num)) lenObj = objs[lenRef.Num];
                    if (lenObj is double)
                    {
                        int len = (int)(double)lenObj;
                        if (len >= 0 && (long)start + len <= d.Length)
                        {
                            int q = start + len;
                            while (q < d.Length && PdfLexer.IsWhite(d[q])) q++;
                            if (q + 9 <= d.Length && Matches(d, q, "endstream")) length = len;
                        }
                    }
                    int after;
                    if (length < 0)
                    {
                        int es = IndexOf(d, EndStream, start);
                        if (es < 0) es = d.Length;
                        int e = es;
                        if (e > start && d[e - 1] == 10) e--;
                        if (e > start && d[e - 1] == 13) e--;
                        length = e - start;
                        after = Math.Min(d.Length, es + 9);
                    }
                    else after = start + length;

                    PdfStream s = new PdfStream();
                    s.Dict = dict; s.Source = d; s.Start = start; s.Length = length; s.Num = num; s.Gen = gen;
                    objs[num] = s;
                    gens[num] = gen;
                    return after;
                }
            }
            objs[num] = value;
            gens[num] = gen;
            return Math.Max(lx.Pos, pos);
        }

        private static readonly byte[] EndStream = Encoding.ASCII.GetBytes("endstream");

        private static bool Matches(byte[] d, int at, string s)
        {
            if (at + s.Length > d.Length) return false;
            for (int i = 0; i < s.Length; i++) if (d[at + i] != s[i]) return false;
            return true;
        }

        private static int IndexOf(byte[] d, byte[] pat, int from)
        {
            int last = d.Length - pat.Length;
            int i = from;
            while (i <= last)
            {
                int k = Array.IndexOf<byte>(d, pat[0], i, last - i + 1);
                if (k < 0) return -1;
                int j = 1;
                while (j < pat.Length && d[k + j] == pat[j]) j++;
                if (j == pat.Length) return k;
                i = k + 1;
            }
            return -1;
        }

        // ------------------------------------------------------------------ object access

        public object Resolve(object o)
        {
            for (int guard = 0; guard < 32; guard++)
            {
                PdfRef r = o as PdfRef;
                if (r == null) return o;
                object v;
                if (!objs.TryGetValue(r.Num, out v)) return null;
                o = v;
            }
            return null;
        }

        public Dictionary<string, object> DictOf(object o)
        {
            o = Resolve(o);
            PdfStream s = o as PdfStream;
            if (s != null) return s.Dict;
            return o as Dictionary<string, object>;
        }

        public object Get(Dictionary<string, object> dict, string key)
        {
            if (dict == null) return null;
            object v;
            return dict.TryGetValue(key, out v) ? Resolve(v) : null;
        }

        public List<object> ArrayOf(object o)
        {
            return Resolve(o) as List<object>;
        }

        public string NameOf(object o)
        {
            PdfName n = Resolve(o) as PdfName;
            return n == null ? null : n.V;
        }

        public double NumOf(object o, double fallback)
        {
            o = Resolve(o);
            return o is double ? (double)o : fallback;
        }

        public Dictionary<string, object> Root
        {
            get
            {
                for (int i = trailers.Count - 1; i >= 0; i--)
                {
                    Dictionary<string, object> r = DictOf(Get(trailers[i], "Root"));
                    if (r != null) return r;
                }
                foreach (object o in objs.Values)
                {
                    Dictionary<string, object> dct = o as Dictionary<string, object>;
                    if (dct != null && NameOf(Get(dct, "Type")) == "Catalog") return dct;
                }
                return null;
            }
        }

        /// <summary>The document information dictionary and its object number (for decrypting its strings).</summary>
        public Dictionary<string, object> Info(out int num, out int gen)
        {
            num = gen = 0;
            for (int i = trailers.Count - 1; i >= 0; i--)
            {
                object v;
                if (!trailers[i].TryGetValue("Info", out v)) continue;
                PdfRef r = v as PdfRef;
                if (r != null) { num = r.Num; gen = r.Gen; }
                Dictionary<string, object> info = DictOf(v);
                if (info != null) return info;
            }
            return null;
        }

        public IEnumerable<KeyValuePair<int, object>> AllObjects { get { return objs; } }

        // ------------------------------------------------------------------ strings and streams

        /// <summary>A PDF text string (UTF-16BE with BOM, UTF-8 with BOM, or PDFDocEncoding).</summary>
        public string TextString(byte[] s, int num, int gen)
        {
            if (s == null) return "";
            if (crypt != null && num > 0) s = crypt.Decrypt(s, num, gen);
            if (s.Length >= 2 && s[0] == 0xFE && s[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(s, 2, s.Length - 2);
            if (s.Length >= 3 && s[0] == 0xEF && s[1] == 0xBB && s[2] == 0xBF) return Encoding.UTF8.GetString(s, 3, s.Length - 3);
            return Encoding.GetEncoding(1252).GetString(s);
        }

        /// <summary>The stream's data with encryption and filters removed, or null for image codecs.</summary>
        public byte[] Decode(PdfStream s)
        {
            byte[] data = new byte[s.Length];
            Buffer.BlockCopy(s.Source, s.Start, data, 0, s.Length);
            if (crypt != null && s.Source == d && NameOf(Get(s.Dict, "Type")) != "XRef")
                data = crypt.Decrypt(data, s.Num, s.Gen);

            object f = Get(s.Dict, "Filter");
            List<object> filters = f as List<object>;
            if (filters == null)
            {
                filters = new List<object>();
                if (f != null) filters.Add(f);
            }
            object parms = Get(s.Dict, "DecodeParms");
            for (int i = 0; i < filters.Count && data != null; i++)
            {
                string name = NameOf(filters[i]);
                Dictionary<string, object> p = DictOf(parms is List<object> ? ((List<object>)parms).Count > i ? ((List<object>)parms)[i] : null : parms);
                switch (name)
                {
                    case "FlateDecode": case "Fl":
                        data = PdfFilters.Inflate(data);
                        data = PdfFilters.Unpredict(data, p, this);
                        break;
                    case "LZWDecode": case "LZW":
                        data = PdfFilters.Lzw(data, (int)NumOf(Get(p, "EarlyChange"), 1));
                        data = PdfFilters.Unpredict(data, p, this);
                        break;
                    case "ASCIIHexDecode": case "AHx": data = PdfFilters.AsciiHex(data); break;
                    case "ASCII85Decode": case "A85": data = PdfFilters.Ascii85(data); break;
                    case "RunLengthDecode": case "RL": data = PdfFilters.RunLength(data); break;
                    case "Crypt": break;
                    default: return null;   // DCT, JPX, JBIG2, CCITT: images, no text
                }
            }
            return data;
        }

        // ------------------------------------------------------------------ encryption

        private void SetUpEncryption()
        {
            Dictionary<string, object> enc = null;
            List<object> id = null;
            for (int i = trailers.Count - 1; i >= 0; i--)
            {
                if (enc == null) enc = DictOf(Get(trailers[i], "Encrypt"));
                if (id == null) id = ArrayOf(Get(trailers[i], "ID"));
            }
            if (enc == null) return;

            byte[] id0 = id != null && id.Count > 0 ? Resolve(id[0]) as byte[] : null;
            string why;
            crypt = PdfCrypt.Create(this, enc, id0 ?? new byte[0], out why);
            if (crypt == null)
            {
                Locked = true;
                Note = why;
            }
        }
    }

    /// <summary>The standard security handler, opened with the empty user password.</summary>
    internal sealed class PdfCrypt
    {
        private static readonly byte[] Pad =
        {
            0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
            0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A
        };

        private byte[] key;
        private bool aes;
        private bool aes256;
        private bool identity;

        public static PdfCrypt Create(PdfDoc doc, Dictionary<string, object> enc, byte[] id0, out string why)
        {
            why = null;
            if (doc.NameOf(doc.Get(enc, "Filter")) != "Standard")
            {
                why = "PDF uses a certificate or custom encryption";
                return null;
            }
            int v = (int)doc.NumOf(doc.Get(enc, "V"), 0);
            int r = (int)doc.NumOf(doc.Get(enc, "R"), 2);
            byte[] o = doc.Get(enc, "O") as byte[];
            byte[] u = doc.Get(enc, "U") as byte[];
            if (o == null || u == null) { why = "PDF encryption data is damaged"; return null; }

            PdfCrypt c = new PdfCrypt();
            if (v == 4 || v == 5)
            {
                Dictionary<string, object> cf = doc.DictOf(doc.Get(enc, "CF"));
                string stmF = doc.NameOf(doc.Get(enc, "StmF")) ?? "Identity";
                if (stmF == "Identity") c.identity = true;
                Dictionary<string, object> filter = doc.DictOf(doc.Get(cf, stmF));
                string cfm = doc.NameOf(doc.Get(filter, "CFM"));
                c.aes = cfm == "AESV2" || cfm == "AESV3";
                c.aes256 = cfm == "AESV3" || v == 5;
            }

            if (r >= 5)
            {
                c.aes = c.aes256 = true;
                byte[] ue = doc.Get(enc, "UE") as byte[];
                if (u.Length < 48 || ue == null || ue.Length < 32) { why = "PDF encryption data is damaged"; return null; }
                byte[] check = Hash2B(new byte[0], Slice(u, 32, 8), r);
                if (!Same(check, u, 32)) { why = "PDF is password-protected"; return null; }
                byte[] interKey = Hash2B(new byte[0], Slice(u, 40, 8), r);
                c.key = AesCbc(interKey, new byte[16], Slice(ue, 0, 32), false);
                return c;
            }

            int keyBytes = r == 2 ? 5 : (int)doc.NumOf(doc.Get(enc, "Length"), v == 4 ? 128 : 40) / 8;
            if (keyBytes < 5 || keyBytes > 16) keyBytes = 16;
            int p = (int)(long)doc.NumOf(doc.Get(enc, "P"), 0);
            bool encryptMetadata = !(doc.Get(enc, "EncryptMetadata") is bool) || (bool)doc.Get(enc, "EncryptMetadata");

            using (MD5 md5 = MD5.Create())
            {
                List<byte> input = new List<byte>(Pad);
                input.AddRange(Slice(o, 0, 32));
                input.Add((byte)p); input.Add((byte)(p >> 8)); input.Add((byte)(p >> 16)); input.Add((byte)(p >> 24));
                input.AddRange(id0);
                if (r >= 4 && !encryptMetadata) { input.Add(0xFF); input.Add(0xFF); input.Add(0xFF); input.Add(0xFF); }
                byte[] h = md5.ComputeHash(input.ToArray());
                if (r >= 3)
                    for (int i = 0; i < 50; i++) h = md5.ComputeHash(h, 0, keyBytes);
                c.key = Slice(h, 0, keyBytes);

                // Confirm the empty password is the user password (algorithms 4 and 5).
                bool ok;
                if (r == 2)
                {
                    ok = Same(Rc4(c.key, Pad), u, 32);
                }
                else
                {
                    List<byte> x = new List<byte>(Pad);
                    x.AddRange(id0);
                    byte[] t = Rc4(c.key, md5.ComputeHash(x.ToArray()));
                    for (int i = 1; i <= 19; i++)
                    {
                        byte[] k2 = new byte[c.key.Length];
                        for (int j = 0; j < k2.Length; j++) k2[j] = (byte)(c.key[j] ^ i);
                        t = Rc4(k2, t);
                    }
                    ok = Same(t, u, 16);
                }
                if (!ok) { why = "PDF is password-protected"; return null; }
            }
            return c;
        }

        public byte[] Decrypt(byte[] data, int num, int gen)
        {
            if (identity) return data;
            byte[] k = key;
            if (!aes256)
            {
                List<byte> input = new List<byte>(key);
                input.Add((byte)num); input.Add((byte)(num >> 8)); input.Add((byte)(num >> 16));
                input.Add((byte)gen); input.Add((byte)(gen >> 8));
                if (aes) input.AddRange(new byte[] { 0x73, 0x41, 0x6C, 0x54 });   // "sAlT"
                using (MD5 md5 = MD5.Create())
                    k = Slice(md5.ComputeHash(input.ToArray()), 0, Math.Min(key.Length + 5, 16));
            }
            if (!aes) return Rc4(k, data);
            if (data.Length < 32 || data.Length % 16 != 0) return data.Length < 16 ? new byte[0] : data;
            byte[] plain = AesCbc(k, Slice(data, 0, 16), Slice(data, 16, data.Length - 16), false);
            int pad = plain.Length > 0 ? plain[plain.Length - 1] : 0;
            if (pad >= 1 && pad <= 16 && pad <= plain.Length) plain = Slice(plain, 0, plain.Length - pad);
            return plain;
        }

        /// <summary>Algorithm 2.A/2.B: the R5/R6 password hash, with no user-key bytes (user password check).</summary>
        private static byte[] Hash2B(byte[] password, byte[] salt, int r)
        {
            byte[] input = Concat(password, salt);
            byte[] k;
            using (SHA256 sha = SHA256.Create()) k = sha.ComputeHash(input);
            if (r == 5) return k;

            byte[] e = new byte[] { 0 };
            int round = 0;
            while (round < 64 || e[e.Length - 1] > round - 32)
            {
                byte[] block = Concat(password, k);
                byte[] k1 = new byte[block.Length * 64];
                for (int i = 0; i < 64; i++) Buffer.BlockCopy(block, 0, k1, i * block.Length, block.Length);
                e = AesCbc(Slice(k, 0, 16), Slice(k, 16, 16), k1, true);
                int sum = 0;
                for (int i = 0; i < 16; i++) sum += e[i];
                switch (sum % 3)
                {
                    case 0: using (SHA256 h = SHA256.Create()) k = h.ComputeHash(e); break;
                    case 1: using (SHA384 h = SHA384.Create()) k = h.ComputeHash(e); break;
                    default: using (SHA512 h = SHA512.Create()) k = h.ComputeHash(e); break;
                }
                round++;
            }
            return Slice(k, 0, 32);
        }

        private static byte[] AesCbc(byte[] key, byte[] iv, byte[] data, bool encrypt)
        {
            using (RijndaelManaged aesAlg = new RijndaelManaged())
            {
                aesAlg.BlockSize = 128;
                aesAlg.Mode = CipherMode.CBC;
                aesAlg.Padding = PaddingMode.None;
                aesAlg.Key = key;
                aesAlg.IV = iv;
                int len = data.Length - data.Length % 16;
                using (ICryptoTransform t = encrypt ? aesAlg.CreateEncryptor() : aesAlg.CreateDecryptor())
                    return t.TransformFinalBlock(data, 0, len);
            }
        }

        private static byte[] Rc4(byte[] key, byte[] data)
        {
            byte[] s = new byte[256];
            for (int i = 0; i < 256; i++) s[i] = (byte)i;
            for (int i = 0, j = 0; i < 256; i++)
            {
                j = (j + s[i] + key[i % key.Length]) & 255;
                byte t = s[i]; s[i] = s[j]; s[j] = t;
            }
            byte[] o = new byte[data.Length];
            for (int n = 0, i = 0, j = 0; n < data.Length; n++)
            {
                i = (i + 1) & 255;
                j = (j + s[i]) & 255;
                byte t = s[i]; s[i] = s[j]; s[j] = t;
                o[n] = (byte)(data[n] ^ s[(s[i] + s[j]) & 255]);
            }
            return o;
        }

        private static byte[] Slice(byte[] a, int start, int count)
        {
            count = Math.Max(0, Math.Min(count, a.Length - start));
            byte[] o = new byte[count];
            Buffer.BlockCopy(a, start, o, 0, count);
            return o;
        }

        private static byte[] Concat(byte[] a, byte[] b)
        {
            byte[] o = new byte[a.Length + b.Length];
            Buffer.BlockCopy(a, 0, o, 0, a.Length);
            Buffer.BlockCopy(b, 0, o, a.Length, b.Length);
            return o;
        }

        private static bool Same(byte[] a, byte[] b, int count)
        {
            if (a.Length < count || b.Length < count) return false;
            for (int i = 0; i < count; i++) if (a[i] != b[i]) return false;
            return true;
        }
    }

    /// <summary>Stream filters that can carry text.</summary>
    internal static class PdfFilters
    {
        public static byte[] Inflate(byte[] data)
        {
            int skip = 0;
            if (data.Length >= 2 && (data[0] & 0x0F) == 8 && ((data[0] << 8) | data[1]) % 31 == 0) skip = 2;
            MemoryStream outStream = new MemoryStream();
            try
            {
                using (MemoryStream input = new MemoryStream(data, skip, data.Length - skip))
                using (DeflateStream z = new DeflateStream(input, CompressionMode.Decompress))
                {
                    byte[] buf = new byte[65536];
                    int n;
                    while ((n = z.Read(buf, 0, buf.Length)) > 0)
                    {
                        outStream.Write(buf, 0, n);
                        if (outStream.Length > PdfDoc.MaxStreamBytes) break;
                    }
                }
            }
            catch (InvalidDataException) { }   // keep what decoded before the damage
            return outStream.ToArray();
        }

        /// <summary>PNG and TIFF predictors (DecodeParms /Predictor).</summary>
        public static byte[] Unpredict(byte[] data, Dictionary<string, object> p, PdfDoc doc)
        {
            if (p == null || data == null) return data;
            int predictor = (int)doc.NumOf(doc.Get(p, "Predictor"), 1);
            if (predictor < 10) return data;   // TIFF predictor 2 is for images only
            int colors = (int)doc.NumOf(doc.Get(p, "Colors"), 1);
            int bpc = (int)doc.NumOf(doc.Get(p, "BitsPerComponent"), 8);
            int columns = (int)doc.NumOf(doc.Get(p, "Columns"), 1);
            int bpp = Math.Max(1, (colors * bpc + 7) / 8);
            int rowLen = (columns * colors * bpc + 7) / 8;
            if (rowLen <= 0) return data;

            MemoryStream o = new MemoryStream();
            byte[] prev = new byte[rowLen];
            byte[] row = new byte[rowLen];
            for (int pos = 0; pos + 1 + rowLen <= data.Length; pos += 1 + rowLen)
            {
                int type = data[pos];
                Buffer.BlockCopy(data, pos + 1, row, 0, rowLen);
                for (int i = 0; i < rowLen; i++)
                {
                    int left = i >= bpp ? row[i - bpp] : 0;
                    int up = prev[i];
                    int upLeft = i >= bpp ? prev[i - bpp] : 0;
                    switch (type)
                    {
                        case 1: row[i] = (byte)(row[i] + left); break;
                        case 2: row[i] = (byte)(row[i] + up); break;
                        case 3: row[i] = (byte)(row[i] + (left + up) / 2); break;
                        case 4:
                            {
                                int pa = Math.Abs(up - upLeft), pb = Math.Abs(left - upLeft), pc = Math.Abs(left + up - 2 * upLeft);
                                int pr = pa <= pb && pa <= pc ? left : (pb <= pc ? up : upLeft);
                                row[i] = (byte)(row[i] + pr);
                                break;
                            }
                    }
                }
                o.Write(row, 0, rowLen);
                byte[] t = prev; prev = row; row = t;
            }
            return o.ToArray();
        }

        public static byte[] Lzw(byte[] data, int earlyChange)
        {
            MemoryStream o = new MemoryStream();
            List<byte[]> table = new List<byte[]>();
            Action reset = delegate
            {
                table.Clear();
                for (int i = 0; i < 256; i++) table.Add(new byte[] { (byte)i });
                table.Add(null); table.Add(null);   // 256 clear, 257 end
            };
            reset();
            int bits = 9;
            long bitBuf = 0;
            int bitCount = 0;
            byte[] prev = null;
            foreach (byte b in data)
            {
                bitBuf = (bitBuf << 8) | b;
                bitCount += 8;
                while (bitCount >= bits)
                {
                    int code = (int)((bitBuf >> (bitCount - bits)) & ((1 << bits) - 1));
                    bitCount -= bits;
                    if (code == 256) { reset(); bits = 9; prev = null; continue; }
                    if (code == 257) return o.ToArray();
                    byte[] entry;
                    if (code < table.Count && table[code] != null) entry = table[code];
                    else if (prev != null && code == table.Count)
                    {
                        entry = new byte[prev.Length + 1];
                        Buffer.BlockCopy(prev, 0, entry, 0, prev.Length);
                        entry[prev.Length] = prev[0];
                    }
                    else return o.ToArray();
                    o.Write(entry, 0, entry.Length);
                    if (prev != null && table.Count < 4096)
                    {
                        byte[] add = new byte[prev.Length + 1];
                        Buffer.BlockCopy(prev, 0, add, 0, prev.Length);
                        add[prev.Length] = entry[0];
                        table.Add(add);
                    }
                    prev = entry;
                    int next = table.Count + earlyChange;
                    if (next >= 4096) bits = 12;
                    else if (next >= 2048) bits = 12;
                    else if (next >= 1024) bits = 11;
                    else if (next >= 512) bits = 10;
                    if (o.Length > PdfDoc.MaxStreamBytes) return o.ToArray();
                }
            }
            return o.ToArray();
        }

        public static byte[] AsciiHex(byte[] data)
        {
            List<byte> o = new List<byte>();
            int hi = -1;
            foreach (byte c in data)
            {
                if (c == '>') break;
                int h = PdfLexer.Hex(c);
                if (h < 0) continue;
                if (hi < 0) hi = h; else { o.Add((byte)(hi * 16 + h)); hi = -1; }
            }
            if (hi >= 0) o.Add((byte)(hi * 16));
            return o.ToArray();
        }

        public static byte[] Ascii85(byte[] data)
        {
            MemoryStream o = new MemoryStream();
            int[] group = new int[5];
            int n = 0;
            int start = 0;
            if (data.Length >= 2 && data[0] == '<' && data[1] == '~') start = 2;
            for (int i = start; i < data.Length; i++)
            {
                byte c = data[i];
                if (c == '~') break;
                if (PdfLexer.IsWhite(c)) continue;
                if (c == 'z' && n == 0) { o.Write(new byte[4], 0, 4); continue; }
                if (c < '!' || c > 'u') continue;
                group[n++] = c - '!';
                if (n == 5)
                {
                    long v = 0;
                    for (int k = 0; k < 5; k++) v = v * 85 + group[k];
                    o.WriteByte((byte)(v >> 24)); o.WriteByte((byte)(v >> 16)); o.WriteByte((byte)(v >> 8)); o.WriteByte((byte)v);
                    n = 0;
                }
            }
            if (n > 1)
            {
                for (int k = n; k < 5; k++) group[k] = 84;
                long v = 0;
                for (int k = 0; k < 5; k++) v = v * 85 + group[k];
                for (int k = 0; k < n - 1; k++) o.WriteByte((byte)(v >> (24 - 8 * k)));
            }
            return o.ToArray();
        }

        public static byte[] RunLength(byte[] data)
        {
            MemoryStream o = new MemoryStream();
            int i = 0;
            while (i < data.Length)
            {
                int len = data[i++];
                if (len == 128) break;
                if (len < 128)
                {
                    int count = Math.Min(len + 1, data.Length - i);
                    o.Write(data, i, count);
                    i += count;
                }
                else if (i < data.Length)
                {
                    byte b = data[i++];
                    for (int k = 0; k < 257 - len; k++) o.WriteByte(b);
                }
            }
            return o.ToArray();
        }
    }
}
