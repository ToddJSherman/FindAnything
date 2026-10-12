using System;
using System.Collections.Generic;
using System.Text;

namespace FindAnything.Core
{
    /// <summary>How the searched text is stored in the file.</summary>
    public enum TextForm
    {
        /// <summary>One byte per ASCII character: UTF-8, ANSI, plain ASCII.</summary>
        Bytes8 = 0,
        /// <summary>Two bytes per character, little-endian: Windows "Unicode" text.</summary>
        Utf16 = 1
    }

    /// <summary>
    /// A search pattern compiled to bytes for one text form. Matching works on raw bytes, so a file is never
    /// decoded as a whole and binary files are searched too.
    ///
    /// Wildcards: '*' matches any run of characters on the same line (up to StarSpan bytes) and '?' matches
    /// one character. The pattern is a "contains" match: "Govee*6056" finds that anywhere in a line.
    /// Case-insensitive matching folds ASCII letters A-Z only.
    /// </summary>
    public sealed class BytePattern
    {
        /// <summary>Longest run of bytes a single '*' may cover. It never crosses a line break.</summary>
        public const int StarSpan = 1024;

        /// <summary>Longest pattern accepted, in characters.</summary>
        public const int MaxPatternChars = 1000;

        private const int Any = -1;

        private readonly int[][] segments;   // split at '*'; Any = one wildcard byte
        private readonly byte[] fold;        // 256-entry case-folding table (identity when case-sensitive)
        private readonly int unit;           // 1 or 2 bytes per character
        private readonly bool wholeWord;

        // The longest literal run in the first segment, searched with Boyer-Moore-Horspool keyed on the last
        // two bytes of the window (a byte pair rarely repeats inside the pattern, so most steps jump m-1).
        private readonly byte[] anchor;
        private readonly int anchorOffset;
        private readonly int[] shift2;
        // shift2 re-keyed by the two raw bytes as one little-endian ushort read, case folding built in.
        // 0 marks "these bytes fold to the anchor's last two": a candidate to verify.
        private readonly byte[] rawShift;

        /// <summary>The longest number of bytes one match can span.</summary>
        public readonly int MaxLength;

        public readonly TextForm Form;

        // ---- loose matching ("H6056" also finds "H-6056", "h 6056", "H6O56") ----
        private sealed class LooseClass
        {
            public bool[] Set;       // which (low) bytes match
            public int High = -1;    // UTF-16: the required high byte; -1 for single-byte text
            public bool GapBefore;   // separators may sit between this and the previous character
            public byte[][] Alts;    // multi-character look-alikes OCR produces ("1-1" for H, "rn" for m)
        }

        /// <summary>Separators a loose match skips between characters: up to LooseGap of them.</summary>
        public const int LooseGap = 2;
        private static readonly bool[] LooseSeparator = MakeSeparators();
        private readonly LooseClass[] loose;

        private static bool[] MakeSeparators()
        {
            bool[] s = new bool[256];
            foreach (char c in " -_./\\:\t") s[c] = true;
            return s;
        }

        private BytePattern(List<LooseClass> classes, TextForm form, bool wholeWord)
        {
            loose = classes.ToArray();
            Form = form;
            unit = form == TextForm.Utf16 ? 2 : 1;
            this.wholeWord = wholeWord;
            MaxLength = (loose.Length * 3 + (loose.Length - 1) * LooseGap) * unit;   // a character may be a 3-character look-alike
        }

