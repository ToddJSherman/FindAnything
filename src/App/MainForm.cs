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
    internal sealed partial class MainForm : Form
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
        private readonly Chip kFiles = new Chip("Credential files", "\uE8D7", true);
        private readonly Chip showValues = new Chip("Show values", "\uE7B3", true);
        private readonly SkinButton pickSamples = new SkinButton("Pick samples", "", ButtonKind.Secondary);
        private readonly Chip clipCache = new Chip("Remember fingerprints", "\uE74E", true);
        private readonly Chip weaker = new Chip("Show weaker matches", "\uE890", true);
        private readonly Slider cpuSlider = new Slider();
        private Rectangle cpuLabelRect, cpuValueRect;
        private readonly Tabs modeTabs = new Tabs(new[] { "Search text", "Sensitive data", "Similar pictures", "Deleted files", "Drive check" }, new[] { Theme.GSearch, "\uE83D", "\uE8B9", "\uE74D", "\uEDA2" });
        private int mode;                       // 0 search text, 1 sensitive data, 2 drive check, 3 similar pictures, 4 deleted files
        private PictureSearch picSearch;
        private Task picTask;
        private Clip clip;                      // loaded on the first similar-pictures search, kept for the next
        public int StartMode;
        public string StartSample;              // --similar <picture>: search for pictures like it at once
        private string lookTextFiles = "";
        private readonly string[] findTexts = new string[5];   // each tab keeps its own find box (a picture path stays in Similar pictures)
        private List<DiskInfo> disks;
        private DiskInfo selectedDisk;
        private DriveScan driveScan;
        private int driveHitsShown;
        private readonly SkinField types = new SkinField(FieldStyle.Mini, null);
        private readonly SkinField excludeTypes = new SkinField(FieldStyle.Mini, null);
        private readonly SkinField skipFolders = new SkinField(FieldStyle.Mini, null);
        private readonly SkinField maxMb = new SkinField(FieldStyle.Mini, null);
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
            results.DragRowsOut += delegate { DragFilesOut(); };
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
            Tip(cpuSlider, "How hard a search works the processor: how many files (and pictures for OCR or Similar pictures) are read at once.\n" +
                           "Left: one at a time on one CPU thread - cool and quiet, slower. Right: every thread - fastest, and the fans will know.\n" +
                           "On a spinning hard drive, 1-2 is often fastest. Takes effect at once, even during a search, and always runs at low priority.");
            cpuSlider.Scrolling += delegate { OnCpuSlider(); };
            cpuSlider.Committed += delegate { OnCpuSlider(); settings.Set("CpuUse", CpuWorkers); };
            ocr.CheckedChanged += delegate { LayoutHero(); };
            SetUpDeletedChips();
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
            foreach (SkinField f in new[] { maxMb })
                f.Box.KeyPress += delegate(object s, KeyPressEventArgs e) { if (!char.IsControl(e.KeyChar) && !char.IsDigit(e.KeyChar)) e.Handled = true; };
            foreach (SkinField f in new[] { types, excludeTypes, skipFolders, maxMb })
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
            Tip(kFiles, "Files that are credentials by what they are: SSH and PuTTY keys, .env files, Git and AWS credentials,\n.npmrc / .pypirc logins, certificates with private keys, VPN and Remote Desktop files, password vaults, crypto wallets.");
            Tip(showValues, "Off: secrets are masked on screen (ab\u2022\u2022\u2022\u2022yz), safe for screenshots and onlookers. Copy the key still copies the real value.");
            showValues.CheckedChanged += delegate { results.ShowSecrets = showValues.Checked; results.Invalidate(); };

            hero.Controls.AddRange(new Control[]
            {
                findField, lookField, browseButton, aboutButton, modeTabs,
                matchCase, wholeWord, wildcards, loose, inContents, inNames, insideDocs, utf16, ocr, onlyPics, ocrCache,
                clipCache, weaker, cpuSlider, delRecycle, delOnlyGood, kProduct, kLabeled, kPasswords, kTokens, kFinancial, kFiles, showValues, filtersChip,
                types, excludeTypes, skipFolders, maxMb
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
                case 1: return new[] { new[] { kPasswords, kTokens, kFiles, kFinancial, kProduct, kLabeled }, new[] { insideDocs, ocr, onlyPics, ocrCache }, new[] { showValues } };
                case 2: return new[] { new[] { matchCase, utf16 } };
                case 3: return new[] { new[] { weaker }, new[] { clipCache } };
                case 4: return new[] { new[] { delOnlyGood, delRecycle } };
                default: return new[] { new[] { matchCase, wholeWord, wildcards, loose }, new[] { inContents, inNames }, new[] { insideDocs, utf16, ocr, onlyPics, ocrCache } };
            }
        }

        private string ButtonText
        {
            get
            {
                if (Busy) return "Stop";
                if (mode == 1) return "Sweep";
                if (mode == 2) return RawDisk.IsAdmin ? "Scan drive" : "Run as admin";
                if (mode == 3) return "Find pictures";
                if (mode == 4) return RawDisk.IsAdmin ? "Find deleted" : "Run as admin";
                return "Search";
            }
        }

        /// <summary>Places everything to the mockup's measurements (CSS px, scaled).</summary>
        private void LayoutHero()
        {
            int W = hero.Width > 0 ? hero.Width : ClientSize.Width;
            int L = Theme.S(22), R = W - Theme.S(22);
            int y = Theme.S(18) + Theme.S(34) + Theme.S(14);

            // About and the mode tabs, top right; in a narrow window the tabs drop their icons, then About its word
            int titleRight = L + Theme.S(34) + Theme.S(12) + Theme.Advance("Find Anything", Theme.Display(21)) + Theme.S(16);
            modeTabs.ShowIcons = true;
            aboutButton.Text = "About";
            if (R - aboutButton.PreferredWidth(30) - modeTabs.PreferredWidth() < titleRight) modeTabs.ShowIcons = false;
            if (R - aboutButton.PreferredWidth(30) - modeTabs.PreferredWidth() < titleRight) aboutButton.Text = "";
            int aw = aboutButton.PreferredWidth(30);
            aboutButton.Bounds = new Rectangle(R - aw + Theme.S(10), Theme.S(18), aw, Theme.S(34));
            int tw = modeTabs.PreferredWidth();
            modeTabs.Invalidate();
            modeTabs.Bounds = new Rectangle(aboutButton.Left - Theme.S(10) - tw, Theme.S(16), tw, Theme.S(38));

            // find + main button
            searchButton.Text = ButtonText;
            searchButton.Icon = Busy ? Theme.GStop : mode == 1 ? "\uE83D" : mode == 2 ? "\uEDA2" : mode == 3 ? "\uE8B9" : mode == 4 ? "\uE74D" : Theme.GSearch;
            int bw = 0;
            foreach (string label in new[] { "Search", "Sweep", "Scan drive", "Run as admin", "Find pictures", "Find deleted", "Stop" })
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
            foreach (Chip c in new[] { matchCase, wholeWord, wildcards, loose, inContents, inNames, insideDocs, utf16, ocr, onlyPics, ocrCache, clipCache, weaker, kProduct, kLabeled, kPasswords, kTokens, kFinancial, kFiles, showValues, delRecycle, delOnlyGood })
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
            // CPU use, for the heavy work (Similar pictures; OCR when it is on): right end of the last chip row, or a
            // row of its own when that row is full
            bool cpu = mode != 2 && mode != 4;
            cpuSlider.Visible = cpu;
            cpuLabelRect = cpuValueRect = Rectangle.Empty;
            if (cpu)
            {
                int vw = Theme.S(112), sw = Theme.S(170), lw = Theme.Advance("CPU use", Theme.UiSemibold(13)) + Theme.S(4);
                int gw = lw + Theme.S(6) + sw + Theme.S(8) + vw;
                if (x + Theme.S(16) + gw > limit) { y += Theme.S(40); limit = R; }
                int right = mode == 3 || limit != R ? limit : R;
                cpuValueRect = new Rectangle(right - vw, y, vw, Theme.S(32));
                cpuSlider.Bounds = new Rectangle(cpuValueRect.Left - Theme.S(8) - sw, y, sw, Theme.S(32));
                cpuLabelRect = new Rectangle(cpuSlider.Left - Theme.S(6) - lw, y, lw, Theme.S(32));
            }
            y += Theme.S(32);

            // filters
            miniLabels.Clear();
            bool show = filtersChip.Checked && filters;
            foreach (SkinField f in new[] { types, excludeTypes, skipFolders, maxMb }) f.Visible = show;
            if (show)
            {
                y += Theme.S(12);
                int labelH = Theme.S(18), boxH = Theme.S(34), gap = Theme.S(10);
                int fixedW = Theme.S(130) + 3 * gap;
                int flex = Math.Max(Theme.S(300), R - L - fixedW);
                int[] widths = { (int)(flex * 0.38), (int)(flex * 0.25), 0, Theme.S(130) };
                widths[2] = flex - widths[0] - widths[1];
                string[] labels = { "File types", "Except", "Skip folders", "Max size (MB)" };
                SkinField[] boxes = { types, excludeTypes, skipFolders, maxMb };
                x = L;
                for (int i = 0; i < boxes.Length; i++)
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

        /// <summary>Pictures read at once (each on one CPU thread), from the CPU use slider: 1 .. every logical processor.</summary>
        private int CpuWorkers
        {
            get { int n = Math.Max(1, Environment.ProcessorCount); return 1 + (int)Math.Round(cpuSlider.Value * (n - 1)); }
            set { int n = Math.Max(1, Environment.ProcessorCount); cpuSlider.Value = n == 1 ? 1 : (Math.Max(1, Math.Min(n, value)) - 1) / (double)(n - 1); }
        }

        private void OnCpuSlider()
        {
            if (picSearch != null) picSearch.Workers = CpuWorkers;
            if (job != null) job.Workers = CpuWorkers;
            Ocr.MaxAtOnce = CpuWorkers;
            hero.Invalidate(cpuValueRect);
        }

        // The tabs read Search text, Sensitive data, Similar pictures, Deleted files, Drive check; the mode numbers are
        // older than that order (Drive check is mode 2).
        private static readonly int[] tabModes = { 0, 1, 3, 4, 2 };
        private static int TabToMode(int i) { return tabModes[i]; }
        private static int ModeToTab(int m) { return Array.IndexOf(tabModes, m); }

        private bool Busy { get { return job != null || driveScan != null || picSearch != null || delScan != null; } }

        /// <summary>Switches between Search text, Find keys, Similar pictures and Drive check.</summary>
        private void SetMode(int m)
        {
            if (Busy) { modeTabs.SelectedIndex = ModeToTab(mode); return; }
            if (mode != 2) lookTextFiles = lookField.Text;
            findTexts[mode] = findField.Text;
            mode = m;
            findField.Text = findTexts[mode] ?? "";
            allPictures.Clear();
            if (modeTabs.SelectedIndex != ModeToTab(m)) modeTabs.SelectedIndex = ModeToTab(m);
            results.Similar = mode == 3;
            results.Deleted = mode == 4;
            results.EmptyImage = mode == 3 ? Skin.Art("grandma-256") : null;
            results.FadeInEmpty();
            hits.Clear();
            results.ResetSelection();
            lookField.Box.ReadOnly = mode == 2;
            lookField.Icon = mode == 2 ? "\uEDA2" : Theme.GFolder;
            switch (mode)
            {
                case 1:
                    findField.SetPlaceholder("Optional: only findings mentioning... (AWS, Office, bank)");
                    lookField.Text = lookTextFiles;
                    results.EmptyTitle = "Sweep for sensitive data lying around in plain files";
                    results.EmptyText = "Passwords, tokens, private keys, credential files, card numbers and licence keys: how bad each one is, and what to do.";
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
                case 4:
                    findField.SetPlaceholder("Optional: part of the name (roxy, .jpg, invoice*)");
                    lookField.Text = lookTextFiles;
                    results.EmptyTitle = RawDisk.IsAdmin ? "Find deleted files and get them back" : "Deleted files needs administrator rights";
                    results.EmptyText = RawDisk.IsAdmin
                        ? "Reads the drive's file table directly (read-only). Pick a drive, or a folder to look only in it."
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
            ToolStripMenuItem copyFiles = new ToolStripMenuItem("Copy files (paste them in Explorer)", null, delegate { CopyFilesToClipboard(); });
            copyFiles.ShortcutKeyDisplayString = "Ctrl+Shift+C";
            ToolStripMenuItem copyAgain = new ToolStripMenuItem("Copy to ...", null, delegate { CopyOrMoveSelected(sessionFolder, false); });
            copyAgain.Font = Theme.UiSemibold(14);
            ToolStripMenuItem copyTo = new ToolStripMenuItem("Copy to a folder...", null, delegate { CopyOrMoveSelected(null, false); });
            ToolStripMenuItem moveTo = new ToolStripMenuItem("Move to a folder...", null, delegate { CopyOrMoveSelected(null, true); });
            ToolStripMenuItem selectAll = new ToolStripMenuItem("Select all", null, delegate { results.SelectAll(); });
            ToolStripMenuItem recoverAgain = new ToolStripMenuItem("Recover to ...", null, delegate { RecoverSelected(recoveryFolder); });
            recoverAgain.Font = Theme.UiSemibold(14);
            ToolStripMenuItem recoverTo = new ToolStripMenuItem("Recover to a folder...", null, delegate { RecoverSelected(null); });
            selectAll.ShortcutKeyDisplayString = "Ctrl+A";
            m.Items.AddRange(new ToolStripItem[] { view, copyKey, recoverAgain, recoverTo, def, npp, similar, new ToolStripSeparator(),
                copyAgain, copyTo, moveTo, copyFiles, new ToolStripSeparator(), folder, copy, selectAll, new ToolStripSeparator(), export });
            m.Opening += delegate
            {
                bool any = results.SelectedItems.Count > 0;
                int n = RealFiles(results.SelectedItems).Count;
                string count = n == 1 ? "" : " (" + n.ToString("N0") + ")";
                copyAgain.Visible = sessionFolder != null && n > 0;
                if (sessionFolder != null) copyAgain.Text = "Copy" + count + " to \"" + Path.GetFileName(sessionFolder.TrimEnd('\\')) + "\"";
                copyTo.Text = "Copy" + count + " to a folder...";
                moveTo.Text = "Move" + count + " to a folder...";
                copyTo.Visible = moveTo.Visible = copyFiles.Visible = n > 0;
                int del = 0;
                foreach (FileHit dh in results.SelectedItems) if (IsDeletedRow(dh)) del++;
                string dcount = del == 1 ? "" : " (" + del.ToString("N0") + ")";
                recoverTo.Visible = del > 0;
                recoverTo.Text = "Recover" + dcount + " to a folder...";
                recoverAgain.Visible = del > 0 && recoveryFolder != null;
                if (recoveryFolder != null) recoverAgain.Text = "Recover" + dcount + " to \"" + Path.GetFileName(recoveryFolder.TrimEnd('\\')) + "\"";
                if (mode == 4) { copyAgain.Visible = copyTo.Visible = moveTo.Visible = copyFiles.Visible = false; view.Text = "Preview"; }
                else view.Text = "Open in viewer";
                view.Enabled = def.Enabled = folder.Enabled = copy.Enabled = any;
                npp.Visible = Shell.NotepadPlusPlus != null && mode != 2;
                FileHit fh = results.FocusedItem;
                copyKey.Visible = fh != null && fh.Secret != null;
                def.Visible = folder.Visible = mode != 2 && mode != 4;
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
            delRecycle.Checked = settings.GetBool("Deleted.RecycleBin", true);
            delOnlyGood.Checked = settings.GetBool("Deleted.OnlyRecoverable", true);
            // default: a quarter of the CPU threads (2 on an 8-thread laptop, 6 on a 24-thread desktop)
            CpuWorkers = (int)settings.GetLong("CpuUse", settings.GetLong("PictureWorkers", Math.Max(1, Environment.ProcessorCount / 4)));
            settings.Remove("PictureWorkers");
            Ocr.MaxAtOnce = CpuWorkers;
            Clip.UseCache = clipCache.Checked;
            if (!clipCache.Checked) Clip.ClearCache();
            Ocr.UseCache = ocrCache.Checked;
            if (!ocrCache.Checked) Ocr.ClearCache();   // off means nothing stored, including anything from before
            kProduct.Checked = settings.GetBool("Keys.Product", true);
            kLabeled.Checked = settings.GetBool("Keys.Labeled", true);
            kPasswords.Checked = settings.GetBool("Keys.Passwords", true);
            kTokens.Checked = settings.GetBool("Keys.Tokens", true);
            kFinancial.Checked = settings.GetBool("Keys.Financial", true);
            kFiles.Checked = settings.GetBool("Keys.Files", true);
            wildcards.Checked = settings.GetBool("Wildcards", true);
            wholeWord.Checked = settings.GetBool("WholeWord", false);
            inContents.Checked = settings.GetBool("InContents", true);
            inNames.Checked = settings.GetBool("InNames", true);
            insideDocs.Checked = settings.GetBool("InsideDocs", true);
            utf16.Checked = settings.GetBool("Utf16", true);
            skipFolders.Text = settings.Get("SkipFolders", "System Volume Information;$Recycle.Bin");
            maxMb.Text = Math.Max(0, settings.GetLong("MaxMB", 0)).ToString();
            settings.Remove("Threads");   // the CPU use slider took over from the Threads box
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
            settings.Set("Deleted.RecycleBin", delRecycle.Checked);
            settings.Set("Deleted.OnlyRecoverable", delOnlyGood.Checked);
            settings.Set("CpuUse", CpuWorkers);
            settings.Set("Keys.Product", kProduct.Checked);
            settings.Set("Keys.Labeled", kLabeled.Checked);
            settings.Set("Keys.Passwords", kPasswords.Checked);
            settings.Set("Keys.Tokens", kTokens.Checked);
            settings.Set("Keys.Financial", kFinancial.Checked);
            settings.Set("Keys.Files", kFiles.Checked);
            settings.Set("Wildcards", wildcards.Checked);
            settings.Set("WholeWord", wholeWord.Checked);
            settings.Set("InContents", inContents.Checked);
            settings.Set("InNames", inNames.Checked);
            settings.Set("InsideDocs", insideDocs.Checked);
            settings.Set("Utf16", utf16.Checked);
            settings.Set("SkipFolders", skipFolders.Text);
            settings.Set("MaxMB", ParseLong(maxMb.Text, 0));
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
                samples.Clear();
                samples.Add(StartSample);
                UpdateSampleButton();
                BeginInvoke((MethodInvoker)StartPictureSearch);
            }
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (cts != null) cts.Cancel();
            CleanUpPreviews();
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
            if (draggingOut) return;   // our own rows dropped back on the window
            string[] files = e.Data.GetData(DataFormats.FileDrop) as string[];
            if (files == null || files.Length == 0) return;
            // Similar pictures: dropped pictures are the samples, dropped folders are where to look
            List<string> pics = new List<string>(), other = new List<string>();
            foreach (string p in files) (mode == 3 && File.Exists(p) && Ocr.IsImage(p) ? pics : other).Add(p);
            if (pics.Count > 0) { samples.Clear(); samples.AddRange(pics); UpdateSampleButton(); }
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
            if (mode == 4) { StartDeletedSearch(); return; }
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
            s.Threads = CpuWorkers;
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
                s.Secrets.CredentialFiles = kFiles.Checked;
                if (!(kProduct.Checked || kLabeled.Checked || kPasswords.Checked || kTokens.Checked || kFinancial.Checked || kFiles.Checked))
                { Warn("Turn on at least one kind of sensitive data to look for."); return; }
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
            if (delScan != null) { OnDeletedTick(); return; }
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
            if (mode == 1)
            {
                int[] sev = new int[4];
                foreach (FileHit x in hits) if (x.Secret != null) sev[Math.Max(0, Math.Min(3, x.Secret.Severity))]++;
                add(sev[3].ToString("N0"), true); add("high", false);
                add(sev[2].ToString("N0"), true); add("medium", false);
                add(sev[1].ToString("N0"), true); add("low", false);
            }
            else { add(hits.Count.ToString("N0"), true); add("found", false); }
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
                // Results arrive in whatever order the threads finish; settle them into path order (worst first in a sweep).
                hits.Sort(delegate(FileHit a, FileHit b)
                {
                    if (mode == 1 && a.Secret != null && b.Secret != null && a.Secret.Severity != b.Secret.Severity) return b.Secret.Severity.CompareTo(a.Secret.Severity);
                    return string.Compare(a.Path, b.Path, StringComparison.CurrentCultureIgnoreCase);
                });
            }
            results.EmptyTitle = mode == 1 ? "No sensitive data found" : "Nothing found";
            results.EmptyText = mode == 1 ? "Nothing that looks like a password, token, key, card number or credential file here. Try Pictures (OCR), PDF & Office, or another folder."
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
            if (e.KeyCode == Keys.C && e.Control && e.Shift) { e.Handled = true; CopyFilesToClipboard(); }
            else if (e.KeyCode == Keys.C && e.Control && !e.Shift && mode == 1) { e.Handled = true; CopyKeys(); }
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
            if (IsDeletedRow(h)) { PreviewDeleted(h); return; }
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

        // ------------------------------------------------------------------ bulk: copy, move, drag out

        private string sessionFolder;   // the last folder files were copied or moved to (this session only)
        private bool draggingOut;

        /// <summary>Results that are real files on disk (not drive-check offsets).</summary>
        private static List<FileHit> RealFiles(IList<FileHit> items)
        {
            List<FileHit> l = new List<FileHit>();
            foreach (FileHit h in items) if (h.Source != "DISK" && h.IoPath != null && File.Exists(h.IoPath)) l.Add(h);
            return l;
        }

        /// <summary>Copies or moves the selected files to a folder (asked for when null), remembered for the session.</summary>
        private void CopyOrMoveSelected(string folder, bool move)
        {
            List<FileHit> items = RealFiles(results.SelectedItems);
            if (items.Count == 0) return;
            if (folder == null)
            {
                folder = FolderPicker.Pick(this, sessionFolder ?? Environment.GetFolderPath(Environment.SpecialFolder.MyPictures));
                if (folder == null) return;
            }
            if (!Directory.Exists(LongPath.Add(folder))) { Warn("Can't find \"" + folder + "\"."); return; }
            sessionFolder = folder;
            List<string> problemsNow = new List<string>();
            string title = (move ? "Moving " : "Copying ") + (items.Count == 1 ? "1 file" : items.Count.ToString("N0") + " files");
            ProgressDialog.Run(this, title, folder, delegate(Action<string, double> report, CancellationToken ct)
            {
                return FileOps.CopyOrMove(items, folder, move, report, ct, problemsNow);
            });
            if (problemsNow.Count > 0) { problems.AddRange(problemsNow); status.Notes += problemsNow.Count; }
            if (move) results.Invalidate();   // moved rows now show their new folder
        }

        private void CopyFilesToClipboard()
        {
            List<FileHit> items = RealFiles(results.SelectedItems);
            if (items.Count == 0) return;
            System.Collections.Specialized.StringCollection files = new System.Collections.Specialized.StringCollection();
            foreach (FileHit h in items) files.Add(h.Path);
            try { Clipboard.SetFileDropList(files); } catch (Exception ex) { Warn(ex.Message); }
        }

        /// <summary>Drag the selected files out to Explorer, an email, a chat window...</summary>
        private void DragFilesOut()
        {
            List<FileHit> items = RealFiles(results.SelectedItems);
            if (items.Count == 0) return;
            string[] paths = new string[items.Count];
            for (int i = 0; i < items.Count; i++) paths[i] = items[i].Path;
            DataObject data = new DataObject(DataFormats.FileDrop, paths);
            draggingOut = true;
            try { results.DoDragDrop(data, DragDropEffects.Copy); }
            finally { draggingOut = false; }
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
                sb.Append("Path,Name,Folder,Hits,Line,Found in,First match,Size,Modified,Severity,What to do\r\n");
                foreach (FileHit h in hits)
                {
                    sb.Append(Csv(h.Path)).Append(',').Append(Csv(h.Name)).Append(',').Append(Csv(h.Folder)).Append(',')
                      .Append(h.HitsText).Append(',').Append(h.ContentHits > 0 ? h.FirstLine.ToString() : "").Append(',')
                      .Append(Csv(h.MatchText)).Append(',').Append(Csv(ExportLine(h))).Append(',')
                      .Append(h.Size).Append(',').Append(h.Modified.ToString("yyyy-MM-dd HH:mm:ss")).Append(',')
                      .Append(h.Secret == null ? "" : h.Secret.Severity >= 3 ? "High" : h.Secret.Severity == 2 ? "Medium" : "Low").Append(',')
                      .Append(Csv(h.Secret == null ? "" : h.Secret.Advice ?? "")).Append("\r\n");
                }
                try { File.WriteAllText(d.FileName, sb.ToString(), new UTF8Encoding(true)); }
                catch (Exception ex) { Warn(ex.Message); }
            }
        }

        /// <summary>The line for a CSV export: secrets stay masked unless Show values is on (a report should not be a new leak).</summary>
        private string ExportLine(FileHit h)
        {
            string line = h.Preview ?? "";
            SecretFinding s = h.Secret;
            if (s == null || showValues.Checked || s.Kind == SecretKind.CredentialFile) return line;
            int i = line.IndexOf(s.Value, StringComparison.Ordinal);
            return i < 0 ? line : line.Substring(0, i) + ResultsView.Mask(s.Value) + line.Substring(i + s.Value.Length);
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

        /// <summary>"Pick samples", or how many sample pictures there are; the find box then takes optional words.</summary>
        private void UpdateSampleButton()
        {
            pickSamples.Text = samples.Count == 0 ? "Pick samples" : samples.Count == 1 ? "1 sample" : samples.Count + " samples";
            pickSamples.Invalidate();
            List<string> names = new List<string>();
            foreach (string p in samples) names.Add(Path.GetFileName(p));
            Tip(pickSamples, samples.Count == 0
                ? "Choose one or more pictures to match. Several pictures of the same thing (a pet, a person, a place) find it more reliably.\nYou can also drop pictures on the window."
                : "Samples: " + string.Join(", ", names.ToArray()) + "\nClick to add, change or clear them. Words in the search box narrow the look-alikes down (\"in a costume\").");
            if (mode == 3)
                findField.SetPlaceholder(samples.Count == 0 ? "Describe it (a dog in a costume, the beach) or pick samples"
                                                            : "Optional: words to narrow the look-alikes down (in a costume, at the beach)");
        }

        /// <summary>The sample button: pick pictures, or, once there are some, a small menu to add, replace or clear them.</summary>
        private void PickSamples()
        {
            if (samples.Count == 0) { PickSampleFiles(false); return; }
            ContextMenuStrip m = DarkMenuRenderer.Menu();
            foreach (string p in samples)
            {
                ToolStripMenuItem it = new ToolStripMenuItem(Path.GetFileName(p));
                it.Enabled = false;
                m.Items.Add(it);
            }
            m.Items.Add(new ToolStripSeparator());
            m.Items.Add("Add more pictures...", null, delegate { PickSampleFiles(true); });
            m.Items.Add("Pick different pictures...", null, delegate { PickSampleFiles(false); });
            m.Items.Add("Clear the samples", null, delegate { samples.Clear(); UpdateSampleButton(); findField.Box.Focus(); });
            m.Show(pickSamples, new Point(pickSamples.ShadowPad, pickSamples.Height - pickSamples.ShadowPad));
        }

        private void PickSampleFiles(bool add)
        {
            using (OpenFileDialog d = new OpenFileDialog())
            {
                d.Title = "Sample pictures to match";
                d.Multiselect = true;
                d.Filter = "Pictures|" + Ocr.PictureMasks + "|All files|*.*";
                if (d.ShowDialog(this) != DialogResult.OK) return;
                if (!add) samples.Clear();
                foreach (string p in d.FileNames) if (!samples.Contains(p)) samples.Add(p);
                UpdateSampleButton();
                findField.Box.Focus();
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
            samples.Clear();
            samples.AddRange(pics);
            findField.Text = "";
            UpdateSampleButton();
            StartPictureSearch();
        }

        private readonly List<string> samples = new List<string>();   // Similar pictures: the sample pictures

        /// <summary>Picture paths typed or pasted into the find box (separated by ;) become samples.</summary>
        private void TakePastedSamples()
        {
            List<string> pics = new List<string>();
            foreach (string part in findField.Text.Split(';'))
            {
                string p = part.Trim().Trim('"');
                if (p.Length == 0) continue;
                if (!Ocr.IsImage(p) || !File.Exists(LongPath.Add(p))) return;
                pics.Add(p);
            }
            if (pics.Count == 0) return;
            samples.Clear();
            samples.AddRange(pics);
            findField.Text = "";
            UpdateSampleButton();
        }

        private void StartPictureSearch()
        {
            if (Busy) return;
            List<string> roots = Roots();
            if (roots.Count == 0) { Warn("Pick a folder or drive to look in."); lookField.Box.Focus(); return; }
            foreach (string r in roots)
                if (!Directory.Exists(LongPath.Add(r)) && !File.Exists(LongPath.Add(r))) { Warn("Can't find \"" + r + "\"."); lookField.Box.Focus(); return; }
            TakePastedSamples();
            string description = findField.Text.Trim();
            if (samples.Count == 0 && description.Length == 0) { Warn("Pick a sample picture (or drop one here), or describe what is in the pictures."); return; }
            List<string> sampleList = new List<string>(samples);
            if (!AiPack.Ensure(this)) return;
            SaveSettings();

            hits.Clear();
            allPictures.Clear();
            problems.Clear();
            picKind = sampleList.Count == 0 ? PicWords : description.Length == 0 ? PicLook : PicBoth;
            int kind = picKind;
            results.SortColumn = -1;
            results.ScoreLow = results.ScoreHigh = 0;
            results.EmptyTitle = clip == null ? "Loading the picture model..." : "Looking at pictures...";
            results.EmptyText = "Pictures that look alike appear here as they are found.";
            results.ResetSelection();

            List<string> skip = sampleList;
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
                    // Both: look-alikes first (DINOv2), then ranked by the words (CLIP).
                    List<float[]> looks = new List<float[]>();
                    foreach (string s in sampleList) looks.Add(loaded.EmbedLook(LongPath.Add(s)));
                    if (kind == PicLook) ps.Run(loaded.EmbedLook, looks, workers, token);
                    else if (kind == PicWords) ps.Run(loaded.EmbedImage, new List<float[]> { loaded.EmbedText(description) }, workers, token);
                    else ps.Run(PictureSearch.LookThenWords(loaded, looks, loaded.EmbedText(description)), workers, token);
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
        private const int PicLook = 0, PicWords = 1, PicBoth = 2;
        private int picKind;

        /// <summary>
        /// The score a picture needs to be listed. Samples: DINOv2's measured cut-off. Descriptions: CLIP's text scores
        /// are not calibrated across sentences, so the top band (within 0.05 of the best, and at least 0.22).
        /// </summary>
        private float PictureCutoff()
        {
            if (picKind != PicWords) return Clip.LookAlike;
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
            foreach (FileHit x in allPictures) if (weaker.Checked || (picKind == PicBoth ? !x.Weak : x.Score >= cut)) hits.Add(x);
            RankPictures();
            if (keep != null && hadSelection && hits.Contains(keep)) results.Select(keep);
            results.ItemsChanged();
        }

        private void AddPictureCounts(Action<string, bool> add)
        {
            if (weaker.Checked) { add(hits.Count.ToString("N0"), true); add(hits.Count == 1 ? "picture ranked" : "pictures ranked", false); return; }
            add(hits.Count.ToString("N0"), true); add(picKind == PicWords ? "match" + (hits.Count == 1 ? "" : "es") : "alike", false);
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
                results.EmptyTitle = picKind == PicWords ? "Nothing here matches that description" : "Nothing here looks like your sample";
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
            // the bar spans the alike pictures only (rows that do not look alike show "not alike" instead)
            int lastAlike = byScore.Count - 1;
            while (lastAlike > 0 && byScore[lastAlike].Weak) lastAlike--;
            if (byScore.Count > 0) { results.ScoreHigh = byScore[0].Score; results.ScoreLow = byScore[Math.Max(0, lastAlike)].Score; }
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
                Process.Start(new ProcessStartInfo(Application.ExecutablePath, mode == 4 ? "--deleted" : "--drive-check") { UseShellExecute = true, Verb = "runas" });
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
