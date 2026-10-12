using System;
using System.Collections.Generic;
using System.Text;

namespace FindAnything.Core
{
    internal sealed class PdfName
    {
        public readonly string V;
        public PdfName(string v) { V = v; }
        public override string ToString() { return "/" + V; }
    }

    internal sealed class PdfRef
    {
        public readonly int Num, Gen;
        public PdfRef(int num, int gen) { Num = num; Gen = gen; }
    }

    /// <summary>A bare keyword: an operator in a content stream, or obj/stream/R/[ ]/&lt;&lt; &gt;&gt; while parsing.</summary>
    internal sealed class PdfOp
    {
        public readonly string V;
        public PdfOp(string v) { V = v; }
        public override string ToString() { return V; }
    }

    internal sealed class PdfStream
    {
        public Dictionary<string, object> Dict;
        public byte[] Source;   // the bytes the stream data lives in (the file)
        public int Start, Length;
        public int Num, Gen;
    }

    /// <summary>Tokenizer and object parser for PDF syntax (ISO 32000 section 7.2-7.3).</summary>
    internal sealed class PdfLexer
    {
        public readonly byte[] D;
        public int Pos;
        public readonly int End;

        private static readonly PdfOp ArrayOpen = new PdfOp("["), ArrayClose = new PdfOp("]");
        private static readonly PdfOp DictOpen = new PdfOp("<<"), DictClose = new PdfOp(">>");
        private static readonly PdfOp BraceOpen = new PdfOp("{"), BraceClose = new PdfOp("}");

        public PdfLexer(byte[] d, int start, int end)
        {
            D = d; Pos = start; End = Math.Min(end, d.Length);
        }

        public static bool IsWhite(byte b)
        {
            return b == 32 || b == 10 || b == 13 || b == 9 || b == 0 || b == 12;
        }

        public static bool IsDelim(byte b)
        {
            return b == '(' || b == ')' || b == '<' || b == '>' || b == '[' || b == ']' || b == '{' || b == '}' || b == '/' || b == '%';
        }

        public void SkipWhite()
        {
            while (Pos < End)
            {
                byte b = D[Pos];
                if (IsWhite(b)) { Pos++; continue; }
                if (b == '%')
                {
                    while (Pos < End && D[Pos] != 10 && D[Pos] != 13) Pos++;
                    continue;
                }
                break;
            }
        }

        /// <summary>Next raw token: double, PdfName, byte[] (string), PdfOp, or null at the end.</summary>
        public object Next()
        {
            SkipWhite();
            if (Pos >= End) return null;
            byte b = D[Pos];

            if (b == '/')
            {
                Pos++;
                StringBuilder sb = new StringBuilder();
                while (Pos < End && !IsWhite(D[Pos]) && !IsDelim(D[Pos]))
                {
                    byte c = D[Pos++];
                    if (c == '#' && Pos + 1 < End && Hex(D[Pos]) >= 0 && Hex(D[Pos + 1]) >= 0)
                    {
                        c = (byte)(Hex(D[Pos]) * 16 + Hex(D[Pos + 1]));
                        Pos += 2;
                    }
                    sb.Append((char)c);
                }
                return new PdfName(sb.ToString());
            }
            if (b == '(') { Pos++; return LiteralString(); }
            if (b == '<')
            {
                if (Pos + 1 < End && D[Pos + 1] == '<') { Pos += 2; return DictOpen; }
                Pos++;
                return HexString();
            }
            if (b == '>')
            {
                Pos++;
                if (Pos < End && D[Pos] == '>') Pos++;
                return DictClose;
            }
            if (b == '[') { Pos++; return ArrayOpen; }
            if (b == ']') { Pos++; return ArrayClose; }
            if (b == '{') { Pos++; return BraceOpen; }
            if (b == '}') { Pos++; return BraceClose; }
            if (b == ')') { Pos++; return Next(); }   // stray close paren: skip it

            int start = Pos;
            while (Pos < End && !IsWhite(D[Pos]) && !IsDelim(D[Pos])) Pos++;
            if (Pos == start) { Pos++; return Next(); }

            double num;
            if (TryNumber(start, Pos, out num)) return num;
            return new PdfOp(Encoding.ASCII.GetString(D, start, Pos - start));
        }

