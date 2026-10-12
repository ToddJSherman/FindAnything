using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

namespace FindAnything.Setup
{
    // FindAnything-Setup.exe               installer window
    // FindAnything-Setup.exe /silent       install with the default options, no window
    // Uninstall.exe /uninstall [/silent]   (the installed copy of this exe) remove everything
    //
    // Installs for the current user only (no admin): %LOCALAPPDATA%\Programs\Find Anything
    internal static class Program
    {
        [STAThread]
        private static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            bool silent = Has(args, "/silent") || Has(args, "/s");

            try
            {
                if (Has(args, "/uninstall"))
                {
                    string dir = Value(args, "/dir");
                    if (dir == null)
                    {
                        // Running from the install folder: work from a temp copy so the folder can be deleted.
                        string self = Assembly.GetExecutingAssembly().Location;
                        string temp = Path.Combine(Path.GetTempPath(), "FindAnything-uninstall-" + Guid.NewGuid().ToString("N").Substring(0, 8) + ".exe");
                        File.Copy(self, temp);
                        Process.Start(new ProcessStartInfo(temp,
                            "/uninstall /dir \"" + Path.GetDirectoryName(self) + "\"" + (silent ? " /silent" : "")) { UseShellExecute = false });
                        return 0;
                    }
                    return Installer.Uninstall(dir, silent) ? 0 : 1;
                }

                if (silent)
                {
                    InstallOptions o = InstallOptions.Defaults();
                    Installer.Install(o, true, delegate(string s) { });
                    return 0;
                }

                Application.Run(new SetupForm());
                return 0;
            }
            catch (Exception ex)
            {
                if (!silent) MessageBox.Show(ex.Message, AppInfo.Name + " Setup", MessageBoxButtons.OK, MessageBoxIcon.Error);
                return 2;
            }
        }

