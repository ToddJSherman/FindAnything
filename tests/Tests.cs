using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Printing;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using FindAnything.Core;

// Test runner: tests.exe [folder of generated PDFs]   |   tests.exe huge <file> <GB>
internal static class Tests
{
    private static int passed, failed;
    private static readonly CancellationToken None = CancellationToken.None;
    private const string Phrase = "Govee H6056";

    private static int Main(string[] args)
    {
        LongPath.EnableInProcess();

        if (args.Length >= 3 && args[0] == "huge") return Huge(args[1], double.Parse(args[2]));

        string work = Path.Combine(Path.GetTempPath(), "FindAnythingTests");
        if (Directory.Exists(work)) Directory.Delete(LongPath.Add(work), true);
        Directory.CreateDirectory(work);

        PatternTests();
        BoundaryTests();
        FileMaskTests();
        OfficeTests(work);
        HandmadePdfTests(work);
        if (args.Length >= 1) GeneratedPdfTests(args[0]);
        PrintToPdfTest(work);
        JobTests(work);

        Console.WriteLine();
        Console.WriteLine("{0} passed, {1} failed", passed, failed);
        try { Directory.Delete(LongPath.Add(work), true); } catch (Exception) { }
        return failed == 0 ? 0 : 1;
    }

    private static void Check(string name, bool ok, string detail = null)
    {
        if (ok) passed++;
        else
        {
            failed++;
            Console.WriteLine("FAIL  " + name + (detail != null ? "  [" + detail + "]" : ""));
        }
    }

    private static FileHit ScanBytes(byte[] data, string pattern, bool cs = false, bool wild = true, bool word = false, bool utf16 = true)
    {
        Scanner s = new Scanner(pattern, cs, wild, word, utf16);
        FileHit h = new FileHit();
        using (MemoryStream ms = new MemoryStream(data))
            s.Scan(ms, s.NewBuffer(), h, None, true);
        return h;
    }

    private static FileHit ScanText(string text, string pattern, bool cs = false, bool wild = true, bool word = false)
    {
        return ScanBytes(Encoding.UTF8.GetBytes(text), pattern, cs, wild, word);
    }

    // ------------------------------------------------------------------ matching rules

    private static void PatternTests()
    {
        Check("literal", ScanText("xx Govee H6056 yy", Phrase).ContentHits == 1);
        Check("case-insensitive", ScanText("xx govee h6056 yy", Phrase).ContentHits == 1);
        Check("case-sensitive rejects", ScanText("xx govee h6056 yy", Phrase, true).ContentHits == 0);
        Check("case-sensitive accepts", ScanText("xx Govee H6056 yy", Phrase, true).ContentHits == 1);
        Check("star", ScanText("Govee RGBIC strip H6056", "Govee*H6056").ContentHits == 1);
        Check("star stays on its line", ScanText("Govee\nH6056", "Govee*H6056").ContentHits == 0);
        Check("star span limit", ScanText("Govee" + new string('x', 2000) + "H6056", "Govee*H6056").ContentHits == 0);
        Check("leading/trailing star", ScanText("a Govee b", "*Govee*").ContentHits == 1);
        Check("question mark", ScanText("H6056 H6066 H6x56", "H60?6").ContentHits == 2);
        Check("question first", ScanText("H6056", "?6056").ContentHits == 1);
        Check("all questions", ScanText("ab", "??").ContentHits == 1);
        Check("wildcards off: literal star", ScanText("a*b axb", "a*b", false, false).ContentHits == 1);
        Check("whole word rejects", ScanText("Govees", "Govee", false, true, true).ContentHits == 0);
        Check("whole word accepts", ScanText("(Govee), Govee.", "Govee", false, true, true).ContentHits == 2);
        Check("counts every hit", ScanText("Govee H6056 Govee H6056 govee h6056", Phrase).ContentHits == 3);
        Check("non-overlapping", ScanText("aaaa", "aa").ContentHits == 2);

        bool threw = false;
        try { new Scanner("*", false, true, false, true); } catch (ArgumentException) { threw = true; }
        Check("star-only pattern rejected", threw);

        FileHit h = ScanText("line one\nline two\nthe Govee H6056 is here\n", Phrase);
        Check("line number", h.FirstLine == 3, h.FirstLine.ToString());
        Check("preview", h.Preview == "the Govee H6056 is here", h.Preview);
        Check("offset", h.FirstOffset == 22, h.FirstOffset.ToString());

        // UTF-16: with a BOM (Notepad "Unicode"), and embedded in binary without one.
        byte[] u16 = Concat(new byte[] { 0xFF, 0xFE }, Encoding.Unicode.GetBytes("abc\r\nGovee H6056 here"));
        h = ScanBytes(u16, Phrase);
        Check("utf16 bom", h.ContentHits == 1 && h.FirstForm == TextForm.Utf16 && h.FirstLine == 2, h.ContentHits + " " + h.FirstLine);
        Check("utf16 preview", h.Preview == "Govee H6056 here", h.Preview);
        byte[] bin = Concat(new byte[] { 0, 1, 2, 3, 0x90, 0x91 }, Encoding.Unicode.GetBytes("govee h6056"), new byte[] { 0, 0, 7 });
        Check("utf16 in binary", ScanBytes(bin, Phrase).ContentHits == 1);
        Check("utf16 off", ScanBytes(bin, Phrase, false, true, false, false).ContentHits == 0);
        Check("utf16 wildcard", ScanBytes(Encoding.Unicode.GetBytes("Govee XY H6056"), "Govee*H60?6").ContentHits == 1);
        Check("utf8 bom", ScanBytes(Concat(new byte[] { 0xEF, 0xBB, 0xBF }, Encoding.UTF8.GetBytes("Govee H6056")), Phrase).ContentHits == 1);
        Check("utf8 accents", ScanText("cafÃ© Govee", "cafÃ© gov").ContentHits == 1);

        h = ScanText(Repeat("Govee H6056 ", 1500), Phrase);
        Check("hit cap", h.ContentHits == Scanner.MaxHits && h.HitsCapped, h.ContentHits.ToString());

        NameMatcher nm = new NameMatcher("govee*manual", false, true, false);
        Check("name match", nm.IsMatch("GOVEE H6056 Manual.pdf"));
        Check("name no match", !nm.IsMatch("manual govee.pdf"));
    }

