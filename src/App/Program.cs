using System;
using System.IO;
using System.Windows.Forms;
using FindAnything.Core;

namespace FindAnything.App
{
    internal static class Program
    {
        // FindAnything.exe [folder]                       start with "Look in" set
        // FindAnything.exe --view <file> [--find <text>]  open just the viewer
        [STAThread]
        private static void Main(string[] args)
        {
            LongPath.EnableInProcess();
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);

            if (args.Length >= 2 && args[0] == "--view")
            {
                string find = "";
                for (int i = 2; i + 1 < args.Length; i++) if (args[i] == "--find") find = args[i + 1];
                FileHit hit = new FileHit();
                hit.Path = Path.GetFullPath(args[1]);
                hit.IoPath = LongPath.Add(hit.Path);
                hit.Name = Path.GetFileName(hit.Path);
                hit.Size = new FileInfo(hit.IoPath).Length;
                string kind = DocText.KindOf(hit.Path);
                if (kind != null) hit.Source = kind;
                ViewOptions o = new ViewOptions();
                o.Text = find;
                Application.Run(new ViewerForm(hit, o));
                return;
            }

            MainForm form = new MainForm();
            if (args.Length > 0)
            {
                string start = CleanPathArg(args[0]);
                if (Directory.Exists(LongPath.Add(start)) || File.Exists(LongPath.Add(start))) form.StartFolder = start;
            }
            Application.Run(form);
        }

        /// <summary>
        /// Explorer passes a drive as "C:\" in quotes, and the command-line parser reads the \" as an escaped quote,
        /// so the argument arrives as C:" . Undo that.
        /// </summary>
        internal static string CleanPathArg(string arg)
        {
            string s = arg.Trim().Trim('"').Trim();
            if (s.Length == 2 && s[1] == ':') s += "\\";
            return s;
        }
    }
}
