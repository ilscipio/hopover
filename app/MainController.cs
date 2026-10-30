using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shell;
using System.Text.RegularExpressions;

namespace Hopover.App
{
    /// <summary>All screens of the wizard. The engine does the work; this class only shows it.</summary>
    public class MainController
    {
        const string Store = Program.Store;

        static readonly (string Key, string Label)[] ExportSteps =
        {
            ("apps", "App list"), ("env", "Environment variables"), ("settings", "Windows and app settings"),
            ("certs", "Certificates"), ("tasks", "Scheduled tasks"), ("wsl", "WSL distros"), ("docker", "Docker volumes"),
        };

        static readonly (string Key, string Label)[] ImportSteps =
        {
            ("env", "Environment variables and PATH"), ("apps", "Apps (winget, in the background)"), ("files", "Files and folders"),
            ("certs", "Certificates"), ("wsl", "WSL distros"), ("settings", "Windows and app settings"),
            ("docker", "Docker volumes"), ("tasks", "Scheduled tasks (switched off)"),
        };

        static readonly Dictionary<string, string> SwitchLabels = new Dictionary<string, string>
        {
            ["apps"] = "Apps (winget list)", ["env"] = "Environment variables", ["files"] = "Files and folders",
            ["settings"] = "Windows and app settings, Wi-Fi", ["certs"] = "Personal certificates", ["tasks"] = "Scheduled tasks",
            ["wsl"] = "WSL distros", ["docker"] = "Docker named volumes",
        };

        public readonly Window Window;
        readonly string engineDir;
        readonly string exePath;
        readonly EngineRun engine;
        readonly Dictionary<string, FrameworkElement> pages = new Dictionary<string, FrameworkElement>();
        string currentPage;

        ObservableCollection<GroupRow> groups = new ObservableCollection<GroupRow>();
        readonly ObservableCollection<StepRow> exportRows = new ObservableCollection<StepRow>();
        readonly ObservableCollection<StepRow> importRows = new ObservableCollection<StepRow>();
        readonly ObservableCollection<ActiveRow> active = new ObservableCollection<ActiveRow>();
        readonly ObservableCollection<CheckRow> checks = new ObservableCollection<CheckRow>();
        readonly Dictionary<string, CheckBox> oldSwitches = new Dictionary<string, CheckBox>();
        readonly Dictionary<string, CheckBox> newSwitches = new Dictionary<string, CheckBox>();
        readonly Queue<(DateTime At, long Bytes)> rateSamples = new Queue<(DateTime, long)>();
        readonly List<AppRow> installed = new List<AppRow>();
        bool checkpointShown;
        int sumApps, sumTasks, sumDistros;
        long sumBytes;
        double lastFrac;
        string lastRouteText = "";
        string returnPage = "PageImport";
        string from;
        bool certsOnOldPc;
        int chunksDone, chunksTotal;
        Action dialogOk;

        public MainController(string engineDir, string exePath)
        {
            this.engineDir = engineDir;
            this.exePath = exePath;
            Window = Res.Load<Window>("Main.xaml");
            engine = new EngineRun(Window.Dispatcher);

            foreach (var p in new[] { "PagePledge", "PageWelcome", "PageScan", "PageExport", "PageConnect", "PageImport", "PageCheckpoint", "PageDone", "PageChecklist" })
                pages[p] = F<FrameworkElement>(p);

            SetupChrome();
            F<ListBox>("PlanList").ItemsSource = groups;
            F<ItemsControl>("ExportSteps").ItemsSource = exportRows;
            F<ItemsControl>("ImportSteps").ItemsSource = importRows;
            F<ItemsControl>("ActiveList").ItemsSource = active;
            F<ListBox>("CheckList").ItemsSource = checks;
            SetupCoffee();

            Click("BtnOld", StartScan);
            Click("BtnNew", () => { ShowPage("PageConnect", "New PC"); F<TextBox>("FromBox").Focus(); });
            Click("BtnExport", StartExport);
            Click("BtnStopShare", StopSharing);
            Click("BtnConnect", () => _ = Connect());
            Click("BtnImport", StartImport);
            Click("BtnChecklist", ShowChecklist);
            Click("BtnCheckBack", () => ShowPage(returnPage, "New PC"));
            Click("BtnCheckOpen", () => OpenFile(Path.Combine(Store, "CHECKLIST.md")));
            Click("BtnLogs", () => OpenFile(Path.Combine(Store, "logs")));
            Click("BtnTasks", EnableTasks);
            Click("BtnVerify", Verify);
            Click("BtnExportLog", () => ToggleLog("ExportLogBorder", "BtnExportLog"));
            Click("BtnImportLog", () => ToggleLog("ImportLogBorder", "BtnImportLog"));
            Click("BtnCopyPc", () => Copy(F<TextBlock>("SharePc").Text));
            Click("BtnCopyIp", () => Copy(F<TextBlock>("ShareIp").Text));
            Click("BtnCopyUser", () => Copy(F<TextBlock>("ShareUser").Text));
            Click("DlgOk", () => { HideDialog(); var a = dialogOk; dialogOk = null; a?.Invoke(); });
            Click("DlgCancel", HideDialog);

            F<TextBox>("FromBox").LostFocus += (s, e) =>
            {
                string pc = F<TextBox>("FromBox").Text.Trim().TrimStart('\\');
                if (pc.Length > 0 && F<TextBox>("UserBox").Text.Length == 0 && !char.IsDigit(pc[0]))
                    F<TextBox>("UserBox").Text = pc + "\\" + Environment.UserName;
            };
            F<PasswordBox>("PwBox").KeyDown += (s, e) => { if (e.Key == Key.Enter) _ = Connect(); };
            foreach (var r in new[] { "SpeedGentle", "SpeedBalanced", "SpeedFull" })
                F<RadioButton>(r).Checked += (s, e) => UpdateSpeedHint();
            UpdateSpeedHint();

            engine.OnLine = AppendLog;
            if (File.Exists(Path.Combine(Store, "state-import.json")))
                F<UIElement>("ResumeBanner").Visibility = Visibility.Visible;
            if (Pledge.Signed) ShowPage("PageWelcome", ""); else ShowPledge();
            Window.Closing += OnClosing;
        }

