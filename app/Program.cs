using System;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;

[assembly: AssemblyTitle("Hopover")]
[assembly: AssemblyProduct("Hopover")]
[assembly: AssemblyDescription("Moves a Windows PC to a new one over the LAN")]
[assembly: AssemblyCompany("Ilscipio")]
[assembly: AssemblyCopyright("(c) 2026 Ilscipio")]
[assembly: AssemblyVersion("1.0.0.0")]
[assembly: AssemblyFileVersion("1.0.0.0")]
[assembly: AssemblyInformationalVersion("1.0.0")]

namespace Hopover.App
{
    static class Program
    {
        public const string Store = @"C:\hopover-store";
        static readonly string Version = "1.0.0";

        [STAThread]
        static int Main(string[] args)
        {
            // Headless link verbs (serve, copy, selftest...) run before any WPF code: they start fast and need no window.
            if (args.Length >= 1 && args[0] == "--link") return Hopover.Link.LinkMain.Run(args.Skip(1).ToArray());
            var app = new Application { ShutdownMode = ShutdownMode.OnExplicitShutdown };
            Pledge.Load();
            app.Resources.MergedDictionaries.Add(Res.Load<ResourceDictionary>("Theme.xaml"));
            int shot = Array.IndexOf(args, "--shot");
            app.DispatcherUnhandledException += (s, e) =>
            {
                e.Handled = true;
                File.WriteAllText(Path.Combine(Path.GetTempPath(), "hopover-crash.txt"), e.Exception.ToString());
                // The screenshot run has nobody to close a message box: fail fast instead.
                if (shot >= 0) { File.WriteAllText(Path.Combine(args[shot + 1], "error.txt"), e.Exception.ToString()); app.Shutdown(1); return; }
                MessageBox.Show(e.Exception.Message + "\n\nDetails: %TEMP%\\hopover-crash.txt", "Hopover");
            };

            if (shot >= 0) { app.Dispatcher.BeginInvoke(new Action(() => Screenshots.Run(app, args[shot + 1]))); return app.Run(); }

            var splash = new Splash(Version);
            splash.Show();
            app.Dispatcher.BeginInvoke(new Action(async () => await Startup(app, splash, args)));
            return app.Run();
        }

        static string Arg(string[] args, string name)
        {
            int i = Array.IndexOf(args, name);
            return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
        }