    // ------------------------------------------------------------------ chunk boundaries

    private static void BoundaryTests()
    {
        // 13 MB of filler with hits planted on and around every possible buffer edge.
        int size = 13 << 20;
        byte[] data = new byte[size];
        for (int i = 0; i < size; i++) data[i] = (byte)('a' + i % 23);
        List<int> spots = new List<int>();
        Random rnd = new Random(42);
        for (int k = 1; k <= 3; k++)
            for (int d = -12; d <= 2; d += 2) spots.Add(k * Scanner.ChunkSize + d);
        for (int i = 0; i < 40; i++) spots.Add(rnd.Next(size - 20));
        spots.Sort();
        List<int> placed = new List<int>();
        int lastEnd = -1;
        byte[] p = Encoding.ASCII.GetBytes(Phrase);
        foreach (int s in spots)
        {
            if (s <= lastEnd + 1) continue;
            Buffer.BlockCopy(p, 0, data, s, p.Length);
            placed.Add(s);
            lastEnd = s + p.Length;
        }
        // Line breaks before the first hit, to check the line count across buffers.
        int firstSpot = placed[0];
        int breaks = 0;
        for (int i = 100; i < firstSpot - 100; i += 997) { data[i] = 10; breaks++; }

        FileHit h = ScanBytes(data, Phrase);
        Check("boundary hit count", h.ContentHits == placed.Count, h.ContentHits + " vs " + placed.Count);
        Check("boundary first offset", h.FirstOffset == placed[0]);
        Check("boundary line", h.FirstLine == breaks + 1, h.FirstLine + " vs " + (breaks + 1));

        // A wildcard match that straddles a buffer edge.
        byte[] w = new byte[9 << 20];
        for (int i = 0; i < w.Length; i++) w[i] = (byte)'.';
        byte[] parts = Encoding.ASCII.GetBytes("Govee" + new string('-', 900) + "H6056");
        int at = Scanner.ChunkSize - 450;
        Buffer.BlockCopy(parts, 0, w, at, parts.Length);
        h = ScanBytes(w, "Govee*H6056");
        Check("wildcard across buffer edge", h.ContentHits == 1 && h.FirstOffset == at);
    }