        // ---------- helpers ----------
        T F<T>(string name) where T : class => (T)Window.FindName(name);
        void Click(string name, Action a) => F<Button>(name).Click += (s, e) => a();

        void SetupChrome()
        {
            WindowChrome.SetWindowChrome(Window, new WindowChrome
            {
                CaptionHeight = 56,
                ResizeBorderThickness = new Thickness(6),
                GlassFrameThickness = new Thickness(0),
                CornerRadius = new CornerRadius(0),
                UseAeroCaptionButtons = false,
            });
            foreach (var b in new[] { "BtnMin", "BtnMax", "BtnClose" })
                WindowChrome.SetIsHitTestVisibleInChrome(F<Button>(b), true);
            Click("BtnMin", () => Window.WindowState = WindowState.Minimized);
            Click("BtnMax", () => Window.WindowState = Window.WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized);
            Click("BtnClose", () => Window.Close());
            Window.SourceInitialized += (s, e) => Win32.StyleWindow(new WindowInteropHelper(Window).Handle);
            Window.StateChanged += (s, e) =>
            {
                // A maximized chrome-less window reaches past the screen edge by the frame width.
                bool max = Window.WindowState == WindowState.Maximized;
                F<Grid>("Root").Margin = new Thickness(max ? 7 : 0);
                F<Button>("BtnMax").Tag = Window.FindResource(max ? "GlyphRestore" : "GlyphMax");
            };
        }

        public void ShowPage(string name, string crumb)
        {
            if (currentPage == name) return;
            foreach (var kv in pages) kv.Value.Visibility = kv.Key == name ? Visibility.Visible : Visibility.Collapsed;
            currentPage = name;
            bool route = name == "PageImport" || name == "PageExport" || name == "PageCheckpoint" || name == "PageDone";
            F<UIElement>("RouteBar").Visibility = route ? Visibility.Visible : Visibility.Collapsed;
            F<TextBlock>("Crumb").Text = crumb.ToUpperInvariant();
            if (route) SetRoute(lastFrac, lastRouteText);
            var page = pages[name];
            if (!Motion.Enabled) return;
            var ease = new CubicEase { EasingMode = EasingMode.EaseOut };
            page.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
            if (page.RenderTransform is TranslateTransform tt)
                tt.BeginAnimation(TranslateTransform.YProperty, new DoubleAnimation(10, 0, TimeSpan.FromMilliseconds(180)) { EasingFunction = ease });
        }

        /// <summary>The route in the header: ink up to here, hazard at the front, dashes ahead.</summary>
        void SetRoute(double frac, string text)
        {
            frac = Math.Max(0, Math.Min(1, frac));
            lastFrac = frac; lastRouteText = text;
            var cols = F<Grid>("RouteBar").ColumnDefinitions;
            double tip = frac >= 1 || frac <= 0 ? 0 : 0.05;
            cols[0].Width = new GridLength(Math.Max(0.0001, frac - tip), GridUnitType.Star);
            cols[1].Width = new GridLength(Math.Max(0.0001, tip), GridUnitType.Star);
            cols[2].Width = new GridLength(Math.Max(0.0001, 1 - frac), GridUnitType.Star);
            F<UIElement>("RouteTip").Visibility = tip > 0 ? Visibility.Visible : Visibility.Collapsed;
            if (text.Length > 0 && (currentPage == "PageImport" || currentPage == "PageExport" || currentPage == "PageCheckpoint" || currentPage == "PageDone"))
                F<TextBlock>("Crumb").Text = text;
        }

        static void Fade(UIElement e, int ms)
        {
            if (Motion.Enabled) e.BeginAnimation(UIElement.OpacityProperty, new DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(ms)));
        }

        void ShowDialog(string title, string text, string ok, string cancel, Action onOk)
        {
            F<TextBlock>("DlgTitle").Text = title;
            F<TextBlock>("DlgText").Text = text;
            F<Button>("DlgOk").Content = ok;
            F<Button>("DlgCancel").Content = cancel ?? "";
            F<Button>("DlgCancel").Visibility = cancel == null ? Visibility.Collapsed : Visibility.Visible;
            dialogOk = onOk;
            var o = F<UIElement>("Overlay");
            o.Visibility = Visibility.Visible;
            Fade(o, 180);
        }

        void HideDialog() => F<UIElement>("Overlay").Visibility = Visibility.Collapsed;

        void AppendLog(string line)
        {
            var box = currentPage == "PageExport" ? F<TextBox>("ExportLog") : F<TextBox>("ImportLog");
            box.AppendText(line + "\n");
            // Keep the box light; the full log is in C:\hopover-store\logs.
            if (box.Text.Length > 300_000) box.Text = box.Text.Substring(box.Text.Length - 150_000);
            box.ScrollToEnd();
        }

        void ToggleLog(string border, string button)
        {
            var b = F<UIElement>(border);
            bool show = b.Visibility != Visibility.Visible;
            b.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            F<Button>(button).Content = show ? "Hide details" : "Show details";
        }

        static void Copy(string text) { try { Clipboard.SetText(text ?? ""); } catch { } }