        static async Task Startup(Application app, Splash splash, string[] args)
        {
            var started = DateTime.Now;
            string exe = Process.GetCurrentProcess().MainModule.FileName;

            if (!Win32.IsAdmin())
            {
                splash.Status("Asking for administrator rights");
                try
                {
                    // An elevated process cannot read the share or network path this one came from:
                    // run a local copy.
                    string run = exe;
                    if (!Win32.IsLocalFixedPath(exe))
                    {
                        string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hopover");
                        Directory.CreateDirectory(dir);
                        run = Path.Combine(dir, "Hopover.exe");
                        // The copy from an earlier run can still run (or serve the link): then one copy per build.
                        try { File.Copy(exe, run, true); }
                        catch (IOException)
                        {
                            run = Path.Combine(dir, "Hopover-" + File.GetLastWriteTimeUtc(exe).Ticks + ".exe");
                            if (!File.Exists(run)) File.Copy(exe, run, false);
                        }
                        string custom = Path.Combine(Path.GetDirectoryName(exe), "include.json");
                        if (File.Exists(custom)) File.Copy(custom, Path.Combine(dir, "include.json"), true);
                    }
                    Process.Start(new ProcessStartInfo(run, "--session-user \"" + Environment.UserName + "\"") { Verb = "runas", UseShellExecute = true });
                    await Task.Delay(300);
                    app.Shutdown(0);
                }
                catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
                {
                    splash.Error("Hopover needs administrator rights: it installs apps and moves settings and scheduled tasks.",
                                 () => { splash.ClearError(); _ = Startup(app, splash, args); }, () => app.Shutdown(1));
                }
                catch (Exception ex)
                {
                    // Without this the splash would stay and the process never end (explicit shutdown).
                    splash.Error("Hopover could not start: " + ex.Message,
                                 () => { splash.ClearError(); _ = Startup(app, splash, args); }, () => app.Shutdown(1));
                }
                return;
            }

            string sessionUser = Arg(args, "--session-user");
            if (sessionUser != null && !string.Equals(sessionUser, Environment.UserName, StringComparison.OrdinalIgnoreCase))
            {
                // UAC with another admin's password: Hopover would move into THAT user's profile.
                splash.Error("Hopover runs as " + Environment.UserName + ", but you are signed in as " + sessionUser +
                             ". Make " + sessionUser + " an administrator, sign in again and start Hopover again.", null, () => app.Shutdown(1));
                return;
            }

            splash.Status("Unpacking the engine");
            string engineDir;
            try { engineDir = await Task.Run(() => EngineFiles.Extract(exe)); }
            catch (Exception ex) { splash.Error("The engine could not be unpacked: " + ex.Message, null, () => app.Shutdown(1)); return; }

            splash.Status("Checking Windows PowerShell");
            string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            if (!File.Exists(ps)) { splash.Error("Windows PowerShell 5.1 is missing on this PC.", null, () => app.Shutdown(1)); return; }

            splash.Status("Opening");
            MainController main;
            try { main = new MainController(engineDir, exe); }
            catch (Exception ex) { splash.Error("The window could not open: " + ex.Message, null, () => app.Shutdown(1)); return; }
            var wait = TimeSpan.FromMilliseconds(1600) - (DateTime.Now - started);
            if (wait > TimeSpan.Zero) await Task.Delay(wait);
            main.Window.Closed += (s, e) => app.Shutdown(0);
            main.Window.Show();
            main.Window.Activate();
            splash.FadeOutAndClose();
        }
    }

    /// <summary>The load screen: the arch of the mark hops (1.4 s loop) and a crate rides the hop line.</summary>
    class Splash
    {
        readonly Window w;
        Action retryAction, quitAction;

        public Splash(string version)
        {
            w = Res.Load<Window>("Splash.xaml");
            ((Button)w.FindName("BtnRetry")).Click += (s, e) => retryAction?.Invoke();
            ((Button)w.FindName("BtnQuit")).Click += (s, e) => quitAction?.Invoke();
            ((TextBlock)w.FindName("Version")).Text = "v" + version;
            w.SourceInitialized += (s, e) => Win32.StyleWindow(new System.Windows.Interop.WindowInteropHelper(w).Handle);
            w.Loaded += (s, e) => Animate();
        }

        public void Show() => w.Show();

        void Animate()
        {
            // The mark and the hop line animate themselves (Motion.cs); here only the fade-in.
            if (!Motion.Enabled) return;
            w.Opacity = 0;
            w.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)));
        }

        public void Status(string text) => ((TextBlock)w.FindName("Status")).Text = text + "...";

        public void Error(string text, Action retry, Action quit)
        {
            ((UIElement)w.FindName("Track")).Visibility = Visibility.Collapsed;
            ((UIElement)w.FindName("Status")).Visibility = Visibility.Collapsed;
            ((TextBlock)w.FindName("ErrorText")).Text = text;
            ((UIElement)w.FindName("ErrorPanel")).Visibility = Visibility.Visible;
            ((Button)w.FindName("BtnRetry")).Visibility = retry == null ? Visibility.Collapsed : Visibility.Visible;
            retryAction = retry;
            quitAction = quit;
        }

        public void ClearError()
        {
            ((UIElement)w.FindName("ErrorPanel")).Visibility = Visibility.Collapsed;
            ((UIElement)w.FindName("Track")).Visibility = Visibility.Visible;
            ((UIElement)w.FindName("Status")).Visibility = Visibility.Visible;
        }

        public void FadeOutAndClose()
        {
            if (!Motion.Enabled) { w.Close(); return; }
            var a = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(180));
            a.Completed += (s, e) => w.Close();
            w.BeginAnimation(UIElement.OpacityProperty, a);
        }

        public Window Window => w;
    }
}
