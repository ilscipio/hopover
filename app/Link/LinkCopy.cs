using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace Hopover.Link
{
    /// <summary>
    /// "Hopover.exe --link copy SRC DST [FILE...] [/E] [/XD d...] [/XF f...] [/XO] [/XJ] [/L] [/MT:n] [/R:n] [/W:n]":
    /// the robocopy of the link. SRC is \\anyname\hopover-store\... or \\anyname\hopover-d\... (the old PC's virtual
    /// roots), DST a local folder. Output uses robocopy's tab format and its exit codes (1 copied, 8 failed, 16 fatal),
    /// so the engine's pool, progress bar and resume list work unchanged.
    /// Rule (/XO): copy when the destination is missing, the source is newer, or the time is equal and the size differs.
    /// Nothing is ever deleted except an older destination file that is replaced. No ACLs and no alternate streams.
    /// </summary>
    public static class LinkCopy
    {
        const long PartStep = 32L << 20;       // flush + sidecar every 32 MiB
        const int ReqSize = 2 << 20;
        const int Depth = 3;
        static readonly long TwoSec = TimeSpan.FromSeconds(2).Ticks;

        sealed class Job { public string Root, Rel, Dst; public Entry E; }

        sealed class Ctx
        {
            public MuxClient Mux;
            public TextWriter Out;
            public bool Recurse, List;
            public int Retries = 1, WaitSec = 1;
            public List<string> XdNames = new List<string>(), XdPaths = new List<string>(), XfPatterns = new List<string>();
            public long Copied;
            public int FilesCopied, Failed;
            public readonly object OutLock = new object();
            public readonly List<KeyValuePair<string, Entry>> Dirs = new List<KeyValuePair<string, Entry>>();
        }

        public static string Lp(string p)
        {
            // Always the \\?\ form: no 260 limit, and names like "nul" or "con" are plain files, as in robocopy.
            if (p.StartsWith(@"\\?\")) return p;
            // No GetFullPath for an absolute path: it turns a file called "nul" into the device \\.\nul.
            string full = (p.Length > 2 && p[1] == ':' && p[2] == '\\') || p.StartsWith(@"\\") ? p : Path.GetFullPath(p);
            return full.StartsWith(@"\\") ? @"\\?\UNC\" + full.Substring(2) : @"\\?\" + full;
        }

        static void Say(Ctx c, string line) { lock (c.OutLock) c.Out.WriteLine(line); }

        static void Bytes(Ctx c, string label, long n, string path)
        {
            // Same shape as a robocopy /BYTES line: tab, label, tab, tab, size, tab, path.
            Say(c, "\t    " + label + "  \t\t" + n.ToString().PadLeft(12) + "\t" + path);
        }

        public static string RootOf(string share)
        {
            string s = share.ToLowerInvariant();
            if (s == "store" || s == "hopover-store") return "store";
            if (s.StartsWith("hopover-") && s.Length == 9) return "drive-" + s[8];
            if (s.StartsWith("drive-") && s.Length == 7) return s;
            throw new ArgumentException("unknown source: " + share);
        }

        /// <summary>\\host\share\rest into (root, rel). rel has no leading or trailing backslash.</summary>
        public static void ParseSrc(string src, out string root, out string rel)
        {
            string s = src;
            if (!s.StartsWith(@"\\")) throw new ArgumentException("source must look like \\\\link\\hopover-store\\...: " + src);
            string[] p = s.Substring(2).Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            if (p.Length < 2) throw new ArgumentException("source has no share: " + src);
            root = RootOf(p[1]);
            rel = string.Join("\\", p.Skip(2));
        }

        static bool Wild(string pat, string name)
        {
            // * and ? only, case-insensitive, like robocopy.
            int p = 0, n = 0, star = -1, mark = 0;
            pat = pat.ToLowerInvariant(); name = name.ToLowerInvariant();
            while (n < name.Length)
            {
                if (p < pat.Length && (pat[p] == '?' || pat[p] == name[n])) { p++; n++; }
                else if (p < pat.Length && pat[p] == '*') { star = p++; mark = n; }
                else if (star >= 0) { p = star + 1; n = ++mark; }
                else return false;
            }
            while (p < pat.Length && pat[p] == '*') p++;
            return p == pat.Length;
        }

        static string Canon(string root, string rel) { return (root + "\\" + rel).TrimEnd('\\').ToLowerInvariant(); }

        public static int Run(string[] args, TextWriter output)
        {
            var c = new Ctx { Out = output };
            var pos = new List<string>();
            int i = 0;
            for (; i < args.Length && !args[i].StartsWith("/"); i++) pos.Add(args[i]);
            int mt = 8;
            while (i < args.Length)
            {
                string a = args[i++];
                string u = a.ToUpperInvariant();
                if (u == "/E" || u == "/S") c.Recurse = true;
                else if (u == "/L") c.List = true;
                else if (u == "/XD" || u == "/XF")
                {
                    for (; i < args.Length && !args[i].StartsWith("/"); i++)
                    {
                        if (u == "/XF") { c.XfPatterns.Add(args[i]); continue; }
                        string v = args[i];
                        if (v.StartsWith(@"\\"))
                        {
                            try { string r, rel; ParseSrc(v, out r, out rel); c.XdPaths.Add(Canon(r, rel)); } catch { }
                        }
                        else c.XdNames.Add(v);
                    }
                }
                else if (u.StartsWith("/MT:")) int.TryParse(a.Substring(4), out mt);
                else if (u.StartsWith("/R:")) int.TryParse(a.Substring(3), out c.Retries);
                else if (u.StartsWith("/W:")) int.TryParse(a.Substring(3), out c.WaitSec);
            }
            if (pos.Count < 2) { Say(c, "ERROR usage: --link copy SRC DST [FILE...] [/E] [/XD ...] [/XF ...]"); return 16; }
            mt = Math.Max(1, Math.Min(mt, 8));

            string root, rel0, dst;
            try { ParseSrc(pos[0], out root, out rel0); dst = Path.GetFullPath(pos[1]); }
            catch (Exception e) { Say(c, "ERROR " + e.Message); return 16; }

            try { c.Mux = MuxClient.ConnectFromEnv(); }
            catch (Exception e) { Say(c, "ERROR no link: " + e.Message); return 16; }

            var queue = new BlockingCollection<Job>(256);
            var workers = new List<Thread>();
            for (int k = 0; k < mt; k++)
            {
                var t = new Thread(() =>
                {
                    foreach (var j in queue.GetConsumingEnumerable())
                    {
                        try { CopyFile(c, j); }
                        catch (Exception e) { Fail(c, j.Dst, e); }
                    }
                }) { IsBackground = true };
                t.Start();
                workers.Add(t);
            }

            int fatal = 0;
            try
            {
                if (pos.Count > 2)
                {
                    // Kind=file: SRCDIR DSTDIR NAME...
                    if (!c.List) Directory.CreateDirectory(Lp(dst));
                    foreach (string name in pos.Skip(2))
                    {
                        string r = rel0.Length == 0 ? name : rel0 + "\\" + name;
                        Entry e;
                        try { e = c.Mux.Stat(root, r); }
                        catch (Exception ex)
                        {
                            // Deleted on the old PC since the scan: nothing to copy, not a failure (else the chunk fails forever).
                            if (IsGone(ex)) Say(c, "SKIPPED source gone: " + pos[0] + "\\" + name);
                            else Fail(c, pos[0] + "\\" + name, ex);
                            continue;
                        }
                        if (e == null || e.IsDir) { Fail(c, pos[0] + "\\" + name, new FileNotFoundException("not a file")); continue; }
                        e.Name = name;
                        queue.Add(new Job { Root = root, Rel = r, Dst = Path.Combine(dst, name), E = e });
                    }
                }
                else
                {
                    if (!c.List) Directory.CreateDirectory(Lp(dst));
                    List<Entry> top;
                    try { top = c.Mux.List(root, rel0); }
                    catch (Exception ex)
                    {
                        top = null;
                        if (SourceGone(c, root, rel0)) Say(c, "SKIPPED source gone: " + pos[0]);
                        else { Say(c, "ERROR source " + pos[0] + ": " + ex.Message); fatal = 16; }
                    }
                    if (top != null) Walk(c, queue, root, rel0, dst, top);
                }
            }
            catch (Exception e) { Say(c, "ERROR " + e.Message); fatal = 16; }
            queue.CompleteAdding();
            foreach (var t in workers) t.Join();
            if (!c.List) ApplyDirTimes(c);
            c.Mux.Dispose();
            int code = fatal;
            if (c.Failed > 0) code |= 8;
            if (c.FilesCopied > 0) code |= 1;
            return code;
        }

        // The server answers "not found" (its own text, not localised) for a path that does not exist.
        static bool IsGone(Exception e) { return e is LinkException && e.Message == "not found"; }

        static bool SourceGone(Ctx c, string root, string rel)
        {
            try { c.Mux.Stat(root, rel); return false; }
            catch (Exception e) { return IsGone(e); }
        }

        static void Fail(Ctx c, string path, Exception e)
        {
            Interlocked.Increment(ref c.Failed);
            Say(c, "ERROR copying " + path + " : " + e.Message.Replace('\n', ' ').Replace('\r', ' '));
        }

        static void Walk(Ctx c, BlockingCollection<Job> queue, string root, string rel, string dstDir, List<Entry> entries)
        {
            foreach (var e in entries)
            {
                if (e.IsReparse)                                    // /XJ: junctions and symlinks are never followed
                {
                    Say(c, "SKIPPED link " + Path.Combine(dstDir, e.Name));
                    continue;
                }
                string childRel = rel.Length == 0 ? e.Name : rel + "\\" + e.Name;
                string childDst = Path.Combine(dstDir, e.Name);
                if (e.IsDir)
                {
                    if (!c.Recurse) continue;
                    if (c.XdNames.Any(p => Wild(p, e.Name)) || c.XdPaths.Contains(Canon(root, childRel))) continue;
                    if (!c.List)
                    {
                        try { Directory.CreateDirectory(Lp(childDst)); }
                        catch (Exception ex) { Fail(c, childDst, ex); continue; }
                        lock (c.Dirs) c.Dirs.Add(new KeyValuePair<string, Entry>(childDst, e));
                    }
                    List<Entry> sub;
                    try { sub = c.Mux.List(root, childRel); }
                    catch (Exception ex) { Fail(c, childRel, ex); continue; }
                    Walk(c, queue, root, childRel, childDst, sub);
                }
                else
                {
                    if (c.XfPatterns.Any(p => Wild(p, e.Name))) continue;
                    queue.Add(new Job { Root = root, Rel = childRel, Dst = childDst, E = e });
                }
            }
        }

        static void ApplyDirTimes(Ctx c)
        {
            for (int k = c.Dirs.Count - 1; k >= 0; k--)
            {
                try
                {
                    string d = Lp(c.Dirs[k].Key);
                    var e = c.Dirs[k].Value;
                    Directory.SetCreationTimeUtc(d, new DateTime(e.CTime, DateTimeKind.Utc));
                    Directory.SetLastWriteTimeUtc(d, new DateTime(e.MTime, DateTimeKind.Utc));
                    var keep = (FileAttributes)e.Attrs & (FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive | FileAttributes.ReadOnly);
                    File.SetAttributes(d, keep == 0 ? FileAttributes.Directory : keep | FileAttributes.Directory);
                }
                catch (Exception) { }
            }
        }

        /// <summary>The /XO decision.</summary>
        static bool NeedsCopy(string dst, Entry e)
        {
            var fi = new FileInfo(Lp(dst));
            if (!fi.Exists) return true;
            long dm = fi.LastWriteTimeUtc.Ticks;
            if (e.MTime > dm + TwoSec) return true;             // source newer
            if (e.MTime < dm - TwoSec) return false;            // destination newer: keep it
            return fi.Length != e.Size;                         // same time, other size
        }

        static void CopyFile(Ctx c, Job j)
        {
            if (!NeedsCopy(j.Dst, j.E)) return;
            if (c.List)
            {
                Bytes(c, "New File", j.E.Size, j.Dst);
                Interlocked.Increment(ref c.FilesCopied);
                return;
            }
            for (int attempt = 0; ; attempt++)
            {
                try { DoCopy(c, j); return; }
                catch (Exception e)
                {
                    if (attempt >= c.Retries) throw;
                    Say(c, "Retry " + (attempt + 1) + ": " + j.Dst + " : " + e.Message.Replace('\n', ' '));
                    Thread.Sleep(Math.Max(0, c.WaitSec) * 1000);
                }
            }
        }

        static bool ReadMeta(string meta, Entry e, out long offset)
        {
            offset = 0;
            try
            {
                string[] l = File.ReadAllLines(Lp(meta));
                if (l.Length < 3) return false;
                if (long.Parse(l[0]) != e.Size || long.Parse(l[1]) != e.MTime) return false;
                offset = long.Parse(l[2]);
                return offset >= 0 && offset <= e.Size;
            }
            catch (Exception) { return false; }
        }

        static void WriteMeta(string meta, Entry e, long offset)
        {
            try { File.WriteAllText(Lp(meta), e.Size + "\n" + e.MTime + "\n" + offset + "\n"); } catch (Exception) { }
        }

        static void DoCopy(Ctx c, Job j)
        {
            Entry e = j.E;
            string part = j.Dst + ".hopover-part", meta = part + ".meta";
            string dir = Path.GetDirectoryName(j.Dst);
            Directory.CreateDirectory(Lp(dir));
            long offset = 0;
            if (File.Exists(Lp(part)) && ReadMeta(meta, e, out offset))
            {
                if (new FileInfo(Lp(part)).Length < offset) offset = 0;
            }
            else offset = 0;
            long start = offset, written = offset, lastMeta = offset, reportedHere = 0;
            var inflight = new Queue<MuxClient.Call>();
            MuxClient.Call current = null;
            try
            {
                using (var fs = new FileStream(Lp(part), FileMode.OpenOrCreate, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.SequentialScan))
                {
                    fs.SetLength(offset);
                    fs.Position = offset;
                    long reqPos = offset;
                    var lens = new Queue<long>();
                    while (written < e.Size)
                    {
                        while (inflight.Count < Depth && reqPos < e.Size)
                        {
                            long len = Math.Min(ReqSize, e.Size - reqPos);
                            inflight.Enqueue(c.Mux.Read(j.Root, j.Rel, reqPos, len));
                            lens.Enqueue(len);
                            reqPos += len;
                        }
                        var call = inflight.Dequeue();
                        current = call;
                        long expect = lens.Dequeue(), got = 0;
                        byte[] chunk;
                        while ((chunk = call.Next()) != null)
                        {
                            fs.Write(chunk, 0, chunk.Length);
                            got += chunk.Length;
                            written += chunk.Length;
                            if (e.Size >= 64L << 20 && written - start - reportedHere >= 64L << 20)
                            {
                                long step = written - start - reportedHere;
                                Interlocked.Add(ref c.Copied, step);
                                Bytes(c, "Progress", step, j.Dst);
                                reportedHere += step;
                            }
                        }
                        if (got != expect) throw new IOException("the source file changed while it was read (short read)");
                        if (e.Size >= PartStep && written - lastMeta >= PartStep)
                        {
                            fs.Flush(true);
                            WriteMeta(meta, e, written);
                            lastMeta = written;
                        }
                    }
                    fs.Flush();
                }
                // A big file (database, mailbox, log) can change while it is read and keep its size: the part file
                // would then carry the old time and /XO would skip it forever. Check the source again.
                if (e.Size >= PartStep)
                {
                    var now = c.Mux.Stat(j.Root, j.Rel);
                    if (now == null || now.Size != e.Size || now.MTime != e.MTime)
                    {
                        if (now != null) { now.Name = e.Name; j.E = now; }
                        throw new IOException("the source file changed while it was read");
                    }
                }
            }
            catch
            {
                if (current != null) current.Cancel();
                foreach (var call in inflight) call.Cancel();
                throw;
            }
            // The part file is complete: times and attributes, then it takes the real name.
            string lpPart = Lp(part), lpDst = Lp(j.Dst);
            File.SetAttributes(lpPart, FileAttributes.Normal);
            // An odd source time (before 1601) must not fail a file that is fully copied.
            try { File.SetCreationTimeUtc(lpPart, new DateTime(e.CTime, DateTimeKind.Utc)); } catch (Exception) { }
            try { File.SetLastWriteTimeUtc(lpPart, new DateTime(e.MTime, DateTimeKind.Utc)); } catch (Exception) { }
            if (File.Exists(lpDst))
            {
                File.SetAttributes(lpDst, FileAttributes.Normal);
                File.Delete(lpDst);
            }
            File.Move(lpPart, lpDst);
            var keep = (FileAttributes)e.Attrs & (FileAttributes.ReadOnly | FileAttributes.Hidden | FileAttributes.System | FileAttributes.Archive);
            File.SetAttributes(lpDst, keep == 0 ? FileAttributes.Normal : keep);
            try { File.Delete(Lp(meta)); } catch (Exception) { }
            long rest = written - start - reportedHere;
            Interlocked.Add(ref c.Copied, rest);
            Interlocked.Increment(ref c.FilesCopied);
            Bytes(c, "New File", rest, j.Dst);
        }
    }
}
