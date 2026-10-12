using System;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Windows.Forms;
using FindAnything.Ui;
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
            // Save over an existing shortcut rather than deleting and recreating it: Explorer drops a shortcut's
            // hotkey when it sees the delete and can miss the quick re-create, which leaves Ctrl+Alt+F dead.
            if (o.StartMenu) Shortcut(StartMenuShortcut, exe, dir, o.Hotkey ? "Ctrl+Alt+F" : "");
            else DeleteFile(StartMenuShortcut);
            if (o.Desktop) Shortcut(DesktopShortcut, exe, dir, (!o.StartMenu && o.Hotkey) ? "Ctrl+Alt+F" : "");
            else DeleteFile(DesktopShortcut);

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
            sc.Hotkey = hotkey;
            sc.Save();
            // Tell Explorer the shortcut changed so it re-reads the hotkey now.
            SHChangeNotify(0x00002000 /* SHCNE_UPDATEITEM */, 0x0005 /* SHCNF_PATHW */, lnk, IntPtr.Zero);
        }

        [System.Runtime.InteropServices.DllImport("shell32.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        private static extern void SHChangeNotify(int eventId, uint flags, string item1, IntPtr item2);

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
        private readonly Backdrop body = new Backdrop();
        private readonly SkinField folder = new SkinField(FieldStyle.Normal, Theme.GFolder);
        private readonly SkinButton browse = new SkinButton("Browse", Theme.GOpenFolder, ButtonKind.Secondary);
        private readonly SkinCheck startMenu, hotkey, desktop, explorer, launch;
        private readonly SkinButton install = new SkinButton("Install", "", ButtonKind.Primary);
        private readonly SkinButton cancel = new SkinButton("Cancel", "", ButtonKind.Secondary);
        private readonly string existing;
        private string status = "";
        private bool done;
        private readonly Image icon = Skin.AppIcon();

        public SetupForm()
        {
            SuspendLayout();
            AutoScaleMode = AutoScaleMode.None;
            Text = AppInfo.Name + " Setup";
            Font = Theme.Ui(14);
            BackColor = Theme.Bg;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(Theme.S(640), Theme.S(560));
            try { Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch (Exception) { }

            existing = Installer.ExistingVersion();
            InstallOptions d = InstallOptions.Defaults();
            startMenu = new SkinCheck("Start menu shortcut", d.StartMenu);
            hotkey = new SkinCheck("Ctrl+Alt+F opens " + AppInfo.Name + " from anywhere", d.Hotkey);
            desktop = new SkinCheck("Desktop shortcut", d.Desktop);
            explorer = new SkinCheck("\"" + AppInfo.Name + " here\" on Explorer's right-click menu", d.ExplorerMenu);
            launch = new SkinCheck("Open " + AppInfo.Name + " now", true);
            launch.Visible = false;
            startMenu.CheckedChanged += delegate { if (!startMenu.Checked && !desktop.Checked) hotkey.Checked = false; };

            folder.Text = d.Folder;
            browse.Click += delegate
            {
                using (FolderBrowserDialog dlg = new FolderBrowserDialog())
                {
                    dlg.SelectedPath = folder.Text;
                    if (dlg.ShowDialog(this) == DialogResult.OK) folder.Text = Path.Combine(dlg.SelectedPath, AppInfo.Name);
                }
            };
            install.Text = existing == null ? "Install" : existing == AppInfo.Version ? "Reinstall" : "Update";
            install.Click += OnInstall;
            cancel.Click += delegate { Close(); };

            body.Hero = true;
            body.Dock = DockStyle.Fill;
            body.Paint += PaintBody;
            body.Controls.AddRange(new Control[] { folder, browse, startMenu, hotkey, desktop, explorer, launch, cancel, install });
            install.SendToBack();   // its glow sits under Cancel instead of clipping it
            Controls.Add(body);
            LayoutControls();
            ResumeLayout(false);
            AcceptButton = install;
            CancelButton = cancel;
            ActiveControl = install;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this, Color.FromArgb(17, 26, 36));
        }

        private void LayoutControls()
        {
            int L = Theme.S(32), R = ClientSize.Width - Theme.S(32);
            int y = Theme.S(196);
            int bp = browse.ShadowPad, fp = folder.ShadowPad;
            int bw = browse.PreferredWidth(46) - 2 * bp;
            browse.Bounds = Rectangle.Inflate(new Rectangle(R - bw, y, bw, Theme.S(46)), bp, bp);
            folder.Bounds = Rectangle.Inflate(new Rectangle(L, y, R - L - bw - Theme.S(10), Theme.S(46)), fp, fp);
            y += Theme.S(46) + Theme.S(26);
            foreach (SkinCheck c in new[] { startMenu, hotkey, desktop, explorer })
            {
                c.Bounds = new Rectangle(L, y, R - L, Theme.S(30));
                y += Theme.S(36);
            }
            launch.Bounds = new Rectangle(L, y + Theme.S(4), R - L, Theme.S(30));

            int h = Theme.S(48), by = ClientSize.Height - Theme.S(28) - h;
            int ip = install.ShadowPad, cp = cancel.ShadowPad;
            int iw = Math.Max(Theme.S(140), install.PreferredWidth(48) - 2 * ip);
            install.Bounds = Rectangle.Inflate(new Rectangle(R - iw, by, iw, h), ip, ip);
            int cw = Math.Max(Theme.S(110), cancel.PreferredWidth(48) - 2 * cp);
            cancel.Bounds = Rectangle.Inflate(new Rectangle(R - iw - Theme.S(12) - cw, by, cw, h), cp, cp);
        }

        private void PaintBody(object sender, PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            int L = Theme.S(32);
            if (icon != null)
            {
                g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
                g.DrawImage(icon, new Rectangle(L, Theme.S(30), Theme.S(64), Theme.S(64)));
            }
            int x = L + Theme.S(82);
            Font tf = Theme.Display(28);
            int w1 = Theme.Advance("Find ", tf);
            TextRenderer.DrawText(g, "Find", tf, new Rectangle(x, Theme.S(30), w1 + 4, Theme.S(40)), Theme.Text, Theme.Left);
            int w2 = Theme.Advance("Anything", tf);
            TextRenderer.DrawText(g, "Anything", tf, new Rectangle(x + w1, Theme.S(30), w2 + 4, Theme.S(40)), Theme.AmberHi, Theme.Left);

            // version chip
            Font vf = Theme.UiSemibold(12);
            string v = "v" + AppInfo.Version;
            int vw = Theme.TextWidth(v, vf) + Theme.S(18);
            Rectangle vr = new Rectangle(x + w1 + w2 + Theme.S(14), Theme.S(38), vw, Theme.S(24));
            Theme.FillRound(g, Theme.Alpha(Theme.Amber, 34), vr, vr.Height / 2f);
            TextRenderer.DrawText(g, v, vf, vr, Theme.AmberHi, Theme.Center);

            TextRenderer.DrawText(g, "Text inside any file. Any size.", Theme.Ui(14), new Rectangle(x, Theme.S(72), Theme.S(420), Theme.S(22)), Theme.Dim, Theme.Left);

            string sub = existing == null ? "Installs just for you. No admin rights needed."
                : existing == AppInfo.Version ? "This version is already installed. Installing again repairs it."
                : "Version " + existing + " is installed. This updates it in place.";
            TextRenderer.DrawText(g, sub, Theme.Ui(13.5f), new Rectangle(L, Theme.S(126), ClientSize.Width - 2 * L, Theme.S(22)), Theme.Text, Theme.Left);
            TextRenderer.DrawText(g, "Free and open source (" + AppInfo.License + "). Created by " + AppInfo.Author + ".",
                Theme.Ui(12.5f), new Rectangle(L, Theme.S(150), ClientSize.Width - 2 * L, Theme.S(20)), Theme.Faint, Theme.Left);

            TextRenderer.DrawText(g, "I\u200AN\u200AS\u200AT\u200AA\u200AL\u200AL\u200A \u200AT\u200AO", Theme.UiSemibold(11.5f), new Rectangle(L + Theme.S(1), Theme.S(174), Theme.S(200), Theme.S(18)), Theme.Faint, Theme.Left);

            if (status.Length > 0)
            {
                int by = ClientSize.Height - Theme.S(28) - Theme.S(48);
                TextRenderer.DrawText(g, status, Theme.Ui(13), new Rectangle(L, by, Theme.S(330), Theme.S(48)),
                    done ? Theme.Green : Theme.Dim, TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            }
        }

        private void SetStatus(string s)
        {
            status = s;
            body.Invalidate();
            body.Update();
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
            if (o.Folder.Length == 0) { SetStatus("Pick a folder to install to."); return; }

            install.Enabled = cancel.Enabled = browse.Enabled = false;
            UseWaitCursor = true;
            try
            {
                Installer.Install(o, false, delegate(string s) { SetStatus(s); });
                done = true;
                SetStatus("Installed. Find it in the Start menu" + (o.Hotkey ? " or press Ctrl+Alt+F." : "."));
                foreach (Control c in new Control[] { folder, browse, startMenu, hotkey, desktop, explorer }) c.Enabled = false;
                launch.Visible = true;
                install.Text = "Finish";
                install.Enabled = true;
                cancel.Visible = false;
                install.Invalidate();
            }
            catch (OperationCanceledException ex)
            {
                SetStatus(ex.Message);
                install.Enabled = cancel.Enabled = browse.Enabled = true;
            }
            catch (Exception ex)
            {
                SetStatus("Install failed: " + ex.Message);
                install.Enabled = cancel.Enabled = browse.Enabled = true;
            }
            finally
            {
                UseWaitCursor = false;
            }
        }
    }
}