    private static void FileMaskTests()
    {
        string[] m = FileMask.Split("txt; .log ;*.c??");
        Check("mask split", m.Length == 3 && m[0] == "*.txt" && m[1] == "*.log");
        Check("mask case", FileMask.AnyMatch("NOTES.TXT", m));
        Check("mask anchored", !FileMask.AnyMatch("notes.txt.doc", m));
        Check("mask question", FileMask.AnyMatch("data.csv", m) && !FileMask.AnyMatch("data.cs", m));
        Check("mask star", FileMask.IsMatch("abc", "*") && FileMask.IsMatch("report-01.log", "report-??.log"));
    }

    // ------------------------------------------------------------------ Office / OpenDocument

    private static void OfficeTests(string work)
    {
        string docx = Path.Combine(work, "letter.docx");
        WriteZip(docx, "word/document.xml",
            "<w:document xmlns:w=\"http://schemas.openxmlformats.org/wordprocessingml/2006/main\"><w:body>" +
            "<w:p><w:r><w:t>Model: Gov</w:t></w:r><w:r><w:rPr><w:b/></w:rPr><w:t>ee H60</w:t></w:r><w:r><w:t>56 &amp; more</w:t></w:r></w:p>" +
            "<w:p><w:r><w:t>Second paragraph</w:t></w:r></w:p></w:body></w:document>");
        string text = ExtractText(docx);
        Check("docx split runs", text.Contains("Model: Govee H6056 & more"), text);
        Check("docx paragraphs", text.Contains("more\nSecond paragraph"), text);

        string xlsx = Path.Combine(work, "sheet.xlsx");
        WriteZip(xlsx,
            "xl/sharedStrings.xml", "<sst xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><si><t>Govee H6056</t></si></sst>",
            "xl/worksheets/sheet1.xml", "<worksheet xmlns=\"http://schemas.openxmlformats.org/spreadsheetml/2006/main\"><sheetData>" +
            "<row><c r=\"A1\" t=\"s\"><v>0</v></c><c r=\"B1\"><v>12345.5</v></c></row></sheetData></worksheet>");
        text = ExtractText(xlsx);
        Check("xlsx shared string", text.Contains(Phrase), text);
        Check("xlsx number kept, index dropped", text.Contains("12345.5") && !text.Contains("\n0\t"), text);

        string pptx = Path.Combine(work, "deck.pptx");
        WriteZip(pptx, "ppt/slides/slide1.xml",
            "<p:sld xmlns:p=\"p\" xmlns:a=\"a\"><a:p><a:r><a:t>Govee </a:t></a:r><a:r><a:t>H6056</a:t></a:r></a:p></p:sld>");
        Check("pptx", ExtractText(pptx).Contains(Phrase));

        string notZip = Path.Combine(work, "fake.docx");
        File.WriteAllText(notZip, "plain text pretending, Govee H6056");
        string note;
        Check("renamed file falls back", DocText.Extract(notZip, None, out note) == null);
    }

    // ------------------------------------------------------------------ PDF

