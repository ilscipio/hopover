// Parallel robocopy pool for the engine. Loaded with Add-Type, so it must stay C# 5
// (Windows PowerShell 5.1 compiles it with the .NET Framework compiler).
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;

namespace Hopover
{
    public static class Native
    {
        [DllImport("ntdll.dll")]
        static extern int NtSetInformationProcess(IntPtr process, int infoClass, ref int info, int length);

        const int ProcessIoPriority = 33;

        // gentle: idle CPU, low I/O. balanced: below-normal CPU, low I/O. full: normal.
        // Low I/O is the "background" class: the user's own programs get the disk first.
        public static void Lower(Process p, string speed)
        {
            if (speed == "full") return;
            try { p.PriorityClass = speed == "gentle" ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal; } catch { }
            try { int io = 1; NtSetInformationProcess(p.Handle, ProcessIoPriority, ref io, 4); } catch { }
        }
    }

    public class RoboJob
    {
        public int Index;
        public string Path;
        public string Exe;          // null = robocopy.exe; the link copy sets Hopover.exe
        public string Arguments;
        public long Planned;
        public int ExitCode = -1;
        public long Copied;
        public long Files;
        public long Bytes;
    }

    public class RoboPool
    {
        static readonly Regex SummaryRow = new Regex(@"^\s*[^:]+:\s+(\d+)\s+\d+", RegexOptions.Compiled);

        readonly RoboJob[] jobs;
        readonly int workers;
        readonly string speed;
        readonly bool measure;
        int next = -1;
        int finished;
        long copied;
        volatile bool cancel;

        public readonly ConcurrentQueue<RoboJob> Started = new ConcurrentQueue<RoboJob>();
        public readonly ConcurrentQueue<RoboJob> Done = new ConcurrentQueue<RoboJob>();
        public readonly ConcurrentQueue<string> Errors = new ConcurrentQueue<string>();

        public RoboPool(RoboJob[] jobs, int workers, string speed, bool measure)
        {
            this.jobs = jobs;
            this.workers = Math.Max(1, Math.Min(workers, Math.Max(1, jobs.Length)));
            this.speed = speed;
            this.measure = measure;
        }

        public long Copied { get { return Interlocked.Read(ref copied); } }
        public int FinishedCount { get { return Thread.VolatileRead(ref finished); } }
        public bool Finished { get { return FinishedCount >= jobs.Length; } }

        public void Start()
        {
            for (int i = 0; i < workers; i++)
            {
                Thread t = new Thread(Work);
                t.IsBackground = true;
                t.Priority = speed == "full" ? ThreadPriority.Normal : ThreadPriority.BelowNormal;
                t.Start();
            }
        }

        public void Cancel() { cancel = true; }

        void Work()
        {
            while (!cancel)
            {
                int i = Interlocked.Increment(ref next);
                if (i >= jobs.Length) return;
                RoboJob j = jobs[i];
                Started.Enqueue(j);
                try { Run(j); }
                catch (Exception e) { j.ExitCode = 16; Errors.Enqueue(j.Path + ": " + e.Message); }
                // Done first: a reader that sees Finished must also find the job in Done.
                Done.Enqueue(j);
                Interlocked.Increment(ref finished);
            }
        }

        void Run(RoboJob j)
        {
            ProcessStartInfo psi = new ProcessStartInfo(j.Exe ?? "robocopy.exe", j.Arguments);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            // Default output encoding on purpose: only digits are parsed, and the OEM code
            // page lookup fails in a hidden process ("1 is not a supported code page").
            using (Process p = Process.Start(psi))
            {
                Native.Lower(p, speed);
                p.ErrorDataReceived += delegate { };
                p.BeginErrorReadLine();
                List<long> rows = new List<long>();
                string line;
                // Lines are handled one by one and dropped: 3 million file lines cost no memory.
                while ((line = p.StandardOutput.ReadLine()) != null)
                {
                    if (cancel) { try { p.Kill(); } catch { } break; }
                    if (measure)
                    {
                        Match m = SummaryRow.Match(line);
                        if (m.Success) rows.Add(long.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture));
                        continue;
                    }
                    long n = FileBytes(line);
                    if (n >= 0) { Interlocked.Add(ref copied, n); j.Copied += n; }
                    else if (line.IndexOf("ERROR", StringComparison.Ordinal) >= 0 || line.IndexOf("FEHLER", StringComparison.Ordinal) >= 0)
                        Errors.Enqueue(line.Trim());
                }
                p.WaitForExit();
                j.ExitCode = cancel ? 16 : p.ExitCode;
                // Summary rows are Dirs, Files, Bytes; labels are localised, so take them by position.
                if (measure && rows.Count >= 3) { j.Files = rows[1]; j.Bytes = rows[2]; }
            }
        }

        // A file line with /BYTES: "\t    New File  \t\t   1048576\tC:\path". "*EXTRA" lines copy nothing.
        static long FileBytes(string line)
        {
            if (line.Length == 0 || line[0] != '\t') return -1;
            string[] parts = line.Split('\t');
            if (parts.Length < 4 || parts[1].IndexOf('*') >= 0) return -1;
            // Lines for files robocopy does NOT copy (only with some switches): never count them.
            string label = parts[1].Trim().ToLowerInvariant();
            if (label.StartsWith("older") || label.StartsWith("älter") || label.StartsWith("same") || label.StartsWith("gleich") ||
                label.StartsWith("skipped") || label.StartsWith("übersprungen") || label.StartsWith("mismatch")) return -1;
            for (int k = 2; k < parts.Length - 1; k++)
            {
                string s = parts[k].Trim();
                if (s.Length == 0) continue;
                long v;
                return long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v) ? v : -1;
            }
            return -1;
        }
    }
}