        /// <summary>
        /// A loose pattern: case and punctuation are ignored, up to two separators (space - _ . / \ :) may sit between
        /// characters, and the look-alikes O/0 and I/l/1 count as the same. Wildcards are not used.
        /// </summary>
        public static BytePattern CompileLoose(string pattern, bool wholeWord, TextForm form)
        {
            if (pattern == null) pattern = "";
            if (pattern.Length > MaxPatternChars)
                throw new ArgumentException("The search text is longer than " + MaxPatternChars + " characters.");
            List<LooseClass> classes = new List<LooseClass>();
            foreach (char ch in pattern)
            {
                if (!char.IsLetterOrDigit(ch)) continue;
                if (ch < 0x80)
                {
                    LooseClass c = new LooseClass();
                    c.Set = new bool[256];
                    c.GapBefore = true;
                    if (form == TextForm.Utf16) c.High = 0;
                    string same = Alike(ch);
                    foreach (char a in same) c.Set[a] = true;
                    string[] alts = AltSequences(ch);
                    if (alts.Length > 0)
                    {
                        c.Alts = new byte[alts.Length][];
                        for (int k = 0; k < alts.Length; k++) c.Alts[k] = Encoding.ASCII.GetBytes(alts[k]);
                    }
                    classes.Add(c);
                }
                else if (form == TextForm.Utf16)
                {
                    LooseClass c = new LooseClass();
                    c.Set = new bool[256];
                    c.GapBefore = true;
                    c.Set[ch & 0xFF] = true;
                    c.High = ch >> 8;
                    classes.Add(c);
                }
                else
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(ch.ToString());
                    for (int i = 0; i < bytes.Length; i++)
                    {
                        LooseClass c = new LooseClass();
                        c.Set = new bool[256];
                        c.Set[bytes[i]] = true;
                        c.GapBefore = i == 0;
                        classes.Add(c);
                    }
                }
            }
            if (classes.Count == 0) throw new ArgumentException("Type some letters or digits to search for.");
            return new BytePattern(classes, form, wholeWord);
        }

        /// <summary>Compile or CompileLoose, by flag.</summary>
        public static BytePattern Make(string pattern, bool caseSensitive, bool wildcards, bool wholeWord, TextForm form, bool looseMatch)
        {
            return looseMatch ? CompileLoose(pattern, wholeWord, form) : Compile(pattern, caseSensitive, wildcards, wholeWord, form);
        }

        /// <summary>Characters treated as the same in a loose match.</summary>
        private static string Alike(char c)
        {
            char lower = char.ToLowerInvariant(c);
            switch (lower)
            {
                case '0': case 'o': return "0oO";
                case '1': case 'i': case 'l': return "1iIlL|";
                case '5': case 's': return "5sS";
                case '8': case 'b': return "8bB";
                case '2': case 'z': return "2zZ";
            }
            return lower.ToString() + char.ToUpperInvariant(lower);
        }

        /// <summary>Multi-character look-alikes that OCR produces for a character.</summary>
        private static string[] AltSequences(char c)
        {
            switch (char.ToLowerInvariant(c))
            {
                case 'h': return new[] { "1-1", "I-I", "l-l", "1-I", "I-1", "l-1", "1-l", "|-|" };
                case 'm': return new[] { "rn" };
                case 'w': return new[] { "vv" };
                case 'd': return new[] { "cl" };
            }
            return new string[0];
        }

        /// <summary>Length in bytes of class c matched at pos, or 0.</summary>
        private int MatchClass(LooseClass c, byte[] buf, int pos, int dataEnd)
        {
            if (pos + unit <= dataEnd && c.Set[buf[pos]] && (unit == 1 || buf[pos + 1] == c.High)) return unit;
            if (c.Alts == null) return 0;
            foreach (byte[] alt in c.Alts)
            {
                if (pos + alt.Length * unit > dataEnd) continue;
                bool ok = true;
                for (int i = 0; i < alt.Length && ok; i++)
                {
                    int p = pos + i * unit;
                    byte b = buf[p];
                    if (b >= 'A' && b <= 'Z') b = (byte)(b + 32);
                    byte a = alt[i];
                    if (a >= 'A' && a <= 'Z') a = (byte)(a + 32);
                    if (b != a || (unit == 2 && buf[p + 1] != 0)) ok = false;
                }
                if (ok) return alt.Length * unit;
            }
            return 0;
        }