    private static void HandmadePdfTests(string work)
    {
        // Words placed one by one with Td, no font widths (standard 14 font).
        string a = BuildPdf(false,
            "BT /F1 12 Tf 72 700 Td (Model:) Tj 45 0 Td (Govee) Tj 40 0 Td (H6056) Tj 0 -14 Td (next line) Tj ET",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica >>");
        string t = PdfToText(a);
        Check("pdf words by Td", t.Contains("Model: Govee H6056\nnext line"), t);

        // Compressed, kerned TJ that splits a word, and a wide TJ gap that is a word space.
        string b = BuildPdf(true,
            "BT /F1 12 Tf 72 700 Td [(Gov) -20 (ee) -300 (H60) 15 (56)] TJ ET",
            "<< /Type /Font /Subtype /Type1 /BaseFont /Helvetica /FirstChar 32 /LastChar 126 /Widths [" + Repeat("556 ", 95) + "] >>");
        t = PdfToText(b);
        Check("pdf TJ kerning", t.Contains("Govee H6056"), t);

        // Two-byte Identity-H font: glyph ids mapped back to text by a ToUnicode bfrange/bfchar.
        string cmap = "/CIDInit /ProcSet findresource begin 12 dict begin begincmap\n1 begincodespacerange <0000> <FFFF> endcodespacerange\n" +
                      "2 beginbfrange <0010> <0030> <0041> <0040> <0049> [<0030> <0031> <0032> <0033> <0034> <0035> <0036> <0037> <0038> <0039>] endbfrange\n" +
                      "1 beginbfchar <0003> <0020> endbfchar\nendcmap\n";
        // G=0x16 o->? Use the bfrange: code 0x10 -> 'A', so letter X -> 0x10 + (X - 'A'); digits 0x40.. -> '0'..
        string glyphs = Hex2("GOVEE") + "0003" + Hex2("H") + Digits("6056");
        string c = BuildPdf(true,
            "BT /F1 10 Tf 50 50 Td <" + glyphs + "> Tj ET",
            "<< /Type /Font /Subtype /Type0 /BaseFont /X /Encoding /Identity-H /DescendantFonts [<< /Type /Font /Subtype /CIDFontType2 /DW 600 >>] /ToUnicode 9 0 R >>",
            cmap);
        t = PdfToText(c);
        Check("pdf Type0 ToUnicode", t.Contains("GOVEE H6056"), t);
    }

    private static string Hex2(string letters)
    {
        StringBuilder sb = new StringBuilder();
        foreach (char ch in letters) sb.Append((0x10 + (ch - 'A')).ToString("X4"));
        return sb.ToString();
    }

    private static string Digits(string digits)
    {
        StringBuilder sb = new StringBuilder();
        foreach (char ch in digits) sb.Append((0x40 + (ch - '0')).ToString("X4"));
        return sb.ToString();
    }

    private static void GeneratedPdfTests(string folder)
    {
        if (!Directory.Exists(folder)) { Check("generated pdf folder exists", false, folder); return; }
        foreach (string f in Directory.GetFiles(folder, "*.pdf"))
        {
            string name = Path.GetFileName(f);
            string note;
            byte[] bytes = DocText.Extract(f, None, out note);
            string text = bytes == null ? "" : Encoding.UTF8.GetString(bytes);
            if (name.StartsWith("locked"))
            {
                Check("pdf " + name + " reports password", note != null && note.Contains("password"), note);
                continue;
            }
            bool ok = text.Contains("Model: Govee H6056 Flow Plus light bars");
            Check("pdf " + name, ok, (note ?? "") + " | " + Clip(text));
        }
    }

    /// <summary>Prints a page through Windows' own "Microsoft Print to PDF" and reads it back.</summary>
    private static void PrintToPdfTest(string work)
    {
        string pdf = Path.Combine(work, "printed.pdf");
        try
        {
            using (PrintDocument pd = new PrintDocument())
            {
                pd.PrinterSettings.PrinterName = "Microsoft Print to PDF";
                if (!pd.PrinterSettings.IsValid) { Console.WriteLine("skip  Microsoft Print to PDF not installed"); return; }
                pd.PrinterSettings.PrintToFile = true;
                pd.PrinterSettings.PrintFileName = pdf;
                pd.PrintController = new StandardPrintController();
                pd.PrintPage += delegate(object s, PrintPageEventArgs e)
                {
                    using (Font f = new Font("Segoe UI", 12))
                    {
                        e.Graphics.DrawString("Order notes", f, Brushes.Black, 100, 100);
                        e.Graphics.DrawString("Model: Govee H6056 Flow Plus light bars", f, Brushes.Black, 100, 130);
                    }
                };
                pd.Print();
            }
        }
        catch (Exception ex) { Console.WriteLine("skip  print to PDF failed: " + ex.Message); return; }
        for (int i = 0; i < 50 && (!File.Exists(pdf) || new FileInfo(pdf).Length == 0); i++) Thread.Sleep(100);
        string note;
        byte[] bytes = DocText.Extract(pdf, None, out note);
        string text = bytes == null ? "" : Encoding.UTF8.GetString(bytes);
        Check("Microsoft Print to PDF", text.Contains("Model: Govee H6056 Flow Plus light bars"), (note ?? "") + " | " + Clip(text));
    }

    // ------------------------------------------------------------------ whole search job

    private static void JobTests(string work)
    {
        string root = Path.Combine(work, "tree");
        Directory.CreateDirectory(Path.Combine(root, "sub"));
        Directory.CreateDirectory(Path.Combine(root, "skipme"));
        File.WriteAllText(Path.Combine(root, "a.txt"), "the Govee H6056 lamp");
        File.WriteAllBytes(Path.Combine(root, "b.doc"), Concat(new byte[] { 0xD0, 0xCF, 0x11, 0xE0 }, Encoding.Unicode.GetBytes("Govee H6056"), new byte[8]));
        File.WriteAllText(Path.Combine(root, "c.log"), "nothing to see");
        File.WriteAllText(Path.Combine(root, "sub", "d.TXT"), "govee h6056 lower case");
        File.WriteAllText(Path.Combine(root, "Govee H6056 manual.bin"), "no phrase inside");
        File.WriteAllText(Path.Combine(root, "skipme", "e.txt"), "Govee H6056");
        File.WriteAllText(Path.Combine(root, "big.txt"), new string('x', 2 << 20) + "Govee H6056");
        File.Copy(Path.Combine(work, "letter.docx"), Path.Combine(root, "letter.docx"));

        // A file deeper than the old 260-character path limit.
        string deep = root;
        for (int i = 0; i < 8; i++) deep = Path.Combine(deep, "a-folder-name-that-is-forty-characters-x" + i);
        Directory.CreateDirectory(LongPath.Add(deep));
        string deepFile = Path.Combine(deep, "deep.txt");
        File.WriteAllText(LongPath.Add(deepFile), "deep Govee H6056");

        SearchSettings s = Settings(root);
        s.SkipFolders = "skipme";
        List<FileHit> hits = RunJob(s);
        Check("job: all types", Names(hits) == "a.txt b.doc big.txt d.TXT deep.txt letter.docx", Names(hits));
        Check("job: long path found", deepFile.Length > 260 && Names(hits).Contains("deep.txt"), deepFile.Length.ToString());

        s = Settings(root);
        s.SkipFolders = "skipme";
        s.IncludeMasks = "*.txt";
        hits = RunJob(s);
        Check("job: TXT only, DOC not counted", Names(hits) == "a.txt big.txt d.TXT deep.txt", Names(hits));

        s.MaxFileBytes = 1 << 20;
        hits = RunJob(s);
        Check("job: size limit skips big file", Names(hits) == "a.txt d.TXT deep.txt", Names(hits));

        s = Settings(root);
        s.SkipFolders = "skipme";
        s.CaseSensitive = true;
        s.IncludeMasks = "txt";
        hits = RunJob(s);
        Check("job: case-sensitive", Names(hits) == "a.txt big.txt deep.txt", Names(hits));

        s = Settings(root);
        s.SearchContents = false;
        s.SearchNames = true;
        hits = RunJob(s);
        Check("job: names only", Names(hits) == "Govee H6056 manual.bin", Names(hits));

        s = Settings(root);
        s.SkipFolders = "";
        s.SearchNames = true;
        hits = RunJob(s);
        Check("job: names + contents, nothing skipped", hits.Count == 8, Names(hits));
        foreach (FileHit h in hits)
            if (h.Name == "letter.docx") Check("job: docx source", h.Source == "DOCX" && h.MatchText == "DOCX", h.MatchText);
    }

    private static SearchSettings Settings(string root)
    {
        SearchSettings s = new SearchSettings();
        s.Roots.Add(root);
        s.Text = Phrase;
        return s;
    }

    private static List<FileHit> RunJob(SearchSettings s)
    {
        SearchJob job = new SearchJob(s);
        job.Run(None);
        List<FileHit> list = new List<FileHit>(job.Hits);
        list.Sort(delegate(FileHit a, FileHit b) { return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase); });
        return list;
    }

