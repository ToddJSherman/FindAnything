using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace FindAnything.Core
{
    /// <summary>A file sitting in the Recycle Bin: its content ($R...) and where it came from ($I...).</summary>
    public sealed class RecycledFile
    {
        public string DataPath;      // $Recycle.Bin\<user>\$Rxxxxxx.ext - the file itself (or a folder)
        public string OriginalPath;
        public long Size;
        public DateTime Deleted;
        public bool IsFolder;
    }

    /// <summary>
    /// Lists a drive's Recycle Bin from its $I records (Windows Vista and later): version 1 holds the original path in a
    /// fixed 260-character field, version 2 (Windows 10/11) in a counted one. Only the folders this account can read.
    /// </summary>
    public static class RecycleBin
    {
        public static List<RecycledFile> List(string root, List<string> problems)
        {
            List<RecycledFile> list = new List<RecycledFile>();
            string bin = Path.Combine(root, "$Recycle.Bin");
            string[] users;
            try { users = Directory.GetDirectories(bin); }
            catch (Exception) { return list; }
            foreach (string user in users)
            {
                string[] infos;
                try { infos = Directory.GetFiles(user, "$I*"); }
                catch (Exception ex) { if (problems != null) problems.Add(user + ": " + ex.Message); continue; }
                foreach (string info in infos)
                {
                    try
                    {
                        RecycledFile f = ReadInfo(File.ReadAllBytes(info));
                        if (f == null) continue;
                        string data = Path.Combine(user, "$R" + Path.GetFileName(info).Substring(2));
                        f.IsFolder = Directory.Exists(data);
                        if (!f.IsFolder && !File.Exists(data)) continue;   // emptied
                        f.DataPath = data;
                        list.Add(f);
                    }
                    catch (Exception) { }
                }
            }
            return list;
        }

        /// <summary>Parses a $I record.</summary>
        public static RecycledFile ReadInfo(byte[] b)
        {
            if (b.Length < 24) return null;
            long version = BitConverter.ToInt64(b, 0);
            RecycledFile f = new RecycledFile();
            f.Size = BitConverter.ToInt64(b, 8);
            long ft = BitConverter.ToInt64(b, 16);
            try { f.Deleted = DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch (Exception) { f.Deleted = DateTime.MinValue; }
            string path;
            if (version == 1)
            {
                path = Encoding.Unicode.GetString(b, 24, Math.Min(520, b.Length - 24));
            }
            else if (version == 2 && b.Length >= 28)
            {
                int chars = BitConverter.ToInt32(b, 24);
                if (chars <= 0 || 28 + chars * 2 > b.Length) return null;
                path = Encoding.Unicode.GetString(b, 28, chars * 2);
            }
            else return null;
            int nul = path.IndexOf('\0');
            f.OriginalPath = nul >= 0 ? path.Substring(0, nul) : path;
            return f.OriginalPath.Length > 0 ? f : null;
        }
    }
}
