using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows.Forms;
using FindAnything.Core;
using Microsoft.Win32;

namespace FindAnything.App
{
    /// <summary>Plain-text settings in %APPDATA%\FindAnything\settings.txt, one key=value per line.</summary>
    internal sealed class AppSettings
    {
        private readonly Dictionary<string, string> values = new Dictionary<string, string>();

        private static string FilePath
        {
            get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "FindAnything", "settings.txt"); }
        }

        public static AppSettings Load()
        {
            AppSettings s = new AppSettings();
            try
            {
                if (File.Exists(FilePath))
                    foreach (string line in File.ReadAllLines(FilePath, Encoding.UTF8))
                    {
                        int eq = line.IndexOf('=');
                        if (eq > 0) s.values[line.Substring(0, eq)] = line.Substring(eq + 1);
                    }
            }
            catch (Exception) { }
            return s;
        }

        public void Save()
        {
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(FilePath));
                List<string> keys = new List<string>(values.Keys);
                keys.Sort(StringComparer.Ordinal);
                StringBuilder sb = new StringBuilder();
                foreach (string k in keys) sb.Append(k).Append('=').Append(values[k]).Append("\r\n");
                File.WriteAllText(FilePath, sb.ToString(), new UTF8Encoding(false));
            }
            catch (Exception) { }
        }

        public string Get(string key, string fallback)
        {
            string v;
            return values.TryGetValue(key, out v) ? v : fallback;
        }

        public bool GetBool(string key, bool fallback)
        {
            string v = Get(key, null);
            return v == null ? fallback : v == "1";
        }

        public long GetLong(string key, long fallback)
        {
            long v;
            return long.TryParse(Get(key, null), out v) ? v : fallback;
        }

        public void Set(string key, string value)
        {
            values[key] = (value ?? "").Replace("\r", " ").Replace("\n", " ");
        }

        public void Set(string key, bool value) { Set(key, value ? "1" : "0"); }
        public void Set(string key, long value) { Set(key, value.ToString()); }

        public List<string> GetList(string key)
        {
            List<string> list = new List<string>();
            for (int i = 0; i < 30; i++)
            {
                string v = Get(key + "." + i, null);
                if (v == null) break;
                if (v.Length > 0) list.Add(v);
            }
            return list;
        }

        public void SetList(string key, IList<string> list)
        {
            for (int i = 0; i < 30; i++)
            {
                if (i < list.Count) Set(key + "." + i, list[i]);
                else values.Remove(key + "." + i);
            }
        }
    }

    internal static class Fmt
    {
        public static string Size(long bytes)
        {
            if (bytes < 1024) return bytes + " B";
            double v = bytes;
            string[] units = { "KB", "MB", "GB", "TB" };
            int u = -1;
            do { v /= 1024; u++; } while (v >= 1024 && u < units.Length - 1);
            return v.ToString(v < 10 ? "0.0" : "0") + " " + units[u];
        }
    }

    internal static class Shell
    {
        /// <summary>Files at least this big get a warning before another program opens them.</summary>
        public const long BigFileBytes = 200L << 20;

        public static void OpenDefault(string path)
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }

        public static void ShowInFolder(string path)
        {
            Process.Start("explorer.exe", "/select,\"" + path + "\"");
        }

        private static string nppPath;
        private static bool nppLooked;

        /// <summary>Notepad++ install location, or null when it is not installed.</summary>
        public static string NotepadPlusPlus
        {
            get
            {
                if (nppLooked) return nppPath;
                nppLooked = true;
                List<string> candidates = new List<string>();
                try
                {
                    using (RegistryKey k = Registry.LocalMachine.OpenSubKey(@"SOFTWARE\Notepad++"))
                        if (k != null && k.GetValue(null) is string) candidates.Add(Path.Combine((string)k.GetValue(null), "notepad++.exe"));
                }
                catch (Exception) { }
                candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"Notepad++\notepad++.exe"));
                candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Notepad++\notepad++.exe"));
                foreach (string c in candidates)
                    if (File.Exists(c)) { nppPath = c; break; }
                return nppPath;
            }
        }

        public static void OpenInNotepadPlusPlus(string path, long line)
        {
            string args = (line > 0 ? "-n" + line + " " : "") + "\"" + path + "\"";
            Process.Start(new ProcessStartInfo(NotepadPlusPlus, args) { UseShellExecute = false });
        }
    }

    /// <summary>The Vista-style folder picker (IFileOpenDialog with FOS_PICKFOLDERS).</summary>
    internal static class FolderPicker
    {
        public static string Pick(IWin32Window owner, string start)
        {
            try
            {
                IFileOpenDialog dlg = (IFileOpenDialog)new FileOpenDialogRcw();
                dlg.SetOptions(0x20 | 0x40 | 0x800);   // PICKFOLDERS | FORCEFILESYSTEM | PATHMUSTEXIST
                dlg.SetTitle("Look in");
                if (!string.IsNullOrEmpty(start) && Directory.Exists(start))
                {
                    IShellItem folder;
                    Guid iid = typeof(IShellItem).GUID;
                    if (SHCreateItemFromParsingName(start, IntPtr.Zero, ref iid, out folder) == 0) dlg.SetFolder(folder);
                }
                if (dlg.Show(owner == null ? IntPtr.Zero : owner.Handle) != 0) return null;
                IShellItem result;
                dlg.GetResult(out result);
                string path;
                result.GetDisplayName(0x80058000, out path);   // SIGDN_FILESYSPATH
                return path;
            }
            catch (Exception)
            {
                using (FolderBrowserDialog fb = new FolderBrowserDialog())
                {
                    fb.SelectedPath = start ?? "";
                    return fb.ShowDialog(owner) == DialogResult.OK ? fb.SelectedPath : null;
                }
            }
        }

        [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = true)]
        private static extern int SHCreateItemFromParsingName(string path, IntPtr pbc, ref Guid riid, out IShellItem item);

        [ComImport, Guid("DC1C5A9C-E88A-4dde-A5A1-60F82A20AEF7")]
        private class FileOpenDialogRcw { }

        [ComImport, Guid("d57c7288-d4ad-4768-be02-9d969532d960"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IFileOpenDialog
        {
            [PreserveSig] int Show(IntPtr parent);
            void SetFileTypes();
            void SetFileTypeIndex();
            void GetFileTypeIndex();
            void Advise();
            void Unadvise();
            void SetOptions(uint fos);
            void GetOptions();
            void SetDefaultFolder(IShellItem psi);
            void SetFolder(IShellItem psi);
            void GetFolder();
            void GetCurrentSelection();
            void SetFileName();
            void GetFileName();
            void SetTitle([MarshalAs(UnmanagedType.LPWStr)] string title);
            void SetOkButtonLabel();
            void SetFileNameLabel();
            void GetResult(out IShellItem item);
        }

        [ComImport, Guid("43826D1E-E718-42EE-BC55-A1E261C37BFE"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
        private interface IShellItem
        {
            void BindToHandler();
            void GetParent();
            void GetDisplayName(uint sigdn, [MarshalAs(UnmanagedType.LPWStr)] out string name);
        }
    }

    internal static class Native
    {
        private const int WM_SETREDRAW = 0x000B;

        [DllImport("user32.dll")]
        private static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);

        private const int EM_GETFIRSTVISIBLELINE = 0x00CE, EM_LINESCROLL = 0x00B6;

        /// <summary>Scrolls a text box so the line holding charIndex sits in the middle of the view.</summary>
        public static void CenterLine(RichTextBox box, int charIndex)
        {
            int line = box.GetLineFromCharIndex(charIndex);
            int first = (int)SendMessage(box.Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
            int lineHeight = Math.Max(1, TextRenderer.MeasureText("X", box.Font).Height);
            int visible = Math.Max(1, box.ClientSize.Height / lineHeight);
            int delta = line - visible / 2 - first;
            if (delta != 0) SendMessage(box.Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)delta);
        }

        public static void Redraw(Control c, bool on)
        {
            SendMessage(c.Handle, WM_SETREDRAW, on ? (IntPtr)1 : IntPtr.Zero, IntPtr.Zero);
            if (on) c.Invalidate();
        }
    }
}