        private bool TryNumber(int start, int end, out double value)
        {
            value = 0;
            int i = start;
            bool neg = false;
            while (i < end && (D[i] == '+' || D[i] == '-')) { if (D[i] == '-') neg = !neg; i++; }
            if (i == end) return false;
            double v = 0, scale = 0;
            bool digits = false;
            for (; i < end; i++)
            {
                byte c = D[i];
                if (c >= '0' && c <= '9')
                {
                    digits = true;
                    if (scale == 0) v = v * 10 + (c - '0');
                    else { v += (c - '0') * scale; scale /= 10; }
                }
                else if (c == '.' && scale == 0) scale = 0.1;
                else return false;
            }
            if (!digits) return false;
            value = neg ? -v : v;
            return true;
        }

        private byte[] LiteralString()
        {
            List<byte> o = new List<byte>();
            int depth = 1;
            while (Pos < End)
            {
                byte c = D[Pos++];
                if (c == '(') { depth++; o.Add(c); }
                else if (c == ')') { if (--depth == 0) break; o.Add(c); }
                else if (c == '\\' && Pos < End)
                {
                    byte e = D[Pos++];
                    switch (e)
                    {
                        case (byte)'n': o.Add(10); break;
                        case (byte)'r': o.Add(13); break;
                        case (byte)'t': o.Add(9); break;
                        case (byte)'b': o.Add(8); break;
                        case (byte)'f': o.Add(12); break;
                        case 13: if (Pos < End && D[Pos] == 10) Pos++; break;   // line continuation
                        case 10: break;
                        default:
                            if (e >= '0' && e <= '7')
                            {
                                int v = e - '0';
                                for (int k = 0; k < 2 && Pos < End && D[Pos] >= '0' && D[Pos] <= '7'; k++)
                                    v = v * 8 + (D[Pos++] - '0');
                                o.Add((byte)v);
                            }
                            else o.Add(e);
                            break;
                    }
                }
                else if (c == 13)
                {
                    // An end-of-line inside a string is read as a single LF.
                    if (Pos < End && D[Pos] == 10) Pos++;
                    o.Add(10);
                }
                else o.Add(c);
            }
            return o.ToArray();
        }

        private byte[] HexString()
        {
            List<byte> o = new List<byte>();
            int hi = -1;
            while (Pos < End)
            {
                byte c = D[Pos++];
                if (c == '>') break;
                int h = Hex(c);
                if (h < 0) continue;
                if (hi < 0) hi = h;
                else { o.Add((byte)(hi * 16 + h)); hi = -1; }
            }
            if (hi >= 0) o.Add((byte)(hi * 16));
            return o.ToArray();
        }

        public static int Hex(byte c)
        {
            if (c >= '0' && c <= '9') return c - '0';
            if (c >= 'a' && c <= 'f') return c - 'a' + 10;
            if (c >= 'A' && c <= 'F') return c - 'A' + 10;
            return -1;
        }

        /// <summary>
        /// Parses one complete value: arrays and dictionaries are built, "n g R" becomes a PdfRef when
        /// refs is true. Returns null at the end of input; true/false/null come back as bool / PdfOp("null").
        /// </summary>
        public object ParseValue(bool refs, int depth)
        {
            object t = Next();
            if (t == null) return null;
            PdfOp op = t as PdfOp;
            if (op != null)
            {
                if (depth > 64) return op;
                if (op == ArrayOpen)
                {
                    List<object> list = new List<object>();
                    while (true)
                    {
                        object v = ParseValue(refs, depth + 1);
                        if (v == null || v == ArrayClose) break;
                        if (v == DictClose) continue;   // stray ">>": ignore it
                        list.Add(v);
                    }
                    return list;
                }
                if (op == DictOpen)
                {
                    Dictionary<string, object> dict = new Dictionary<string, object>();
                    while (true)
                    {
                        object k = ParseValue(refs, depth + 1);
                        if (k == null || k == DictClose) break;
                        PdfName key = k as PdfName;
                        if (key == null) continue;
                        object v = ParseValue(refs, depth + 1);
                        if (v == null) break;
                        if (v == DictClose) { dict[key.V] = null; break; }
                        dict[key.V] = v;
                    }
                    return dict;
                }
                if (op.V == "true") return true;
                if (op.V == "false") return false;
                return op;
            }

            if (refs && t is double)
            {
                int save = Pos;
                object g = Next();
                if (g is double)
                {
                    object r = Next();
                    PdfOp rop = r as PdfOp;
                    if (rop != null && rop.V == "R") return new PdfRef((int)(double)t, (int)(double)g);
                }
                Pos = save;
            }
            return t;
        }

        public static bool IsClose(object o)
        {
            return o == ArrayClose || o == DictClose;
        }
    }
}
