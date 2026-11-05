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
using Hopover.Link;

namespace Hopover.App
{
    /// <summary>All screens of the wizard. The engine does the work; this class only shows it.</summary>
    public class MainController
    {
        const string Store = Program.Store;

        static readonly (string Key, string Label)[] ExportSteps =
        {
            ("apps", "App list"), ("env", "Environment variables"), ("system", "Hosts, Windows features, saved logins, VPN"),
            ("settings", "Windows and app settings"), ("certs", "Certificates"), ("tasks", "Scheduled tasks"), ("wsl", "WSL distros"),
            ("docker", "Docker volumes"), ("databases", "PostgreSQL"),
        };

        static readonly (string Key, string Label)[] ImportSteps =
        {
            ("env", "Environment variables and PATH"), ("system", "Hosts, Windows features, saved logins"),
            ("apps", "Apps (winget, Chocolatey, Store)"), ("files", "Files and folders"),
            ("certs", "Certificates"), ("wsl", "WSL distros"), ("settings", "Windows and app settings"),
            ("docker", "Docker volumes"), ("databases", "PostgreSQL"), ("tasks", "Scheduled tasks (switched off)"),
        };

        static readonly (string Key, string Label)[] CatchupSteps =
        {
            ("refresh", "The old PC prepares its changes"), ("env", "Environment variables and PATH"),
            ("system", "Hosts, Windows features, saved logins"), ("apps", "New apps"), ("files", "Changed files"),
            ("wsl", "Changes in WSL"), ("settings", "Changed settings"), ("docker", "Docker volumes"),
            ("databases", "PostgreSQL"), ("tasks", "New scheduled tasks (switched off)"),
        };