        static void OpenFile(string path)
        {
            try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); } catch { }
        }

        Dictionary<string, CheckBox> BuildSwitches(string panel, IEnumerable<string> keys, ICollection<string> done)
        {
            var p = F<StackPanel>(panel);
            p.Children.Clear();
            var map = new Dictionary<string, CheckBox>();
            foreach (var k in keys)
            {
                var content = new StackPanel();
                content.Children.Add(new TextBlock { Text = SwitchLabels[k] });
                if (done.Contains(k))
                    content.Children.Add(new TextBlock { Text = "Done in an earlier run", FontSize = 12, Foreground = (Brush)Window.FindResource("Steel") });
                var cb = new CheckBox { Style = (Style)Window.FindResource("Switch"), Content = content, IsChecked = !done.Contains(k), Tag = k };
                p.Children.Add(cb);
                map[k] = cb;
            }
            return map;
        }

        static List<string> Checked(Dictionary<string, CheckBox> map) =>
            map.Where(kv => kv.Value.IsChecked == true).Select(kv => kv.Key).ToList();

        static void FillSteps(ObservableCollection<StepRow> rows, (string Key, string Label)[] steps, ICollection<string> selected)
        {
            rows.Clear();
            for (int i = 0; i < steps.Length; i++)
                rows.Add(new StepRow
                {
                    Key = steps[i].Key, No = (i + 1).ToString("00"), Label = steps[i].Label, IsLast = i == steps.Length - 1,
                    State = selected == null || selected.Contains(steps[i].Key) ? "pending" : "skipped",
                });
        }

        static void SetStep(ObservableCollection<StepRow> rows, string data)
        {
            var parts = data.Split(new[] { ' ' }, 2);
            var row = rows.FirstOrDefault(r => r.Key == parts[0]);
            if (row == null || parts.Length < 2) return;
            row.State = parts[1] == "start" ? "running" : parts[1];
        }

        // ---------- OLD PC ----------
        void StartScan()
        {
            ShowPage("PageScan", "Old PC");
            if (engine.Running) return;
            oldSwitches.Clear();
            foreach (var kv in BuildSwitches("OldSteps", ExportSteps.Select(s => s.Key), new string[0])) oldSwitches[kv.Key] = kv.Value;
            groups.Clear();
            F<UIElement>("ScanWait").Visibility = Visibility.Visible;
            F<Button>("BtnExport").IsEnabled = false;
            F<ProgressBar>("ScanBar").Value = 0;
            F<TextBlock>("ScanText").Text = "Listing the folders";
            engine.OnEvent = (k, d) =>
            {
                if (k != "SCAN") return;
                var p = d.Split(new[] { ' ' }, 3);
                double i = double.Parse(p[0]), n = Math.Max(1, double.Parse(p[1]));
                F<ProgressBar>("ScanBar").Value = i / n;
                F<TextBlock>("ScanText").Text = "Measured " + p[0] + " of " + p[1] + "  ·  " + (p.Length > 2 ? p[2] : "");
            };
            engine.OnExit = code =>
            {
                var m = Json.Read(Path.Combine(Store, "manifest.json"));
                if (code != 0 || m == null)
                {
                    F<TextBlock>("ScanText").Text = "The scan failed. The log is in " + Store + "\\logs.";
                    return;
                }
                LoadGroups(m);
                F<UIElement>("ScanWait").Visibility = Visibility.Collapsed;
                F<ProgressBar>("ScanBar").Value = 1;
                F<TextBlock>("ScanText").Text = "Scan done. Untick what must not move.";
                certsOnOldPc = Json.Long(m, "Certs") > 0;
                F<UIElement>("OldCertPanel").Visibility = certsOnOldPc ? Visibility.Visible : Visibility.Collapsed;
                F<Button>("BtnExport").IsEnabled = true;
            };
            engine.Start(engineDir, new[] { "inventory", "-Store", Store, "-Speed", "balanced" });
        }

        void LoadGroups(object manifest)
        {
            var rows = Json.List(Json.Get(manifest, "Plan"))
                .GroupBy(p => Json.Str(p, "Group") ?? Json.Str(p, "Path"))
                .Select(g => new GroupRow
                {
                    Path = g.Key,
                    Name = DisplayName(g.Key),
                    Why = Json.Str(g.First(), "Why"),
                    Bytes = g.Sum(p => Json.Long(p, "Bytes")),
                    Files = g.Sum(p => Json.Long(p, "Files")),
                })
                .OrderByDescending(r => r.Bytes).ToList();
            double max = Math.Max(1, rows.Count > 0 ? rows[0].Bytes : 1);
            groups.Clear();
            int code = 0;
            foreach (var r in rows)
            {
                r.Code = "HOP-" + (++code).ToString("0000");
                // Square root: small folders still get a visible bar next to a 500 GB one.
                r.BarRatio = Math.Sqrt(r.Bytes / max);
                r.PropertyChanged += (s, e) => { if (e.PropertyName == "Include") UpdateTotal(); };
                groups.Add(r);
            }
            UpdateTotal();
        }

        static string DisplayName(string path)
        {
            if (path.Length <= 3 && path.EndsWith(":\\")) return "Drive " + path.Substring(0, 1);
            string name = Path.GetFileName(path.TrimEnd('\\'));
            return string.IsNullOrEmpty(name) ? path : name;
        }

        void UpdateTotal()
        {
            var on = groups.Where(g => g.Include).ToList();
            long bytes = on.Sum(g => g.Bytes);
            F<TextBlock>("TotalBig").Text = Fmt.Size(bytes);
            // About 100 MB/s over a 1 Gbit/s LAN; many small files make it slower.
            F<TextBlock>("TotalSub").Text = on.Count + " of " + groups.Count + " folders  ·  " +
                                            on.Sum(g => g.Files).ToString("N0") + " files\n" +
                                            Fmt.Duration(bytes / 100e6) + " over a 1 Gbit/s network";
        }

        void StartExport()
        {
            if (engine.Running) return;
            var steps = Checked(oldSwitches);
            string certPw = F<PasswordBox>("OldCertPw").Password;
            if (steps.Contains("certs") && certsOnOldPc && certPw.Length == 0)
            {
                ShowDialog("Certificate password", "Type a password for the certificates, or switch the certificates off.", "OK", null, null);
                return;
            }
            Json.Write(Path.Combine(Store, "deselected.json"), groups.Where(g => !g.Include).Select(g => g.Path).ToArray());
            ShowPage("PageExport", "Old PC");
            FillSteps(exportRows, ExportSteps, steps);
            F<UIElement>("ShareCard").Visibility = Visibility.Collapsed;
            F<UIElement>("ExportWorking").Visibility = Visibility.Visible;
            F<TextBlock>("ExportTitle").Text = "Preparing the move";
            ExportRoute();
            engine.OnEvent = (k, d) =>
            {
                if (k == "STEP") { SetStep(exportRows, d); ExportRoute(); }
                if (k == "SHARE") ApplyShare(d);
            };
            engine.OnExit = code =>
            {
                F<UIElement>("ExportWorking").Visibility = Visibility.Collapsed;
                F<TextBlock>("ExportTitle").Text = code == 0 ? "This PC is ready" : code == 2 ? "Ready, but some steps failed" : "Something failed";
                SetRoute(code == 0 ? 1 : lastFrac, code == 0 ? "READY · SHARED READ-ONLY" : "STOPPED");
                if (code == 1) ToggleLogOpen("ExportLogBorder", "BtnExportLog");
            };
            engine.Start(engineDir, new[] { "export", "-Store", Store, "-Only", string.Join(",", steps.Concat(new[] { "none" })) },
                         new Dictionary<string, string> { ["HOPOVER_EXE"] = exePath, ["HOPOVER_CERT_PW"] = certPw.Length > 0 ? certPw : null });
        }

        void ExportRoute()
        {
            int n = exportRows.Count, d = exportRows.Count(r => r.State == "done" || r.State == "skipped" || r.State == "failed");
            SetRoute(n == 0 ? 0 : (double)d / n, "PACKING  ·  " + d + " / " + n);
        }

        void ApplyShare(string d)
        {
            var p = d.Split('|');
            F<TextBlock>("SharePc").Text = p[0];
            F<TextBlock>("ShareUser").Text = p.Length > 1 ? p[1] : "";
            F<TextBlock>("ShareIp").Text = p.Length > 2 ? p[2].Split(',')[0] : "";
            F<TextBlock>("ShareWhere").Text = @"On the new PC, start Hopover.exe from OneDrive\Hopover (or \\" + p[0] + @"\hopover-store\tool) and choose 'This is the new PC'.";
            F<UIElement>("ExportWorking").Visibility = Visibility.Collapsed;
            var card = F<FrameworkElement>("ShareCard");
            card.Visibility = Visibility.Visible;
            Fade(card, 180);
        }

        void ToggleLogOpen(string border, string button)
        {
            if (F<UIElement>(border).Visibility != Visibility.Visible) ToggleLog(border, button);
        }

        void StopSharing()
        {
            if (engine.Running) return;
            engine.OnEvent = null;
            engine.OnExit = code => ShowDialog("Sharing stopped",
                "Delete " + Store + " on both PCs when the new PC works: it holds Wi-Fi passwords and tokens.", "OK", null, null);
            engine.Start(engineDir, new[] { "cleanup", "-Store", Store });
        }

        // ---------- NEW PC ----------
        async Task Connect()
        {
            string pc = F<TextBox>("FromBox").Text.Trim().TrimStart('\\');
            string user = F<TextBox>("UserBox").Text.Trim();
            string pw = F<PasswordBox>("PwBox").Password;
            var msg = F<TextBlock>("ConnectText");
            if (pc.Length == 0) { msg.Text = "Type the name or IP address of the old PC."; msg.Foreground = (Brush)Window.FindResource("RustDark"); return; }
            F<Button>("BtnConnect").IsEnabled = false;
            F<UIElement>("ConnectSpin").Visibility = Visibility.Visible;
            msg.Text = "Connecting to " + pc;
            msg.Foreground = (Brush)Window.FindResource("Ink2");
            string manifestPath = "\\\\" + pc + "\\hopover-store\\manifest.json";
            string error = await Task.Run(() =>
            {
                if (File.Exists(manifestPath)) return null;
                if (user.Length == 0) return "Type the user of the old PC.";
                Net("use \\\\" + pc + "\\IPC$ /delete /y");
                var (code, output) = Net("use \\\\" + pc + "\\IPC$ " + EngineRun.Quote(pw) + " " + EngineRun.Quote("/user:" + user) + " /persistent:no");
                if (code != 0) return "Login failed. " + output.Trim();
                return File.Exists(manifestPath) ? null : "Connected, but there is no Hopover share. Click 'Prepare the move' on the old PC first.";
            });
            F<Button>("BtnConnect").IsEnabled = true;
            F<UIElement>("ConnectSpin").Visibility = Visibility.Collapsed;
            if (error != null) { msg.Text = error; msg.Foreground = (Brush)Window.FindResource("RustDark"); return; }

            from = pc;
            var m = Json.Read(manifestPath);
            var apps = Json.Read("\\\\" + pc + "\\hopover-store\\apps\\winget.json");
            int appCount = Json.List(Json.Get(apps, "Sources")).Sum(s => Json.List(Json.Get(s, "Packages")).Count());
            int taskCount = Json.List(Json.Read("\\\\" + pc + "\\hopover-store\\tasks\\tasks.json")).Count();
            var plan = Json.List(Json.Get(m, "Plan")).ToList();
            long bytes = plan.Sum(p => Json.Long(p, "Bytes"));
            sumApps = appCount; sumBytes = bytes; sumTasks = taskCount;
            var distros = Json.List(Json.Get(m, "Distros")).Select(x => x.ToString()).Where(x => !x.StartsWith("docker-desktop")).ToList();
            sumDistros = distros.Count;
            msg.Text = "Connected.";
            msg.Foreground = (Brush)Window.FindResource("Ink");
            F<TextBlock>("SummaryTitle").Text = Json.Str(m, "Computer") + "  ·  " + Json.Str(m, "User");
            F<TextBlock>("SummaryText").Text = appCount + " apps  ·  " + Fmt.Size(bytes) + " in " + plan.Count + " chunks  ·  " +
                                               taskCount + " scheduled tasks" + (distros.Count > 0 ? "  ·  WSL: " + string.Join(", ", distros) : "");
            var state = Json.Read(Path.Combine(Store, "state-import.json"));
            var done = Json.List(Json.Get(state, "Done")).Select(x => x.ToString()).Where(x => x != "files").ToList();
            newSwitches.Clear();
            foreach (var kv in BuildSwitches("NewSteps", ImportSteps.Select(s => s.Key), done)) newSwitches[kv.Key] = kv.Value;
            F<UIElement>("NewCertPanel").Visibility = Directory.Exists("\\\\" + pc + "\\hopover-store\\certs") &&
                Directory.GetFiles("\\\\" + pc + "\\hopover-store\\certs", "*.pfx").Length > 0 ? Visibility.Visible : Visibility.Collapsed;
            var card = F<FrameworkElement>("ImportCard");
            card.Visibility = Visibility.Visible;
            Fade(card, 180);
        }

        static (int, string) Net(string args)
        {
            var psi = new ProcessStartInfo("net.exe", args) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            using (var p = Process.Start(psi))
            {
                string o = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
                p.WaitForExit();
                return (p.ExitCode, o);
            }
        }

        string Speed => F<RadioButton>("SpeedGentle").IsChecked == true ? "gentle" : F<RadioButton>("SpeedFull").IsChecked == true ? "full" : "balanced";

        void UpdateSpeedHint()
        {
            F<TextBlock>("SpeedHint").Text =
                Speed == "gentle" ? "2 copies at once, lowest priority. Best while you work on this PC." :
                Speed == "full" ? "8 copies at once, normal priority. Fastest; the PC feels busy." :
                "4 copies at once, low CPU and disk priority. Your own programs come first.";
        }

        void StartImport()
        {
            if (engine.Running) return;
            var steps = Checked(newSwitches);
            if (steps.Count == 0) return;
            string certPw = F<PasswordBox>("NewCertPw").Password;
            if (steps.Contains("certs") && F<UIElement>("NewCertPanel").Visibility == Visibility.Visible && certPw.Length == 0)
            {
                ShowDialog("Certificate password", "Type the certificate password from the old PC, or switch the certificates off.", "OK", null, null);
                return;
            }
            var args = new List<string> { "import", "-From", from, "-Store", Store, "-Only", string.Join(",", steps), "-Speed", Speed };
            if (F<CheckBox>("DrySwitch").IsChecked == true) args.Add("-WhatIf");

            ShowPage("PageImport", "New PC");
            FillSteps(importRows, ImportSteps, steps);
            active.Clear();
            rateSamples.Clear();
            chunksDone = 0; chunksTotal = 0;
            installed.Clear();
            checkpointShown = false;
            F<UIElement>("HeroHazard").Visibility = Visibility.Visible;
            F<UIElement>("HeroWait").Visibility = Visibility.Visible;
            SetRoute(0, "LOADING THE TRUCK");
            F<UIElement>("HeroCard").Visibility = Visibility.Visible;
            F<UIElement>("DoneCard").Visibility = Visibility.Collapsed;
            F<TextBlock>("ImportTitle").Text = "Moving to this PC";
            engine.OnEvent = OnImportEvent;
            engine.OnExit = OnImportExit;
            engine.Start(engineDir, args, new Dictionary<string, string> { ["HOPOVER_CERT_PW"] = certPw.Length > 0 ? certPw : null });
        }

        void OnImportEvent(string kind, string data)
        {
            switch (kind)
            {
                case "APP":
                {
                    // "##APP <winget id>|<name>": one app finished installing (background install).
                    var a = data.Split(new[] { '|' }, 2);
                    string id = a[0].Trim(), name = a.Length > 1 && a[1].Trim().Length > 0 ? a[1].Trim() : id.Substring(id.LastIndexOf('.') + 1);
                    if (installed.Any(x => x.Id == id)) break;
                    installed.Add(new AppRow { Id = id, Name = name, Launch = FindLaunch(name), Check = CheckHint(name) });
                    if (!checkpointShown && installed.Count >= 4) ShowCheckpoint();
                    break;
                }
                case "STEP":
                    SetStep(importRows, data);
                    if (data == "apps done" && !checkpointShown && installed.Count > 0) ShowCheckpoint();
                    if (data == "files start")
                    {
                        F<TextBlock>("HeroBytes").Text = "Starting the copy";
                        F<UIElement>("ActivePanel").Visibility = Visibility.Visible;
                    }
                    if (data.StartsWith("files ") && data != "files start") F<UIElement>("ActivePanel").Visibility = Visibility.Collapsed;
                    break;
                case "ITEM":
                {
                    var p = data.Split(new[] { ' ' }, 4);
                    chunksTotal = int.Parse(p[1]);
                    active.Add(new ActiveRow { Index = int.Parse(p[0]), Path = p.Length > 3 ? p[3] : "", SizeText = Fmt.Size(double.Parse(p[2])) });
                    break;
                }
                case "ITEMDONE":
                {
                    int idx = int.Parse(data.Split(' ')[0]);
                    var row = active.FirstOrDefault(a => a.Index == idx);
                    if (row != null) active.Remove(row);
                    chunksDone++;
                    F<TextBlock>("HeroItem").Text = "Chunk " + chunksDone.ToString("N0") + " of " + chunksTotal.ToString("N0") + " done";
                    break;
                }
                case "PROGRESS":
                {
                    var p = data.Split(' ');
                    double done = double.Parse(p[0]), total = Math.Max(1, double.Parse(p[1]));
                    long run = long.Parse(p[2]);
                    double frac = Math.Min(1, done / total);
                    F<ProgressBar>("HeroBar").Value = frac;
                    F<UIElement>("HeroWait").Visibility = Visibility.Collapsed;
                    SetRoute(frac, "IN TRANSIT  ·  " + Fmt.Size(done) + " / " + Fmt.Size(total));
                    F<TextBlock>("HeroPct").Text = ((int)Math.Floor(frac * 100)).ToString();
                    F<TextBlock>("HeroBytes").Text = Fmt.Size(done) + " of " + Fmt.Size(total);
                    // Speed over the last 20 seconds: steady, but quick to follow a change.
                    var now = DateTime.Now;
                    rateSamples.Enqueue((now, run));
                    while (rateSamples.Count > 2 && (now - rateSamples.Peek().At).TotalSeconds > 20) rateSamples.Dequeue();
                    var first = rateSamples.Peek();
                    double secs = (now - first.At).TotalSeconds;
                    if (secs >= 3)
                    {
                        double rate = (run - first.Bytes) / secs;
                        F<TextBlock>("HeroRate").Text = Fmt.Size(rate) + "/s  ·  " + (rate > 0 ? Fmt.Duration((total - done) / rate) + " left" : "waiting for data");
                    }
                    break;
                }
            }
        }

        void OnImportExit(int code)
        {
            F<UIElement>("ActivePanel").Visibility = Visibility.Collapsed;
            F<UIElement>("HeroHazard").Visibility = Visibility.Collapsed;
            F<UIElement>("HeroWait").Visibility = Visibility.Collapsed;
            foreach (var r in importRows.Where(r => r.State == "running")) r.State = code == 0 ? "done" : "failed";
            bool ok = code == 0;
            F<TextBlock>("ImportTitle").Text = ok ? "Moved" : "Stopped";
            if (ok)
            {
                ShowDone();
                return;
            }
            F<UIElement>("HeroCard").Visibility = Visibility.Collapsed;
            var card = F<FrameworkElement>("DoneCard");
            card.Visibility = Visibility.Visible;
            Fade(card, 180);
            returnPage = "PageImport";
            F<TextBlock>("DoneStampText").Text = code == 2 ? "SOME FAILED" : "STOPPED";
            F<TextBlock>("DoneTitle").Text = code == 2 ? "Done, but some steps failed" : "The move stopped";
            F<TextBlock>("DoneText").Text = "Start Hopover again: it retries the failed steps and copies only what is missing. The details show why.";
            SetRoute(lastFrac, "STOPPED");
            if (currentPage != "PageImport") ShowPage("PageImport", "New PC");
            ToggleLogOpen("ImportLogBorder", "BtnImportLog");
        }

        // ---------- coffee pledge ----------
        void ShowPledge()
        {
            F<TextBlock>("PledgeText").Text = "I promise a coffee (" + Branding.PriceLabel + ") when my move works.";
            F<TextBlock>("TicketNo").Text = "NO. " + Pledge.TicketNo;
            F<TextBlock>("TicketFrom").Text = Environment.MachineName.ToUpperInvariant();
            F<TextBlock>("LicenceLine").Text = "Source available under " + Branding.LicenceName + ".";
            F<TextBlock>("SignedLine").Text = "";
            F<CheckBox>("PledgeCheck").IsChecked = false;
            F<Button>("BtnDeal").IsEnabled = false;
            ShowPage("PagePledge", "Before we load the truck");
        }

        void SetupCoffee()
        {
            var check = F<CheckBox>("PledgeCheck");
            check.Checked += (s, e) =>
            {
                F<Button>("BtnDeal").IsEnabled = true;
                F<TextBlock>("SignedLine").Text = Environment.UserName + " · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            };
            check.Unchecked += (s, e) => { F<Button>("BtnDeal").IsEnabled = false; F<TextBlock>("SignedLine").Text = ""; };
            Click("BtnDeal", () =>
            {
                if (check.IsChecked != true) return;
                Pledge.Sign();
                ShowPage("PageWelcome", "");
            });
            Click("BtnSource", () => Pledge.OpenUrl(Branding.SourceUrl, "SourceUrl", AppendLog));
            Click("BtnPayNow", () => PayFlow());
            Click("BtnCoffee", () => PayFlow());
            Click("BtnPayLater", () => ShowPage("PageImport", "New PC"));
            Click("BtnCheckContinue", () => ShowPage("PageImport", "New PC"));
            Click("BtnReview", () => Pledge.OpenUrl(Branding.ReviewUrl, "ReviewUrl", AppendLog));
            Click("BtnDoneChecklist", ShowChecklist);
            Click("BtnDoneTasks", EnableTasks);
            Click("BtnDoneVerify", Verify);
            Click("BtnDoneLogs", () => OpenFile(Path.Combine(Store, "logs")));
            F<ItemsControl>("CheckApps").AddHandler(Button.ClickEvent, new RoutedEventHandler((s, e) =>
            {
                var b = e.OriginalSource as Button;
                var row = b?.Tag as AppRow;
                if (row == null) return;
                if (b.Uid == "open") { try { Process.Start(new ProcessStartInfo(row.Launch) { UseShellExecute = true }); } catch (Exception ex) { AppendLog("Could not open " + row.Name + ": " + ex.Message); } }
                else if (b.Uid == "broken") OpenFile(Path.Combine(Store, "logs"));
            }));
        }

        /// <summary>Opens the coffee page, then asks. Honour system: Hopover cannot see the payment.</summary>
        void PayFlow()
        {
            if (!Pledge.OpenUrl(Branding.CoffeeUrl, "CoffeeUrl", AppendLog)) return;
            ShowDialog("Did it go through?", "Hopover cannot see your payment. It trusts you.", "Yes, it went through", "Not yet",
                () => { Pledge.MarkPaid(); RefreshPaid(); });
        }

        void RefreshPaid()
        {
            bool paid = Pledge.Paid;
            F<UIElement>("CheckNote").Visibility = paid ? Visibility.Collapsed : Visibility.Visible;
            F<UIElement>("BtnCoffee").Visibility = paid ? Visibility.Collapsed : Visibility.Visible;
            F<UIElement>("ThanksBlock").Visibility = paid ? Visibility.Visible : Visibility.Collapsed;
            F<TextBlock>("ReceiptOpenLabel").Text = paid ? "SETTLED" : "STILL OPEN";
            F<TextBlock>("ReceiptOpenValue").Text = paid ? "1 coffee, paid" : "1 coffee";
        }

        static readonly Regex NonWord = new Regex(@"[^a-z0-9]", RegexOptions.IgnoreCase);
        List<string> startLinks;

        /// <summary>The Start menu shortcut of an installed app, or null.</summary>
        string FindLaunch(string name)
        {
            if (startLinks == null)
            {
                startLinks = new List<string>();
                foreach (var f in new[] { Environment.SpecialFolder.CommonPrograms, Environment.SpecialFolder.Programs })
                {
                    try
                    {
                        string dir = Environment.GetFolderPath(f);
                        if (Directory.Exists(dir)) startLinks.AddRange(Directory.GetFiles(dir, "*.lnk", SearchOption.AllDirectories));
                    }
                    catch { }
                }
            }
            string key = NonWord.Replace(name, "").ToLowerInvariant();
            if (key.Length < 3) return null;
            return startLinks
                .Where(l => { string n = Path.GetFileNameWithoutExtension(l); return !n.ToLowerInvariant().Contains("uninstall"); })
                .Select(l => new { l, k = NonWord.Replace(Path.GetFileNameWithoutExtension(l), "").ToLowerInvariant() })
                .Where(x => x.k.Length >= 3 && (x.k.Contains(key) || key.Contains(x.k)))
                .OrderBy(x => Math.Abs(x.k.Length - key.Length)).Select(x => x.l).FirstOrDefault();
        }

        static string CheckHint(string name)
        {
            string n = name.ToLowerInvariant();
            if (n.Contains("1password") || n.Contains("keepass") || n.Contains("bitwarden")) return "Does it unlock?";
            if (n.Contains("intellij") || n.Contains("pycharm") || n.Contains("webstorm") || n.Contains("jetbrains") || n.Contains("code")) return "Are your plugins there?";
            if (n.Contains("terminal") || n.Contains("powershell") || n.Contains("git")) return "Is your profile there?";
            if (n.Contains("firefox") || n.Contains("chrome") || n.Contains("edge") || n.Contains("brave") || n.Contains("onedrive")) return "Are you still signed in?";
            return "Does it start and know you?";
        }

        void ShowCheckpoint()
        {
            if (checkpointShown) return;
            checkpointShown = true;
            var pick = installed.Where(a => a.CanOpen).Concat(installed.Where(a => !a.CanOpen)).Take(4).ToList();
            FillCheckpoint(pick);
            ShowPage("PageCheckpoint", "New PC");
            RefreshPaid();
            if (lastRouteText.Length == 0) SetRoute(lastFrac, "IN TRANSIT");
        }

        void FillCheckpoint(List<AppRow> pick)
        {
            F<ItemsControl>("CheckApps").ItemsSource = pick;
            string rest = sumBytes > 0 && lastFrac > 0 ? Fmt.Size(sumBytes * (1 - lastFrac)) + " still to copy" : "the rest of your files";
            F<TextBlock>("NoteText").Text = "It rides in the back, wedged between " + (sumDistros > 0 ? "WSL" : "your settings") + " and " + rest +
                                            ". No pressure. Well, a little pressure.";
            F<TextBlock>("NoteQuote").Text = "\u201CI promise a coffee when my move works.\u201D";
            F<TextBlock>("NoteSigned").Text = "SIGNED " + Environment.UserName + " · " + (Pledge.SignedAt.Length >= 16 ? Pledge.SignedAt.Substring(11, 5) : "");
            F<UIElement>("CheckNote").Visibility = Pledge.Paid ? Visibility.Collapsed : Visibility.Visible;
        }


        void ShowDone()
        {
            var lines = new List<ReceiptLine>
            {
                new ReceiptLine { What = "Apps installed", N = (installed.Count > 0 ? installed.Count : sumApps).ToString("N0") },
                new ReceiptLine { What = "Files and folders", N = Fmt.Size(sumBytes) },
                new ReceiptLine { What = "Scheduled tasks", N = sumTasks.ToString("N0") },
                new ReceiptLine { What = "WSL distros", N = sumDistros.ToString("N0") },
                new ReceiptLine { What = "Steps that failed", N = importRows.Count(r => r.State == "failed").ToString("N0") },
            };
            F<ItemsControl>("ReceiptLines").ItemsSource = lines;
            F<TextBlock>("ReceiptNo").Text = "NO. " + Pledge.TicketNo;
            F<TextBlock>("CoffeeLabel").Text = Branding.PriceLabel.ToUpperInvariant().Replace(", ", " · ");
            F<Button>("BtnReview").Content = "Leave a review on " + Branding.ReviewSite;
            RefreshPaid();
            returnPage = "PageDone";
            SetRoute(1, "ARRIVED  ·  " + Fmt.Size(sumBytes) + " / " + Fmt.Size(sumBytes));
            ShowPage("PageDone", "New PC");
            Window.Dispatcher.BeginInvoke(new Action(() =>
                Motion.DropStamp(F<FrameworkElement>("StampDelivered"), F<FrameworkElement>("Receipt"), -14, -1.5)),
                System.Windows.Threading.DispatcherPriority.Loaded);
        }

        void EnableTasks()
        {
            if (engine.Running) return;
            engine.OnEvent = null;
            engine.OnExit = code => ShowDialog(code == 0 ? "Scheduled tasks are on" : "Some tasks did not switch on",
                "The tasks from the old PC run on their schedule now. The details list each one.", "OK", null, null);
            engine.Start(engineDir, new[] { "enable-tasks", "-Store", Store });
        }

        void Verify()
        {
            if (engine.Running) return;
            engine.OnEvent = null;
            engine.OnExit = code =>
            {
                string path = Path.Combine(Store, "verify.txt");
                var gaps = File.Exists(path) ? File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList() : new List<string>();
                ShowDialog(gaps.Count == 0 ? "No gaps" : gaps.Count + " gaps",
                    gaps.Count == 0 ? "Every app, folder, variable, distro and task from the old PC is here."
                                    : string.Join("\n", gaps.Take(12)) + (gaps.Count > 12 ? "\n..." : ""),
                    gaps.Count == 0 ? "OK" : "Open the full list", gaps.Count == 0 ? null : "Close",
                    gaps.Count == 0 ? (Action)null : () => OpenFile(path));
            };
            engine.Start(engineDir, new[] { "verify", "-Store", Store });
        }

        // ---------- checklist ----------
        void ShowChecklist()
        {
            string md = Path.Combine(Store, "CHECKLIST.md");
            string statePath = Path.Combine(Store, "checklist-state.json");
            var ticked = new HashSet<string>(Json.List(Json.Read(statePath)).Select(x => x.ToString()));
            // Parse first: "## " is a section, "- [ ] " an item, "  - " a detail line of the item above.
            var parsed = new List<(bool Header, string Text, StringBuilder Detail)>();
            if (File.Exists(md))
                foreach (var raw in File.ReadAllLines(md, Encoding.UTF8))
                {
                    string l = raw.TrimEnd();
                    if (l.StartsWith("## ")) parsed.Add((true, l.Substring(3).ToUpperInvariant(), null));
                    else if (l.StartsWith("- [ ] ") || l.StartsWith("- [x] ")) parsed.Add((false, l.Substring(6).Replace("`", ""), new StringBuilder()));
                    else if (l.StartsWith("  - ") && parsed.Count > 0 && !parsed[parsed.Count - 1].Header)
                    {
                        var d = parsed[parsed.Count - 1].Detail;
                        if (d.Length > 0) d.Append('\n');
                        d.Append(l.Substring(4).Replace("`", ""));
                    }
                }
            checks.Clear();
            foreach (var p in parsed)
            {
                var row = new CheckRow { Text = p.Text, IsHeader = p.Header, Detail = p.Detail?.ToString(), Done = !p.Header && ticked.Contains(p.Text) };
                if (!p.Header)
                    row.PropertyChanged += (s, e) =>
                    {
                        if (((CheckRow)s).Done) ticked.Add(row.Text); else ticked.Remove(row.Text);
                        Json.Write(statePath, ticked.ToArray());
                        UpdateCheckCount();
                    };
                checks.Add(row);
            }
            UpdateCheckCount();
            ShowPage("PageChecklist", "Checklist");
        }

        void UpdateCheckCount()
        {
            var items = checks.Where(c => !c.IsHeader).ToList();
            F<TextBlock>("CheckCount").Text = items.Count(c => c.Done) + " of " + items.Count + " done. Hopover remembers the ticks.";
        }

        // ---------- closing ----------
        bool closeConfirmed;

        void OnClosing(object sender, System.ComponentModel.CancelEventArgs e)
        {
            if (!engine.Running || closeConfirmed) return;
            e.Cancel = true;
            ShowDialog("Stop Hopover?", "Hopover is still working. You can start it again later: it goes on where it stopped.",
                "Stop and close", "Keep working", () => { engine.Kill(); closeConfirmed = true; Window.Close(); });
        }

        // ---------- screenshots (build check) ----------
        internal void Demo(string page)
        {
            switch (page)
            {
                case "scan":
                    foreach (var kv in BuildSwitches("OldSteps", ExportSteps.Select(s => s.Key), new string[0])) oldSwitches[kv.Key] = kv.Value;
                    var demo = new Dictionary<string, object>
                    {
                        ["Plan"] = new object[]
                        {
                            Row(@"C:\java", "c-root", 498e9, 3282185), Row(@"C:\Users\pp\.android", "profile", 45.9e9, 558),
                            Row(@"C:\Users\pp\.julia", "profile", 27.2e9, 263802), Row(@"C:\slaide-distill", "c-root", 22.2e9, 15773),
                            Row(@"C:\Users\pp\AppData\Roaming\Claude", "appdata-roaming", 11.2e9, 41328), Row(@"C:\Users\pp\.claude", "profile", 9.4e9, 37302),
                            Row(@"C:\Users\pp\AppData\Roaming\JetBrains", "appdata-roaming", 4.8e9, 20285), Row(@"C:\Users\pp\.ssh", "profile", 0.2e6, 55),
                            Row(@"C:\Users\pp\Documents\vault.kdbx", "keepass", 0.4e6, 1),
                        }
                    };
                    LoadGroups(demo);
                    groups[1].Include = false;
                    F<UIElement>("ScanWait").Visibility = Visibility.Collapsed;
                    F<ProgressBar>("ScanBar").Value = 1;
                    F<TextBlock>("ScanText").Text = "Scan done. Untick what must not move.";
                    F<Button>("BtnExport").IsEnabled = true;
                    ShowPage("PageScan", "Old PC");
                    break;
                case "share":
                    FillSteps(exportRows, ExportSteps, null);
                    foreach (var r in exportRows) r.State = "done";
                    exportRows[3].State = "skipped";
                    ApplyShare("MADPPIPER-2022|MADPPIPER-2022\\pp|192.168.1.42");
                    F<TextBlock>("ExportTitle").Text = "This PC is ready";
                    ShowPage("PageExport", "Old PC");
                    SetRoute(1, "READY  ·  SHARED READ-ONLY");
                    break;
                case "connect":
                    foreach (var kv in BuildSwitches("NewSteps", ImportSteps.Select(s => s.Key), new[] { "env" })) newSwitches[kv.Key] = kv.Value;
                    F<TextBox>("FromBox").Text = "MADPPIPER-2022";
                    F<TextBox>("UserBox").Text = "MADPPIPER-2022\\pp";
                    F<TextBlock>("ConnectText").Text = "Connected.";
                    F<TextBlock>("ConnectText").Foreground = (Brush)Window.FindResource("Ink");
                    F<TextBlock>("SummaryTitle").Text = "MADPPIPER-2022  ·  pp";
                    F<TextBlock>("SummaryText").Text = "312 apps  ·  697 GB in 486 chunks  ·  15 scheduled tasks  ·  WSL: Ubuntu-22.04";
                    F<UIElement>("ImportCard").Visibility = Visibility.Visible;
                    ShowPage("PageConnect", "New PC");
                    break;
                case "import":
                    FillSteps(importRows, ImportSteps, null);
                    importRows[0].State = "done"; importRows[1].State = "running"; importRows[2].State = "running";
                    importRows[1].Detail = "winget: 41 of 312";
                    ShowPage("PageImport", "New PC");
                    OnImportEvent("STEP", "files start");
                    for (int i = 0; i < 4; i++) OnImportEvent("ITEM", i + " 486 " + (40e9 / (i + 1)) + " " + new[] { @"C:\java\workspace\aivory-guard", @"C:\Users\pp\.android\avd", @"C:\java\workspace\slaides", @"C:\Users\pp\.julia\packages" }[i]);
                    OnImportEvent("PROGRESS", "293000000000 697000000000 120000000000");
                    F<TextBlock>("HeroRate").Text = "112 MB/s  ·  about 1.0 h left";
                    F<TextBlock>("HeroItem").Text = "Chunk 212 of 486 done";
                    F<UIElement>("HeroHazard").Visibility = Visibility.Visible;
                    break;
                case "checkpoint":
                    FillSteps(importRows, ImportSteps, null);
                    sumBytes = 697000000000; sumDistros = 2;
                    OnImportEvent("PROGRESS", "217000000000 697000000000 120000000000");
                    var sample = new List<AppRow>
                    {
                        new AppRow { Name = "1Password", Check = "Does it unlock?", Launch = "x" },
                        new AppRow { Name = "IntelliJ IDEA", Check = "Are your plugins there?", Launch = "x" },
                        new AppRow { Name = "Windows Terminal", Check = "Is your profile there?", Launch = "x" },
                        new AppRow { Name = "Firefox", Check = "Are you still signed in?", Launch = "x" },
                    };
                    FillCheckpoint(sample);
                    ShowPage("PageCheckpoint", "New PC");
                    break;
                case "done":
                case "done-paid":
                    FillSteps(importRows, ImportSteps, null);
                    foreach (var r in importRows) r.State = "done";
                    sumApps = 84; sumBytes = 697000000000; sumTasks = 15; sumDistros = 2;
                    ShowPage("PageImport", "New PC");
                    OnImportExit(0);
                    break;
                case "pledge":
                    F<CheckBox>("PledgeCheck").IsChecked = true;
                    break;
            }
        }


        static Dictionary<string, object> Row(string path, string why, double bytes, long files) =>
            new Dictionary<string, object> { ["Path"] = path, ["Group"] = path, ["Why"] = why, ["Bytes"] = (long)bytes, ["Files"] = files };
    }
}