        private static bool Has(string[] args, string flag)
        {
            foreach (string a in args) if (string.Equals(a, flag, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        private static string Value(string[] args, string flag)
        {
            for (int i = 0; i + 1 < args.Length; i++)
                if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return args[i + 1].Trim('"');
            return null;
        }
    }

    internal sealed class InstallOptions
    {
        public string Folder;
        public bool StartMenu = true;
        public bool Hotkey = true;
        public bool Desktop = true;
        public bool ExplorerMenu = true;

        public static string DefaultFolder
        {
            get
            {
                return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Programs", AppInfo.Name);
            }
        }

        public static InstallOptions Defaults()
        {
            InstallOptions o = new InstallOptions();
            o.Folder = Installer.ExistingFolder() ?? DefaultFolder;
            return o;
        }
    }

    internal static class Installer
    {
        public const string AppExe = "FindAnything.exe";
        public const string UninstallExe = "Uninstall.exe";
        private const string UninstallKey = @"Software\Microsoft\Windows\CurrentVersion\Uninstall\FindAnything";
        private static readonly string[] MenuKeys =
        {
            @"Software\Classes\Directory\shell\FindAnything",
            @"Software\Classes\Directory\Background\shell\FindAnything",
            @"Software\Classes\Drive\shell\FindAnything",
        };

        public static string StartMenuShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Programs), AppInfo.Name + ".lnk"); }
        }

        public static string DesktopShortcut
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.DesktopDirectory), AppInfo.Name + ".lnk"); }
        }

        /// <summary>The folder of an earlier install, or null.</summary>
        public static string ExistingFolder()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                string s = k == null ? null : k.GetValue("InstallLocation") as string;
                return string.IsNullOrEmpty(s) ? null : s;
            }
        }

        public static string ExistingVersion()
        {
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKey))
                return k == null ? null : k.GetValue("DisplayVersion") as string;
        }

        public static void Install(InstallOptions o, bool silent, Action<string> step)
        {
            string dir = Path.GetFullPath(o.Folder);
            string exe = Path.Combine(dir, AppExe);
            if (!CloseRunning(exe, silent)) throw new OperationCanceledException(AppInfo.Name + " is still open.");

            step("Copying files...");
            Directory.CreateDirectory(dir);
            WriteResource("FindAnything.exe", exe);
            WriteResource("LICENSE.txt", Path.Combine(dir, "LICENSE.txt"));
            string self = Assembly.GetExecutingAssembly().Location;
            string uninstaller = Path.Combine(dir, UninstallExe);
            if (!string.Equals(Path.GetFullPath(self), uninstaller, StringComparison.OrdinalIgnoreCase))
                File.Copy(self, uninstaller, true);

            step("Creating shortcuts...");
            DeleteFile(StartMenuShortcut);
            DeleteFile(DesktopShortcut);
            if (o.StartMenu) Shortcut(StartMenuShortcut, exe, dir, o.Hotkey ? "Ctrl+Alt+F" : "");
            if (o.Desktop) Shortcut(DesktopShortcut, exe, dir, (!o.StartMenu && o.Hotkey) ? "Ctrl+Alt+F" : "");

            step("Adding the right-click menu...");
            RemoveExplorerMenu();
            if (o.ExplorerMenu) AddExplorerMenu(exe);

            step("Registering with Windows...");
            using (RegistryKey k = Registry.CurrentUser.CreateSubKey(UninstallKey))
            {
                k.SetValue("DisplayName", AppInfo.Name);
                k.SetValue("DisplayVersion", AppInfo.Version);
                k.SetValue("Publisher", AppInfo.Author);
                k.SetValue("DisplayIcon", "\"" + exe + "\",0");
                k.SetValue("InstallLocation", dir);
                k.SetValue("UninstallString", "\"" + uninstaller + "\" /uninstall");
                k.SetValue("QuietUninstallString", "\"" + uninstaller + "\" /uninstall /silent");
                k.SetValue("URLInfoAbout", AppInfo.Url);
                k.SetValue("HelpLink", AppInfo.Url);
                k.SetValue("NoModify", 1, RegistryValueKind.DWord);
                k.SetValue("NoRepair", 1, RegistryValueKind.DWord);
                k.SetValue("EstimatedSize", (int)(DirSize(dir) / 1024), RegistryValueKind.DWord);
                k.SetValue("InstallDate", DateTime.Now.ToString("yyyyMMdd"));
                k.SetValue("ShortcutOptions", (o.StartMenu ? "S" : "") + (o.Hotkey ? "H" : "") + (o.Desktop ? "D" : "") + (o.ExplorerMenu ? "E" : ""));
            }
            step("Done.");
        }

        public static bool Uninstall(string dir, bool silent)
        {
            string exe = Path.Combine(dir, AppExe);
            if (!silent)
            {
                DialogResult r = MessageBox.Show("Remove " + AppInfo.Name + " from this PC?", AppInfo.Name + " Uninstall",
                    MessageBoxButtons.OKCancel, MessageBoxIcon.Question);
                if (r != DialogResult.OK) { SelfDelete(); return false; }
            }
            if (!CloseRunning(exe, silent)) { SelfDelete(); return false; }

            RemoveIfTarget(StartMenuShortcut, exe);
            RemoveIfTarget(DesktopShortcut, exe);
            RemoveExplorerMenuFor(exe);
            using (RegistryKey k = Registry.CurrentUser.OpenSubKey(UninstallKey))
            {
                string loc = k == null ? null : k.GetValue("InstallLocation") as string;
                if (loc != null && string.Equals(Path.GetFullPath(loc).TrimEnd('\\'), Path.GetFullPath(dir).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase))
                {
                    k.Close();
                    Registry.CurrentUser.DeleteSubKeyTree(UninstallKey, false);
                }
            }

            // The uninstaller that launched us may still be exiting: retry the folder for a few seconds.
            for (int i = 0; i < 20; i++)
            {
                try
                {
                    DeleteFile(exe);
                    DeleteFile(Path.Combine(dir, UninstallExe));
                    DeleteFile(Path.Combine(dir, "LICENSE.txt"));
                    if (Directory.Exists(dir) && Directory.GetFileSystemEntries(dir).Length == 0) Directory.Delete(dir);
                    break;
                }
                catch (IOException) { Thread.Sleep(250); }
                catch (UnauthorizedAccessException) { Thread.Sleep(250); }
            }

            string settings = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FindAnything");
            if (!silent && Directory.Exists(settings))
            {
                DialogResult keep = MessageBox.Show("Also delete your saved settings (recent folders and searches)?",
                    AppInfo.Name + " Uninstall", MessageBoxButtons.YesNo, MessageBoxIcon.Question, MessageBoxDefaultButton.Button2);
                if (keep == DialogResult.Yes) { try { Directory.Delete(settings, true); } catch (Exception) { } }
            }
            if (!silent) MessageBox.Show(AppInfo.Name + " was removed.", AppInfo.Name + " Uninstall", MessageBoxButtons.OK, MessageBoxIcon.Information);
            SelfDelete();
            return true;
        }

        /// <summary>Asks a running copy of the app to close. Returns false if it is still running.</summary>
        public static bool CloseRunning(string exe, bool silent)
        {
            for (int attempt = 0; attempt < 2; attempt++)
            {
                bool any = false;
                foreach (Process p in Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exe)))
                {
                    try
                    {
                        if (!string.Equals(p.MainModule.FileName, exe, StringComparison.OrdinalIgnoreCase)) continue;
                    }
                    catch (Exception) { continue; }
                    any = true;
                    if (attempt == 0)
                    {
                        if (!silent && MessageBox.Show(AppInfo.Name + " is open. Close it and continue?", AppInfo.Name + " Setup",
                            MessageBoxButtons.OKCancel, MessageBoxIcon.Warning) != DialogResult.OK)
                            throw new OperationCanceledException(AppInfo.Name + " is still open.");
                        p.CloseMainWindow();
                        if (!p.WaitForExit(5000)) p.Kill();
                        p.WaitForExit(3000);
                    }
                }
                if (!any) return true;
            }
            return false;
        }

        private static void WriteResource(string name, string path)
        {
            using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name))
            {
                if (s == null) throw new InvalidOperationException("Setup is damaged: " + name + " is missing.");
                string tmp = path + ".new";
                using (FileStream f = File.Create(tmp)) s.CopyTo(f);
                if (File.Exists(path)) File.Delete(path);
                File.Move(tmp, path);
            }
        }

        private static void Shortcut(string lnk, string target, string workDir, string hotkey)
        {
            Type t = Type.GetTypeFromProgID("WScript.Shell");
            dynamic shell = Activator.CreateInstance(t);
            dynamic sc = shell.CreateShortcut(lnk);
            sc.TargetPath = target;
            sc.WorkingDirectory = workDir;
            sc.IconLocation = target + ",0";
            sc.Description = "Find text inside files";
            if (hotkey.Length > 0) sc.Hotkey = hotkey;
            sc.Save();
        }

        private static string ShortcutTarget(string lnk)
        {
            try
            {
                Type t = Type.GetTypeFromProgID("WScript.Shell");
                dynamic shell = Activator.CreateInstance(t);
                dynamic sc = shell.CreateShortcut(lnk);
                return (string)sc.TargetPath;
            }
            catch (Exception) { return null; }
        }

        private static void RemoveIfTarget(string lnk, string exe)
        {
            if (!File.Exists(lnk)) return;
            string target = ShortcutTarget(lnk);
            if (target != null && string.Equals(target, exe, StringComparison.OrdinalIgnoreCase)) DeleteFile(lnk);
        }

        private static void AddExplorerMenu(string exe)
        {
            string[] args = { "%1", "%V", "%1" };
            for (int i = 0; i < MenuKeys.Length; i++)
            {
                using (RegistryKey k = Registry.CurrentUser.CreateSubKey(MenuKeys[i]))
                {
                    k.SetValue("", AppInfo.Name + " here");
                    k.SetValue("Icon", "\"" + exe + "\",0");
                }
                using (RegistryKey c = Registry.CurrentUser.CreateSubKey(MenuKeys[i] + @"\command"))
                    c.SetValue("", "\"" + exe + "\" \"" + args[i] + "\"");
            }
        }

        private static void RemoveExplorerMenu()
        {
            foreach (string key in MenuKeys) Registry.CurrentUser.DeleteSubKeyTree(key, false);
        }

        /// <summary>Removes the right-click menu only if it points at this install (a portable copy may own it).</summary>
        private static void RemoveExplorerMenuFor(string exe)
        {
            foreach (string key in MenuKeys)
            {
                string cmd;
                using (RegistryKey c = Registry.CurrentUser.OpenSubKey(key + @"\command"))
                    cmd = c == null ? null : c.GetValue("") as string;
                if (cmd != null && cmd.IndexOf(exe, StringComparison.OrdinalIgnoreCase) >= 0)
                    Registry.CurrentUser.DeleteSubKeyTree(key, false);
            }
        }

        private static void DeleteFile(string path)
        {
            if (File.Exists(path)) File.Delete(path);
        }

        private static long DirSize(string dir)
        {
            long n = 0;
            foreach (string f in Directory.GetFiles(dir)) n += new FileInfo(f).Length;
            return n;
        }

        /// <summary>The temp copy of the uninstaller deletes itself once it has exited.</summary>
        private static void SelfDelete()
        {
            string self = Assembly.GetExecutingAssembly().Location;
            if (!self.StartsWith(Path.GetTempPath(), StringComparison.OrdinalIgnoreCase)) return;
            Process.Start(new ProcessStartInfo("cmd.exe", "/c ping 127.0.0.1 -n 3 > nul & del \"" + self + "\"")
            {
                CreateNoWindow = true,
                UseShellExecute = false,
                WindowStyle = ProcessWindowStyle.Hidden
            });
        }
    }

    internal sealed class SetupForm : Form
    {
        private readonly TextBox folder = new TextBox();
        private readonly CheckBox startMenu = new CheckBox();
        private readonly CheckBox hotkey = new CheckBox();
        private readonly CheckBox desktop = new CheckBox();
        private readonly CheckBox explorer = new CheckBox();
        private readonly CheckBox launch = new CheckBox();
        private readonly Label status = new Label();
        private readonly Button install = new Button();
        private readonly Button cancel = new Button();
        private readonly Button browse = new Button();
        private bool done;

        public SetupForm()
        {
            SuspendLayout();
            AutoScaleDimensions = new SizeF(96F, 96F);
            AutoScaleMode = AutoScaleMode.Dpi;
            Text = AppInfo.Name + " Setup";
            Font = new Font("Segoe UI", 9.75F);
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            AutoSize = true;
            AutoSizeMode = AutoSizeMode.GrowAndShrink;
            Padding = new Padding(18);
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            string existing = Installer.ExistingVersion();
            InstallOptions defaults = InstallOptions.Defaults();

            TableLayoutPanel t = new TableLayoutPanel();
            t.AutoSize = true;
            t.ColumnCount = 2;
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));

            PictureBox logo = new PictureBox();
            logo.SizeMode = PictureBoxSizeMode.Zoom;
            logo.Size = new Size(64, 64);
            logo.Margin = new Padding(0, 0, 14, 0);
            logo.Image = LargestIconImage();
            t.Controls.Add(logo, 0, 0);

            Label title = new Label();
            title.AutoSize = true;
            title.Font = new Font(Font.FontFamily, 15F, FontStyle.Bold);
            title.Text = AppInfo.Name + " " + AppInfo.Version;
            Label sub = new Label();
            sub.AutoSize = true;
            sub.MaximumSize = new Size(560, 0);
            sub.Text = "Find text inside any file: plain text, PDF, Office, Unicode, and files of any size.\n" +
                       "Free and open source (" + AppInfo.License + "). Created by " + AppInfo.Author + "." +
                       (existing == null ? "" : existing == AppInfo.Version ? "\n\nThis version is already installed. Installing again repairs it." : "\n\nVersion " + existing + " is installed; this updates it.");
            FlowLayoutPanel head = new FlowLayoutPanel();
            head.FlowDirection = FlowDirection.TopDown;
            head.AutoSize = true;
            head.WrapContents = false;
            head.Controls.Add(title);
            head.Controls.Add(sub);
            t.Controls.Add(head, 1, 0);

            FlowLayoutPanel body = new FlowLayoutPanel();
            body.FlowDirection = FlowDirection.TopDown;
            body.AutoSize = true;
            body.WrapContents = false;
            body.Margin = new Padding(0, 16, 0, 0);

            Label where = new Label();
            where.AutoSize = true;
            where.Text = "Install to (just for you, no admin needed):";
            body.Controls.Add(where);
            FlowLayoutPanel folderRow = new FlowLayoutPanel();
            folderRow.AutoSize = true;
            folderRow.WrapContents = false;
            folder.Width = 520;
            folder.Text = defaults.Folder;
            folder.Margin = new Padding(0, 4, 6, 8);
            browse.Text = "Browse...";
            browse.AutoSize = true;
            browse.Margin = new Padding(0, 2, 0, 8);
            browse.Click += delegate
            {
                using (FolderBrowserDialog d = new FolderBrowserDialog())
                {
                    d.SelectedPath = folder.Text;
                    if (d.ShowDialog(this) == DialogResult.OK) folder.Text = Path.Combine(d.SelectedPath, AppInfo.Name);
                }
            };
            folderRow.Controls.Add(folder);
            folderRow.Controls.Add(browse);
            body.Controls.Add(folderRow);

            body.Controls.Add(Check(startMenu, "Start menu shortcut", defaults.StartMenu));
            body.Controls.Add(Check(hotkey, "Ctrl+Alt+F opens " + AppInfo.Name, defaults.Hotkey));
            body.Controls.Add(Check(desktop, "Desktop shortcut", defaults.Desktop));
            body.Controls.Add(Check(explorer, "\"" + AppInfo.Name + " here\" on Explorer's right-click menu for folders and drives", defaults.ExplorerMenu));
            launch.Visible = false;
            body.Controls.Add(Check(launch, "Open " + AppInfo.Name + " now", true));
            startMenu.CheckedChanged += delegate { if (!startMenu.Checked && !desktop.Checked) hotkey.Checked = false; };

            status.AutoSize = true;
            status.ForeColor = SystemColors.GrayText;
            status.Margin = new Padding(0, 10, 0, 0);
            body.Controls.Add(status);

            t.Controls.Add(body, 0, 1);
            t.SetColumnSpan(body, 2);

            FlowLayoutPanel buttons = new FlowLayoutPanel();
            buttons.FlowDirection = FlowDirection.RightToLeft;
            buttons.AutoSize = true;
            buttons.Dock = DockStyle.Fill;
            buttons.Margin = new Padding(0, 14, 0, 0);
            cancel.Text = "Cancel";
            cancel.AutoSize = true;
            cancel.Click += delegate { Close(); };
            install.Text = existing == null ? "Install" : existing == AppInfo.Version ? "Reinstall" : "Update";
            install.AutoSize = true;
            install.Click += OnInstall;
            buttons.Controls.Add(cancel);
            buttons.Controls.Add(install);
            t.Controls.Add(buttons, 0, 2);
            t.SetColumnSpan(buttons, 2);

            Controls.Add(t);
            ResumeLayout(false);
            PerformLayout();
            AcceptButton = install;
            CancelButton = cancel;
            ActiveControl = install;
        }

        /// <summary>
        /// The biggest image in the embedded app.ico. Its frames are PNG-compressed, which System.Drawing.Icon
        /// cannot turn into a bitmap, so the PNG bytes are read straight out of the icon directory.
        /// </summary>
        private static Image LargestIconImage()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("app.ico"))
                using (BinaryReader r = new BinaryReader(s))
                {
                    byte[] all = r.ReadBytes((int)s.Length);
                    int count = BitConverter.ToUInt16(all, 4);
                    int best = -1, bestSize = 0;
                    for (int i = 0; i < count; i++)
                    {
                        int w = all[6 + 16 * i];
                        if (w == 0) w = 256;
                        if (w > bestSize) { bestSize = w; best = i; }
                    }
                    int len = BitConverter.ToInt32(all, 6 + 16 * best + 8);
                    int off = BitConverter.ToInt32(all, 6 + 16 * best + 12);
                    return new Bitmap(new MemoryStream(all, off, len));
                }
            }
            catch (Exception) { return null; }
        }

        private static CheckBox Check(CheckBox c, string text, bool on)
        {
            c.Text = text;
            c.AutoSize = true;
            c.Checked = on;
            c.Margin = new Padding(0, 2, 0, 2);
            return c;
        }

        private void OnInstall(object sender, EventArgs e)
        {
            if (done)
            {
                if (launch.Checked)
                {
                    try { Process.Start(Path.Combine(Path.GetFullPath(folder.Text), Installer.AppExe)); } catch (Exception) { }
                }
                Close();
                return;
            }

            InstallOptions o = new InstallOptions();
            o.Folder = folder.Text.Trim();
            o.StartMenu = startMenu.Checked;
            o.Hotkey = hotkey.Checked;
            o.Desktop = desktop.Checked;
            o.ExplorerMenu = explorer.Checked;
            if (o.Folder.Length == 0) { status.Text = "Pick a folder to install to."; return; }

            install.Enabled = cancel.Enabled = browse.Enabled = false;
            UseWaitCursor = true;
            try
            {
                Installer.Install(o, false, delegate(string s) { status.Text = s; status.Refresh(); });
                done = true;
                status.Text = AppInfo.Name + " is installed. Find it in the Start menu" + (o.Hotkey ? " or press Ctrl+Alt+F" : "") + ".";
                foreach (Control c in new Control[] { folder, startMenu, hotkey, desktop, explorer }) c.Enabled = false;
                launch.Visible = true;
                install.Text = "Finish";
                install.Enabled = true;
            }
            catch (OperationCanceledException ex)
            {
                status.Text = ex.Message;
                install.Enabled = cancel.Enabled = browse.Enabled = true;
            }
            catch (Exception ex)
            {
                status.Text = "Install failed: " + ex.Message;
                install.Enabled = cancel.Enabled = browse.Enabled = true;
            }
            finally
            {
                UseWaitCursor = false;
            }
        }
    }
}
