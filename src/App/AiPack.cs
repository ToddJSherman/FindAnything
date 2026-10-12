using System;
using System.Drawing;
using System.IO;
using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Threading;
using System.Windows.Forms;
using FindAnything.Core;
using FindAnything.Ui;

namespace FindAnything.App
{
    /// <summary>
    /// The picture model (OpenAI CLIP ViT-B/32, 8-bit, plus Microsoft ONNX Runtime) is not in the installer: it is
    /// downloaded once, only after the user agrees, from this project's GitHub release, and checked against a pinned
    /// SHA-256 before it is unpacked to %LOCALAPPDATA%\FindAnything\ai.
    /// </summary>
    internal static class AiPack
    {
        public const string Url = "https://github.com/ToddJSherman/FindAnything/releases/download/picture-model-1/FindAnything-picture-model.zip";
        public const string Sha256 = "64c5314685b21ae6f1c8a272c0b8dc2db31b0cc0caa3413076936f0d0eda0f98";
        public const string SizeText = "117 MB";

        /// <summary>True when the model is ready; otherwise asks, downloads and unpacks it.</summary>
        public static bool Ensure(IWin32Window owner)
        {
            if (Clip.Installed) return true;
            DialogResult r = MessageBox.Show(owner,
                "Similar pictures uses a picture-recognition model that runs on this PC (OpenAI CLIP, with Microsoft ONNX Runtime).\n\n" +
                "It is not part of the installer: it is downloaded once (" + SizeText + ") from this app's GitHub page, then works offline. " +
                "Nothing about your pictures is ever sent anywhere.\n\nDownload it now?",
                "Picture model", MessageBoxButtons.YesNo, MessageBoxIcon.Question);
            if (r != DialogResult.Yes) return false;
            using (DownloadForm f = new DownloadForm()) f.ShowDialog(owner);
            return Clip.Installed;
        }

        private sealed class DownloadForm : Form
        {
            private string text = "Connecting...";
            private double fraction;
            private readonly CancellationTokenSource cts = new CancellationTokenSource();

            public DownloadForm()
            {
                AutoScaleMode = AutoScaleMode.None;
                Text = "Downloading the picture model";
                FormBorderStyle = FormBorderStyle.FixedDialog;
                MaximizeBox = MinimizeBox = false;
                ShowInTaskbar = false;
                StartPosition = FormStartPosition.CenterParent;
                ClientSize = new Size(Theme.S(520), Theme.S(150));
                BackColor = Theme.Bg;
                SkinButton cancel = new SkinButton("Cancel", "", ButtonKind.Secondary);
                int p = cancel.ShadowPad;
                cancel.Bounds = new Rectangle(ClientSize.Width - Theme.S(24) - Theme.S(110) - p, ClientSize.Height - Theme.S(20) - Theme.S(40) - p, Theme.S(110) + 2 * p, Theme.S(40) + 2 * p);
                cancel.Click += delegate { cts.Cancel(); };
                Controls.Add(cancel);
                SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            }

            protected override void OnHandleCreated(EventArgs e)
            {
                base.OnHandleCreated(e);
                Theme.DarkTitleBar(this, Theme.Bg2);
            }

            protected override void OnShown(EventArgs e)
            {
                base.OnShown(e);
                Thread t = new Thread(Work);
                t.IsBackground = true;
                t.Start();
            }

            private void Report(string s, double f)
            {
                text = s;
                fraction = f;
                try { BeginInvoke((MethodInvoker)Invalidate); } catch (Exception) { }
            }

            private void Work()
            {
                string tmp = Path.Combine(Path.GetTempPath(), "FindAnything-picture-model.zip");
                string error = null;
                try
                {
                    ServicePointManager.SecurityProtocol |= SecurityProtocolType.Tls12;
                    HttpWebRequest req = (HttpWebRequest)WebRequest.Create(Url);
                    req.UserAgent = "FindAnything/" + AppInfo.Version;
                    using (WebResponse resp = req.GetResponse())
                    using (Stream s = resp.GetResponseStream())
                    using (FileStream f = File.Create(tmp))
                    using (SHA256 sha = SHA256.Create())
                    {
                        long total = resp.ContentLength, got = 0;
                        byte[] buf = new byte[1 << 16];
                        int n;
                        while ((n = s.Read(buf, 0, buf.Length)) > 0)
                        {
                            if (cts.IsCancellationRequested) throw new OperationCanceledException();
                            f.Write(buf, 0, n);
                            sha.TransformBlock(buf, 0, n, null, 0);
                            got += n;
                            if (total > 0) Report("Downloading: " + (got >> 20) + " of " + (total >> 20) + " MB", (double)got / total);
                        }
                        sha.TransformFinalBlock(buf, 0, 0);
                        string hex = BitConverter.ToString(sha.Hash).Replace("-", "").ToLowerInvariant();
                        if (hex != Sha256) throw new IOException("The download did not match its expected checksum, so it was not used.");
                    }
                    Report("Unpacking...", 1);
                    Directory.CreateDirectory(Clip.Folder);
                    using (FileStream zs = File.OpenRead(tmp))
                    using (ZipArchive z = new ZipArchive(zs, ZipArchiveMode.Read))
                        foreach (ZipArchiveEntry e in z.Entries)
                        {
                            if (e.FullName.EndsWith("/")) continue;
                            string dest = Path.Combine(Clip.Folder, Path.GetFileName(e.FullName));   // flat, no paths from the zip
                            using (Stream i = e.Open()) using (FileStream o = File.Create(dest)) i.CopyTo(o);
                        }
                }
                catch (OperationCanceledException) { error = null; }
                catch (Exception ex) { error = ex.Message; }
                finally { try { File.Delete(tmp); } catch (Exception) { } }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (error != null) MessageBox.Show(this, "The picture model could not be downloaded: " + error, "Picture model", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                        Close();
                    });
                }
                catch (Exception) { }
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                using (SolidBrush b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);
                int L = Theme.S(24);
                TextRenderer.DrawText(g, text, Theme.Ui(14), new Rectangle(L, Theme.S(20), ClientSize.Width - 2 * L, Theme.S(24)), Theme.Text, Theme.Left);
                Rectangle bar = new Rectangle(L, Theme.S(56), ClientSize.Width - 2 * L, Theme.S(8));
                Theme.FillRound(g, Theme.Line, bar, bar.Height / 2f);
                if (fraction > 0) Theme.FillRound(g, Theme.Amber, new Rectangle(bar.X, bar.Y, Math.Max(bar.Height, (int)(bar.Width * fraction)), bar.Height), bar.Height / 2f);
            }

            protected override void OnFormClosing(FormClosingEventArgs e)
            {
                cts.Cancel();
                base.OnFormClosing(e);
            }
        }
    }
}
