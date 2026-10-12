using System;
using System.IO;
using System.Text;
using System.Threading;

namespace FindAnything.Core
{
    /// <summary>One file in the results list.</summary>
    public sealed class FileHit
    {
        public string Path;              // for display, copying and other programs
        public string IoPath;            // for opening here (\\?\ form, works past 260 characters)
        public string Name;
        public string Folder;
        public long Size;
        public DateTime Modified;

        public bool NameMatch;
        public int ContentHits;
        public bool HitsCapped;          // stopped counting at Scanner.MaxHits

        public long FirstOffset = -1;    // byte offset of the first content match (in Source text)
        public TextForm FirstForm;
        public long FirstLine;           // 1-based line of the first content match
        public string Preview;           // the line around the first content match

        /// <summary>Where the text came from: "" raw file bytes, or "PDF" / "DOCX" / ... extracted text.</summary>
        public string Source = "";

        public string HitsText
        {
            get { return ContentHits == 0 ? "" : (HitsCapped ? ContentHits + "+" : ContentHits.ToString()); }
        }

        public string MatchText
        {
            get
            {
                string content = ContentHits == 0 ? "" : (FirstForm == TextForm.Utf16 ? "UTF-16" : "Text");
                if (Source.Length > 0 && ContentHits > 0) content = Source;
                if (NameMatch && content.Length > 0) return "Name + " + content;
                return NameMatch ? "Name" : content;
            }
        }
    }

    /// <summary>
    /// Streams a file through fixed-size buffers looking for a pattern. Memory use is the buffer, whatever
    /// the file size: a 25 GB file costs the same 4 MB as a 4 KB one.
    /// </summary>
    public sealed class Scanner
    {
        public const int ChunkSize = 4 << 20;
        public const int MaxHits = 1000;

        private readonly BytePattern p8;
        private readonly BytePattern p16;   // null when UTF-16 search is off
        private readonly int overlap;

        public Scanner(string text, bool caseSensitive, bool wildcards, bool wholeWord, bool utf16)
        {
            p8 = BytePattern.Compile(text, caseSensitive, wildcards, wholeWord, TextForm.Bytes8);
            if (utf16) p16 = BytePattern.Compile(text, caseSensitive, wildcards, wholeWord, TextForm.Utf16);
            overlap = Math.Max(p8.MaxLength, p16 == null ? 0 : p16.MaxLength) + 2;
        }

        /// <summary>A buffer sized for this scanner. Give each thread its own.</summary>
        public byte[] NewBuffer()
        {
            return new byte[ChunkSize + overlap + 4];
        }

        public BytePattern Pattern8 { get { return p8; } }
        public BytePattern Pattern16 { get { return p16; } }

        /// <summary>
        /// Scans the stream from its current position, filling in the hit's content fields.
        /// Returns the number of bytes read. allowUtf16 = false for text we decoded ourselves (always UTF-8).
        /// </summary>
        public long Scan(Stream s, byte[] buf, FileHit hit, CancellationToken ct, bool allowUtf16)
        {
            bool use8 = true;
            bool use16 = p16 != null && allowUtf16;

            long bufStart = 0;      // file offset of buf[0]
            int dataLen = 0;
            long next8 = 0, next16 = 0;
            long linesBefore = 0;   // line breaks in [0, bufStart), counted until the first match is found
            long total = 0;
            bool first = true;

            while (true)
            {
                ct.ThrowIfCancellationRequested();

                int want = buf.Length - dataLen;
                int got = 0;
                while (got < want)
                {
                    int n = s.Read(buf, dataLen + got, want - got);
                    if (n <= 0) break;
                    got += n;
                }
                total += got;
                dataLen += got;
                bool eof = got < want;

                if (first)
                {
                    first = false;
                    if (dataLen >= 2 && buf[0] == 0xFF && buf[1] == 0xFE) { use8 = false; use16 = p16 != null; }
                    else if (dataLen >= 3 && buf[0] == 0xEF && buf[1] == 0xBB && buf[2] == 0xBF) use16 = false;
                }

                int startLimit = eof ? dataLen : dataLen - overlap;
                int firstIdx = -1, firstEnd = -1;
                TextForm firstForm = TextForm.Bytes8;

                for (int pass = 0; pass < 2; pass++)
                {
                    BytePattern p = pass == 0 ? p8 : p16;
                    if (pass == 0 ? !use8 : !use16) continue;
                    if (hit.HitsCapped) break;

                    long next = pass == 0 ? next8 : next16;
                    int from = (int)Math.Max(0, next - bufStart);
                    while (true)
                    {
                        int end;
                        int st = p.Find(buf, from, startLimit, dataLen, out end);
                        if (st < 0) break;
                        hit.ContentHits++;
                        if (hit.FirstOffset < 0 && (firstIdx < 0 || st < firstIdx))
                        {
                            firstIdx = st; firstEnd = end; firstForm = p.Form;
                        }
                        from = Math.Max(end, st + 1);
                        if (hit.ContentHits >= MaxHits) { hit.HitsCapped = true; break; }
                    }
                    next = bufStart + Math.Max(from, startLimit);
                    if (pass == 0) next8 = next; else next16 = next;
                }

                if (firstIdx >= 0)
                {
                    hit.FirstOffset = bufStart + firstIdx;
                    hit.FirstForm = firstForm;
                    hit.FirstLine = linesBefore + CountLineBreaks(buf, 0, firstIdx) + 1;
                    hit.Preview = MakePreview(buf, firstIdx, firstEnd, dataLen, firstForm);
                }

                if (eof || hit.HitsCapped) break;

                // Keep the tail: matches may start there, and whole-word checks need the byte before.
                int keep = Math.Min(overlap + 2, dataLen);
                int consumed = dataLen - keep;
                if (hit.FirstOffset < 0) linesBefore += CountLineBreaks(buf, 0, consumed);
                Buffer.BlockCopy(buf, consumed, buf, 0, keep);
                bufStart += consumed;
                dataLen = keep;
            }
            return total;
        }

