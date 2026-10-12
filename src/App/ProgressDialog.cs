using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Threading;
using System.Windows.Forms;
using FindAnything.Core;
using FindAnything.Ui;

namespace FindAnything.App
{
    /// <summary>A unit of work run behind a small dialog: progress bar, Cancel, and a result line with an optional folder to open.</summary>
    internal sealed class ProgressDialog : Form
    {
        public delegate string Work(Action<string, double> report, CancellationToken ct);

        private string text = "Starting...", doneText;
        private double fraction;
        private string openFolder;
        private readonly CancellationTokenSource cts = new CancellationTokenSource();
        private readonly Work work;
        private readonly SkinButton cancel = new SkinButton("Cancel", "", ButtonKind.Secondary);
        private readonly SkinButton open = new SkinButton("Open folder", Theme.GOpenFolder, ButtonKind.Primary);

        /// <summary>Runs the work; returns when the dialog closes. The work returns the line shown when it is done.</summary>
        public static void Run(IWin32Window owner, string title, string folderWhenDone, Work work)
        {
            using (ProgressDialog d = new ProgressDialog(title, folderWhenDone, work)) d.ShowDialog(owner);
        }

        private ProgressDialog(string title, string folderWhenDone, Work work)
        {
            this.work = work;
            openFolder = folderWhenDone;
            AutoScaleMode = AutoScaleMode.None;
            Text = title;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = new Size(Theme.S(560), Theme.S(156));
            BackColor = Theme.Bg;
            SetStyle(ControlStyles.OptimizedDoubleBuffer | ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
            Place(cancel, 0);
            cancel.Click += delegate { if (doneText != null) Close(); else cts.Cancel(); };
            open.Visible = false;
            open.Click += delegate { try { Process.Start("explorer.exe", "\"" + openFolder + "\""); } catch (Exception) { } Close(); };
            Controls.Add(cancel);
            Controls.Add(open);
        }

        private void Place(SkinButton b, int slot)
        {
            int p = b.ShadowPad, w = Math.Max(Theme.S(120), b.PreferredWidth(40) - 2 * p), h = Theme.S(40);
            int right = ClientSize.Width - Theme.S(24) - slot * (Theme.S(130) + Theme.S(12));
            b.Bounds = new Rectangle(right - w - p, ClientSize.Height - Theme.S(20) - h - p, w + 2 * p, h + 2 * p);
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            Theme.DarkTitleBar(this, Theme.Bg2);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Thread t = new Thread(delegate()
            {
                string result;
                try { result = work(Report, cts.Token); }
                catch (OperationCanceledException) { result = "Stopped."; }
                catch (Exception ex) { result = "Failed: " + ex.Message; }
                try { BeginInvoke((MethodInvoker)delegate { Finish(result); }); } catch (Exception) { }
            });
            t.IsBackground = true;
            t.Start();
        }

        private void Report(string s, double f)
        {
            text = s;
            fraction = f;
            try { BeginInvoke((MethodInvoker)Invalidate); } catch (Exception) { }
        }

        private void Finish(string result)
        {
            doneText = result ?? "Done.";
            fraction = 1;
            cancel.Text = "Close";
            if (openFolder != null && Directory.Exists(openFolder))
            {
                Place(cancel, 1);
                Place(open, 0);
                open.Visible = true;
                open.Focus();
            }
            Invalidate();
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            Graphics g = e.Graphics;
            using (SolidBrush b = new SolidBrush(Theme.Bg)) g.FillRectangle(b, ClientRectangle);
            int L = Theme.S(24);
            TextRenderer.DrawText(g, doneText ?? text, Theme.Ui(14), new Rectangle(L, Theme.S(18), ClientSize.Width - 2 * L, Theme.S(40)), Theme.Text,
                TextFormatFlags.Left | TextFormatFlags.WordBreak | TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix);
            Rectangle bar = new Rectangle(L, Theme.S(62), ClientSize.Width - 2 * L, Theme.S(8));
            Theme.FillRound(g, Theme.Line, bar, bar.Height / 2f);
            if (fraction > 0) Theme.FillRound(g, doneText != null ? Theme.Green : Theme.Amber, new Rectangle(bar.X, bar.Y, Math.Max(bar.Height, (int)(bar.Width * Math.Min(1, fraction))), bar.Height), bar.Height / 2f);
        }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            cts.Cancel();
            base.OnFormClosing(e);
        }
    }
}
