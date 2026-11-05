using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Hopover.Link
{
    /// <summary>One directory entry from the old PC.</summary>
    public sealed class Entry
    {
        public int Attrs;
        public long Size, CTime, MTime;
        public string Name;
        /// <summary>For a junction or symbolic link: Reparse.Read()'s value. Empty for anything else.</summary>
        public string Target;
        public bool IsDir { get { return (Attrs & 0x10) != 0; } }
        public bool IsReparse { get { return (Attrs & 0x400) != 0; } }

        public static Entry Parse(string line)
        {
            string[] p = line.Split(new[] { '\t' }, 6);
            if (p.Length < 5) return null;
            return new Entry
            {
                Attrs = int.Parse(p[0]), Size = long.Parse(p[1]), CTime = long.Parse(p[2]), MTime = long.Parse(p[3]), Name = p[4],
                Target = p.Length > 5 ? p[5] : null,
            };
        }

        public static string Format(Entry e)
        {
            return e.Attrs + "\t" + e.Size + "\t" + e.CTime + "\t" + e.MTime + "\t" + e.Name + (string.IsNullOrEmpty(e.Target) ? "" : "\t" + e.Target);
        }
    }

    /// <summary>Plain frames on the loopback proxy connection: u32 len | type | sid u32 | payload (len counts type, sid and payload).</summary>
    public static class LocalFrames
    {
        public static void Write(Stream s, byte type, uint sid, byte[] buf, int off, int len)
        {
            byte[] f = new byte[4 + 5 + len];
            int n = 5 + len;
            f[0] = (byte)(n >> 24); f[1] = (byte)(n >> 16); f[2] = (byte)(n >> 8); f[3] = (byte)n;
            f[4] = type;
            f[5] = (byte)(sid >> 24); f[6] = (byte)(sid >> 16); f[7] = (byte)(sid >> 8); f[8] = (byte)sid;
            if (len > 0) Buffer.BlockCopy(buf, off, f, 9, len);
            s.Write(f, 0, f.Length);
        }

        public static bool Read(Stream s, out byte type, out uint sid, out byte[] payload)
        {
            type = 0; sid = 0; payload = null;
            byte[] h = new byte[9];
            if (!Fill(s, h, 0, 4)) return false;
            long n = ((long)h[0] << 24) | ((long)h[1] << 16) | ((long)h[2] << 8) | h[3];
            if (n < 5 || n > Channel.MaxFrame + 16) throw new LinkException("bad local frame");
            if (!Fill(s, h, 4, 5)) return false;
            type = h[4];
            sid = ((uint)h[5] << 24) | ((uint)h[6] << 16) | ((uint)h[7] << 8) | h[8];
            payload = new byte[n - 5];
            return payload.Length == 0 || Fill(s, payload, 0, payload.Length);
        }

        static bool Fill(Stream s, byte[] b, int off, int len)
        {
            while (len > 0)
            {
                int r;
                try { r = s.Read(b, off, len); } catch (IOException) { return false; } catch (ObjectDisposedException) { return false; }
                if (r <= 0) return false;
                off += r; len -= r;
            }
            return true;
        }
    }

    /// <summary>The client side of the loopback proxy: request/response calls multiplexed on one connection.</summary>
    public sealed class MuxClient : IDisposable
    {
        public sealed class Call
        {
            internal readonly uint Sid;
            internal readonly BlockingCollection<KeyValuePair<byte, byte[]>> Q = new BlockingCollection<KeyValuePair<byte, byte[]>>();
            const int Timeout = 300000;
            readonly MuxClient owner;
            bool done;
            internal Call(MuxClient o, uint sid) { owner = o; Sid = sid; }

            /// <summary>The next data chunk, or null at the end. Throws LinkException on an error from the other PC.</summary>
            public byte[] Next()
            {
                if (done) return null;
                // A live connection to an old PC that stopped answering must fail the chunk, not hold it for ever.
                KeyValuePair<byte, byte[]> item;
                if (!Q.TryTake(out item, Timeout))
                {
                    Cancel();
                    throw new LinkException("the old PC did not answer for " + Timeout / 60000 + " minutes");
                }
                if (item.Key == Msg.Data) return item.Value;
                done = true;
                owner.Forget(Sid);
                if (item.Key == Msg.Err) throw new LinkException(Encoding.UTF8.GetString(item.Value));
                return null;
            }

            public void Cancel()
            {
                if (done) return;
                done = true;
                owner.Forget(Sid);
                try { owner.Send(Msg.Cancel, Sid, null); } catch { }
            }

            public byte[] ReadAll()
            {
                var ms = new MemoryStream();
                byte[] c;
                while ((c = Next()) != null) ms.Write(c, 0, c.Length);
                return ms.ToArray();
            }
        }

        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();
        readonly Socket sock;
        readonly Stream stream;
        readonly object wlock = new object();
        readonly ConcurrentDictionary<uint, Call> calls = new ConcurrentDictionary<uint, Call>();
        int nextSid;
        volatile string dead;

        MuxClient(Socket s)
        {
            sock = s;
            stream = new NetworkStream(s, false);
            var t = new Thread(ReadLoop) { IsBackground = true };
            t.Start();
        }

        /// <summary>Connects with HOPOVER_LINK = "port:token" (set by the GUI for the engine and its children).</summary>
        public static MuxClient ConnectFromEnv()
        {
            string v = Environment.GetEnvironmentVariable("HOPOVER_LINK");
            if (string.IsNullOrEmpty(v) || v.IndexOf(':') < 0) throw new LinkException("HOPOVER_LINK is not set: connect to the old PC in Hopover first");
            int i = v.IndexOf(':');
            return Connect(int.Parse(v.Substring(0, i)), v.Substring(i + 1));
        }

        public static MuxClient Connect(int port, string token)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            s.NoDelay = true;
            s.Connect(new IPEndPoint(IPAddress.Loopback, port));
            byte[] hello = Encoding.ASCII.GetBytes("HOPOVERPROXY1 " + token + "\n");
            s.Send(hello);
            byte[] ok = new byte[3];
            int got = 0;
            while (got < 3) { int n = s.Receive(ok, got, 3 - got, SocketFlags.None); if (n <= 0) break; got += n; }
            if (got != 3 || ok[0] != 'O' || ok[1] != 'K') { s.Close(); throw new LinkException("the proxy refused the token"); }
            return new MuxClient(s);
        }

        void ReadLoop()
        {
            try
            {
                while (true)
                {
                    byte type; uint sid; byte[] payload;
                    if (!LocalFrames.Read(stream, out type, out sid, out payload)) break;
                    Call c;
                    if (calls.TryGetValue(sid, out c)) c.Q.Add(new KeyValuePair<byte, byte[]>(type, payload));
                }
            }
            catch (Exception) { }
            dead = "the link to the old PC is gone";
            foreach (var c in calls.Values) c.Q.Add(new KeyValuePair<byte, byte[]>(Msg.Err, Encoding.UTF8.GetBytes(dead)));
        }

        internal void Forget(uint sid) { Call c; calls.TryRemove(sid, out c); }

        internal void Send(byte type, uint sid, byte[] payload)
        {
            lock (wlock) LocalFrames.Write(stream, type, sid, payload, 0, payload == null ? 0 : payload.Length);
        }

        public Call Request(object request)
        {
            if (dead != null) throw new LinkException(dead);
            uint sid = (uint)Interlocked.Increment(ref nextSid);
            var call = new Call(this, sid);
            calls[sid] = call;
            // ReadLoop may have failed every open call between the check above and this line.
            if (dead != null) { Forget(sid); throw new LinkException(dead); }
            try { Send(Msg.Req, sid, Encoding.UTF8.GetBytes(Ser.Serialize(request))); }
            catch (Exception e) { Forget(sid); throw new LinkException("send failed", e); }
            return call;
        }

        public Call Read(string root, string path, long off, long len)
        {
            return Request(new Dictionary<string, object> { { "op", "read" }, { "root", root }, { "path", path }, { "off", off }, { "len", len } });
        }

        public List<Entry> List(string root, string path)
        {
            var call = Request(new Dictionary<string, object> { { "op", "list" }, { "root", root }, { "path", path } });
            var res = new List<Entry>();
            byte[] chunk;
            while ((chunk = call.Next()) != null)
                foreach (string line in Encoding.UTF8.GetString(chunk).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var e = Entry.Parse(line);
                    if (e != null) res.Add(e);
                }
            return res;
        }

        public Entry Stat(string root, string path)
        {
            var call = Request(new Dictionary<string, object> { { "op", "stat" }, { "root", root }, { "path", path } });
            return Entry.Parse(Encoding.UTF8.GetString(call.ReadAll()).Trim('\n'));
        }

        public string Info()
        {
            return Encoding.UTF8.GetString(Request(new Dictionary<string, object> { { "op", "info" } }).ReadAll());
        }

        /// <summary>
        /// Asks the old PC to prepare its changes (hopover.ps1 export -Refresh). Its output lines come back one by
        /// one; the last line is "##REFRESHED exitcode". The old PC sends "." while it works without a word.
        /// </summary>
        public int Refresh(Action<string> line)
        {
            var call = Request(new Dictionary<string, object> { { "op", "refresh" } });
            int code = 16;
            byte[] chunk;
            while ((chunk = call.Next()) != null)
                foreach (string l in Encoding.UTF8.GetString(chunk).Split(new[] { '\n' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    if (l.StartsWith("##REFRESHED ")) int.TryParse(l.Substring(12), out code);
                    else if (l != ".") line(l);
                }
            return code;
        }

        public byte[] ReadAllBytes(string root, string path)
        {
            var e = Stat(root, path);
            if (e == null || e.IsDir) throw new LinkException("not a file: " + path);
            return Read(root, path, 0, e.Size).ReadAll();
        }

        public void Dispose() { try { sock.Close(); } catch { } }
    }
}
