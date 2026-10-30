using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Hopover.Link
{
    public sealed class ServerOptions
    {
        public string Store = @"C:\hopover-store";
        public int TcpPort = 47800, UdpPort = 47801;
        public string Rendezvous;          // host:port, null = no internet path
        public string FixedCode;           // tests only
        public string Name = Environment.MachineName;
        /// <summary>Extra virtual roots for tests: name -> directory.</summary>
        public Dictionary<string, string> Roots = new Dictionary<string, string>();
        public bool Background = true;
        public Action<string> Event = delegate { };
    }

    public static class Net
    {
        public static void KeepAlive(Socket s)
        {
            try
            {
                byte[] v = new byte[12];
                BitConverter.GetBytes(1).CopyTo(v, 0);
                BitConverter.GetBytes(15000).CopyTo(v, 4);
                BitConverter.GetBytes(5000).CopyTo(v, 8);
                s.IOControl(IOControlCode.KeepAliveValues, v, null);
            }
            catch { }
        }

        public static List<IPAddress> LocalIPv4()
        {
            var res = new List<IPAddress>();
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up) continue;
                    if (ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                        if (ua.Address.AddressFamily == AddressFamily.InterNetwork && !ua.Address.ToString().StartsWith("169.254.")) res.Add(ua.Address);
                }
            }
            catch { }
            return res;
        }
    }

    /// <summary>
    /// The old PC. Shows a pairing code, accepts one authenticated peer at a time and serves read-only virtual roots
    /// ("store", "drive-c", ...). A wrong confirmation burns the code and makes a new one. No file byte leaves
    /// through anyone but the peer that knew the code.
    /// </summary>
    public sealed class LinkServer
    {
        [DllImport("kernel32.dll")] static extern IntPtr GetCurrentThread();
        [DllImport("kernel32.dll")] static extern bool SetThreadPriority(IntPtr h, int p);

        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();
        readonly ServerOptions o;
        readonly object codeLock = new object();
        string code, room;
        byte[] w;
        int codeVersion;
        Channel active;
        volatile bool stopped;
        TcpListener tcp;
        Socket udp;
        Rendezvous.Holder meet = new Rendezvous.Holder();
        readonly BlockingCollection<Action> work = new BlockingCollection<Action>();

        public LinkServer(ServerOptions o) { this.o = o; }

        public string Code { get { lock (codeLock) return code; } }
        public int TcpPort { get; private set; }

        public void Start()
        {
            NewCode(o.FixedCode);
            tcp = new TcpListener(IPAddress.Any, o.TcpPort);
            tcp.Server.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, false);
            tcp.Start();
            TcpPort = ((IPEndPoint)tcp.LocalEndpoint).Port;
            for (int i = 0; i < 8; i++) new Thread(Worker) { IsBackground = true }.Start();
            new Thread(AcceptLoop) { IsBackground = true }.Start();
            new Thread(UdpLoop) { IsBackground = true }.Start();
            if (!string.IsNullOrEmpty(o.Rendezvous)) new Thread(RendezvousLoop) { IsBackground = true }.Start();
        }

        public void Stop()
        {
            stopped = true;
            try { tcp.Stop(); } catch { }
            try { udp.Close(); } catch { }
            try { meet.Close(); } catch { }
            Channel a = active;
            if (a != null) a.Close();
        }

        // ---------- the code ----------
        static int Rand(int n)
        {
            using (var rng = new RNGCryptoServiceProvider())
            {
                byte[] b = new byte[4];
                uint v;
                do { rng.GetBytes(b); v = BitConverter.ToUInt32(b, 0); } while (v >= uint.MaxValue - (uint.MaxValue % (uint)n));
                return (int)(v % (uint)n);
            }
        }

        public static string MakeCode()
        {
            return Rand(10) + "-" + Words.List[Rand(Words.List.Length)] + "-" + Words.List[Rand(Words.List.Length)] + "-" + Words.List[Rand(Words.List.Length)];
        }

        public static string Normalize(string c)
        {
            var sb = new StringBuilder();
            foreach (string part in (c ?? "").ToLowerInvariant().Split(new[] { ' ', '-', '_', '.', ',', '\t' }, StringSplitOptions.RemoveEmptyEntries))
            {
                if (sb.Length > 0) sb.Append('-');
                sb.Append(part);
            }
            return sb.ToString();
        }

        /// <summary>The meeting-point room: SHA-256("hopover-room-v1:" + digit + "-" + word1). The server never sees the full code.</summary>
        public static string RoomOf(string c)
        {
            string[] p = Normalize(c).Split('-');
            string text = "hopover-room-v1:" + p[0] + "-" + (p.Length > 1 ? p[1] : "");
            using (var sha = SHA256.Create()) return P256.ToHex(sha.ComputeHash(Encoding.UTF8.GetBytes(text)));
        }

        void NewCode(string fixedCode)
        {
            string c = fixedCode != null ? Normalize(fixedCode) : MakeCode();
            byte[] ww = Spake2.DeriveW(c);
            lock (codeLock) { code = c; room = RoomOf(c); w = ww; codeVersion++; }
            o.Event("##LINK code " + c);
            try { meet.Close(); } catch { }   // the meeting point must hear the new room
        }

        void Burn()
        {
            o.Event("##LINK state burned");
            NewCode(null);
        }

        // ---------- listeners ----------
        void AcceptLoop()
        {
            while (!stopped)
            {
                Socket s;
                try { s = tcp.AcceptSocket(); } catch { return; }
                var sock = s;
                new Thread(() => OnSocket(sock, "lan")) { IsBackground = true }.Start();
            }
        }

        void UdpLoop()
        {
            try
            {
                udp = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                udp.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                udp.EnableBroadcast = true;
                udp.Bind(new IPEndPoint(IPAddress.Any, o.UdpPort));
                byte[] buf = new byte[512];
                while (!stopped)
                {
                    EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                    int n = udp.ReceiveFrom(buf, ref from);
                    string text = Encoding.ASCII.GetString(buf, 0, n);
                    string r;
                    lock (codeLock) r = room;
                    if (text == "HOPOVER-DISCOVER1 " + r)
                    {
                        byte[] reply = Encoding.UTF8.GetBytes("HOPOVER-HERE1 " + TcpPort + " " + o.Name);
                        udp.SendTo(reply, from);
                    }
                }
            }
            catch (Exception) { if (!stopped) o.Event("##LINK warn lan discovery is off (UDP " + o.UdpPort + " in use?)"); }
        }

        void RendezvousLoop()
        {
            int backoff = 2000;
            while (!stopped)
            {
                string r; int ver;
                lock (codeLock) { r = room; ver = codeVersion; }
                string err;
                var res = Rendezvous.Meet(o.Rendezvous, "host", r, 11 * 60, meet, out err);
                if (stopped) return;
                if (res == null)
                {
                    // Offline, busy or code changed: wait a little and register again.
                    int waited = 0;
                    while (waited < backoff && !stopped) { Thread.Sleep(250); waited += 250; }
                    backoff = Math.Min(backoff * 2, 30000);
                    continue;
                }
                backoff = 2000;
                o.Event("##LINK warn meeting point sent a peer: trying to connect");
                Rendezvous.Punch(res, 12000, s => { var sock = s; new Thread(() => OnSocket(sock, "internet")) { IsBackground = true }.Start(); }, () => stopped);
            }
        }

        // ---------- one connection ----------
        void OnSocket(Socket s, string path)
        {
            byte[] ww;
            lock (codeLock) ww = w;
            Hs result;
            Channel ch = null;
            try
            {
                ch = Channel.Open(s, false, ww, 10000, out result);
                if (result == Hs.WrongCode) { Burn(); return; }
                if (result != Hs.Ok) return;
                // The joining PC picks ONE of the connections (several can come up while hole punching).
                s.ReceiveTimeout = 15000;
                byte[] use;
                if (ch.Receive(out use) != Msg.Use) { ch.Close(); return; }
                s.ReceiveTimeout = 0;
                Net.KeepAlive(s);
                ch.PeerName = Encoding.UTF8.GetString(use);
                ch.Path = path;
                Channel old;
                lock (codeLock) { old = active; active = ch; }
                if (old != null) old.Close();
                o.Event("##LINK path " + path);
                o.Event("##LINK peer " + ch.PeerName);
                o.Event("##LINK state connected");
                Serve(ch);
            }
            catch (Exception) { }
            finally
            {
                if (ch != null)
                {
                    bool was;
                    lock (codeLock) { was = active == ch; if (was) active = null; }
                    ch.Close();
                    if (was) o.Event("##LINK state dropped");
                }
                else { try { s.Close(); } catch { } }
            }
        }

        // ---------- serving ----------
        readonly ConcurrentDictionary<string, bool> cancelled = new ConcurrentDictionary<string, bool>();

        void Worker()
        {
            if (o.Background) SetThreadPriority(GetCurrentThread(), 0x00010000);   // THREAD_MODE_BACKGROUND_BEGIN: low CPU and I/O priority
            foreach (var a in work.GetConsumingEnumerable())
            {
                try { a(); } catch (Exception) { }
            }
        }

        void Serve(Channel ch)
        {
            string tag = Guid.NewGuid().ToString("N");
            while (true)
            {
                byte[] p;
                byte type = ch.Receive(out p);
                if (p.Length < 4) continue;
                uint sid = ((uint)p[0] << 24) | ((uint)p[1] << 16) | ((uint)p[2] << 8) | p[3];
                if (type == Msg.Cancel) { cancelled[tag + sid] = true; continue; }
                if (type != Msg.Req) continue;
                string json = Encoding.UTF8.GetString(p, 4, p.Length - 4);
                work.Add(() => Handle(ch, tag, sid, json));
            }
        }

        static byte[] Wrap(uint sid, byte[] data, int off, int len)
        {
            byte[] r = new byte[4 + len];
            r[0] = (byte)(sid >> 24); r[1] = (byte)(sid >> 16); r[2] = (byte)(sid >> 8); r[3] = (byte)sid;
            if (len > 0) Buffer.BlockCopy(data, off, r, 4, len);
            return r;
        }

        void Handle(Channel ch, string tag, uint sid, string json)
        {
            try
            {
                var req = (Dictionary<string, object>)Ser.DeserializeObject(json);
                string op = Convert.ToString(req["op"]);
                switch (op)
                {
                    case "info":
                        byte[] info = Encoding.UTF8.GetBytes(Ser.Serialize(new Dictionary<string, object> {
                            { "name", o.Name }, { "user", Environment.UserName }, { "v", 1 } }));
                        ch.Send(Msg.Data, Wrap(sid, info, 0, info.Length));
                        break;
                    case "list": List(ch, sid, Resolve(Str(req, "root"), Str(req, "path"))); break;
                    case "stat":
                        {
                            var fi = new FileInfo(Lp(Resolve(Str(req, "root"), Str(req, "path"))));
                            FileSystemInfo e = fi.Exists ? (FileSystemInfo)fi : new DirectoryInfo(fi.FullName);
                            if (!e.Exists) throw new FileNotFoundException("not found");
                            byte[] b = Encoding.UTF8.GetBytes(Entry.Format(ToEntry(e)) + "\n");
                            ch.Send(Msg.Data, Wrap(sid, b, 0, b.Length));
                            break;
                        }
                    case "read": Read(ch, tag, sid, Resolve(Str(req, "root"), Str(req, "path")), Convert.ToInt64(req["off"]), Convert.ToInt64(req["len"])); break;
                    default: throw new InvalidOperationException("unknown op");
                }
                ch.Send(Msg.End, Wrap(sid, null, 0, 0));
            }
            catch (LinkException) { }
            catch (Exception e)
            {
                try { byte[] m = Encoding.UTF8.GetBytes(e.Message); ch.Send(Msg.Err, Wrap(sid, m, 0, m.Length)); } catch { }
            }
            finally { bool x; cancelled.TryRemove(tag + sid, out x); }
        }

        static string Str(Dictionary<string, object> d, string k) { object v; return d.TryGetValue(k, out v) && v != null ? Convert.ToString(v) : ""; }

        static string Lp(string path)
        {
            return LinkCopy.Lp(path);
        }

        /// <summary>Virtual root + relative path to a real path. Rejects "..", rooted names, drive letters, and any junction or symlink on the way.</summary>
        public string Resolve(string root, string rel)
        {
            string baseDir;
            if (root == "store") baseDir = o.Store;
            else if (o.Roots.TryGetValue(root, out baseDir)) { }
            else if (root != null && root.Length == 7 && root.StartsWith("drive-") && char.IsLetter(root[6]))
            {
                char letter = char.ToUpperInvariant(root[6]);
                var di = new DriveInfo(letter.ToString());
                if (di.DriveType != DriveType.Fixed || !di.IsReady) throw new UnauthorizedAccessException("no such drive");
                baseDir = letter + @":\";
            }
            else throw new UnauthorizedAccessException("no such root");
            if (string.IsNullOrEmpty(rel)) return baseDir;
            if (rel.IndexOfAny(new[] { ':', '*', '?', '"', '<', '>', '|', '\0' }) >= 0 || rel[0] == '\\' || rel[0] == '/')
                throw new UnauthorizedAccessException("bad path");
            string cur = baseDir;
            foreach (string seg in rel.Split(new[] { '\\', '/' }))
            {
                if (seg.Length == 0 || seg == "." || seg == "..") throw new UnauthorizedAccessException("bad path");
                cur = Path.Combine(cur, seg);
                FileAttributes a;
                try { a = File.GetAttributes(Lp(cur)); } catch (Exception) { continue; }   // missing: the caller's open fails with "not found"
                if ((a & FileAttributes.ReparsePoint) != 0) throw new UnauthorizedAccessException("reparse point");
            }
            return cur;
        }

        static Entry ToEntry(FileSystemInfo e)
        {
            bool dir = (e.Attributes & FileAttributes.Directory) != 0;
            return new Entry
            {
                Attrs = (int)e.Attributes,
                Size = dir ? 0 : ((FileInfo)e).Length,
                CTime = e.CreationTimeUtc.Ticks,
                MTime = e.LastWriteTimeUtc.Ticks,
                Name = e.Name,
            };
        }

        void List(Channel ch, uint sid, string dir)
        {
            var di = new DirectoryInfo(Lp(dir));
            var sb = new StringBuilder();
            int n = 0;
            foreach (var e in di.EnumerateFileSystemInfos())
            {
                Entry en;
                try { en = ToEntry(e); } catch (Exception) { continue; }
                sb.Append(Entry.Format(en)).Append('\n');
                if (++n % 500 == 0) { Flush(ch, sid, sb); }
            }
            Flush(ch, sid, sb);
        }

        static void Flush(Channel ch, uint sid, StringBuilder sb)
        {
            if (sb.Length == 0) return;
            byte[] b = Encoding.UTF8.GetBytes(sb.ToString());
            sb.Length = 0;
            ch.Send(Msg.Data, Wrap(sid, b, 0, b.Length));
        }

        void Read(Channel ch, string tag, uint sid, string file, long off, long len)
        {
            using (var fs = new FileStream(Lp(file), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, FileOptions.SequentialScan))
            {
                if (off > fs.Length) off = fs.Length;
                fs.Seek(off, SeekOrigin.Begin);
                byte[] buf = new byte[4 + (256 << 10)];
                buf[0] = (byte)(sid >> 24); buf[1] = (byte)(sid >> 16); buf[2] = (byte)(sid >> 8); buf[3] = (byte)sid;
                while (len > 0)
                {
                    if (cancelled.ContainsKey(tag + sid)) return;
                    int want = (int)Math.Min(len, 256 << 10);
                    int got = fs.Read(buf, 4, want);
                    if (got <= 0) break;
                    ch.Send(Msg.Data, buf, 0, 4 + got);
                    len -= got;
                }
            }
        }
    }
}