    private static string Names(List<FileHit> hits)
    {
        List<string> n = new List<string>();
        foreach (FileHit h in hits) n.Add(h.Name);
        return string.Join(" ", n.ToArray());
    }

    // ------------------------------------------------------------------ huge file

    /// <summary>Creates a sparse file of the given size with the phrase near the end, then times a scan.</summary>
    private static int Huge(string path, double gb)
    {
        long size = (long)(gb * 1024 * 1024 * 1024);
        if (!File.Exists(path) || new FileInfo(path).Length != size)
        {
            using (FileStream fs = new FileStream(path, FileMode.Create, FileAccess.Write)) { }
            Process.Start(new ProcessStartInfo("fsutil", "sparse setflag \"" + path + "\"") { UseShellExecute = false, CreateNoWindow = true }).WaitForExit();
            using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Write))
            {
                fs.SetLength(size);
                fs.Position = size - 1000;
                byte[] p = Encoding.ASCII.GetBytes("\nGovee H6056 near the end\n");
                fs.Write(p, 0, p.Length);
            }
        }
        Process me = Process.GetCurrentProcess();
        Stopwatch sw = Stopwatch.StartNew();
        Scanner s = new Scanner(Phrase, false, true, false, true);
        FileHit h = new FileHit();
        using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 4096, FileOptions.SequentialScan))
            s.Scan(fs, s.NewBuffer(), h, None, true);
        sw.Stop();
        me.Refresh();
        Console.WriteLine("size {0:N0} bytes, hits {1}, offset {2:N0}, {3:N1} s, {4:N0} MB/s, peak working set {5:N0} MB",
            size, h.ContentHits, h.FirstOffset, sw.Elapsed.TotalSeconds, size / 1048576.0 / sw.Elapsed.TotalSeconds,
            me.PeakWorkingSet64 / 1048576.0);
        return h.ContentHits == 1 ? 0 : 1;
    }

    // ------------------------------------------------------------------ helpers

    private static string ExtractText(string path)
    {
        string note;
        byte[] b = DocText.Extract(path, None, out note);
        return b == null ? "" : Encoding.UTF8.GetString(b);
    }

    private static string PdfToText(string path)
    {
        string note;
        return PdfText.Extract(File.ReadAllBytes(path), None, out note) ?? ("null: " + note);
    }

    private static int pdfCount;

    /// <summary>A one-page PDF with font F1. An optional third object body becomes object 9 (a stream).</summary>
    private static string BuildPdf(bool compress, string content, string fontDict, string extraStream = null)
    {
        string path = Path.Combine(Path.GetTempPath(), "FindAnythingTests", "hand" + (++pdfCount) + ".pdf");
        MemoryStream o = new MemoryStream();
        Action<string> w = delegate(string s) { byte[] b = Encoding.ASCII.GetBytes(s); o.Write(b, 0, b.Length); };
        w("%PDF-1.4\n%Ã¢Ã£\n");
        w("1 0 obj << /Type /Catalog /Pages 2 0 R >> endobj\n");
        w("2 0 obj << /Type /Pages /Kids [3 0 R] /Count 1 >> endobj\n");
        w("3 0 obj << /Type /Page /Parent 2 0 R /MediaBox [0 0 612 792] /Resources << /Font << /F1 5 0 R >> >> /Contents 4 0 R >> endobj\n");
        byte[] body = Encoding.ASCII.GetBytes(content);
        if (compress) body = Zlib(body);
        w("4 0 obj << /Length " + body.Length + (compress ? " /Filter /FlateDecode" : "") + " >> stream\n");
        o.Write(body, 0, body.Length);
        w("\nendstream endobj\n");
        w("5 0 obj " + fontDict + " endobj\n");
        if (extraStream != null)
        {
            byte[] eb = Zlib(Encoding.ASCII.GetBytes(extraStream));
            w("9 0 obj << /Length 10 0 R /Filter /FlateDecode >> stream\r\n");
            o.Write(eb, 0, eb.Length);
            w("\r\nendstream\nendobj\n10 0 obj " + eb.Length + " endobj\n");
        }
        w("trailer << /Root 1 0 R >>\n%%EOF\n");
        File.WriteAllBytes(path, o.ToArray());
        return path;
    }

    private static byte[] Zlib(byte[] data)
    {
        MemoryStream o = new MemoryStream();
        o.WriteByte(0x78); o.WriteByte(0x9C);
        using (DeflateStream z = new DeflateStream(o, CompressionMode.Compress, true)) z.Write(data, 0, data.Length);
        return o.ToArray();   // Adler-32 trailer left off; readers (and ours) tolerate that
    }

    private static void WriteZip(string path, params string[] nameAndXml)
    {
        using (FileStream fs = new FileStream(path, FileMode.Create))
        using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Create))
        {
            for (int i = 0; i + 1 < nameAndXml.Length; i += 2)
            {
                ZipArchiveEntry e = zip.CreateEntry(nameAndXml[i]);
                using (StreamWriter sw = new StreamWriter(e.Open(), new UTF8Encoding(false)))
                    sw.Write("<?xml version=\"1.0\" encoding=\"UTF-8\" standalone=\"yes\"?>" + nameAndXml[i + 1]);
            }
        }
    }

    private static byte[] Concat(params byte[][] parts)
    {
        MemoryStream o = new MemoryStream();
        foreach (byte[] p in parts) o.Write(p, 0, p.Length);
        return o.ToArray();
    }

    private static string Repeat(string s, int n)
    {
        StringBuilder sb = new StringBuilder();
        for (int i = 0; i < n; i++) sb.Append(s);
        return sb.ToString();
    }

    private static string Clip(string s)
    {
        s = s.Replace("\n", "\\n");
        return s.Length > 300 ? s.Substring(0, 300) + "..." : s;
    }
}