        private int FindLoose(byte[] buf, int from, int startLimit, int dataEnd, out int end)
        {
            end = -1;
            for (int s = from; s < startLimit; s++)
            {
                int len0 = MatchClass(loose[0], buf, s, dataEnd);
                if (len0 == 0) continue;
                int pos = s + len0;
                bool ok = true;
                for (int k = 1; k < loose.Length && ok; k++)
                {
                    LooseClass c = loose[k];
                    if (c.GapBefore)
                        for (int g = 0; g < LooseGap && pos + unit <= dataEnd && LooseSeparator[buf[pos]] && (unit == 1 || buf[pos + 1] == 0); g++)
                            pos += unit;
                    int len = MatchClass(c, buf, pos, dataEnd);
                    if (len == 0) ok = false;
                    else pos += len;
                }
                if (!ok) continue;
                if (wholeWord)
                {
                    if (s - unit >= 0 && IsWordByte(buf[s - unit])) continue;
                    if (pos < dataEnd && IsWordByte(buf[pos])) continue;
                }
                end = pos;
                return s;
            }
            return -1;
        }

        private BytePattern(List<int[]> segs, bool caseSensitive, TextForm form, bool wholeWord)
        {
            segments = segs.ToArray();
            Form = form;
            unit = form == TextForm.Utf16 ? 2 : 1;
            this.wholeWord = wholeWord;

            fold = new byte[256];
            for (int i = 0; i < 256; i++) fold[i] = (byte)i;
            if (!caseSensitive)
                for (int c = 'A'; c <= 'Z'; c++) fold[c] = (byte)(c + 32);

            // Fold the literal bytes once so matching compares fold[data] against the pattern directly.
            foreach (int[] seg in segments)
                for (int i = 0; i < seg.Length; i++)
                    if (seg[i] != Any) seg[i] = fold[seg[i]];

            int total = 0;
            foreach (int[] seg in segments) total += seg.Length;
            MaxLength = total + (segments.Length - 1) * StarSpan;

            // Pick the longest wildcard-free run of the first segment as the fast-search anchor.
            int[] first = segments[0];
            int bestStart = 0, bestLen = 0, runStart = 0;
            for (int i = 0; i <= first.Length; i++)
            {
                if (i == first.Length || first[i] == Any)
                {
                    if (i - runStart > bestLen) { bestLen = i - runStart; bestStart = runStart; }
                    runStart = i + 1;
                }
            }
            if (bestLen > 0)
            {
                anchor = new byte[bestLen];
                for (int i = 0; i < bestLen; i++) anchor[i] = (byte)first[bestStart + i];
                anchorOffset = bestStart;
                if (bestLen >= 2)
                {
                    shift2 = new int[65536];
                    for (int i = 0; i < shift2.Length; i++) shift2[i] = bestLen - 1;
                    for (int j = 1; j <= bestLen - 2; j++) shift2[(anchor[j - 1] << 8) | anchor[j]] = bestLen - 1 - j;

                    int lastKey = (anchor[bestLen - 2] << 8) | anchor[bestLen - 1];
                    rawShift = new byte[65536];
                    for (int r = 0; r < 65536; r++)
                    {
                        int key = (fold[r & 0xFF] << 8) | fold[r >> 8];
                        rawShift[r] = key == lastKey ? (byte)0 : (byte)Math.Min(255, shift2[key]);
                    }
                }
            }
        }

        /// <summary>
        /// Compiles the pattern for one text form. Throws ArgumentException when the pattern is empty or has
        /// nothing in it but '*'.
        /// </summary>
        public static BytePattern Compile(string pattern, bool caseSensitive, bool wildcards, bool wholeWord, TextForm form)
        {
            if (pattern == null) pattern = "";
            if (pattern.Length > MaxPatternChars)
                throw new ArgumentException("The search text is longer than " + MaxPatternChars + " characters.");

            Encoding enc = form == TextForm.Utf16 ? (Encoding)new UnicodeEncoding(false, false) : new UTF8Encoding(false);
            int unitBytes = form == TextForm.Utf16 ? 2 : 1;

            List<int[]> segs = new List<int[]>();
            List<int> current = new List<int>();
            StringBuilder literal = new StringBuilder();

            for (int i = 0; i <= pattern.Length; i++)
            {
                char ch = i < pattern.Length ? pattern[i] : '\0';
                bool end = i == pattern.Length;
                bool star = !end && wildcards && ch == '*';
                bool question = !end && wildcards && ch == '?';

                if (end || star || question)
                {
                    if (literal.Length > 0)
                    {
                        foreach (byte b in enc.GetBytes(literal.ToString())) current.Add(b);
                        literal.Length = 0;
                    }
                    if (question)
                    {
                        for (int k = 0; k < unitBytes; k++) current.Add(Any);
                    }
                    else if (current.Count > 0)
                    {
                        // A star (or the end) closes the segment. Leading and repeated stars add nothing to a
                        // "contains" search, so empty segments are dropped.
                        segs.Add(current.ToArray());
                        current = new List<int>();
                    }
                }
                else
                {
                    literal.Append(ch);
                }
            }

            if (segs.Count == 0)
                throw new ArgumentException("Type something to search for (a pattern of only * matches nothing).");

            return new BytePattern(segs, caseSensitive, form, wholeWord);
        }

