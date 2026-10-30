using System;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace Hopover.App
{
    /// <summary>
    /// "Hopover.exe --shot DIR" renders the splash and every screen with sample data to PNG files.
    /// A build check: it needs no admin rights, no engine and no second PC.
    /// </summary>
    static class Screenshots
    {
        static readonly string[] Pages = { "pledge", "welcome", "scan", "share", "connect", "import", "checkpoint", "done", "done-paid", "pledge-signed", "share-waiting", "connect-noroute" };

        public static void Run(Application app, string dir)
        {
            Directory.CreateDirectory(dir);
            Pledge.Sample = true;
            try
            {
                var splash = new Splash("1.0.0");
                Shoot(splash.Window, Path.Combine(dir, "0-splash.png"), () =>
                {
                    splash.Window.Close();
                    Next(app, dir, 0);
                });
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(dir, "error.txt"), ex.ToString());
                app.Shutdown(1);
            }
        }

        static void Next(Application app, string dir, int i)
        {
            if (i >= Pages.Length) { app.Shutdown(0); return; }
            try
            {
                // Sample pledge state: unsigned only on the pledge shot, paid only on the thank-you shot.
                Pledge.Signed = !Pages[i].StartsWith("pledge");
                Pledge.SignedAt = "2026-10-30 09:14";
                Pledge.Paid = Pages[i] == "done-paid";
                var c = new MainController("", "");
                if (Pages[i] != "welcome") c.Demo(Pages[i]);
                Shoot(c.Window, Path.Combine(dir, (Pages[i] == "pledge-signed" ? "1b" : (i + 1).ToString()) + "-" + Pages[i] + ".png"), () =>
                {
                    c.Window.Close();
                    Next(app, dir, i + 1);
                });
            }
            catch (Exception ex)
            {
                File.WriteAllText(Path.Combine(dir, "error.txt"), Pages[i] + ": " + ex);
                app.Shutdown(1);
            }
        }

        static void Shoot(Window w, string file, Action then)
        {
            w.ShowActivated = false;
            w.ShowInTaskbar = false;
            w.WindowStartupLocation = WindowStartupLocation.Manual;
            w.Left = -20000;
            w.Top = 0;
            w.Show();
            // Wait for the page fade-in and the splash animation to settle.
            var t = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(900) };
            t.Tick += (s, e) =>
            {
                t.Stop();
                // The window itself, not its content: a margin on the content would crop the image.
                var root = (FrameworkElement)w;
                var bmp = new RenderTargetBitmap((int)Math.Ceiling(root.ActualWidth), (int)Math.Ceiling(root.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                var bg = new DrawingVisual();
                using (var dc = bg.RenderOpen())
                {
                    if (w.Background is SolidColorBrush b && b.Color.A > 0) dc.DrawRectangle(b, null, new Rect(0, 0, root.ActualWidth, root.ActualHeight));
                }
                bmp.Render(bg);
                bmp.Render(root);
                var enc = new PngBitmapEncoder();
                enc.Frames.Add(BitmapFrame.Create(bmp));
                using (var f = File.Create(file)) enc.Save(f);
                then();
            };
            t.Start();
        }
    }
}
