using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text;
using System.Threading;
using System.Xml;

namespace FindAnything.Core
{
    /// <summary>
    /// Pulls the readable text out of document formats whose text is not stored as plain bytes:
    /// Office Open XML (docx/xlsx/pptx), OpenDocument (odt/ods/odp) and PDF. The result is UTF-8 bytes that the
    /// normal scanner searches, and that the viewer shows.
    /// </summary>
    public static class DocText
    {
        /// <summary>Text extracted from one document is capped here (in characters).</summary>
        public const int MaxChars = 128 << 20;

        /// <summary>PDFs are read into memory to parse, so larger ones are searched as raw bytes instead.</summary>
        public const long MaxPdfBytes = 512L << 20;

        /// <summary>Short label for the format ("DOCX", "PDF"), or null when the file is searched as raw bytes.</summary>
        public static string KindOf(string path)
        {
            string ext = Path.GetExtension(path).ToLowerInvariant();
            switch (ext)
            {
                case ".docx": case ".docm": case ".dotx": case ".dotm":
                case ".xlsx": case ".xlsm": case ".xltx": case ".xltm":
                case ".pptx": case ".pptm": case ".potx": case ".ppsx":
                case ".odt": case ".ods": case ".odp": case ".odg":
                case ".pdf":
                    return ext.Substring(1).ToUpperInvariant();
            }
            return null;
        }

        /// <summary>
        /// Extracts the text as UTF-8. Returns null when the file is not really that format (for example a
        /// renamed file), so the caller can fall back to a raw search. 'note' explains a partial result.
        /// </summary>
        public static byte[] Extract(string path, CancellationToken ct, out string note)
        {
            note = null;
            string kind = KindOf(path);
            if (kind == null) return null;
            string text;
            if (kind == "PDF")
            {
                long len = new FileInfo(path).Length;
                if (len > MaxPdfBytes) { note = "PDF too large to parse; searched as raw bytes"; return null; }
                text = PdfText.Extract(File.ReadAllBytes(path), ct, out note);
            }
            else
            {
                text = ExtractZipXml(path, ct);
            }
            if (text == null) return null;
            // Documents often use no-break spaces between words; a typed space should still match them.
            text = text.Replace(' ', ' ').Replace(' ', ' ').Replace(' ', ' ');
            return Encoding.UTF8.GetBytes(text);
        }

        // ------------------------------------------------------------------ Office Open XML / OpenDocument

        private static string ExtractZipXml(string path, CancellationToken ct)
        {
            try
            {
                using (FileStream fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                using (ZipArchive zip = new ZipArchive(fs, ZipArchiveMode.Read))
                {
                    List<ZipArchiveEntry> parts = new List<ZipArchiveEntry>();
                    foreach (ZipArchiveEntry e in zip.Entries)
                        if (PartRank(e.FullName) >= 0) parts.Add(e);
                    if (parts.Count == 0) return null;

                    // Main body first, then headers/notes/comments, slides and sheets in number order.
                    parts.Sort(delegate(ZipArchiveEntry x, ZipArchiveEntry y)
                    {
                        int r = PartRank(x.FullName).CompareTo(PartRank(y.FullName));
                        if (r != 0) return r;
                        r = x.FullName.Length.CompareTo(y.FullName.Length);
                        return r != 0 ? r : string.CompareOrdinal(x.FullName, y.FullName);
                    });

                    StringBuilder sb = new StringBuilder();
                    foreach (ZipArchiveEntry e in parts)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (sb.Length >= MaxChars) break;
                        bool sheet = e.FullName.StartsWith("xl/worksheets/", StringComparison.OrdinalIgnoreCase);
                        using (Stream es = e.Open())
                            AppendXmlText(es, sb, sheet);
                        if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                    }
                    return sb.ToString();
                }
            }
            catch (InvalidDataException) { return null; }   // not a zip after all
            catch (XmlException) { return null; }
        }

        /// <summary>Which zip parts hold document text, and their reading order. -1 = skip.</summary>
        private static int PartRank(string name)
        {
            string n = name.ToLowerInvariant();
            if (!n.EndsWith(".xml")) return -1;
            if (n == "word/document.xml" || n == "content.xml") return 0;
            if (n.StartsWith("ppt/slides/slide")) return 0;
            if (n == "xl/sharedstrings.xml") return 0;
            if (n.StartsWith("xl/worksheets/sheet")) return 1;
            if (n.StartsWith("word/header") || n.StartsWith("word/footer")) return 2;
            if (n == "word/footnotes.xml" || n == "word/endnotes.xml" || n == "word/comments.xml") return 3;
            if (n.StartsWith("ppt/notesslides/") || n.StartsWith("ppt/comments/")) return 3;
            if (n.StartsWith("xl/comments")) return 3;
            if (n == "docprops/core.xml" || n == "meta.xml") return 4;
            return -1;
        }

        private static void AppendXmlText(Stream s, StringBuilder sb, bool worksheet)
        {
            XmlReaderSettings settings = new XmlReaderSettings();
            settings.DtdProcessing = DtdProcessing.Prohibit;
            settings.XmlResolver = null;
            settings.IgnoreComments = true;
            settings.IgnoreProcessingInstructions = true;

            bool sharedStringCell = false;   // worksheet <c t="s">: its <v> is an index, not text
            bool inValue = false;

            using (XmlReader r = XmlReader.Create(s, settings))
            {
                while (r.Read())
                {
                    switch (r.NodeType)
                    {
                        case XmlNodeType.Element:
                            {
                                string ln = r.LocalName;
                                if (worksheet && ln == "c") sharedStringCell = r.GetAttribute("t") == "s";
                                else if (ln == "v") inValue = true;
                                else if (ln == "tab") sb.Append('\t');
                                else if (ln == "br" || ln == "cr" || ln == "line-break") sb.Append('\n');
                                else if (ln == "s" && r.NamespaceURI.Contains("opendocument")) sb.Append(' ');
                                if (r.IsEmptyElement && ln == "v") inValue = false;
                                break;
                            }
                        case XmlNodeType.EndElement:
                            {
                                string ln = r.LocalName;
                                if (ln == "v") inValue = false;
                                // Paragraphs, rows and shared strings end a line; cells end with a tab.
                                if (ln == "p" || ln == "h" || ln == "si" || ln == "row" || ln == "tr" || ln == "table-row")
                                {
                                    if (sb.Length > 0 && sb[sb.Length - 1] != '\n') sb.Append('\n');
                                }
                                else if (ln == "c" || ln == "tc" || ln == "table-cell") sb.Append('\t');
                                else if (ln == "title" || ln == "subject" || ln == "creator" || ln == "keywords" || ln == "description")
                                    sb.Append('\n');
                                break;
                            }
                        case XmlNodeType.Text:
                        case XmlNodeType.CDATA:
                        case XmlNodeType.SignificantWhitespace:
                            if (worksheet && inValue && sharedStringCell) break;
                            sb.Append(r.Value);
                            if (sb.Length >= MaxChars) return;
                            break;
                    }
                }
            }
        }
    }
}