        /// <summary>
        /// Finds the first match that starts in [from, startLimit) of buf. A match may extend up to dataEnd.
        /// Returns the start index and sets end (exclusive), or returns -1.
        /// Bytes before 'from' are read only as context for whole-word checks.
        /// </summary>
        public int Find(byte[] buf, int from, int startLimit, int dataEnd, out int end)
        {
            end = -1;
            if (startLimit > dataEnd) startLimit = dataEnd;
            if (from < 0) from = 0;
            if (from >= startLimit) return -1;
            if (loose != null) return FindLoose(buf, from, startLimit, dataEnd, out end);

            if (anchor == null)
            {
                // The first segment is all '?'. Rare enough that a plain scan is fine.
                for (int s = from; s < startLimit; s++)
                    if (TryComplete(buf, s, dataEnd, out end)) return s;
                return -1;
            }

            int m = anchor.Length;
            int i = from + anchorOffset;
            int last = Math.Min(startLimit - 1 + anchorOffset, dataEnd - m);
            byte[] f = fold;

            if (m == 1)
            {
                byte only = anchor[0];
                for (; i <= last; i++)
                {
                    if (f[buf[i]] != only) continue;
                    int s = i - anchorOffset;
                    if (TryComplete(buf, s, dataEnd, out end)) return s;
                }
                return -1;
            }

            int lastKey = (anchor[m - 2] << 8) | anchor[m - 1];
            while (i <= last)
            {
                int hit = Horspool(buf, i, last, m);
                if (hit < 0) break;
                int j = m - 3;
                while (j >= 0 && f[buf[hit + j]] == anchor[j]) j--;
                if (j < 0)
                {
                    int s = hit - anchorOffset;
                    if (TryComplete(buf, s, dataEnd, out end)) return s;
                }
                i = hit + shift2[lastKey];
            }
            return -1;
        }

        /// <summary>
        /// The hot loop: returns the first window start in [i, last] whose last two bytes equal the anchor's,
        /// or -1. Pointer code so the JIT drops the bounds checks.
        /// </summary>
        private unsafe int Horspool(byte[] buf, int i, int last, int m)
        {
            fixed (byte* pb = buf)
            fixed (byte* pt = rawShift)
            {
                byte* tail = pb + m - 2;
                while (i <= last)
                {
                    int s = pt[*(ushort*)(tail + i)];
                    if (s == 0) return i;
                    i += s;
                }
            }
            return -1;
        }

        /// <summary>
        /// Finds the first match inside a short string (a preview line) and returns it in characters.
        /// Only for Bytes8 patterns; the string is matched as UTF-8.
        /// </summary>
        public bool FindInString(string s, out int charStart, out int charLength)
        {
            charStart = charLength = 0;
            if (string.IsNullOrEmpty(s)) return false;
            byte[] b = Encoding.UTF8.GetBytes(s);
            int end;
            int st = Find(b, 0, b.Length, b.Length, out end);
            if (st < 0) return false;
            charStart = Encoding.UTF8.GetCharCount(b, 0, st);
            charLength = Encoding.UTF8.GetCharCount(b, st, end - st);
            return charLength > 0 && charStart + charLength <= s.Length;
        }

