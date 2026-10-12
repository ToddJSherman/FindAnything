using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using FindAnything.Core;
using FindAnything.Ui;

namespace FindAnything.App
{
    internal sealed class MainForm : Form
    {
        public string StartFolder;

        private readonly AppSettings settings = AppSettings.Load();

        // hero
        private readonly Backdrop hero = new Backdrop();
        private readonly SkinField findField = new SkinField(FieldStyle.Find, Theme.GSearch);
        private readonly SkinButton searchButton = new SkinButton("Search", Theme.GSearch, ButtonKind.Primary);
        private readonly SkinField lookField = new SkinField(FieldStyle.Normal, Theme.GFolder);
        private readonly SkinButton browseButton = new SkinButton("Browse", Theme.GOpenFolder, ButtonKind.Secondary);
        private readonly SkinButton aboutButton = new SkinButton("About", Theme.GInfo, ButtonKind.Ghost);
        private readonly Chip matchCase = new Chip("Match case", "Aa", false);
        private readonly Chip wholeWord = new Chip("Whole word", "ab", false);
        private readonly Chip wildcards = new Chip("Wildcards", "*?", false);
        private readonly Chip inContents = new Chip("Inside files", Theme.GPage, true);
        private readonly Chip inNames = new Chip("File names", Theme.GRename, true);
        private readonly Chip insideDocs = new Chip("PDF & Office", Theme.GPdf, true);
        private readonly Chip utf16 = new Chip("Unicode", Theme.GFont, true);
        private readonly Chip filtersChip = new Chip("Filters", Theme.GFilter, true);
        private readonly Chip loose = new Chip("Loose", "\u2248", false);
        private readonly Chip ocr = new Chip("Pictures (OCR)", "\uE91B", true);
        private readonly Chip ocrCache = new Chip("Remember OCR text", "\uE74E", true);
        private readonly Chip onlyPics = new Chip("Only pictures", "\uE71C", true);
        private readonly Chip kProduct = new Chip("Product keys", "\uE192", true);
        private readonly Chip kLabeled = new Chip("Licenses & serials", "\uE8EC", true);
        private readonly Chip kPasswords = new Chip("Passwords & PINs", "\uE72E", true);
        private readonly Chip kTokens = new Chip("Tokens & private keys", "\uE943", true);
        private readonly Chip kFinancial = new Chip("Cards & SSNs", "\uE8C7", true);
        private readonly SkinButton pickSamples = new SkinButton("Pick samples", "", ButtonKind.Secondary);
        private readonly Chip clipCache = new Chip("Remember fingerprints", "\uE74E", true);
        private readonly Chip weaker = new Chip("Show weaker matches", "\uE890", true);
        private readonly Slider cpuSlider = new Slider();
        private Rectangle cpuLabelRect, cpuValueRect;
        private readonly Tabs modeTabs = new Tabs(new[] { "Search text", "Find keys", "Similar pictures", "Drive check" }, new[] { Theme.GSearch, "\uE192", "\uE8B9", "\uEDA2" });
        private int mode;                       // 0 search text, 1 find keys, 2 drive check, 3 similar pictures
        private PictureSearch picSearch;
        private Task picTask;
        private Clip clip;                      // loaded on the first similar-pictures search, kept for the next
        public int StartMode;
        public string StartSample;              // --similar <picture>: search for pictures like it at once
        private string lookTextFiles = "";
        private List<DiskInfo> disks;
        private DiskInfo selectedDisk;
        private DriveScan driveScan;
        private int driveHitsShown;
        private readonly SkinField types = new SkinField(FieldStyle.Mini, null);
        private readonly SkinField excludeTypes = new SkinField(FieldStyle.Mini, null);
        private readonly SkinField skipFolders = new SkinField(FieldStyle.Mini, null);
        private readonly SkinField maxMb = new SkinField(FieldStyle.Mini, null);
        private readonly SkinField threads = new SkinField(FieldStyle.Mini, null);
        private readonly List<int> separatorsX = new List<int>();
        private readonly List<int> separatorsY = new List<int>();
        private readonly List<KeyValuePair<string, Rectangle>> miniLabels = new List<KeyValuePair<string, Rectangle>>();
        private Image appIcon;

        private readonly ResultsView results = new ResultsView();
        private readonly StatusBar status = new StatusBar();
        private readonly ToolTip tips = new ToolTip();
        private readonly System.Windows.Forms.Timer tick = new System.Windows.Forms.Timer();

        private readonly List<FileHit> hits;
        private readonly List<string> problems = new List<string>();
        private SearchJob job;
        private SearchSettings jobSettings;
        private BytePattern previewPattern;
        private CancellationTokenSource cts;
        private Task jobTask;
        private Stopwatch clock = new Stopwatch();
        private bool sortDirty;

        private static readonly string[] TypePresets =
        {
            "",
            "*.txt",
            "*.txt;*.log;*.csv;*.md;*.ini;*.cfg;*.json;*.xml;*.yaml;*.yml",
            "*.pdf;*.docx;*.doc;*.xlsx;*.xls;*.pptx;*.rtf;*.odt",
            "*.cs;*.ps1;*.py;*.js;*.ts;*.html;*.css;*.c;*.cpp;*.h;*.java;*.sql;*.bat;*.cmd",
            "*.eml;*.msg;*.mbox",
            "*.jpg;*.jpeg;*.jfif;*.png;*.bmp;*.gif;*.tif;*.tiff;*.heic;*.heif;*.webp",
        };

        private static readonly string[] TypePresetNames =
        {
            "All files", "Text files", "Text, logs and config", "Documents (PDF, Office)", "Source code", "Email", "Pictures",
        };

        private const string AllDrivesItem = "(all drives)";

        public MainForm()
        {
            hits = results.Items;
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            Text = AppInfo.Name;
            Font = Theme.Ui(14);
            BackColor = Theme.Bg;
            ForeColor = Theme.Text;
            ClientSize = new Size(Theme.S(1180), Theme.S(760));
            MinimumSize = new Size(Theme.S(880), Theme.S(560));
            StartPosition = FormStartPosition.CenterScreen;
            KeyPreview = true;
            AllowDrop = true;
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }
            appIcon = Skin.AppIcon();

            BuildHero();
            results.Dock = DockStyle.Fill;
            results.FindSpan = FindSpan;
            results.ItemActivate += delegate { OpenViewer(); };
            results.SortRequested += OnSortRequested;
            results.KeyDown += OnResultsKeyDown;
            results.ContextMenuStrip = BuildMenu();
            status.Dock = DockStyle.Bottom;
            status.NotesClicked += delegate { ShowProblems(); };
            Controls.Add(results);
            Controls.Add(hero);
            Controls.Add(status);

            tips.OwnerDraw = true;
            tips.Draw += DrawTip;
            tips.Popup += delegate(object s, PopupEventArgs e)
            {
                Size sz = TextRenderer.MeasureText(tips.GetToolTip(e.AssociatedControl), Theme.Ui(13), new Size(Theme.S(420), 0), TextFormatFlags.WordBreak);
                e.ToolTipSize = new Size(sz.Width + Theme.S(20), sz.Height + Theme.S(14));
            };
            Tip(findField, "Text to find.  *  = any characters on the same line,  ?  = any one character.");
            Tip(lookField, "A folder or drive. Several at once: separate them with ;   (for example  C:\\;D:\\)");
            Tip(matchCase, "Upper and lower case must match exactly.");
            Tip(wholeWord, "\"Govee\" does not match inside \"Govees\".");
            Tip(wildcards, "Off: * and ? are searched for as plain characters.");
            Tip(inContents, "Search the text inside each file.");
            Tip(inNames, "Also list files whose name matches.");
            Tip(insideDocs, "Read the real text out of PDF, Word, Excel, PowerPoint and OpenDocument files.");
            Tip(utf16, "Also find text stored two bytes per character: Notepad \"Unicode\" files, old .doc files, programs.");
            Tip(types, "Only these file types, separated by ;   Empty = every file.  *.txt finds TXT files only.");
            Tip(excludeTypes, "Never search these types, for example  *.exe;*.dll;*.iso");
            Tip(skipFolders, "Folder names to skip, separated by ;   Wildcards work:  node_modules;.git;*cache*");
            Tip(maxMb, "Skip files bigger than this many MB. 0 = no limit. Big files never fill memory either way.");
            Tip(threads, "Files searched at the same time. 4-8 suits SSDs; use 1-2 for spinning drives.");

