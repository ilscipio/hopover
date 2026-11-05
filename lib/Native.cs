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
        // scan: below-normal CPU, normal I/O. The scan only reads folder entries, and somebody waits for it.
        public static void Lower(Process p, string speed)
        {
            if (speed == "full") return;
            if (speed == "scan") { try { p.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { } return; }
            try { p.PriorityClass = speed == "gentle" ? ProcessPriorityClass.Idle : ProcessPriorityClass.BelowNormal; } catch { }
            try { int io = 1; NtSetInformationProcess(p.Handle, ProcessIoPriority, ref io, 4); } catch { }
        }
    }

    // Generic Windows Credential Manager entries (git, gh, docker, ...). Each one belongs to this user; a process of
    // this user reads and writes them in clear. Domain passwords are not readable, and Windows keeps those to itself.
    public class Cred
    {
        public string Target;
        public string User;
        public string Comment;
        public int Persist;
        public string Blob;

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        struct NativeCred
        {
            public uint Flags;
            public uint Type;
            public IntPtr TargetName;
            public IntPtr Comment;
            public long LastWritten;
            public uint BlobSize;
            public IntPtr Blob;
            public uint Persist;
            public uint AttributeCount;
            public IntPtr Attributes;
            public IntPtr TargetAlias;
            public IntPtr UserName;
        }

        [DllImport("advapi32.dll", EntryPoint = "CredEnumerateW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredEnumerate(string filter, int flags, out int count, out IntPtr creds);
        [DllImport("advapi32.dll", EntryPoint = "CredWriteW", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CredWrite(ref NativeCred cred, int flags);
        [DllImport("advapi32.dll")]
        static extern void CredFree(IntPtr p);

        public static Cred[] ReadAll()
        {
            List<Cred> res = new List<Cred>();
            int count;
            IntPtr list;
            if (!CredEnumerate(null, 0, out count, out list)) return res.ToArray();
            try
            {
                for (int i = 0; i < count; i++)
                {
                    IntPtr item = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                    NativeCred n = (NativeCred)Marshal.PtrToStructure(item, typeof(NativeCred));
                    if (n.Type != 1 || n.Persist < 2 || n.Blob == IntPtr.Zero || n.BlobSize == 0) continue;
                    byte[] b = new byte[n.BlobSize];
                    Marshal.Copy(n.Blob, b, 0, b.Length);
                    Cred c = new Cred();
                    c.Target = Marshal.PtrToStringUni(n.TargetName);
                    c.User = n.UserName == IntPtr.Zero ? null : Marshal.PtrToStringUni(n.UserName);
                    c.Comment = n.Comment == IntPtr.Zero ? null : Marshal.PtrToStringUni(n.Comment);
                    c.Persist = (int)n.Persist;
                    c.Blob = Convert.ToBase64String(b);
                    res.Add(c);
                }
            }
            finally { CredFree(list); }
            return res.ToArray();
        }

        public static bool Write(string target, string user, string comment, int persist, string blob)
        {
            byte[] b = Convert.FromBase64String(blob);
            NativeCred n = new NativeCred();
            n.Type = 1;
            n.Persist = (uint)persist;
            n.BlobSize = (uint)b.Length;
            n.TargetName = Marshal.StringToCoTaskMemUni(target);
            n.UserName = user == null ? IntPtr.Zero : Marshal.StringToCoTaskMemUni(user);
            n.Comment = comment == null ? IntPtr.Zero : Marshal.StringToCoTaskMemUni(comment);
            n.Blob = Marshal.AllocCoTaskMem(b.Length);
            try
            {
                Marshal.Copy(b, 0, n.Blob, b.Length);
                return CredWrite(ref n, 0);
            }
            finally
            {
                Marshal.FreeCoTaskMem(n.TargetName);
                Marshal.FreeCoTaskMem(n.UserName);
                Marshal.FreeCoTaskMem(n.Comment);
                Marshal.FreeCoTaskMem(n.Blob);
            }
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
        int running;
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
        // Cancel leaves jobs unstarted, so the count alone would never arrive.
        public bool Finished { get { return FinishedCount >= jobs.Length || (cancel && Thread.VolatileRead(ref running) == 0); } }

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
                Interlocked.Increment(ref running);
                Started.Enqueue(j);
                try { Run(j); }
                catch (Exception e) { j.ExitCode = 16; Errors.Enqueue(j.Path + ": " + e.Message); }
                // Done first: a reader that sees Finished must also find the job in Done.
                Done.Enqueue(j);
                Interlocked.Increment(ref finished);
                Interlocked.Decrement(ref running);
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