        private bool TryComplete(byte[] buf, int s, int dataEnd, out int end)
        {
            end = -1;
            int[] first = segments[0];
            if (s + first.Length > dataEnd || !SegmentAt(first, buf, s)) return false;
            int pos = s + first.Length;

            for (int k = 1; k < segments.Length; k++)
            {
                int[] seg = segments[k];
                int lastStart = Math.Min(pos + StarSpan, dataEnd - seg.Length);
                bool found = false;
                for (int q = pos; q <= lastStart; q++)
                {
                    if (SegmentAt(seg, buf, q)) { pos = q + seg.Length; found = true; break; }
                    if (buf[q] == 0x0A) break;   // '*' stops at the end of the line
                }
                if (!found) return false;
            }

            if (wholeWord)
            {
                if (s - unit >= 0 && IsWordByte(buf[s - unit])) return false;
                if (pos < dataEnd && IsWordByte(buf[pos])) return false;
            }
            end = pos;
            return true;
        }

        private bool SegmentAt(int[] seg, byte[] buf, int at)
        {
            for (int i = 0; i < seg.Length; i++)
            {
                int p = seg[i];
                if (p != Any && fold[buf[at + i]] != p) return false;
            }
            return true;
        }

        private static bool IsWordByte(byte b)
        {
            return (b >= '0' && b <= '9') || (b >= 'A' && b <= 'Z') || (b >= 'a' && b <= 'z') || b == '_' || b >= 0x80;
        }
    }

    /// <summary>Matches file names with the same rules as the content search (contains, * and ?).</summary>
    public sealed class NameMatcher
    {
        private readonly BytePattern pattern;
        private readonly bool caseSensitive;

        public NameMatcher(string text, bool caseSensitive, bool wildcards, bool wholeWord, bool looseMatch = false)
        {
            this.caseSensitive = caseSensitive;
            if (looseMatch) { pattern = BytePattern.CompileLoose(text, wholeWord, TextForm.Bytes8); this.caseSensitive = true; return; }
            // Lower-case both sides here so names get full Unicode case folding, not just A-Z.
            pattern = BytePattern.Compile(caseSensitive ? text : text.ToLowerInvariant(), true, wildcards, wholeWord, TextForm.Bytes8);
        }

        public bool IsMatch(string name)
        {
            byte[] b = Encoding.UTF8.GetBytes(caseSensitive ? name : name.ToLowerInvariant());
            int end;
            return pattern.Find(b, 0, b.Length, b.Length, out end) >= 0;
        }
    }

    /// <summary>
    /// Anchored file-mask matching for the file type filter: "*.txt", "report-??.log", "*" (case-insensitive).
    /// </summary>
    public static class FileMask
    {
        public static string[] Split(string masks)
        {
            List<string> list = new List<string>();
            if (masks == null) return list.ToArray();
            foreach (string part in masks.Split(';', ',', '|'))
            {
                string m = part.Trim();
                if (m.Length == 0) continue;
                // "txt" or ".txt" means "*.txt".
                if (m.IndexOf('*') < 0 && m.IndexOf('?') < 0)
                    m = m.StartsWith(".") ? "*" + m : (m.IndexOf('.') < 0 ? "*." + m : m);
                list.Add(m);
            }
            return list.ToArray();
        }

        public static bool AnyMatch(string name, string[] masks)
        {
            foreach (string m in masks)
                if (IsMatch(name, m)) return true;
            return false;
        }

        /// <summary>Classic glob: '*' any run (including empty), '?' one character. Whole name must match.</summary>
        public static bool IsMatch(string name, string mask)
        {
            int n = 0, m = 0, starM = -1, starN = 0;
            while (n < name.Length)
            {
                if (m < mask.Length && (mask[m] == '?' || char.ToLowerInvariant(mask[m]) == char.ToLowerInvariant(name[n])))
                {
                    n++; m++;
                }
                else if (m < mask.Length && mask[m] == '*')
                {
                    starM = m++; starN = n;
                }
                else if (starM >= 0)
                {
                    m = starM + 1; n = ++starN;
                }
                else return false;
            }
            while (m < mask.Length && mask[m] == '*') m++;
            return m == mask.Length;
        }
    }
}