            tick.Interval = 120;
            tick.Tick += OnTick;
            DragEnter += delegate(object s, DragEventArgs e) { if (e.Data.GetDataPresent(DataFormats.FileDrop)) e.Effect = DragDropEffects.Copy; };
            DragDrop += OnDragDrop;
            ResumeLayout(false);
            LoadSettings();
            LayoutHero();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this, Color.FromArgb(17, 26, 36));
        }

        // ------------------------------------------------------------------ hero layout

        private void BuildHero()
        {
            hero.Hero = true;
            hero.BottomLine = Theme.Line;
            hero.Dock = DockStyle.Top;
            hero.Paint += PaintHero;
            hero.Resize += delegate { LayoutHero(); };

            findField.KeyHint = "Enter";
            findField.SetPlaceholder("What are you looking for?");
            findField.Box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; StartSearch(); }
            };
            searchButton.Click += delegate { if (Busy) StopSearch(); else StartSearch(); };

            lookField.DropDown = true;
            lookField.SetPlaceholder("Folder or drive to search");
            lookField.DropDownClicked += delegate { if (mode == 2) ShowDiskMenu(); else ShowLookInMenu(); };
            lookField.Box.KeyDown += delegate(object s, KeyEventArgs e)
            {
                if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; StartSearch(); }
            };
            lookField.Box.MouseDown += delegate { if (mode == 2) ShowDiskMenu(); };
            browseButton.Click += delegate { OnBrowse(); };
            aboutButton.FontPx = 13;
            aboutButton.Click += delegate { ShowAbout(); };
            modeTabs.SelectedChanged += delegate { SetMode(TabToMode(modeTabs.SelectedIndex)); };
            pickSamples.Picture = Skin.Art("grandma-96");
            pickSamples.Accent = true;
            pickSamples.FontPx = 15;
            pickSamples.Click += delegate { PickSamples(); };
            findField.Box.TextChanged += delegate { if (mode == 3) UpdateSampleButton(); };
            Tip(pickSamples, "Choose one or more pictures to match. Several pictures of the same thing (a pet, a person, a place) find it more reliably.\nYou can also drop pictures on the window.");
            Tip(clipCache, "Remember each picture's fingerprint (512 numbers describing it), so later searches of the same folders are much faster.\n" +
                           "The catch: those fingerprints are stored on this PC, in %LOCALAPPDATA%\\FindAnything\\pictures.\n" +
                           "Off: pictures are read again every search and nothing is kept. Turning this off deletes what was stored.");
            Tip(weaker, "Off: only pictures that really look like your sample (or match the description) are listed.\n" +
                        "On: every picture is listed, ranked from most to least alike.");
            weaker.CheckedChanged += delegate
            {
                if (mode != 3 || allPictures.Count == 0) return;
                ShowAlikePictures();
                if (picSearch == null)
                {
                    SetPictureEmptyText();
                    List<string> parts = new List<string>();
                    List<bool> bold = new List<bool>();
                    AddPictureCounts(delegate(string s, bool b) { parts.Add(s); bold.Add(b); });
                    status.SetParts(parts.ToArray(), bold.ToArray());
                }
            };
            Tip(cpuSlider, "How hard Similar pictures works the processor.\nLeft: one CPU thread - cool and quiet, slower. Right: every thread - fastest, and the fans will know.\n" +
                           "Takes effect at once, even during a search. The work always runs at low priority, so the PC stays responsive.");
            cpuSlider.Scrolling += delegate { OnCpuSlider(); };
            cpuSlider.Committed += delegate { OnCpuSlider(); settings.Set("PictureWorkers", CpuWorkers); };
            clipCache.CheckedChanged += delegate
            {
                Clip.UseCache = clipCache.Checked;
                if (!clipCache.Checked) Clip.ClearCache();
            };

            filtersChip.Suffix = Theme.GDown;
            filtersChip.CheckedChanged += delegate
            {
                filtersChip.Suffix = filtersChip.Checked ? Theme.GUp : Theme.GDown;
                LayoutHero();
            };
            ocr.CheckedChanged += delegate
            {
                if (ocr.Checked && !Ocr.Available)
                {
                    ocr.Checked = false;
                    Warn("Windows has no OCR language installed. Add one in Settings > Time & language > Language & region (any language with \"Optical character recognition\").");
                }
            };

            types.DropDown = true;
            types.SetPlaceholder("All files");
            types.DropDownClicked += delegate { ShowTypePresets(); };
            excludeTypes.SetPlaceholder("*.exe; *.iso");
            skipFolders.SetPlaceholder("Folder names");
            maxMb.SetPlaceholder("0 = no limit");
            foreach (SkinField f in new[] { maxMb, threads })
                f.Box.KeyPress += delegate(object s, KeyPressEventArgs e) { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            foreach (SkinField f in new[] { types, excludeTypes, skipFolders, maxMb, threads })
                f.Box.KeyDown += delegate(object s, KeyEventArgs e) { if (e.KeyCode == Keys.Enter) { e.SuppressKeyPress = true; StartSearch(); } };

            Tip(loose, "Ignore spaces, dashes and case, and treat O/0 and I/l/1 alike:  H6056  also finds  H-6056,  h 6056,  H6O56.");
            Tip(ocr, "Read the text in pictures (JPG, PNG, screenshots, photos of labels) and in scanned PDFs, with Windows' built-in OCR.\nNothing leaves this PC.");
            Tip(ocrCache, "Remember the text OCR reads, so each picture is only read once and later searches are much faster.\n" +
                          "The catch: the text of your pictures is then stored on this PC, in %LOCALAPPDATA%\\FindAnything\\ocr.\n" +
                          "Off: pictures are read again every search and nothing is kept. Turning this off deletes what was stored.");
            Tip(onlyPics, "Search only picture files (JPG, PNG, HEIC, WebP, GIF, BMP, TIFF), reading their text with OCR.\nSkips every other file, so it is quick to point at a whole Pictures folder.");
            onlyPics.CheckedChanged += delegate { if (onlyPics.Checked && !ocr.Checked) ocr.Checked = true; };
            ocr.CheckedChanged += delegate { if (!ocr.Checked && onlyPics.Checked) onlyPics.Checked = false; };
            ocrCache.CheckedChanged += delegate
            {
                Ocr.UseCache = ocrCache.Checked;
                if (!ocrCache.Checked) Ocr.ClearCache();
            };
            Tip(kProduct, "Key-shaped codes: XXXXX-XXXXX-XXXXX-XXXXX-XXXXX (Windows, Office, games) and similar.");
            Tip(kLabeled, "Values written after a label: License, Product key, Activation code, Serial number, S/N, CD key, Gift card...");
            Tip(kPasswords, "Values after Password, Passcode, Wi-Fi password, PIN, Unlock code...");
            Tip(kTokens, "API keys and tokens by their known formats (GitHub, AWS, Google, Stripe, Slack, OpenAI, Anthropic...) and private key files.");
            Tip(kFinancial, "Card numbers (only ones whose check digit is valid) and US Social Security numbers.");

            hero.Controls.AddRange(new Control[]
            {
                findField, lookField, browseButton, aboutButton, modeTabs,
                matchCase, wholeWord, wildcards, loose, inContents, inNames, insideDocs, utf16, ocr, onlyPics, ocrCache,
                clipCache, weaker, cpuSlider, kProduct, kLabeled, kPasswords, kTokens, kFinancial, filtersChip,
                types, excludeTypes, skipFolders, maxMb, threads
            });
            // Last in z-order: the Search button's glow sits under its neighbours instead of erasing their edges.
            hero.Controls.Add(pickSamples);
            hero.Controls.Add(searchButton);
            searchButton.SendToBack();
        }

        private Chip[][] ChipGroups()
        {
            switch (mode)
            {
                case 1: return new[] { new[] { kProduct, kLabeled, kPasswords, kTokens, kFinancial }, new[] { insideDocs, ocr, onlyPics, ocrCache } };
                case 2: return new[] { new[] { matchCase, utf16 } };
                case 3: return new[] { new[] { weaker }, new[] { clipCache } };
                default: return new[] { new[] { matchCase, wholeWord, wildcards, loose }, new[] { inContents, inNames }, new[] { insideDocs, utf16, ocr, onlyPics, ocrCache } };
            }
        }

        private string ButtonText
        {
            get
            {
                if (Busy) return "Stop";
                if (mode == 1) return "Find keys";
                if (mode == 2) return RawDisk.IsAdmin ? "Scan drive" : "Run as admin";
                if (mode == 3) return "Find pictures";
                return "Search";
            }
        }

        /// <summary>Places everything to the mockup's measurements (CSS px, scaled).</summary>
        private void LayoutHero()
        {
            int W = hero.Width > 0 ? hero.Width : ClientSize.Width;
            int L = Theme.S(22), R = W - Theme.S(22);
            int y = Theme.S(18) + Theme.S(34) + Theme.S(14);

            // About and the mode tabs, top right
            int aw = aboutButton.PreferredWidth(30);
            aboutButton.Bounds = new Rectangle(R - aw + Theme.S(10), Theme.S(18), aw, Theme.S(34));
            int tw = modeTabs.PreferredWidth();
            modeTabs.Bounds = new Rectangle(aboutButton.Left - Theme.S(10) - tw, Theme.S(16), tw, Theme.S(38));

            // find + main button
            searchButton.Text = ButtonText;
            searchButton.Icon = Busy ? Theme.GStop : mode == 1 ? "\uE192" : mode == 2 ? "\uEDA2" : mode == 3 ? "\uE8B9" : Theme.GSearch;
            int bw = 0;
            foreach (string label in new[] { "Search", "Find keys", "Scan drive", "Run as admin", "Find pictures", "Stop" })
                bw = Math.Max(bw, new SkinButton(label, Theme.GSearch, ButtonKind.Primary).PreferredWidth(54));
            int bp = searchButton.ShadowPad;
            Place(searchButton, new Rectangle(R - (bw - 2 * bp), y, bw - 2 * bp, Theme.S(54)), bp);
            int findRight = R - (bw - 2 * bp) - Theme.S(10);
            // Similar pictures: the sample picker sits in the search row, between the box and the button
            pickSamples.Visible = mode == 3;
            if (mode == 3)
            {
                int sp = pickSamples.ShadowPad;
                int pw = SampleButtonWidth() - 2 * sp;
                Place(pickSamples, new Rectangle(findRight - pw, y, pw, Theme.S(54)), sp);
                findRight -= pw + Theme.S(10);
            }
            Place(findField, new Rectangle(L, y, findRight - L, Theme.S(54)), findField.ShadowPad);
            searchButton.Invalidate();
            y += Theme.S(54) + Theme.S(10);

            // look in + Browse (the drive picker has no Browse)
            browseButton.Visible = mode != 2;
            int brw = mode == 2 ? -Theme.S(10) : browseButton.PreferredWidth(46) - 2 * browseButton.ShadowPad;
            if (mode != 2) Place(browseButton, new Rectangle(R - brw, y, brw, Theme.S(46)), browseButton.ShadowPad);
            Place(lookField, new Rectangle(L, y, R - L - brw - Theme.S(10), Theme.S(46)), lookField.ShadowPad);
            y += Theme.S(46) + Theme.S(12);

            // chips, wrapping onto a second row when the window is narrow
            separatorsX.Clear();
            separatorsY.Clear();
            foreach (Chip c in new[] { matchCase, wholeWord, wildcards, loose, inContents, inNames, insideDocs, utf16, ocr, onlyPics, ocrCache, clipCache, weaker, kProduct, kLabeled, kPasswords, kTokens, kFinancial })
                c.Visible = false;
            int pad = Skin.Pad("chip");
            bool filters = mode != 2 && mode != 3;
            filtersChip.Visible = filters;
            int fw = filtersChip.PreferredWidth() - 2 * pad;
            if (filters) Place(filtersChip, new Rectangle(R - fw, y, fw, Theme.S(32)), pad);
            int limit = filters ? R - fw - Theme.S(12) : R;
            int x = L;
            Chip[][] groups = ChipGroups();
            for (int gi = 0; gi < groups.Length; gi++)
            {
                if (gi > 0) { separatorsX.Add(x + Theme.S(4)); separatorsY.Add(y + Theme.S(16)); x += Theme.S(17); }
                bool firstInGroup = true;
                foreach (Chip c in groups[gi])
                {
                    int cw = c.PreferredWidth() - 2 * pad;
                    if (x + cw > limit && x > L)
                    {
                        // a group that starts a new row needs no divider in front of it
                        if (firstInGroup && gi > 0) { separatorsX.RemoveAt(separatorsX.Count - 1); separatorsY.RemoveAt(separatorsY.Count - 1); }
                        x = L; y += Theme.S(40); limit = R;
                    }
                    firstInGroup = false;
                    c.Visible = true;
                    Place(c, new Rectangle(x, y, cw, Theme.S(32)), pad);
                    x += cw + Theme.S(8);
                }
            }
            // Similar pictures: the CPU slider at the right end of the chip row
            cpuSlider.Visible = mode == 3;
            cpuLabelRect = cpuValueRect = Rectangle.Empty;
            if (mode == 3)
            {
                int vw = Theme.S(92), sw = Theme.S(170), lw = Theme.Advance("CPU use", Theme.UiSemibold(13)) + Theme.S(4);
                cpuValueRect = new Rectangle(R - vw, y, vw, Theme.S(32));
                cpuSlider.Bounds = new Rectangle(cpuValueRect.Left - Theme.S(8) - sw, y, sw, Theme.S(32));
                cpuLabelRect = new Rectangle(cpuSlider.Left - Theme.S(6) - lw, y, lw, Theme.S(32));
            }
            y += Theme.S(32);

            // filters
            miniLabels.Clear();
            bool show = filtersChip.Checked && filters;
            foreach (SkinField f in new[] { types, excludeTypes, skipFolders, maxMb, threads }) f.Visible = show;
            if (show)
            {
                y += Theme.S(12);
                int labelH = Theme.S(18), boxH = Theme.S(34), gap = Theme.S(10);
                int fixedW = Theme.S(130) + Theme.S(84) + 4 * gap;
                int flex = Math.Max(Theme.S(300), R - L - fixedW);
                int[] widths = { (int)(flex * 0.37), (int)(flex * 0.24), 0, Theme.S(130), Theme.S(84) };
                widths[2] = flex - widths[0] - widths[1];
                string[] labels = { "File types", "Except", "Skip folders", "Max size (MB)", "Threads" };
                SkinField[] boxes = { types, excludeTypes, skipFolders, maxMb, threads };
                x = L;
                for (int i = 0; i < 5; i++)
                {
                    miniLabels.Add(new KeyValuePair<string, Rectangle>(labels[i], new Rectangle(x + Theme.S(1), y, widths[i], labelH)));
                    Place(boxes[i], new Rectangle(x, y + labelH + Theme.S(5), widths[i], boxH), boxes[i].ShadowPad);
                    x += widths[i] + gap;
                }
                y += labelH + Theme.S(5) + boxH;
            }
            int h = y + Theme.S(16);
            if (hero.Height != h) hero.Height = h;
            hero.Invalidate();
        }

        /// <summary>The tabs read Search text, Find keys, Similar pictures, Drive check; modes 2 and 3 swap places.</summary>
        /// <summary>Pictures read at once (each on one CPU thread), from the CPU use slider: 1 .. every logical processor.</summary>
        private int CpuWorkers
        {
            get { int n = Math.Max(1, Environment.ProcessorCount); return 1 + (int)Math.Round(cpuSlider.Value * (n - 1)); }
            set { int n = Math.Max(1, Environment.ProcessorCount); cpuSlider.Value = n == 1 ? 1 : (Math.Max(1, Math.Min(n, value)) - 1) / (double)(n - 1); }
        }

        private void OnCpuSlider()
        {
            if (picSearch != null) picSearch.Workers = CpuWorkers;
            hero.Invalidate(cpuValueRect);
        }

        private static int TabToMode(int i) { return i == 2 ? 3 : i == 3 ? 2 : i; }

        private bool Busy { get { return job != null || driveScan != null || picSearch != null; } }

        /// <summary>Switches between Search text, Find keys, Similar pictures and Drive check.</summary>
        private void SetMode(int m)
        {
            if (Busy) { modeTabs.SelectedIndex = TabToMode(mode); return; }
            if (mode != 2) lookTextFiles = lookField.Text;
            mode = m;
            allPictures.Clear();
            if (modeTabs.SelectedIndex != TabToMode(m)) modeTabs.SelectedIndex = TabToMode(m);
            results.Similar = mode == 3;
            results.EmptyImage = mode == 3 ? Skin.Art("grandma-256") : null;
            results.FadeInEmpty();
            hits.Clear();
            results.ResetSelection();
            lookField.Box.ReadOnly = mode == 2;
            lookField.Icon = mode == 2 ? "\uEDA2" : Theme.GFolder;
            switch (mode)
            {
                case 1:
                    findField.SetPlaceholder("Optional: only keys mentioning... (Office, Adobe, Steam)");
                    lookField.Text = lookTextFiles;
                    results.EmptyTitle = "Find the keys you stashed away";
                    results.EmptyText = "Product keys, licenses, serials, passwords, PINs and tokens in text files, documents and pictures.";
                    break;
                case 2:
                    findField.SetPlaceholder("Optional: text to look for (your name, email, \"password\")");
                    if (disks == null) { try { disks = RawDisk.List(); } catch (Exception) { disks = new List<DiskInfo>(); } }
                    if (selectedDisk == null && disks.Count > 0) selectedDisk = disks[disks.Count - 1];
                    lookField.Text = selectedDisk != null ? selectedDisk.ToString() : "No drives found";
                    results.EmptyTitle = RawDisk.IsAdmin ? "Check a drive before you sell or give it away" : "Drive check needs administrator rights";
                    results.EmptyText = RawDisk.IsAdmin
                        ? "Reads every sector (read-only): shows how much of the drive still holds data, and where your text appears."
                        : "Press Run as admin: Windows asks for permission, then Find Anything reopens here.";
                    break;
                case 3:
                    findField.SetPlaceholder("Describe it (a dog in a costume, the beach) or pick samples");
                    lookField.Text = lookTextFiles;
                    UpdateSampleButton();
                    results.EmptyTitle = "Find pictures that look alike";
                    results.EmptyText = "Pick or drop a sample picture, or describe what is in it. Runs on this PC; nothing is uploaded.";
                    break;
                default:
                    findField.SetPlaceholder("What are you looking for?");
                    lookField.Text = lookTextFiles;
                    results.EmptyTitle = "Type what to find and press Enter";
                    results.EmptyText = "Searches inside every file: text, PDF, Word, Excel, logs, any size.";
                    break;
            }
            status.SetParts(new[] { "Ready" }, new[] { false });
            status.Notes = 0;
            LayoutHero();
            findField.Box.Focus();
        }

        private void ShowDiskMenu()
        {
            if (disks == null) { try { disks = RawDisk.List(); } catch (Exception ex) { Warn(ex.Message); return; } }
            ContextMenuStrip m = DarkMenuRenderer.Menu();
            foreach (DiskInfo d in disks)
            {
                DiskInfo disk = d;
                ToolStripMenuItem item = new ToolStripMenuItem(d.ToString() + (d.System ? "   (Windows is on this drive)" : ""));
                item.Checked = d == selectedDisk;
                item.Click += delegate { selectedDisk = disk; lookField.Text = disk.ToString(); };
                m.Items.Add(item);
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Refresh the list (after plugging a drive in)", null, delegate { disks = null; ShowDiskMenu(); });
            m.ShowCheckMargin = true;
            lookField.ShowMenu(m);
        }
        private static void Place(Control c, Rectangle shape, int pad)
        {
            c.Bounds = Rectangle.Inflate(shape, pad, pad);
        }

        private void PaintHero(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int L = Theme.S(22), top = Theme.S(18), h = Theme.S(34);
            if (appIcon != null)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(appIcon, new Rectangle(L, top, h, h));
            }
            int x = L + h + Theme.S(12);
            Font tf = Theme.Display(21);
            int w1 = Theme.Advance("Find ", tf);
            TextRenderer.DrawText(g, "Find", tf, new Rectangle(x, top, w1 + 4, h), Theme.Text, Theme.Left);
            int w2 = Theme.Advance("Anything", tf);
            TextRenderer.DrawText(g, "Anything", tf, new Rectangle(x + w1, top, w2 + 4, h), Theme.AmberHi, Theme.Left);
            int tagX = x + w1 + w2 + Theme.S(14);
            if (tagX + Theme.S(230) < modeTabs.Left - Theme.S(12))
                TextRenderer.DrawText(g, "Text inside any file. Any size.", Theme.Ui(13), new Rectangle(tagX, top + Theme.S(2), modeTabs.Left - Theme.S(12) - tagX, h), Theme.Dim, Theme.Left | TextFormatFlags.EndEllipsis);

            using (Pen p = new Pen(Theme.Line2))
                for (int i = 0; i < separatorsX.Count; i++)
                    g.DrawLine(p, separatorsX[i], separatorsY[i] - Theme.S(10), separatorsX[i], separatorsY[i] + Theme.S(10));
            if (!cpuLabelRect.IsEmpty)
            {
                TextRenderer.DrawText(g, "CPU use", Theme.UiSemibold(13), cpuLabelRect, Theme.Dim, Theme.Left);
                int w = CpuWorkers, n = Environment.ProcessorCount;
                TextRenderer.DrawText(g, w + " of " + n + (n == 1 ? " thread" : " threads"), Theme.Ui(13), cpuValueRect, Theme.Text, Theme.Right);
            }
            foreach (KeyValuePair<string, Rectangle> kv in miniLabels)
                TextRenderer.DrawText(g, Spaced(kv.Key.ToUpperInvariant()), Theme.UiSemibold(11.5f), kv.Value, Theme.Faint, Theme.Left);
        }

        private static string Spaced(string s)
        {
            StringBuilder sb = new StringBuilder();
            foreach (char ch in s) { if (sb.Length > 0) sb.Append('\u200A'); sb.Append(ch); }
            return sb.ToString();
        }

        private void Tip(Control c, string text)
        {
            tips.SetToolTip(c, text);
            SkinField f = c as SkinField;
            if (f != null) tips.SetToolTip(f.Box, text);
        }

        private void DrawTip(object sender, DrawToolTipEventArgs e)
        {
            using (SolidBrush b = new SolidBrush(Theme.Surface2)) e.Graphics.FillRectangle(b, e.Bounds);
            using (Pen p = new Pen(Theme.Line2)) e.Graphics.DrawRectangle(p, 0, 0, e.Bounds.Width - 1, e.Bounds.Height - 1);
            TextRenderer.DrawText(e.Graphics, e.ToolTipText, Theme.Ui(13), Rectangle.Inflate(e.Bounds, -Theme.S(10), -Theme.S(7)), Theme.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
        }

        // ------------------------------------------------------------------ menus

        private ContextMenuStrip BuildMenu()
        {
            ContextMenuStrip m = DarkMenuRenderer.Menu();
            ToolStripMenuItem view = new ToolStripMenuItem("Open in viewer", null, delegate { OpenViewer(); });
            view.ShortcutKeyDisplayString = "Enter";
            view.Font = Theme.UiSemibold(14);
            ToolStripMenuItem def = new ToolStripMenuItem("Open with its usual program", null, delegate { OpenDefault(); });
            ToolStripMenuItem npp = new ToolStripMenuItem("Open in Notepad++ at the match", null, delegate { OpenNotepadPlusPlus(); });
            ToolStripMenuItem folder = new ToolStripMenuItem("Show in folder", null, delegate { ForSelected(delegate(FileHit h) { Shell.ShowInFolder(h.Path); }); });
            ToolStripMenuItem copyKey = new ToolStripMenuItem("Copy the key", null, delegate { CopyKeys(); });
            copyKey.Font = Theme.UiSemibold(14);
            ToolStripMenuItem copy = new ToolStripMenuItem("Copy path", null, delegate { CopyPaths(); });
            copy.ShortcutKeyDisplayString = "Ctrl+C";
            ToolStripMenuItem export = new ToolStripMenuItem("Save the list as CSV...", null, delegate { ExportCsv(); });
            ToolStripMenuItem similar = new ToolStripMenuItem("Find similar pictures", null, delegate { FindSimilarToSelected(); });
            m.Items.AddRange(new ToolStripItem[] { view, copyKey, def, npp, similar, new ToolStripSeparator(), folder, copy, new ToolStripSeparator(), export });
            m.Opening += delegate
            {
                bool any = results.SelectedItems.Count > 0;
                view.Enabled = def.Enabled = folder.Enabled = copy.Enabled = any;
                npp.Visible = Shell.NotepadPlusPlus != null && mode != 2;
                FileHit fh = results.FocusedItem;
                copyKey.Visible = fh != null && fh.Secret != null;
                def.Visible = folder.Visible = mode != 2;
                npp.Enabled = any;
                bool pics = false;
                foreach (FileHit sh in results.SelectedItems) if (sh.Source != "DISK" && Ocr.IsImage(sh.Path)) pics = true;
                similar.Visible = pics && !Busy;
                export.Enabled = hits.Count > 0;
            };
            return m;
        }

        private void ShowLookInMenu()
        {
            ContextMenuStrip m = DarkMenuRenderer.Menu();
            m.Items.Add("All drives", null, delegate { lookField.Text = AllDrivesItem; });
            foreach (DriveInfo d in DriveInfo.GetDrives())
            {
                try
                {
                    if (!d.IsReady) continue;
                    string label = d.Name + (string.IsNullOrEmpty(d.VolumeLabel) ? "" : "   " + d.VolumeLabel) + "   (" + Fmt.Size(d.TotalFreeSpace) + " free)";
                    string value = d.Name;
                    m.Items.Add(label, null, delegate { lookField.Text = value; });
                }
                catch (Exception) { }
            }
            lookField.ShowMenu(m);
        }

        private void ShowTypePresets()
        {
            ContextMenuStrip m = DarkMenuRenderer.Menu();
            for (int i = 0; i < TypePresets.Length; i++)
            {
                string value = TypePresets[i];
                ToolStripMenuItem item = new ToolStripMenuItem(TypePresetNames[i] + (value.Length > 0 ? "     " + Shorten(value, 40) : ""));
                item.Click += delegate { types.Text = value; };
                m.Items.Add(item);
            }
            types.ShowMenu(m);
        }

        private static string Shorten(string s, int n) { return s.Length <= n ? s : s.Substring(0, n) + "\u2026"; }

        // ------------------------------------------------------------------ settings

        private void LoadSettings()
        {
            // What people type (searches, folders) is never saved; older versions did, so erase it.
            foreach (string k in new[] { "Find", "LookIn" }) settings.Remove(k);
            settings.SetList("Recent.Find", new string[0]);
            settings.SetList("Recent.LookIn", new string[0]);
            lookField.Text = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            string t = settings.Get("Types", "");
            types.Text = t == "(all files)" ? "" : t;
            excludeTypes.Text = settings.Get("ExcludeTypes", "");
            matchCase.Checked = settings.GetBool("MatchCase", false);
            loose.Checked = settings.GetBool("Loose", false);
            ocr.Checked = settings.GetBool("Ocr", false) && Ocr.Available;
            ocrCache.Checked = settings.GetBool("OcrCache", false);
            onlyPics.Checked = ocr.Checked && settings.GetBool("OnlyPictures", false);
            clipCache.Checked = settings.GetBool("PictureCache", false);
            weaker.Checked = settings.GetBool("ShowWeakerMatches", false);
            // default: a quarter of the CPU threads (2 on an 8-thread laptop, 6 on a 24-thread desktop)
            CpuWorkers = (int)settings.GetLong("PictureWorkers", Math.Max(1, Environment.ProcessorCount / 4));
            Clip.UseCache = clipCache.Checked;
            if (!clipCache.Checked) Clip.ClearCache();
            Ocr.UseCache = ocrCache.Checked;
            if (!ocrCache.Checked) Ocr.ClearCache();   // off means nothing stored, including anything from before
            kProduct.Checked = settings.GetBool("Keys.Product", true);
            kLabeled.Checked = settings.GetBool("Keys.Labeled", true);
            kPasswords.Checked = settings.GetBool("Keys.Passwords", true);
            kTokens.Checked = settings.GetBool("Keys.Tokens", true);
            kFinancial.Checked = settings.GetBool("Keys.Financial", false);
            wildcards.Checked = settings.GetBool("Wildcards", true);
            wholeWord.Checked = settings.GetBool("WholeWord", false);
            inContents.Checked = settings.GetBool("InContents", true);
            inNames.Checked = settings.GetBool("InNames", true);
            insideDocs.Checked = settings.GetBool("InsideDocs", true);
            utf16.Checked = settings.GetBool("Utf16", true);
            skipFolders.Text = settings.Get("SkipFolders", "System Volume Information;$Recycle.Bin");
            maxMb.Text = Math.Max(0, settings.GetLong("MaxMB", 0)).ToString();
            threads.Text = Math.Min(32, Math.Max(1, settings.GetLong("Threads", Math.Min(8, Environment.ProcessorCount / 2)))).ToString();
            filtersChip.Checked = settings.GetBool("ShowFilters", false) || types.Text.Length > 0 || excludeTypes.Text.Length > 0;
        }

        private void SaveSettings()
        {
            settings.Set("Types", types.Text);
            settings.Set("ExcludeTypes", excludeTypes.Text);
            settings.Set("MatchCase", matchCase.Checked);
            settings.Set("Loose", loose.Checked);
            settings.Set("Ocr", ocr.Checked);
            settings.Set("OcrCache", ocrCache.Checked);
            settings.Set("OnlyPictures", onlyPics.Checked);
            settings.Set("PictureCache", clipCache.Checked);
            settings.Set("ShowWeakerMatches", weaker.Checked);
            settings.Set("PictureWorkers", CpuWorkers);
            settings.Set("Keys.Product", kProduct.Checked);
            settings.Set("Keys.Labeled", kLabeled.Checked);
            settings.Set("Keys.Passwords", kPasswords.Checked);
            settings.Set("Keys.Tokens", kTokens.Checked);
            settings.Set("Keys.Financial", kFinancial.Checked);
            settings.Set("Wildcards", wildcards.Checked);
            settings.Set("WholeWord", wholeWord.Checked);
            settings.Set("InContents", inContents.Checked);
            settings.Set("InNames", inNames.Checked);
            settings.Set("InsideDocs", insideDocs.Checked);
            settings.Set("Utf16", utf16.Checked);
            settings.Set("SkipFolders", skipFolders.Text);
            settings.Set("MaxMB", ParseLong(maxMb.Text, 0));
            settings.Set("Threads", Math.Max(1, ParseLong(threads.Text, 4)));
            settings.Set("ShowFilters", filtersChip.Checked);
            settings.Save();
        }

        private static long ParseLong(string s, long fallback)
        {
            long v;
            return long.TryParse((s ?? "").Trim(), out v) ? v : fallback;
        }


        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (StartFolder != null) lookField.Text = StartFolder;
            lookTextFiles = lookField.Text;
            if (StartMode != 0) SetMode(StartMode);
            findField.Box.Focus();
            findField.Box.SelectAll();
            if (StartSample != null)
            {
                findField.Text = StartSample;
                BeginInvoke((MethodInvoker)StartPictureSearch);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (cts != null) cts.Cancel();
            SaveSettings();
            base.OnFormClosing(e);
        }

        protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
        {
            if (keyData == Keys.Escape && (Busy)) { StopSearch(); return true; }
            if (keyData == (Keys.Control | Keys.F)) { findField.Box.Focus(); findField.Box.SelectAll(); return true; }
            if (keyData == (Keys.Control | Keys.L)) { lookField.Box.Focus(); lookField.Box.SelectAll(); return true; }
            return base.ProcessCmdKey(ref msg, keyData);
        }

        // ------------------------------------------------------------------ look in

        private void OnBrowse()
        {
            string start = lookField.Text.Split(';')[0].Trim();
            string picked = FolderPicker.Pick(this, start);
            if (picked != null) lookField.Text = picked;
        }

        private void OnDragDrop(object sender, DragEventArgs e)
        {
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            // Similar pictures: dropped pictures are the samples, dropped folders are where to look
            List<string> pics = new List<string>(), other = new List<string>();
            foreach (string p in files) (mode == 3 && File.Exists(p) && Ocr.IsImage(p) ? pics : other).Add(p);
            if (pics.Count > 0) findField.Text = string.Join(";", pics.ToArray());
            if (other.Count > 0) lookField.Text = string.Join(";", other.ToArray());
        }

        private List<string> Roots()
        {
            List<string> roots = new List<string>();
            string text = lookField.Text.Trim();
            if (text == AllDrivesItem)
            {
                foreach (DriveInfo d in DriveInfo.GetDrives())
                {
                    try
                    {
                        if (d.IsReady && (d.DriveType == DriveType.Fixed || d.DriveType == DriveType.Removable)) roots.Add(d.Name);
                    }
                    catch (Exception) { }
                }
                return roots;
            }
            foreach (string part in text.Split(';'))
            {
                string p = part.Trim().Trim('"');
                if (p.Length == 0) continue;
                if (p.Length == 2 && p[1] == ':') p += "\\";
                roots.Add(p);
            }
            return roots;
        }

        // ------------------------------------------------------------------ search

        private void StartSearch()
        {
            if (Busy) return;
            if (mode == 2) { StartDriveScan(); return; }
            if (mode == 3) { StartPictureSearch(); return; }
            SearchSettings s = new SearchSettings();
            s.Roots = Roots();
            s.Text = findField.Text;
            s.CaseSensitive = matchCase.Checked;
            s.Wildcards = wildcards.Checked;
            s.WholeWord = wholeWord.Checked;
            // Reading pictures is its own source: Only pictures (or OCR with both other sources off) needs no Inside files.
            bool picturesOnly = ocr.Checked && (onlyPics.Checked || (!inContents.Checked && !inNames.Checked));
            s.SearchContents = inContents.Checked || picturesOnly;
            s.SearchNames = inNames.Checked;
            s.IncludeMasks = picturesOnly ? Ocr.PictureMasks : types.Text;
            s.ExcludeMasks = excludeTypes.Text;
            s.SkipFolders = skipFolders.Text;
            s.LookInsideDocuments = insideDocs.Checked;
            s.Utf16 = utf16.Checked;
            s.MaxFileBytes = ParseLong(maxMb.Text, 0) * 1024 * 1024;
            s.Threads = (int)Math.Min(32, Math.Max(1, ParseLong(threads.Text, 4)));
            s.Loose = mode == 0 && loose.Checked;
            s.Ocr = ocr.Checked;
            if (mode == 1)
            {
                s.SecretsMode = true;
                s.Secrets.ProductKeys = kProduct.Checked;
                s.Secrets.Labeled = kLabeled.Checked;
                s.Secrets.Passwords = kPasswords.Checked;
                s.Secrets.Tokens = kTokens.Checked;
                s.Secrets.Financial = kFinancial.Checked;
                if (!(kProduct.Checked || kLabeled.Checked || kPasswords.Checked || kTokens.Checked || kFinancial.Checked))
                { Warn("Turn on at least one kind of key to look for."); return; }
            }

            if (s.Roots.Count == 0) { Warn("Pick a folder or drive to look in."); lookField.Box.Focus(); return; }
            foreach (string r in s.Roots)
                if (!Directory.Exists(LongPath.Add(r)) && !File.Exists(LongPath.Add(r))) { Warn("Can't find \"" + r + "\"."); lookField.Box.Focus(); return; }

            SearchJob newJob;
            try { newJob = new SearchJob(s); }
            catch (ArgumentException ex) { Warn(ex.Message); findField.Box.Focus(); return; }

            SaveSettings();

            try { previewPattern = mode == 1 ? null : BytePattern.Make(s.Text, s.CaseSensitive, s.Wildcards, s.WholeWord, TextForm.Bytes8, s.Loose); }
            catch (ArgumentException) { previewPattern = null; }

            hits.Clear();
            problems.Clear();
            results.SortColumn = -1;
            results.EmptyTitle = "Searching...";
            results.EmptyText = "Results appear here as they are found.";
            results.ResetSelection();

            job = newJob;
            jobSettings = s;
            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            SearchJob running = job;
            jobTask = Task.Factory.StartNew(delegate { running.Run(token); }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            clock = Stopwatch.StartNew();
            LayoutHero();
            status.Running = true;
            status.Notes = 0;
            tick.Start();
        }

        private void StopSearch()
        {
            if (cts != null) cts.Cancel();
            status.SetParts(new[] { "Stopping..." }, new[] { true });
        }

        private int[] FindSpan(string preview)
        {
            int s, len;
            if (previewPattern != null && previewPattern.FindInString(preview, out s, out len)) return new[] { s, len };
            return null;
        }

        private void OnTick(object sender, EventArgs e)
        {
            if (driveScan != null) { OnDriveTick(); return; }
            if (picSearch != null) { OnPictureTick(); return; }
            if (job == null) { tick.Stop(); return; }
            bool done = jobTask.IsCompleted;

            int before = hits.Count;
            FileHit h;
            while (job.Hits.TryDequeue(out h)) hits.Add(h);
            string p;
            while (job.Errors.TryDequeue(out p)) problems.Add(p);
            if (hits.Count != before || (sortDirty && done))
            {
                if (results.SortColumn >= 0) { SortHits(); sortDirty = false; }
                results.ItemsChanged();
            }
            status.Notes = Interlocked.Read(ref job.ErrorCount);

            List<string> parts = new List<string>();
            List<bool> bold = new List<bool>();
            Action<string, bool> add = delegate(string t, bool b) { parts.Add(t); bold.Add(b); };
            long seen = Interlocked.Read(ref job.FilesSeen), searched = Interlocked.Read(ref job.FilesSearched);
            long big = Interlocked.Read(ref job.SkippedLarge), cloud = Interlocked.Read(ref job.SkippedCloud);
            if (done)
            {
                tick.Stop();
                bool stopped = cts.IsCancellationRequested;
                add((stopped ? "Stopped after " : "Done in ") + clock.Elapsed.TotalSeconds.ToString("0.0") + " s", true);
            }
            add(hits.Count.ToString("N0"), true); add("found", false);
            add(searched.ToString("N0"), true); add(searched == 1 ? "file searched" : "files searched", false);
            add(Fmt.Size(Interlocked.Read(ref job.BytesRead)), true); add("read", false);
            if (seen != searched) { add((seen - searched).ToString("N0"), true); add(seen - searched == 1 ? "other file type skipped" : "other types skipped", false); }
            if (big > 0) { add(big.ToString("N0"), true); add("too big", false); }
            if (cloud > 0) { add(cloud.ToString("N0"), true); add("online-only", false); }
            long ocrN = Interlocked.Read(ref job.OcrFiles);
            if (ocrN > 0) { add(ocrN.ToString("N0"), true); add(ocrN == 1 ? "picture read" : "pictures read", false); }
            if (!done) { add(job.Current, false); }
            status.SetParts(parts.ToArray(), bold.ToArray());

            if (!done) return;
            if (results.SortColumn < 0)
            {
                // Results arrive in whatever order the threads finish; settle them into path order.
                hits.Sort(delegate(FileHit a, FileHit b) { return string.Compare(a.Path, b.Path, StringComparison.CurrentCultureIgnoreCase); });
            }
            results.EmptyTitle = mode == 1 ? "No keys found" : "Nothing found";
            results.EmptyText = mode == 1 ? "Nothing that looks like a key, serial or password here. Try Pictures (OCR), PDF & Office, or another folder."
                                          : "Try fewer words, a * wildcard, Loose, or another folder.";
            results.ItemsChanged();
            results.SelectFirst();
            status.Running = false;
            job = null;
            cts.Dispose();
            cts = null;
            LayoutHero();
        }

        // ------------------------------------------------------------------ results

        private void OnSortRequested(int column)
        {
            if (results.SortColumn == column) results.SortDescending = !results.SortDescending;
            else { results.SortColumn = column; results.SortDescending = column >= 2; }
            FileHit keep = results.FocusedItem;
            SortHits();
            if (keep != null) results.Select(keep);
            results.Invalidate();
            if (job != null) sortDirty = true;
        }

        private void SortHits()
        {
            int col = results.SortColumn;
            int dir = results.SortDescending ? -1 : 1;
            hits.Sort(delegate(FileHit a, FileHit b)
            {
                int r;
                switch (col)
                {
                    case 0: r = string.Compare(a.Name, b.Name, StringComparison.CurrentCultureIgnoreCase); break;
                    case 1: r = mode == 3 ? a.Score.CompareTo(b.Score) : string.Compare(a.Preview ?? "", b.Preview ?? "", StringComparison.CurrentCultureIgnoreCase); break;
                    case 2: r = mode == 3 ? b.FirstLine.CompareTo(a.FirstLine) : a.ContentHits.CompareTo(b.ContentHits); break;
                    case 3: r = a.FirstLine.CompareTo(b.FirstLine); break;
                    case 4: r = a.Size.CompareTo(b.Size); break;
                    default: r = a.Modified.CompareTo(b.Modified); break;
                }
                if (r == 0) r = string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);
                return r * dir;
            });
        }

        private void OnResultsKeyDown(object sender, KeyEventArgs e)
        {
            if (e.KeyCode == Keys.C && e.Control && !e.Shift && mode == 1) { e.Handled = true; CopyKeys(); }
            else if (e.KeyCode == Keys.C && e.Control) { e.Handled = true; CopyPaths(); }
        }

        private void ForSelected(Action<FileHit> action)
        {
            foreach (FileHit h in results.SelectedItems)
            {
                try { action(h); }
                catch (Exception ex) { Warn(ex.Message); return; }
            }
        }

        private ViewOptions CurrentViewOptions()
        {
            ViewOptions o = new ViewOptions();
            SearchSettings s = jobSettings;
            o.Text = s != null ? s.Text : findField.Text;
            o.CaseSensitive = s != null ? s.CaseSensitive : matchCase.Checked;
            o.Wildcards = s != null ? s.Wildcards : wildcards.Checked;
            o.WholeWord = s != null ? s.WholeWord : wholeWord.Checked;
            o.Utf16 = s != null ? s.Utf16 : utf16.Checked;
            o.Loose = s != null ? s.Loose : (mode == 0 && loose.Checked);
            return o;
        }

        private void OpenViewer()
        {
            FileHit h = results.FocusedItem;
            if (h == null) return;
            ViewOptions o = CurrentViewOptions();
            if (h.Secret != null)
            {
                // show the key itself, found literally
                o.Text = h.Secret.Value; o.Wildcards = false; o.Loose = false; o.WholeWord = false; o.CaseSensitive = true;
            }
            if (h.Source == "DISK") { o.Text = driveText ?? ""; o.Loose = false; }
            try
            {
                if (h.Source == "SIMILAR") Shell.OpenDefault(h.Path);
                else if (h.Source == "OCR") new ImageViewerForm(h, o).Show(this);
                else new ViewerForm(h, o).Show(this);
            }
            catch (Exception ex) { Warn("Can't open " + h.Name + ": " + ex.Message); }
        }

        /// <summary>Asks before handing a huge file to a program that loads it whole.</summary>
        private bool ConfirmBig(FileHit h, string program)
        {
            if (h.Size < Shell.BigFileBytes) return true;
            DialogResult r = MessageBox.Show(this,
                h.Name + " is " + Fmt.Size(h.Size) + ".\n\n" + program + " loads the whole file into memory and may freeze or crash.\n\n" +
                "Yes: open it in the built-in viewer instead (instant, any size)\nNo: open it in " + program + " anyway",
                "Big file", MessageBoxButtons.YesNoCancel, MessageBoxIcon.Warning);
            if (r == DialogResult.Yes) { new ViewerForm(h, CurrentViewOptions()).Show(this); return false; }
            return r == DialogResult.No;
        }

        private void OpenDefault()
        {
            ForSelected(delegate(FileHit h) { if (ConfirmBig(h, "Its program")) Shell.OpenDefault(h.Path); });
        }

        private void OpenNotepadPlusPlus()
        {
            ForSelected(delegate(FileHit h)
            {
                if (ConfirmBig(h, "Notepad++")) Shell.OpenInNotepadPlusPlus(h.Path, h.Source.Length == 0 ? h.FirstLine : 0);
            });
        }

        private void CopyKeys()
        {
            StringBuilder sb = new StringBuilder();
            foreach (FileHit h in results.SelectedItems) if (h.Secret != null) sb.Append(h.Secret.Value).Append("\r\n");
            if (sb.Length > 0) Clipboard.SetText(sb.ToString().TrimEnd());
        }

        private void CopyPaths()
        {
            StringBuilder sb = new StringBuilder();
            foreach (FileHit h in results.SelectedItems) sb.Append(h.Path).Append("\r\n");
            if (sb.Length > 0) Clipboard.SetText(sb.ToString().TrimEnd());
        }

        private void ExportCsv()
        {
            using (SaveFileDialog d = new SaveFileDialog())
            {
                d.Filter = "CSV (*.csv)|*.csv";
                d.FileName = "Find Anything results.csv";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                StringBuilder sb = new StringBuilder();
                sb.Append("Path,Name,Folder,Hits,Line,Found in,First match,Size,Modified\r\n");
                foreach (FileHit h in hits)
                {
                    sb.Append(Csv(h.Path)).Append(',').Append(Csv(h.Name)).Append(',').Append(Csv(h.Folder)).Append(',')
                      .Append(h.HitsText).Append(',').Append(h.ContentHits > 0 ? h.FirstLine.ToString() : "").Append(',')
                      .Append(Csv(h.MatchText)).Append(',').Append(Csv(h.Preview ?? "")).Append(',')
                      .Append(h.Size).Append(',').Append(h.Modified.ToString("yyyy-MM-dd HH:mm:ss")).Append("\r\n");
                }
                try { File.WriteAllText(d.FileName, sb.ToString(), new UTF8Encoding(true)); }
                catch (Exception ex) { Warn(ex.Message); }
            }
        }

        private static string Csv(string s)
        {
            return "\"" + s.Replace("\"", "\"\"") + "\"";
        }

        // ------------------------------------------------------------------ similar pictures

        /// <summary>Wide enough for every label the sample button can show, so it never jumps.</summary>
        private int SampleButtonWidth()
        {
            string keep = pickSamples.Text;
            int w = 0;
            foreach (string s in new[] { "Pick samples", "88 samples" }) { pickSamples.Text = s; w = Math.Max(w, pickSamples.PreferredWidth(54)); }
            pickSamples.Text = keep;
            return w;
        }

        /// <summary>"Pick samples", or how many sample pictures the find box holds.</summary>
        private void UpdateSampleButton()
        {
            List<string> pics = SamplePictures();
            pickSamples.Text = pics == null ? "Pick samples" : pics.Count == 1 ? "1 sample" : pics.Count + " samples";
            pickSamples.Invalidate();
        }

        private void PickSamples()
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Sample pictures to match";
                d.Multiselect = true;
                d.Filter = "Pictures|" + Ocr.PictureMasks + "|All files|*.*";
                if (d.ShowDialog(this) == DialogResult.OK) findField.Text = string.Join(";", d.FileNames);
            }
        }

        /// <summary>Right-click on picture results: search the same folders for pictures like the selected ones.</summary>
        private void FindSimilarToSelected()
        {
            List<string> pics = new List<string>();
            foreach (FileHit h in results.SelectedItems) if (h.Source != "DISK" && Ocr.IsImage(h.Path)) pics.Add(h.Path);
            if (pics.Count == 0 || Busy) return;
            string look = lookField.Text;
            SetMode(3);
            lookField.Text = look;
            findField.Text = string.Join(";", pics.ToArray());
            StartPictureSearch();
        }

        /// <summary>The find box as sample pictures (paths separated by ;), or null when it is a description.</summary>
        private List<string> SamplePictures()
        {
            List<string> pics = new List<string>();
            foreach (string part in findField.Text.Split(';'))
            {
                string p = part.Trim().Trim('"');
                if (p.Length == 0) continue;
                if (!Ocr.IsImage(p) || !File.Exists(LongPath.Add(p))) return null;
                pics.Add(p);
            }
            return pics.Count > 0 ? pics : null;
        }

        private void StartPictureSearch()
        {
            if (Busy) return;
            List<string> roots = Roots();
            if (roots.Count == 0) { Warn("Pick a folder or drive to look in."); lookField.Box.Focus(); return; }
            foreach (string r in roots)
                if (!Directory.Exists(LongPath.Add(r)) && !File.Exists(LongPath.Add(r))) { Warn("Can't find \"" + r + "\"."); lookField.Box.Focus(); return; }
            List<string> samples = SamplePictures();
            string description = samples == null ? findField.Text.Trim() : null;
            if (samples == null && description.Length == 0) { Warn("Pick a sample picture (or drop one here), or describe what is in the pictures."); return; }
            if (!AiPack.Ensure(this)) return;
            SaveSettings();

            hits.Clear();
            allPictures.Clear();
            problems.Clear();
            picByText = samples == null;
            results.SortColumn = -1;
            results.ScoreLow = results.ScoreHigh = 0;
            results.EmptyTitle = clip == null ? "Loading the picture model..." : "Looking at pictures...";
            results.EmptyText = "Pictures that look alike appear here as they are found.";
            results.ResetSelection();

            List<string> skip = samples ?? new List<string>();
            Clip loaded = clip;
            int workers = CpuWorkers;
            PictureSearch ps = new PictureSearch(roots, skipFolders.Text, skip);
            ps.Workers = workers;
            picSearch = ps;
            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            picTask = Task.Factory.StartNew(delegate
            {
                try
                {
                    if (loaded == null) loaded = new Clip(1);   // one thread per picture; the slider sets how many at once
                    // A sample is matched by how it looks (DINOv2); a description by what is in the picture (CLIP).
                    List<float[]> queries = new List<float[]>();
                    Func<string, float[]> embed;
                    if (samples != null)
                    {
                        embed = loaded.EmbedLook;
                        foreach (string s in samples) queries.Add(loaded.EmbedLook(LongPath.Add(s)));
                    }
                    else
                    {
                        embed = loaded.EmbedImage;
                        queries.Add(loaded.EmbedText(description));
                    }
                    ps.Run(embed, queries, workers, token);
                }
                catch (OperationCanceledException) { }
                catch (Exception ex) { ps.Error = ex.Message; }
                return loaded;
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default).ContinueWith(delegate(Task<Clip> t)
            {
                if (t.Status == TaskStatus.RanToCompletion && t.Result != null) clip = t.Result;
            }, TaskScheduler.FromCurrentSynchronizationContext());
            clock = Stopwatch.StartNew();
            status.Running = true;
            status.Notes = 0;
            LayoutHero();
            tick.Start();
        }

        private void OnPictureTick()
        {
            PictureSearch ps = picSearch;
            bool done = picTask.IsCompleted;
            int before = allPictures.Count;
            FileHit h;
            while (ps.Hits.TryDequeue(out h)) allPictures.Add(h);
            string p;
            while (ps.Errors.TryDequeue(out p)) problems.Add(p);
            if (allPictures.Count != before) ShowAlikePictures();
            status.Notes = Interlocked.Read(ref ps.ErrorCount);

            List<string> parts = new List<string>();
            List<bool> bold = new List<bool>();
            Action<string, bool> add = delegate(string s, bool b) { parts.Add(s); bold.Add(b); };
            long total = Interlocked.Read(ref ps.Pictures), seen = Interlocked.Read(ref ps.Done);
            if (done) add((cts.IsCancellationRequested ? "Stopped after " : "Done in ") + TimeText(clock.Elapsed.TotalSeconds), true);
            AddPictureCounts(add);
            if (!done && total > 0)
            {
                add(seen.ToString("N0") + " of " + total.ToString("N0"), true); add("read", false);
                double rate = seen / Math.Max(0.1, clock.Elapsed.TotalSeconds);
                if (rate > 0 && seen > 20) { add(TimeText((total - seen) / rate), true); add("left", false); }
            }
            else if (!done) add(clip == null ? "Loading the picture model..." : ps.Current, false);
            status.SetParts(parts.ToArray(), bold.ToArray());
            if (!done) return;

            tick.Stop();
            status.Running = false;
            if (ps.Error != null) Warn("The picture search could not run: " + ps.Error);
            SetPictureEmptyText();
            results.ItemsChanged();
            results.SelectFirst();
            picSearch = null;
            cts.Dispose();
            cts = null;
            LayoutHero();
        }

        // ------------------------------------------------------------------ similar pictures: what is shown

        private readonly List<FileHit> allPictures = new List<FileHit>();   // every picture scored; hits shows the alike ones
        private bool picByText;

        /// <summary>
        /// The score a picture needs to be listed. Samples: DINOv2's measured cut-off. Descriptions: CLIP's text scores
        /// are not calibrated across sentences, so the top band (within 0.05 of the best, and at least 0.22).
        /// </summary>
        private float PictureCutoff()
        {
            if (!picByText) return Clip.LookAlike;
            float best = 0;
            foreach (FileHit x in allPictures) best = Math.Max(best, x.Score);
            return Math.Max(0.22f, best - 0.05f);
        }

        private int HiddenPictures { get { return allPictures.Count - hits.Count; } }

        /// <summary>Lists the pictures above the cut-off (all of them with Show weaker matches), most alike first.</summary>
        private void ShowAlikePictures()
        {
            FileHit keep = results.FocusedItem;
            bool hadSelection = results.SelectedItems.Count > 0;
            float cut = PictureCutoff();
            hits.Clear();
            foreach (FileHit x in allPictures) if (weaker.Checked || x.Score >= cut) hits.Add(x);
            RankPictures();
            if (keep != null && hadSelection && hits.Contains(keep)) results.Select(keep);
            results.ItemsChanged();
        }

        private void AddPictureCounts(Action<string, bool> add)
        {
            if (weaker.Checked) { add(hits.Count.ToString("N0"), true); add(hits.Count == 1 ? "picture ranked" : "pictures ranked", false); return; }
            add(hits.Count.ToString("N0"), true); add(picByText ? "match" + (hits.Count == 1 ? "" : "es") : "alike", false);
            if (HiddenPictures > 0) { add(HiddenPictures.ToString("N0"), true); add("not alike (hidden)", false); }
        }

        private void SetPictureEmptyText()
        {
            if (allPictures.Count == 0)
            {
                results.EmptyTitle = "No pictures here";
                results.EmptyText = "Nothing to compare in this folder: JPG, PNG, HEIC, WebP, GIF, BMP and TIFF pictures are read.";
            }
            else
            {
                results.EmptyTitle = picByText ? "Nothing here matches that description" : "Nothing here looks like your sample";
                results.EmptyText = allPictures.Count.ToString("N0") + " pictures checked. Turn on Show weaker matches to see the closest ones anyway.";
            }
        }

        /// <summary>Most alike first; the rank goes in FirstLine and the bar spans the list's lowest to highest score.</summary>
        private void RankPictures()
        {
            Comparison<FileHit> best = delegate(FileHit a, FileHit b)
            {
                int r = b.Score.CompareTo(a.Score);
                return r != 0 ? r : string.Compare(a.Path, b.Path, StringComparison.OrdinalIgnoreCase);   // ties: same order every time
            };
            List<FileHit> byScore = new List<FileHit>(hits);
            byScore.Sort(best);
            for (int i = 0; i < byScore.Count; i++) byScore[i].FirstLine = i + 1;
            if (byScore.Count > 0) { results.ScoreHigh = byScore[0].Score; results.ScoreLow = byScore[byScore.Count - 1].Score; }
            if (results.SortColumn < 0) hits.Sort(best);
            else SortHits();
        }

        // ------------------------------------------------------------------ drive check
        private string driveText;
        private Task driveTask;
        private AlignedStream driveStream;

        private void RestartAsAdmin()
        {
            try
            {
                SaveSettings();
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, "--drive-check") { UseShellExecute = true, Verb = "runas" });
                Close();
            }
            catch (System.ComponentModel.Win32Exception) { }   // the permission prompt was declined
        }

        private void StartDriveScan()
        {
            if (!RawDisk.IsAdmin) { RestartAsAdmin(); return; }
            if (selectedDisk == null) { Warn("Pick a drive first."); return; }
            BytePattern p8 = null, p16 = null;
            driveText = findField.Text.Trim();
            if (driveText.Length > 0)
            {
                try
                {
                    p8 = BytePattern.Compile(driveText, matchCase.Checked, false, false, TextForm.Bytes8);
                    if (utf16.Checked) p16 = BytePattern.Compile(driveText, matchCase.Checked, false, false, TextForm.Utf16);
                    previewPattern = p8;
                }
                catch (ArgumentException ex) { Warn(ex.Message); return; }
            }
            try { driveStream = RawDisk.Open(selectedDisk.Index); }
            catch (Exception ex) { Warn(ex.Message); return; }

            hits.Clear();
            problems.Clear();
            driveHitsShown = 0;
            results.ResetSelection();
            results.EmptyTitle = "Reading " + selectedDisk.Model.Trim() + "...";
            results.EmptyText = "Every sector, start to end, read-only. Text matches appear here as they are found.";
            DriveScan scan = new DriveScan(p8, p16);
            driveScan = scan;
            cts = new CancellationTokenSource();
            CancellationToken token = cts.Token;
            AlignedStream stream = driveStream;
            driveTask = Task.Factory.StartNew(delegate
            {
                try { scan.Run(stream, token); }
                catch (OperationCanceledException) { }
                catch (Exception ex) { scan.Error = ex.Message; }
                finally { stream.Dispose(); }
            }, token, TaskCreationOptions.LongRunning, TaskScheduler.Default);
            clock = Stopwatch.StartNew();
            status.Running = true;
            status.Notes = 0;
            LayoutHero();
            tick.Start();
        }

        private void OnDriveTick()
        {
            DriveScan scan = driveScan;
            bool done = driveTask.IsCompleted;
            lock (scan.Hits)
            {
                for (; driveHitsShown < scan.Hits.Count; driveHitsShown++)
                {
                    DriveHit d = scan.Hits[driveHitsShown];
                    FileHit h = new FileHit();
                    h.Source = "DISK";
                    h.IoPath = selectedDisk.Path;
                    h.Path = selectedDisk.Path;
                    h.Name = "At " + Fmt.Size(d.Offset) + "  (byte " + d.Offset.ToString("N0") + ")";
                    h.Folder = "Disk " + selectedDisk.Index + "  " + selectedDisk.Model.Trim();
                    h.Size = selectedDisk.Size;
                    h.ContentHits = 1;
                    h.FirstOffset = d.Offset;
                    h.FirstForm = d.Form;
                    h.Preview = d.Preview;
                    hits.Add(h);
                }
            }
            results.ItemsChanged();

            long read = Interlocked.Read(ref scan.BytesRead);
            long sectors = Math.Max(1, read / DriveScan.Sector);
            double dataPct = 100.0 * scan.DataSectors / sectors;
            double secs = Math.Max(0.1, clock.Elapsed.TotalSeconds);
            double rate = read / secs;
            List<string> parts = new List<string>();
            List<bool> bold = new List<bool>();
            Action<string, bool> add = delegate(string s, bool b) { parts.Add(s); bold.Add(b); };
            if (done) add((cts.IsCancellationRequested ? "Stopped after " : "Done in ") + TimeText(secs), true);
            add(Fmt.Size(read), true); add("of " + Fmt.Size(scan.Length) + " (" + (100.0 * read / Math.Max(1, scan.Length)).ToString("0.0") + "%)", false);
            add(dataPct.ToString(dataPct < 1 && dataPct > 0 ? "0.00" : "0.0") + "%", true); add("holds data", false);
            if (driveText.Length > 0) { add(hits.Count.ToString("N0") + (hits.Count >= DriveScan.MaxHits ? "+" : ""), true); add("text matches", false); }
            if (!done && rate > 0) { add(TimeText((scan.Length - read) / rate), true); add("left", false); }
            status.SetParts(parts.ToArray(), bold.ToArray());
            if (!done) return;

            tick.Stop();
            status.Running = false;
            if (scan.Error != null) Warn("The drive could not be read to the end: " + scan.Error);
            bool complete = read >= scan.Length;
            if (scan.DataSectors == 0)
            {
                results.EmptyTitle = complete ? "Wiped: every sector is zero" : "No data so far: every sector read was zero";
                results.EmptyText = complete ? "Nothing on this drive can be recovered by reading it." : "The scan stopped before the end of the drive.";
            }
            else
            {
                results.EmptyTitle = "Not wiped: " + dataPct.ToString(dataPct < 1 ? "0.00" : "0.0") + "% of the drive still holds data";
                results.EmptyText = "First data at " + Fmt.Size(scan.FirstData) + (driveText.Length > 0 ? ". Your text was not found." : ".") +
                                    "  (A crypto-erased drive can read as random data even though it is wiped.)";
            }
            results.ItemsChanged();
            results.SelectFirst();
            driveScan = null;
            cts.Dispose();
            cts = null;
            LayoutHero();
        }

        private static string TimeText(double seconds)
        {
            if (seconds < 90) return seconds.ToString("0.0") + " s";
            TimeSpan t = TimeSpan.FromSeconds(seconds);
            return t.TotalHours >= 1 ? ((int)t.TotalHours) + " h " + t.Minutes + " min" : t.Minutes + " min " + t.Seconds + " s";
        }

        // ------------------------------------------------------------------ dialogs

        private void ShowProblems()
        {
            Form f = new Form();
            f.Text = "Notes from the search";
            f.AutoScaleMode = AutoScaleMode.None;
            f.BackColor = Theme.Bg;
            f.ClientSize = new Size(Theme.S(900), Theme.S(440));
            f.StartPosition = FormStartPosition.CenterParent;
            f.HandleCreated += delegate { Theme.DarkTitleBar(f, Theme.Bg2); };
            TextBox t = new TextBox();
            t.Multiline = true;
            t.ReadOnly = true;
            t.ScrollBars = ScrollBars.Both;
            t.WordWrap = false;
            t.Dock = DockStyle.Fill;
            t.BorderStyle = BorderStyle.None;
            t.BackColor = Theme.Bg;
            t.ForeColor = Theme.Text;
            t.Font = Theme.Mono(13);
            StringBuilder sb = new StringBuilder();
            sb.Append("Files and folders that could not be read (locked by Windows, access denied), and documents with no\r\n");
            sb.Append("searchable text (scanned PDFs, password-protected PDFs).\r\n\r\n");
            foreach (string p in problems) sb.Append(p).Append("\r\n");
            if (problems.Count >= SearchJob.MaxErrorsKept) sb.Append("(only the first " + SearchJob.MaxErrorsKept + " are kept)\r\n");
            t.Text = sb.ToString();
            Panel pad = new Panel();
            pad.Dock = DockStyle.Fill;
            pad.Padding = new Padding(Theme.S(16));
            pad.BackColor = Theme.Bg;
            pad.Controls.Add(t);
            f.Controls.Add(pad);
            f.Shown += delegate { Theme.DarkScrollbars(t); t.Select(0, 0); };
            f.Show(this);
        }

        private void ShowAbout()
        {
            using (AboutForm f = new AboutForm()) f.ShowDialog(this);
        }

        private void Warn(string message)
        {
            MessageBox.Show(this, message, AppInfo.Name, MessageBoxButtons.OK, MessageBoxIcon.Information);
        }
    }

    /// <summary>The bottom bar: state dot, counts, notes link, and a running amber shimmer while searching.</summary>
    internal sealed class StatusBar : Control
    {
        private string[] parts = { "Ready" };
        private bool[] bold = { false };
        private bool running;
        private long notes;
        private float phase;
        private Rectangle notesRect;
        private readonly System.Windows.Forms.Timer anim = new System.Windows.Forms.Timer();
        public event EventHandler NotesClicked;

        public StatusBar()
        {
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            Height = Theme.S(38);
            anim.Interval = 30;
            anim.Tick += delegate { phase = (phase + 0.012f) % 1.4f; Invalidate(new Rectangle(0, 0, Width, Theme.S(3))); };
        }

        public bool Running
        {
            get { return running; }
            set { running = value; if (value) anim.Start(); else anim.Stop(); Invalidate(); }
        }

        public long Notes
        {
            get { return notes; }
            set { if (notes != value) { notes = value; Invalidate(); } }
        }

        public void SetParts(string[] p, bool[] b)
        {
            parts = p;
            bold = b;
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(Theme.Bg2)) g.FillRectangle(b, ClientRectangle);
            using (Pen p = new Pen(Theme.Line)) g.DrawLine(p, 0, 0, Width, 0);

            if (running)
            {
                // a soft amber band sliding along the top edge
                float w = Width * 0.4f;
                float bx = (phase - 0.4f) * Width;
                RectangleF band = new RectangleF(bx, 0, w, Theme.S(2));
                using (System.Drawing.Drawing2D.LinearGradientBrush lb = new System.Drawing.Drawing2D.LinearGradientBrush(
                    new RectangleF(bx - 1, 0, w + 2, Theme.S(2)), Color.Transparent, Color.Transparent, System.Drawing.Drawing2D.LinearGradientMode.Horizontal))
                {
                    System.Drawing.Drawing2D.ColorBlend cb = new System.Drawing.Drawing2D.ColorBlend();
                    cb.Colors = new[] { Color.Transparent, Theme.Amber, Theme.AmberHi, Color.Transparent };
                    cb.Positions = new[] { 0f, 0.35f, 0.65f, 1f };
                    lb.InterpolationColors = cb;
                    g.FillRectangle(lb, band);
                }
            }

            int x0 = Theme.S(22);
            int d = Theme.S(7);
            Color dot = running ? Theme.Amber : Theme.Green;
            Theme.FillRound(g, Theme.Alpha(dot, 60), new Rectangle(x0 - Theme.S(3), Height / 2 - d / 2 - Theme.S(3), d + Theme.S(6), d + Theme.S(6)), d);
            Theme.FillRound(g, dot, new Rectangle(x0, Height / 2 - d / 2, d, d), d / 2f);
            int x = x0 + d + Theme.S(12);

            string noteText = notes == 0 ? "" : (notes == 1 ? "1 note" : notes.ToString("N0") + " notes");
            int noteW = noteText.Length > 0 ? Theme.TextWidth(noteText, Theme.Ui(12.5f)) : 0;
            int limit = Width - Theme.S(22) - noteW - Theme.S(16);
            for (int i = 0; i < parts.Length && x < limit; i++)
            {
                Font f = bold[i] ? Theme.UiSemibold(12.5f) : Theme.Ui(12.5f);
                bool lastFree = i == parts.Length - 1 && !bold[i] && running;
                int w = lastFree ? limit - x : Theme.Advance(parts[i], f);
                if (i > 0 && bold[i]) x += Theme.S(16);
                else if (i > 0) x += Theme.S(5);
                TextRenderer.DrawText(g, parts[i], f, new Rectangle(x, 0, Math.Min(w + 2, limit - x), Height),
                    bold[i] ? Theme.Text : (lastFree ? Theme.Faint : Theme.Dim), Theme.Left | (lastFree ? TextFormatFlags.PathEllipsis : 0));
                x += w;
            }
            notesRect = Rectangle.Empty;
            if (noteW > 0)
            {
                notesRect = new Rectangle(Width - Theme.S(22) - noteW, 0, noteW, Height);
                TextRenderer.DrawText(g, noteText, Theme.Ui(12.5f), notesRect, Theme.AmberHi, Theme.Right);
            }
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            base.OnMouseMove(e);
            Cursor = notesRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default;
        }

        protected override void OnMouseClick(MouseEventArgs e)
        {
            base.OnMouseClick(e);
            if (notesRect.Contains(e.Location) && NotesClicked != null) NotesClicked(this, EventArgs.Empty);
        }
    }

    /// <summary>About: credits and link, in the same skin.</summary>
    internal sealed class AboutForm : Form
    {
        private readonly Image icon = Skin.AppIcon();
        private readonly Rectangle linkRect;

        public AboutForm()
        {
            AutoScaleMode = AutoScaleMode.None;
            Text = "About " + AppInfo.Name;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(520), Theme.S(300));
            BackColor = Theme.Bg;
            Backdrop bg = new Backdrop();
            bg.Hero = true;
            bg.Dock = DockStyle.Fill;
            bg.Paint += PaintBody;
            bg.MouseMove += delegate(object s, MouseEventArgs e) { bg.Cursor = linkRect.Contains(e.Location) ? Cursors.Hand : Cursors.Default; };
            bg.MouseClick += delegate(object s, MouseEventArgs e) { if (linkRect.Contains(e.Location)) { try { Shell.OpenDefault(AppInfo.Url); } catch (Exception) { } } };
            SkinButton ok = new SkinButton("OK", "", ButtonKind.Primary);
            ok.DialogResult = DialogResult.OK;
            int w = Theme.S(110) + 2 * ok.ShadowPad;
            ok.Bounds = new Rectangle(ClientSize.Width - Theme.S(26) - w + ok.ShadowPad, ClientSize.Height - Theme.S(26) - Theme.S(44) - ok.ShadowPad, w, Theme.S(44) + 2 * ok.ShadowPad);
            bg.Controls.Add(ok);
            Controls.Add(bg);
            AcceptButton = ok;
            CancelButton = ok;
            linkRect = new Rectangle(Theme.S(28), Theme.S(196), Theme.TextWidth(AppInfo.Url, Theme.Ui(14)) + 4, Theme.S(24));
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this, Color.FromArgb(17, 26, 36));
        }

        private void PaintBody(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int L = Theme.S(28);
            if (icon != null)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon, new Rectangle(L, Theme.S(26), Theme.S(56), Theme.S(56)));
            }
            int x = L + Theme.S(72);
            Font tf = Theme.Display(24);
            int w1 = Theme.Advance("Find ", tf);
            TextRenderer.DrawText(g, "Find", tf, new Rectangle(x, Theme.S(26), w1 + 4, Theme.S(34)), Theme.Text, Theme.Left);
            TextRenderer.DrawText(g, "Anything", tf, new Rectangle(x + w1, Theme.S(26), Theme.S(200), Theme.S(34)), Theme.AmberHi, Theme.Left);
            TextRenderer.DrawText(g, "Version " + AppInfo.Version, Theme.Ui(13), new Rectangle(x, Theme.S(60), Theme.S(300), Theme.S(22)), Theme.Dim, Theme.Left);
            TextRenderer.DrawText(g, "Find text inside any file: plain text, PDF, Office, Unicode, and files of any size.",
                Theme.Ui(14), new Rectangle(L, Theme.S(104), ClientSize.Width - 2 * L, Theme.S(44)), Theme.Text, TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            TextRenderer.DrawText(g, "Created by " + AppInfo.Author + ".  Free and open source under the " + AppInfo.License + ".",
                Theme.Ui(14), new Rectangle(L, Theme.S(156), ClientSize.Width - 2 * L, Theme.S(24)), Theme.Dim, Theme.Left);
            TextRenderer.DrawText(g, AppInfo.Url, Theme.Ui(14, FontStyle.Underline), linkRect, Theme.AmberHi, Theme.Left);
        }
    }
}