        /// <summary>Counts LF bytes eight at a time (finds zero bytes in word ^ 0x0A0A...).</summary>
        public static unsafe long CountLineBreaks(byte[] buf, int start, int end)
        {
            if (end <= start) return 0;
            long n = 0;
            fixed (byte* p0 = buf)
            {
                byte* p = p0 + start;
                byte* e = p0 + end;
                while (p + 8 <= e)
                {
                    ulong x = *(ulong*)p ^ 0x0A0A0A0A0A0A0A0AUL;
                    ulong t = ((x & 0x7F7F7F7F7F7F7F7FUL) + 0x7F7F7F7F7F7F7F7FUL) | x;
                    ulong z = ~t & 0x8080808080808080UL;   // high bit set in each byte that was LF
                    if (z != 0) n += (long)(((z >> 7) * 0x0101010101010101UL) >> 56);
                    p += 8;
                }
                for (; p < e; p++) if (*p == 0x0A) n++;
            }
            return n;
        }

        /// <summary>The line around a match, decoded and cleaned for one-line display.</summary>
        public static string MakePreview(byte[] buf, int start, int end, int dataLen, TextForm form)
        {
            int unit = form == TextForm.Utf16 ? 2 : 1;
            const int context = 80;

            int a = start;
            int limitA = Math.Max(0, start - context * unit);
            while (a - unit >= limitA && buf[a - unit] != 0x0A) a -= unit;
            int b = end;
            int limitB = Math.Min(dataLen, end + context * unit);
            while (b + unit <= limitB && buf[b] != 0x0A && buf[b] != 0x0D) b += unit;

            string text = Decode(buf, a, b - a, form);
            StringBuilder sb = new StringBuilder(text.Length);
            foreach (char c in text) sb.Append(c < 0x20 || c == 0x7F ? ' ' : c);
            string result = sb.ToString().Trim();
            bool cutLeft = a - unit >= 0 && buf[a - unit] != 0x0A;
            bool cutRight = b < dataLen && buf[b] != 0x0A && buf[b] != 0x0D;
            if (cutLeft) result = "..." + result;
            if (cutRight) result = result + "...";
            return result;
        }

        private static readonly Encoding Cp1252 = Encoding.GetEncoding(1252);

        /// <summary>
        /// Decodes bytes for display. UTF-16 for the Utf16 form; otherwise UTF-8, falling back to
        /// Windows-1252 when the bytes are clearly not UTF-8.
        /// </summary>
        public static string Decode(byte[] buf, int offset, int count, TextForm form)
        {
            if (count <= 0) return "";
            if (form == TextForm.Utf16) return Encoding.Unicode.GetString(buf, offset, count & ~1);
            string utf8 = Encoding.UTF8.GetString(buf, offset, count);
            int bad = 0;
            foreach (char c in utf8) if (c == '�') bad++;
            return bad > 2 + utf8.Length / 50 ? Cp1252.GetString(buf, offset, count) : utf8;
        }
    }
}