        static readonly Dictionary<string, string> SwitchLabels = new Dictionary<string, string>
        {
            ["apps"] = "Apps (winget list, Chocolatey, Store)", ["env"] = "Environment variables", ["files"] = "Files and folders",
            ["system"] = "Hosts, Windows features, saved logins, VPN", ["databases"] = "PostgreSQL",
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
        readonly ObservableCollection<TickerRow> ticker = new ObservableCollection<TickerRow>();
        readonly List<string> tickerPaths = new List<string>();
        readonly Dictionary<string, CheckBox> oldSwitches = new Dictionary<string, CheckBox>();
        readonly Dictionary<string, CheckBox> newSwitches = new Dictionary<string, CheckBox>();
        readonly Queue<(DateTime At, long Bytes)> rateSamples = new Queue<(DateTime, long)>();
        readonly List<AppRow> installed = new List<AppRow>();
        bool checkpointShown, previewRun, scanDirty;
        System.Windows.Threading.DispatcherTimer scanTimer;
        bool linkLost, needReboot, serveStopping;
        Action dialogCancel;
        int sumApps, sumTasks, sumDistros;
        long sumBytes;
        double lastFrac;
        string lastRouteText = "";
        string returnPage = "PageImport";
        string from;
        EngineRun serve;                 // old PC: Hopover.exe --link serve
        MuxProxy proxy;                  // new PC: the authenticated link, offered on 127.0.0.1
        MuxClient guiMux;                // new PC: the GUI's own reads (manifest, app list)
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
            F<ItemsControl>("ScanTicker").ItemsSource = ticker;
            SetupCoffee();

            Click("BtnOld", StartScan);
            Click("BtnNew", () => { ShowPage("PageConnect", "New PC"); F<TextBox>("CodeBox").Focus(); });
            Click("BtnExport", StartExport);
            Click("BtnStopShare", StopSharing);
            Click("BtnConnect", () => _ = Connect());
            Click("BtnReconnect", () => { ShowPage("PageConnect", "New PC"); F<TextBox>("CodeBox").Focus(); });
            Click("BtnImport", StartImport);
            Click("BtnCatchupNow", StartCatchup);
            Click("BtnCatchup", StartCatchup);
            Click("BtnCleanup", AskCleanup);
            Click("BtnDoneCleanup", AskCleanup);
            Click("BtnPreviewOpen", () => OpenFile(Path.Combine(Store, "preview.txt")));
            Click("BtnSavePlan", SavePlan);
            Click("BtnDoneCatchup", StartCatchup);
            Click("BtnChecklist", ShowChecklist);
            Click("BtnCheckBack", () => ShowPage(returnPage, "New PC"));
            Click("BtnCheckOpen", () => OpenFile(Path.Combine(Store, "CHECKLIST.md")));
            Click("BtnLogs", () => OpenFile(Path.Combine(Store, "logs")));
            Click("BtnTasks", EnableTasks);
            Click("BtnVerify", Verify);
            Click("BtnExportLog", () => ToggleLog("ExportLogBorder", "BtnExportLog"));
            Click("BtnImportLog", () => ToggleLog("ImportLogBorder", "BtnImportLog"));
            Click("BtnCopyCode", () => Copy(F<TextBlock>("LinkCode").Text));
            Click("DlgOk", () => { HideDialog(); var a = dialogOk; dialogOk = null; a?.Invoke(); });
            Click("DlgCancel", () => { HideDialog(); var a = dialogCancel; dialogCancel = null; a?.Invoke(); });

            F<TextBox>("CodeBox").KeyDown += (s, e) => { if (e.Key == Key.Enter) _ = Connect(); };
            foreach (var r in new[] { "ModeLan", "ModeCable", "ModeNet" })
                F<RadioButton>(r).Checked += (s, e) => UpdateModeHint();
            UpdateModeHint();
            Window.Closed += (s, e) => { try { if (serve != null) serve.Kill(); if (proxy != null) proxy.Dispose(); } catch { } };
            foreach (var r in new[] { "SpeedGentle", "SpeedBalanced", "SpeedFull" })
                F<RadioButton>(r).Checked += (s, e) => UpdateSpeedHint();
            UpdateSpeedHint();

            engine.OnLine = line =>
            {
                AppendLog(line);
                if (line.Contains("Restart the PC")) needReboot = true;
            };
            // A move takes hours: neither PC may fall asleep while Hopover is open (the UI thread lives as long as the app).
            LinkMain.KeepAwake();
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

        void ShowDialog(string title, string text, string ok, string cancel, Action onOk, Action onCancel = null)
        {
            dialogCancel = onCancel;
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
            if (!engine.Running && (serve == null || !serve.Running) && PreparedBefore(out string when))
            {
                ShowDialog("Open the link again?", "This PC was prepared on " + when + ". Open the link with that data (no new scan), or scan and prepare again.",
                           "Open the link", "Scan again", ReopenLink, StartScanNow);
                return;
            }
            StartScanNow();
        }

        bool PreparedBefore(out string when)
        {
            when = null;
            var m = Json.Read(Path.Combine(Store, "manifest.json"));
            var st = Json.Read(Path.Combine(Store, "state-export.json"));
            if (m == null || st == null || !string.Equals(Json.Str(m, "Computer"), Environment.MachineName, StringComparison.OrdinalIgnoreCase)) return false;
            when = (Json.Str(st, "Updated") ?? "").Replace('T', ' ');
            return true;
        }

        // Old PC after a restart or a crash: the store is ready; only the firewall rule and the link server are needed.
        void ReopenLink()
        {
            ShowPage("PageExport", "Old PC");
            F<UIElement>("ShareCard").Visibility = Visibility.Collapsed;
            F<UIElement>("ExportWorking").Visibility = Visibility.Visible;
            F<TextBlock>("ExportTitle").Text = "Opening the link";
            engine.OnEvent = (k, d) => { if (k == "SHARE") ApplyShare(d); };
            engine.OnExit = code =>
            {
                F<UIElement>("ExportWorking").Visibility = Visibility.Collapsed;
                F<TextBlock>("ExportTitle").Text = code == 0 ? "This PC is ready" : "Something failed";
                if (code != 0) ToggleLogOpen("ExportLogBorder", "BtnExportLog");
            };
            engine.Start(engineDir, new[] { "share", "-Store", Store }, new Dictionary<string, string> { ["HOPOVER_EXE"] = exePath });
        }

        void StartScanNow()
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
            ticker.Clear();
            tickerPaths.Clear();
            engine.OnEvent = (k, d) =>
            {
                if (k == "SCANNING") { TickerAdd(d); return; }
                if (k == "PLANREADY") { ShowPartialPlan(); return; }
                if (k == "SIZE") { AddSize(d); return; }
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
                scanTimer?.Stop();
                // The final list sorted by size, with the ticks made during the measuring.
                var off = new HashSet<string>(groups.Where(g => !g.Include).Select(g => g.Path), StringComparer.OrdinalIgnoreCase);
                LoadGroups(m);
                foreach (var g in groups) if (off.Contains(g.Path)) g.Include = false;
                F<UIElement>("ScanWait").Visibility = Visibility.Collapsed;
                F<ProgressBar>("ScanBar").Value = 1;
                F<TextBlock>("ScanText").Text = "Scan done. Untick whatever should stay behind.";
                certsOnOldPc = Json.Long(m, "Certs") > 0;
                F<UIElement>("OldCertPanel").Visibility = certsOnOldPc ? Visibility.Visible : Visibility.Collapsed;
                F<Button>("BtnExport").IsEnabled = true;
            };
            engine.Start(engineDir, new[] { "inventory", "-Store", Store, "-Speed", "balanced" });
        }

        // The rows come up as soon as the folders are listed; the measuring takes much longer than the listing.
        void ShowPartialPlan()
        {
            var m = Json.Read(Path.Combine(Store, "plan-partial.json"));
            if (m == null) return;
            LoadGroups(m);
            foreach (var g in groups) g.Measuring = true;
            F<UIElement>("ScanWait").Visibility = Visibility.Collapsed;
            scanDirty = true;
            if (scanTimer == null)
            {
                scanTimer = new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
                scanTimer.Tick += (s, e) => RefreshSizes();
            }
            scanTimer.Start();
        }

        void AddSize(string data)
        {
            var p = data.Split(new[] { ' ' }, 3);
            if (p.Length < 3) return;
            var row = groups.FirstOrDefault(g => string.Equals(g.Path, p[2], StringComparison.OrdinalIgnoreCase));
            if (row == null) return;
            row.Bytes += long.Parse(p[0]);
            row.Files += long.Parse(p[1]);
            scanDirty = true;
        }

