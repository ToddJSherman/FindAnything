using System;
using System.IO;
using System.Windows.Forms;
using FindAnything.Core;

namespace FindAnything.App
{
    internal static class Program
    {
        private static string[] Skip1(string[] a)
        {
            string[] r = new string[a.Length - 1];
            Array.Copy(a, 1, r, 0, r.Length);
            return r;
        }

        // FindAnything.exe [folder]                       start with "Look in" set
        // FindAnything.exe --view <file> [--find <text>]  open just the viewer
        // FindAnything.exe --keys [folder]                start in Find keys
        // FindAnything.exe --drive-check                  start in Drive check (used when restarting as admin)
        // FindAnything.exe --deleted [drive or folder]    start in Deleted files
        // FindAnything.exe --similar <picture or folder>  start in Similar pictures (a picture: find ones like it, nearby)
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
                if (Ocr.IsImage(hit.Path)) { hit.Source = "OCR"; o.Loose = true; Application.Run(new ImageViewerForm(hit, o)); }
                else Application.Run(new ViewerForm(hit, o));
                return;
            }

            MainForm form = new MainForm();
            if (args.Length > 0 && args[0] == "--drive-check") { form.StartMode = 2; args = new string[0]; }
            else if (args.Length > 0 && args[0] == "--deleted") { form.StartMode = 4; args = Skip1(args); }
            else if (args.Length > 0 && args[0] == "--keys") { form.StartMode = 1; args = Skip1(args); }
            else if (args.Length > 0 && args[0] == "--similar")
            {
                form.StartMode = 3;
                args = Skip1(args);
                if (args.Length > 0)
                {
                    string pic = CleanPathArg(args[0]);
                    if (Ocr.IsImage(pic) && File.Exists(LongPath.Add(pic)))
                    {
                        form.StartSample = Path.GetFullPath(pic);
                        args = new[] { Path.GetDirectoryName(form.StartSample) };
                    }
                }
            }
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
