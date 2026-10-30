using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Threading;

namespace Hopover.App
{
    /// <summary>
    /// Runs hopover.ps1 as a hidden child. Lines that start with ## are events
    /// ("##STEP files start"); every other line is log text. Callbacks run on the UI thread.
    /// </summary>
    public class EngineRun
    {
        readonly Dispatcher ui;
        Process proc;

        public Action<string, string> OnEvent;
        public Action<string> OnLine;
        public Action<int> OnExit;

        public EngineRun(Dispatcher ui) { this.ui = ui; }

        public bool Running => proc != null && !proc.HasExited;

        public void Start(string engineDir, IEnumerable<string> args, IDictionary<string, string> env = null)
        {
            string ps = Path.Combine(Environment.SystemDirectory, @"WindowsPowerShell\v1.0\powershell.exe");
            var psi = new ProcessStartInfo(ps)
            {
                Arguments = "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " +
                            Quote(Path.Combine(engineDir, "hopover.ps1")) + " " + string.Join(" ", args.Select(Quote)),
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                WorkingDirectory = engineDir,
            };
            // The engine must fail instead of prompting: nobody can answer a hidden console.
            psi.EnvironmentVariables["HOPOVER_GUI"] = "1";
            if (env != null) foreach (var kv in env) if (kv.Value != null) psi.EnvironmentVariables[kv.Key] = kv.Value;

            proc = new Process { StartInfo = psi };
            proc.OutputDataReceived += (s, e) => { if (e.Data != null) ui.BeginInvoke(new Action(() => Handle(e.Data))); };
            proc.ErrorDataReceived += (s, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) ui.BeginInvoke(new Action(() => OnLine?.Invoke("! " + e.Data))); };
            proc.Start();
            proc.BeginOutputReadLine();
            proc.BeginErrorReadLine();
            var p = proc;
            Task.Run(() =>
            {
                // WaitForExit() without a timeout also waits for the last output line.
                p.WaitForExit();
                int code = p.ExitCode;
                ui.BeginInvoke(new Action(() => OnExit?.Invoke(code)));
            });
        }

        void Handle(string line)
        {
            if (line.StartsWith("##"))
            {
                int sp = line.IndexOf(' ');
                string kind = sp < 0 ? line.Substring(2) : line.Substring(2, sp - 2);
                string data = sp < 0 ? "" : line.Substring(sp + 1);
                OnEvent?.Invoke(kind, data);
            }
            else if (line.Trim().Length > 0) OnLine?.Invoke(line);
        }

        /// <summary>Stops the engine and its children (robocopy, winget, wsl).</summary>
        public void Kill()
        {
            if (!Running) return;
            try
            {
                using (var k = Process.Start(new ProcessStartInfo("taskkill.exe", "/PID " + proc.Id + " /T /F") { CreateNoWindow = true, UseShellExecute = false }))
                    k?.WaitForExit(5000);
            }
            catch { }
        }

        /// <summary>One command-line argument, quoted by the Windows rules (CommandLineToArgvW).</summary>
        public static string Quote(string a)
        {
            if (a.Length > 0 && a.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return a;
            var sb = new StringBuilder("\"");
            int slashes = 0;
            foreach (char c in a)
            {
                if (c == '\\') { slashes++; continue; }
                // Backslashes before a quote are doubled, and the quote itself escaped.
                if (c == '"') { sb.Append('\\', slashes * 2 + 1).Append('"'); slashes = 0; continue; }
                sb.Append('\\', slashes).Append(c);
                slashes = 0;
            }
            // Backslashes at the end are doubled, or they would escape the closing quote.
            sb.Append('\\', slashes * 2).Append('"');
            return sb.ToString();
        }
    }

    /// <summary>The PowerShell engine ships inside Hopover.exe and unpacks once per build.</summary>
    public static class EngineFiles
    {
        public static string Extract(string exePath)
        {
            var asm = Assembly.GetExecutingAssembly();
            string dir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                                      "Hopover", "engine", asm.ManifestModule.ModuleVersionId.ToString("N").Substring(0, 12));
            string marker = Path.Combine(dir, ".complete");
            if (!File.Exists(marker))
            {
                foreach (string name in asm.GetManifestResourceNames().Where(n => n.StartsWith("engine/")))
                {
                    string target = Path.Combine(dir, name.Substring(7).Replace('/', '\\'));
                    Directory.CreateDirectory(Path.GetDirectoryName(target));
                    using (var src = asm.GetManifestResourceStream(name))
                    using (var dst = File.Create(target))
                        src.CopyTo(dst);
                }
                File.WriteAllText(marker, DateTime.Now.ToString("s"));
            }
            // An include.json next to Hopover.exe overrides what moves.
            string custom = Path.Combine(Path.GetDirectoryName(exePath) ?? "", "include.json");
            if (File.Exists(custom)) File.Copy(custom, Path.Combine(dir, "config", "include.json"), true);
            return dir;
        }
    }
}