        void RefreshSizes()
        {
            if (!scanDirty || groups.Count == 0) return;
            scanDirty = false;
            double max = Math.Max(1, groups.Max(g => g.Bytes));
            foreach (var g in groups) g.BarRatio = Math.Sqrt(g.Bytes / max);
            UpdateTotal();
        }

        // The newest path stands at the bottom in full ink; the ones above it fade away.
        void TickerAdd(string path)
        {
            tickerPaths.Add(path);
            if (tickerPaths.Count > 6) tickerPaths.RemoveAt(0);
            ticker.Clear();
            for (int i = 0; i < tickerPaths.Count; i++)
                ticker.Add(new TickerRow { Text = tickerPaths[i], Fade = 1.0 - 0.16 * (tickerPaths.Count - 1 - i) });
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
                SetRoute(code == 0 ? 1 : lastFrac, code == 0 ? "READY · LINK OPEN" : "STOPPED");
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

        void ApplyShare(string d, bool start = true)
        {
            F<TextBlock>("ShareWhere").Text = @"On the new PC, start Hopover.exe (from OneDrive\Hopover), choose 'This is the new PC' and type this code.";
            F<UIElement>("ExportWorking").Visibility = Visibility.Collapsed;
            SetLinkBadge("WAITING FOR THE NEW PC", false);
            F<TextBlock>("LinkPeer").Text = "";
            var card = F<FrameworkElement>("ShareCard");
            card.Visibility = Visibility.Visible;
            Fade(card, 180);
            if (start && (serve == null || !serve.Running)) StartServe();
        }

        void StartServe()
        {
            KillOrphanServers();
            serveStopping = false;
            serve = new EngineRun(Window.Dispatcher);
            serve.OnEvent = (k, d) => { if (k == "LINK") OnLinkEvent(d); };
            serve.OnLine = AppendLog;
            serve.OnExit = code =>
            {
                if (serveStopping) return;
                SetLinkBadge("LINK STOPPED  ·  CLOSE AND START HOPOVER AGAIN", false);
                F<TextBlock>("LinkPeer").Text = "The link server stopped (exit " + code + "). The details show why.";
                ToggleLogOpen("ExportLogBorder", "BtnExportLog");
            };
            serve.StartExe(exePath, new[] { "--link", "serve", "--store", Store, "--engine", engineDir });
        }

        // A link server left over from a crashed Hopover holds the port, so a new one could not listen.
        // The server runs without a window; another open Hopover window is left alone.
        void KillOrphanServers()
        {
            try
            {
                int me = System.Diagnostics.Process.GetCurrentProcess().Id;
                foreach (var p in System.Diagnostics.Process.GetProcessesByName(Path.GetFileNameWithoutExtension(exePath)))
                {
                    try { if (p.Id != me && p.MainWindowHandle == IntPtr.Zero) { p.Kill(); p.WaitForExit(3000); } }
                    catch { }
                    finally { p.Dispose(); }
                }
            }
            catch { }
        }

        void OnLinkEvent(string d)
        {
            int sp = d.IndexOf(' ');
            string what = sp < 0 ? d : d.Substring(0, sp), rest = sp < 0 ? "" : d.Substring(sp + 1);
            switch (what)
            {
                case "code":
                    F<TextBlock>("LinkCode").Text = rest;
                    SetLinkBadge("WAITING FOR THE NEW PC", false);
                    F<TextBlock>("LinkPeer").Text = "";
                    break;
                case "path":
                    SetLinkBadge(PathText(rest), rest != "none");
                    break;
                case "peer":
                    F<TextBlock>("LinkPeer").Text = "Connected: " + rest;
                    break;
                case "state":
                    if (rest == "burned")
                    {
                        SetLinkBadge("WRONG CODE  ·  NEW CODE", false);
                        F<TextBlock>("LinkPeer").Text = "A wrong code was typed. Use the new code.";
                    }
                    else if (rest == "dropped") SetLinkBadge("LINK DROPPED  ·  WAITING AGAIN", false);
                    break;
            }
        }

        static string PathText(string path) =>
            path == "cable" ? "CABLE  ·  DIRECT" : path == "lan" ? "LAN  ·  DIRECT" : path == "internet" ? "INTERNET  ·  DIRECT" : "NO ROUTE";

        void SetBadge(string border, string text, string label, bool good)
        {
            var b = F<Border>(border);
            var tb = F<TextBlock>(text);
            tb.Text = label;
            b.Background = good ? (Brush)Window.FindResource("Ink") : Brushes.Transparent;
            tb.Foreground = good ? (Brush)Window.FindResource("Paper") : (Brush)Window.FindResource("Ink");
        }

        void SetLinkBadge(string label, bool good) => SetBadge("LinkBadge", "LinkBadgeText", label, good);

        void UpdateModeHint()
        {
            F<TextBlock>("ModeHint").Text = F<RadioButton>("ModeNet").IsChecked == true
                ? "Both PCs reach the internet. A small meeting point only swaps addresses; the files go straight from PC to PC."
                : F<RadioButton>("ModeCable").IsChecked == true
                ? "One LAN cable from PC to PC, or a Thunderbolt or USB4 cable if both PCs have those ports. A plain USB-C cable cannot link two PCs. Plug it in, give Windows a minute, then connect."
                : "Both PCs on the same router or Wi-Fi. The new PC finds the old PC by the code. If a cable joins them too, the cable wins.";
        }

        void ToggleLogOpen(string border, string button)
        {
            if (F<UIElement>(border).Visibility != Visibility.Visible) ToggleLog(border, button);
        }

        void StopSharing()
        {
            if (engine.Running) return;
            serveStopping = true;
            if (serve != null) serve.Kill();
            engine.OnEvent = null;
            engine.OnExit = code => ShowDialog("Link stopped",
                "Clean up this PC now? That deletes " + Store + " with its Wi-Fi passwords, tokens and WSL copy. Your own files stay.",
                "Clean up", "Keep it for now", () => Purge(null));
            engine.Start(engineDir, new[] { "cleanup", "-Store", Store });
        }

        // Deletes Hopover's working folder. The user's data is elsewhere; what a move set aside stays in C:\hopover-held.
        void Purge(Action after)
        {
            if (engine.Running) return;
            engine.OnEvent = null;
            engine.OnExit = code =>
            {
                if (code == 0)
                    foreach (var b in new[] { "BtnCatchup", "BtnDoneCatchup", "BtnCatchupNow", "BtnDoneCleanup", "BtnCleanup" }) F<UIElement>(b).Visibility = Visibility.Collapsed;
                if (after != null && code == 0) { after(); return; }
                ShowDialog(code == 0 ? "All clean" : "Not cleaned up",
                    code == 0 ? "The working folder is gone." + (Directory.Exists(@"C:\hopover-held") ? " C:\\hopover-held still holds what the move set aside: look through it, then delete it." : "")
                              : "A file in " + Store + " is in use. Close the other programs and try again.", "OK", null, null);
            };
            engine.Start(engineDir, new[] { "cleanup", "-Purge", "-Store", Store });
        }

        void AskCleanup()
        {
            if (engine.Running) return;
            ShowDialog("Clean up this PC?",
                "This deletes " + Store + " with the Wi-Fi passwords, tokens and WSL copy in it. After that you can't catch up with the old PC any more. Your own files stay.",
                "Clean up", "Not yet", () => Purge(null));
        }

        // ---------- NEW PC ----------
        string LinkRendezvous()
        {
            var link = Json.Get(Json.Read(Path.Combine(engineDir ?? "", "config", "include.json")), "link");
            return Json.Str(link, "rendezvous") ?? LinkMain.DefaultRendezvous();
        }

        void SetConnectMsg(string text, bool error)
        {
            var msg = F<TextBlock>("ConnectText");
            msg.Text = text;
            msg.Foreground = (Brush)Window.FindResource(error ? "RustDark" : "Ink2");
        }

        void StopLinkClient()
        {
            try { if (guiMux != null) guiMux.Dispose(); if (proxy != null) proxy.Dispose(); } catch { }
            guiMux = null; proxy = null;
        }

        async Task Connect()
        {
            string code = F<TextBox>("CodeBox").Text.Trim();
            bool net = F<RadioButton>("ModeNet").IsChecked == true;
            bool cable = F<RadioButton>("ModeCable").IsChecked == true;
            F<UIElement>("ConnectBadge").Visibility = Visibility.Collapsed;
            if (!LinkClient.LooksLikeCode(code))
            {
                SetConnectMsg("That is not a pairing code. It is a digit and three words, like 3-alpha-bravo-cafe.", true);
                return;
            }
            F<Button>("BtnConnect").IsEnabled = false;
            F<UIElement>("ConnectSpin").Visibility = Visibility.Visible;
            F<UIElement>("ImportCard").Visibility = Visibility.Collapsed;
            SetConnectMsg(net ? "Asking the meeting point" : "Looking for the old PC", false);
            StopLinkClient();
            string rv = LinkRendezvous();
            Action<string> status = s => Window.Dispatcher.BeginInvoke(new Action(() => SetConnectMsg(s, false)));
            // A fresh cable: Windows needs up to a minute before it gives the port an address.
            var res = await Task.Run(() => LinkClient.Connect(code, net ? "internet" : "lan", null, rv, 47801, status, new Rendezvous.Holder(), cable ? 75 : 25));
            F<Button>("BtnConnect").IsEnabled = true;
            F<UIElement>("ConnectSpin").Visibility = Visibility.Collapsed;
            if (res.Channel == null)
            {
                if (res.Error == "wrong-code")
                    SetConnectMsg("Wrong code. The old PC made a new one: read it on its screen.", true);
                else
                {
                    SetBadge("ConnectBadge", "ConnectBadgeText", "NO ROUTE", false);
                    F<UIElement>("ConnectBadge").Visibility = Visibility.Visible;
                    SetConnectMsg(cable ? "No PC found on the cable. Check that Hopover shows the code on the old PC. A plain USB-C cable cannot link two PCs: use a LAN cable."
                                        : "No direct route. Put both PCs on the same network, join them with a LAN cable, or use a USB disk.", true);
                }
                return;
            }

            proxy = new MuxProxy(res.Channel);
            var myProxy = proxy;
            linkLost = false;
            proxy.Closed += () => Window.Dispatcher.BeginInvoke(new Action(() =>
            {
                if (proxy != myProxy) return;            // an old link that was replaced on purpose
                linkLost = true;
                SetConnectMsg("The link to the old PC ended. Type the code again and connect.", true);
                if (engine.Running)
                    F<TextBlock>("HeroItem").Text = "The link to the old PC is lost. Hopover finishes the other steps; then connect again.";
            }));
            proxy.Start();
            SetBadge("ConnectBadge", "ConnectBadgeText", PathText(res.Path), true);
            F<UIElement>("ConnectBadge").Visibility = Visibility.Visible;
            SetConnectMsg("Connected. Reading what the old PC prepared", false);
            var mux = guiMux = MuxClient.Connect(proxy.Port, proxy.Token);
            var got = await Task.Run(() =>
            {
                Func<string, object> read = rel => { try { return Json.Parse(mux.ReadAllBytes("store", rel)); } catch (Exception) { return null; } };
                bool certs = false;
                try { certs = mux.List("store", "certs").Any(e => e.Name.EndsWith(".pfx", StringComparison.OrdinalIgnoreCase)); } catch (Exception) { }
                return new { Manifest = read("manifest.json"), Apps = read(@"apps\winget.json"), Tasks = read(@"tasks\tasks.json"), Certs = certs };
            });
            var m = got.Manifest;
            if (m == null)
            {
                SetConnectMsg("Connected, but the old PC has nothing prepared yet. Click 'Prepare the move' there first.", true);
                return;
            }
            from = "link";
            int appCount = Json.List(Json.Get(got.Apps, "Sources")).Sum(s => Json.List(Json.Get(s, "Packages")).Count());
            int taskCount = Json.List(got.Tasks).Count();
            var plan = Json.List(Json.Get(m, "Plan")).ToList();
            long bytes = plan.Sum(p => Json.Long(p, "Bytes"));
            sumApps = appCount; sumBytes = bytes; sumTasks = taskCount;
            var distros = Json.List(Json.Get(m, "Distros")).Select(x => x.ToString()).Where(x => !x.StartsWith("docker-desktop")).ToList();
            sumDistros = distros.Count;
            bool moved = File.Exists(Path.Combine(Store, "baseline.json"));
            F<UIElement>("BtnCatchupNow").Visibility = moved ? Visibility.Visible : Visibility.Collapsed;
            SetConnectMsg("Connected to " + Json.Str(m, "Computer") + "." + (moved ? " The move is done: 'Catch up' fetches what changed there since." : ""), false);
            F<TextBlock>("ConnectText").Foreground = (Brush)Window.FindResource("Ink");
            F<TextBlock>("SummaryTitle").Text = Json.Str(m, "Computer") + "  ·  " + Json.Str(m, "User");
            F<TextBlock>("SummaryText").Text = appCount + " apps  ·  " + Fmt.Size(bytes) + " in " + plan.Count + " chunks  ·  " +
                                               taskCount + " scheduled tasks" + (distros.Count > 0 ? "  ·  WSL: " + string.Join(", ", distros) : "");
            var state = Json.Read(Path.Combine(Store, "state-import.json"));
            var done = Json.List(Json.Get(state, "Done")).Select(x => x.ToString()).Where(x => x != "files").ToList();
            newSwitches.Clear();
            foreach (var kv in BuildSwitches("NewSteps", ImportSteps.Select(s => s.Key), done)) newSwitches[kv.Key] = kv.Value;
            F<UIElement>("NewCertPanel").Visibility = got.Certs ? Visibility.Visible : Visibility.Collapsed;
            var card = F<FrameworkElement>("ImportCard");
            card.Visibility = Visibility.Visible;
            Fade(card, 180);
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
            if (proxy == null || linkLost)
            {
                ShowDialog("No link", "Connect to the old PC first.", "OK", null, null);
                return;
            }
            string certPw = F<PasswordBox>("NewCertPw").Password;
            if (steps.Contains("certs") && F<UIElement>("NewCertPanel").Visibility == Visibility.Visible && certPw.Length == 0)
            {
                ShowDialog("Certificate password", "Type the certificate password from the old PC, or switch the certificates off.", "OK", null, null);
                return;
            }
            var args = new List<string> { "import", "-From", from, "-Store", Store, "-Only", string.Join(",", steps), "-Speed", Speed };
            previewRun = F<CheckBox>("DrySwitch").IsChecked == true;
            if (previewRun) args.Add("-WhatIf");
            RunImport(args, ImportSteps, steps, certPw, "Moving to this PC", "LOADING THE TRUCK");
        }

        /// <summary>The old PC stayed in use: fetch what changed there. This PC's own changes stay; both sides of a clash are kept.</summary>
        void StartCatchup()
        {
            if (engine.Running) return;
            if (proxy == null || linkLost)
            {
                ShowPage("PageConnect", "New PC");
                SetConnectMsg("Connect to the old PC first: type the code it shows now. Then click 'Catch up'.", false);
                F<TextBox>("CodeBox").Focus();
                return;
            }
            var args = new List<string> { "import", "-Catchup", "-From", from, "-Store", Store, "-Speed", Speed };
            previewRun = F<CheckBox>("DrySwitch").IsChecked == true;
            if (previewRun) args.Add("-WhatIf");
            RunImport(args, CatchupSteps, null, "", "Catching up with the old PC", "PICKING UP THE LAST BOXES");
        }

        void RunImport(List<string> args, (string Key, string Label)[] rows, ICollection<string> steps, string certPw, string title, string route)
        {
            ShowPage("PageImport", "New PC");
            FillSteps(importRows, rows, steps);
            active.Clear();
            rateSamples.Clear();
            chunksDone = 0; chunksTotal = 0;
            installed.Clear();
            checkpointShown = false;
            needReboot = false;
            F<UIElement>("BtnReconnect").Visibility = Visibility.Collapsed;
            F<UIElement>("HeroHazard").Visibility = Visibility.Visible;
            F<UIElement>("HeroWait").Visibility = Visibility.Visible;
            SetRoute(0, route);
            F<UIElement>("HeroCard").Visibility = Visibility.Visible;
            F<UIElement>("DoneCard").Visibility = Visibility.Collapsed;
            F<TextBlock>("ImportTitle").Text = title;
            engine.OnEvent = OnImportEvent;
            engine.OnExit = OnImportExit;
            engine.Start(engineDir, args, new Dictionary<string, string>
            {
                ["HOPOVER_CERT_PW"] = certPw.Length > 0 ? certPw : null,
                ["HOPOVER_EXE"] = exePath,
                ["HOPOVER_LINK"] = proxy.Port + ":" + proxy.Token,
            });
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
                    if (data == "store start") F<TextBlock>("HeroItem").Text = "Copying the prepared data from the old PC (app list, settings, WSL, Docker volumes)";
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

        // Old PC: the list as it stands, ticks included, as text.
        void SavePlan()
        {
            try
            {
                var sb = new StringBuilder();
                sb.AppendLine("Hopover: what moves from " + Environment.MachineName + ", " + DateTime.Now.ToString("yyyy-MM-dd HH:mm"));
                sb.AppendLine("Apps, settings, registry, WSL and the rest are listed by the preview on the new PC.");
                sb.AppendLine();
                foreach (var g in groups.OrderByDescending(x => x.Include).ThenByDescending(x => x.Bytes))
                    sb.AppendLine((g.Include ? "[x] " : "[ ] ") + g.SizeText.PadLeft(10) + "  " + g.Files.ToString("N0").PadLeft(10) + " files  " + g.WhyText.PadRight(26) + g.Path);
                string path = Path.Combine(Store, "plan.txt");
                File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
                OpenFile(path);
            }
            catch (Exception e) { ShowDialog("Not saved", e.Message, "OK", null, null); }
        }

        void ShowPreviewDone(int code)
        {
            previewRun = false;
            F<CheckBox>("DrySwitch").IsChecked = false;
            F<UIElement>("HeroCard").Visibility = Visibility.Collapsed;
            F<UIElement>("DoneCard").Visibility = Visibility.Visible;
            bool ok = File.Exists(Path.Combine(Store, "preview.txt"));
            F<TextBlock>("ImportTitle").Text = "Preview";
            F<TextBlock>("DoneStampText").Text = "NOTHING CHANGED";
            F<TextBlock>("DoneTitle").Text = ok ? "Read the preview, then start" : "The preview stopped";
            F<TextBlock>("DoneText").Text = ok ? "The list is in " + Store + "\\preview.txt. Go back and start the move when it looks right." : "The details show why.";
            var back = F<Button>("BtnReconnect");
            back.Content = "Back to the move";
            back.Visibility = Visibility.Visible;
            F<UIElement>("BtnPreviewOpen").Visibility = ok ? Visibility.Visible : Visibility.Collapsed;
            SetRoute(0, "PREVIEW");
            if (currentPage != "PageImport") ShowPage("PageImport", "New PC");
            if (ok) OpenFile(Path.Combine(Store, "preview.txt"));
        }

        void OnImportExit(int code)
        {
            F<UIElement>("ActivePanel").Visibility = Visibility.Collapsed;
            F<UIElement>("HeroHazard").Visibility = Visibility.Collapsed;
            F<UIElement>("HeroWait").Visibility = Visibility.Collapsed;
            foreach (var r in importRows.Where(r => r.State == "running")) r.State = code == 0 ? "done" : "failed";
            if (previewRun) { ShowPreviewDone(code); return; }
            F<UIElement>("BtnPreviewOpen").Visibility = Visibility.Collapsed;
            F<Button>("BtnReconnect").Content = "Connect again";
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
            F<TextBlock>("DoneText").Text =
                needReboot ? "Restart this PC: WSL needs it. Then start Hopover, connect with the code again and start the move. Done steps are skipped." :
                linkLost ? "The link to the old PC was lost. Check that the old PC is on and shows the code, click 'Connect again' and start the move. Hopover copies only what is missing." :
                "Just start Hopover again - it retries the failed steps and only copies what's missing. The details show what went wrong.";
            F<UIElement>("BtnReconnect").Visibility = linkLost ? Visibility.Visible : Visibility.Collapsed;
            SetRoute(lastFrac, "STOPPED");
            if (currentPage != "PageImport") ShowPage("PageImport", "New PC");
            ToggleLogOpen("ImportLogBorder", "BtnImportLog");
        }

        // ---------- coffee pledge ----------
        void ShowPledge()
        {
            F<TextBlock>("PledgeText").Text = "I promise: if my move works, Paul gets his coffee at the end. A promise is a promise - even on the internet.";
            F<TextBlock>("PriceNote").Text = Branding.PriceLabel.ToUpperInvariant().Replace(", ", " \u00B7 ");
            F<TextBlock>("TicketNo").Text = "NO. " + Pledge.TicketNo;
            F<TextBlock>("TicketFrom").Text = Environment.MachineName.ToUpperInvariant();
            F<TextBlock>("LicenceLine").Text = "Source available under " + Branding.LicenceName + ".";
            F<TextBlock>("SignedLine").Text = "";
            F<CheckBox>("PledgeCheck").IsChecked = false;
            F<Button>("BtnDeal").IsEnabled = false;
            ShowPage("PagePledge", "Before we load the truck");
            ShowArrow();
        }

        // ---------- the "tick this first" arrow ----------
        void ShowArrow()
        {
            if (F<CheckBox>("PledgeCheck").IsChecked == true) return;
            var layer = F<UIElement>("ArrowLayer");
            layer.Visibility = Visibility.Visible;
            UpdateArrow();
            Window.Dispatcher.BeginInvoke(new Action(UpdateArrow), System.Windows.Threading.DispatcherPriority.Loaded);
            if (!Motion.Enabled) return;
            // A gentle pulse: opacity 1 to 0.6 and back, 1.4 s a cycle.
            var pulse = new DoubleAnimation(1, 0.6, TimeSpan.FromMilliseconds(700)) { AutoReverse = true, RepeatBehavior = RepeatBehavior.Forever, EasingFunction = new SineEase() };
            layer.BeginAnimation(UIElement.OpacityProperty, pulse);
        }

        void HideArrow()
        {
            var layer = F<UIElement>("ArrowLayer");
            layer.BeginAnimation(UIElement.OpacityProperty, null);
            layer.Visibility = Visibility.Collapsed;
        }

        /// <summary>A hand-drawn looking curve from under the body text to the checkbox on the ticket.</summary>
        void UpdateArrow()
        {
            var page = F<FrameworkElement>("PagePledge");
            var check = F<CheckBox>("PledgeCheck");
            var deal = F<Button>("BtnDeal");
            if (page == null || !page.IsVisible || !check.IsVisible) return;
            Point head, boxTopLeft;
            try
            {
                head = check.TransformToAncestor(page).Transform(new Point(-12, 12));
                boxTopLeft = deal.TransformToAncestor(page).Transform(new Point(0, 0));
            }
            catch { return; }
            var label = F<TextBlock>("ArrowLabel");
            label.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
            double lx = 56, ly = boxTopLeft.Y - 118;
            Canvas.SetLeft(label, lx); Canvas.SetTop(label, ly);
            var tail = new Point(lx + label.DesiredSize.Width + 12, ly + label.DesiredSize.Height / 2 + 2);
            double dx = head.X - tail.X, dy = head.Y - tail.Y;
            var fig = new PathFigure { StartPoint = tail };
            // Two bezier pieces with a little wobble, like a stroke drawn by hand.
            var mid = new Point(tail.X + dx * 0.52, tail.Y + dy * 0.62 + 12);
            fig.Segments.Add(new BezierSegment(new Point(tail.X + dx * 0.18, tail.Y + 26), new Point(mid.X - dx * 0.14, mid.Y + 18), mid, true));
            var c2 = new Point(mid.X + dx * 0.16, mid.Y - 24);
            var c3 = new Point(head.X - dx * 0.08, head.Y + dy * -0.02 + 34);
            fig.Segments.Add(new BezierSegment(c2, c3, head, true));
            // Arrowhead: two short strokes back along the tangent at the tip.
            double ang = Math.Atan2(head.Y - c3.Y, head.X - c3.X);
            foreach (double side in new[] { 0.5, -0.5 })
            {
                var back = new Point(head.X - 16 * Math.Cos(ang + side), head.Y - 16 * Math.Sin(ang + side));
                fig.Segments.Add(new LineSegment(back, true));
                fig.Segments.Add(new LineSegment(head, true));
            }
            var g = new PathGeometry();
            g.Figures.Add(fig);
            F<System.Windows.Shapes.Path>("ArrowPath").Data = g;
        }

        void SetupCoffee()
        {
            var check = F<CheckBox>("PledgeCheck");
            check.Checked += (s, e) =>
            {
                HideArrow();
                F<Button>("BtnDeal").IsEnabled = true;
                F<TextBlock>("SignedLine").Text = Environment.UserName + " · " + DateTime.Now.ToString("yyyy-MM-dd HH:mm");
            };
            check.Unchecked += (s, e) => { F<Button>("BtnDeal").IsEnabled = false; F<TextBlock>("SignedLine").Text = ""; ShowArrow(); };
            F<FrameworkElement>("PagePledge").SizeChanged += (s, e) => UpdateArrow();
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
            ShowDialog("Did it go through?", "Hopover can't see your payment, so I'll just trust you on this one :)", "Yes, it went through", "Not yet",
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
            string rest = sumBytes > 0 && lastFrac > 0 ? "the " + Fmt.Size(sumBytes * (1 - lastFrac)) + " still to copy" : "the rest of your files";
            F<TextBlock>("NoteText").Text = "It's riding in the back, somewhere between " + (sumDistros > 0 ? "WSL" : "your settings") + " and " + rest +
                                            ". No pressure. Well, a little pressure ;)";
            F<TextBlock>("NoteQuote").Text = "\u201CPinky promise: I buy Paul a coffee when my move works.\u201D";
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
            bool serving = serve != null && serve.Running;
            // The move is over: offer once to leave no passwords behind.
            if (!engine.Running && !serving && !closeConfirmed && File.Exists(Path.Combine(Store, "baseline.json")))
            {
                e.Cancel = true;
                closeConfirmed = true;
                ShowDialog("Clean up before you go?",
                    "Hopover's working folder " + Store + " still holds Wi-Fi passwords, tokens and a WSL copy. Delete it now, or keep it if you want to catch up with the old PC again. Your own files stay.",
                    "Clean up and close", "Just close", () => Purge(() => Window.Close()), () => Window.Close());
                return;
            }
            if ((!engine.Running && !serving) || closeConfirmed) return;
            e.Cancel = true;
            string text = engine.Running
                ? "Hopover is still working. You can close it though - start it again later and it picks up where it stopped."
                : "The new PC copies through this link. Close Hopover here only when the move on the new PC is finished.";
            ShowDialog("Stop Hopover?", text, "Stop and close", "Keep working", () => { engine.Kill(); closeConfirmed = true; Window.Close(); });
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
                    F<TextBlock>("ScanText").Text = "Scan done. Untick whatever should stay behind.";
                    F<Button>("BtnExport").IsEnabled = true;
                    ShowPage("PageScan", "Old PC");
                    break;
                case "scan-reading":
                    foreach (var kv in BuildSwitches("OldSteps", ExportSteps.Select(s => s.Key), new string[0])) oldSwitches[kv.Key] = kv.Value;
                    foreach (var path in new[] { @"C:\Users\pp\.ssh", @"C:\Users\pp\.gradle", @"C:\Users\pp\AppData\Roaming\JetBrains", @"C:\java\workspace\slaides", @"C:\java\workspace\aivory-guard", @"C:\java\workspace\hopover" }) TickerAdd(path);
                    F<ProgressBar>("ScanBar").Value = 0.37;
                    F<TextBlock>("ScanText").Text = "Measured 693 of 1,873  ·  C:\\java\\workspace\\hopover";
                    ShowPage("PageScan", "Old PC");
                    break;
                case "share":
                    FillSteps(exportRows, ExportSteps, null);
                    foreach (var r in exportRows) r.State = "done";
                    exportRows[3].State = "skipped";
                    ApplyShare("", false);
                    F<TextBlock>("LinkCode").Text = "3-alpha-bravo-cafe";
                    SetLinkBadge(PathText("lan"), true);
                    F<TextBlock>("LinkPeer").Text = "Connected: NEWPC-2026";
                    F<TextBlock>("ExportTitle").Text = "This PC is ready";
                    ShowPage("PageExport", "Old PC");
                    SetRoute(1, "READY  ·  LINK OPEN");
                    break;
                case "share-waiting":
                    FillSteps(exportRows, ExportSteps, null);
                    foreach (var r in exportRows) r.State = "done";
                    ApplyShare("", false);
                    F<TextBlock>("LinkCode").Text = "7-yankee-orange-vision";
                    SetLinkBadge("WRONG CODE  ·  NEW CODE", false);
                    F<TextBlock>("LinkPeer").Text = "A wrong code was typed. Use the new code.";
                    F<TextBlock>("ExportTitle").Text = "This PC is ready";
                    ShowPage("PageExport", "Old PC");
                    SetRoute(1, "READY  ·  LINK OPEN");
                    break;
                case "connect-noroute":
                    F<RadioButton>("ModeNet").IsChecked = true;
                    F<TextBox>("CodeBox").Text = "3-alpha-bravo-cafe";
                    SetBadge("ConnectBadge", "ConnectBadgeText", "NO ROUTE", false);
                    F<UIElement>("ConnectBadge").Visibility = Visibility.Visible;
                    SetConnectMsg("No direct route. Put both PCs on the same network, or use a USB disk.", true);
                    ShowPage("PageConnect", "New PC");
                    break;
                case "connect":
                    foreach (var kv in BuildSwitches("NewSteps", ImportSteps.Select(s => s.Key), new[] { "env" })) newSwitches[kv.Key] = kv.Value;
                    F<TextBox>("CodeBox").Text = "3-alpha-bravo-cafe";
                    SetBadge("ConnectBadge", "ConnectBadgeText", PathText("lan"), true);
                    F<UIElement>("ConnectBadge").Visibility = Visibility.Visible;
                    F<TextBlock>("ConnectText").Text = "Connected to MADPPIPER-2022.";
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
                    Window.UpdateLayout();
                    UpdateArrow();
                    break;
                case "pledge-signed":
                    F<CheckBox>("PledgeCheck").IsChecked = true;
                    break;
            }
        }


        static Dictionary<string, object> Row(string path, string why, double bytes, long files) =>
            new Dictionary<string, object> { ["Path"] = path, ["Group"] = path, ["Why"] = why, ["Bytes"] = (long)bytes, ["Files"] = files };
    }
}
