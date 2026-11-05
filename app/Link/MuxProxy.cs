using System;
using System.Collections.Concurrent;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace Hopover.Link
{
    /// <summary>
    /// Holds the authenticated channel on the new PC and offers it on 127.0.0.1 (random port, random token)
    /// to the per-chunk "Hopover.exe --link copy" processes. Each local connection multiplexes calls;
    /// the proxy renumbers the stream ids onto the one channel.
    /// </summary>
    public sealed class MuxProxy : IDisposable
    {
        sealed class Local
        {
            public Socket Sock;
            public Stream Stream;
            public readonly object Lock = new object();
            public readonly ConcurrentDictionary<uint, uint> Map = new ConcurrentDictionary<uint, uint>();
        }

        sealed class Route { public Local Conn; public uint Lsid; }

        readonly Channel ch;
        readonly ConcurrentDictionary<uint, Route> routes = new ConcurrentDictionary<uint, Route>();
        TcpListener listener;
        int nextGlobal;
        volatile bool stopped;

        public int Port { get; private set; }
        public string Token { get; private set; }
        /// <summary>Raised once when the link to the old PC ends.</summary>
        public event Action Closed;

        public MuxProxy(Channel ch) { this.ch = ch; }

        public void Start()
        {
            byte[] t = new byte[12];
            using (var rng = new RNGCryptoServiceProvider()) rng.GetBytes(t);
            Token = P256.ToHex(t);
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            new Thread(AcceptLoop) { IsBackground = true }.Start();
            new Thread(ChannelLoop) { IsBackground = true }.Start();
        }

        void AcceptLoop()
        {
            while (!stopped)
            {
                Socket s;
                try { s = listener.AcceptSocket(); } catch { return; }
                var l = new Local { Sock = s, Stream = new NetworkStream(s, false) };
                s.NoDelay = true;
                // One frozen copy process must not hold the only loop that feeds all the others.
                s.SendTimeout = 60000;
                new Thread(() => LocalLoop(l)) { IsBackground = true }.Start();
            }
        }

        void LocalLoop(Local l)
        {
            try
            {
                // First line: HOPOVERPROXY1 <token>. Only a process that got the token from the environment gets in.
                var sb = new StringBuilder();
                l.Sock.ReceiveTimeout = 5000;
                var one = new byte[1];
                while (sb.Length < 80)
                {
                    if (l.Stream.Read(one, 0, 1) != 1) return;
                    if (one[0] == '\n') break;
                    sb.Append((char)one[0]);
                }
                if (sb.ToString() != "HOPOVERPROXY1 " + Token) { l.Sock.Close(); return; }
                l.Sock.ReceiveTimeout = 0;
                l.Stream.Write(new byte[] { (byte)'O', (byte)'K', (byte)'\n' }, 0, 3);
                while (true)
                {
                    byte type; uint lsid; byte[] payload;
                    if (!LocalFrames.Read(l.Stream, out type, out lsid, out payload)) break;
                    if (type == Msg.Req)
                    {
                        uint g = (uint)Interlocked.Increment(ref nextGlobal);
                        l.Map[lsid] = g;
                        routes[g] = new Route { Conn = l, Lsid = lsid };
                        ch.Send(Msg.Req, Wrap(g, payload));
                    }
                    else if (type == Msg.Cancel)
                    {
                        uint g;
                        if (l.Map.TryRemove(lsid, out g))
                        {
                            Route r;
                            routes.TryRemove(g, out r);
                            ch.Send(Msg.Cancel, Wrap(g, null));
                        }
                    }
                }
            }
            catch (Exception) { }
            // The copy process is gone: cancel what it still had open on the old PC.
            foreach (var kv in l.Map)
            {
                Route r;
                routes.TryRemove(kv.Value, out r);
                try { ch.Send(Msg.Cancel, Wrap(kv.Value, null)); } catch { }
            }
            try { l.Sock.Close(); } catch { }
        }

        static byte[] Wrap(uint sid, byte[] data)
        {
            int n = data == null ? 0 : data.Length;
            byte[] r = new byte[4 + n];
            r[0] = (byte)(sid >> 24); r[1] = (byte)(sid >> 16); r[2] = (byte)(sid >> 8); r[3] = (byte)sid;
            if (n > 0) Buffer.BlockCopy(data, 0, r, 4, n);
            return r;
        }

        void ChannelLoop()
        {
            try
            {
                while (true)
                {
                    byte[] p;
                    byte type = ch.Receive(out p);
                    if (p.Length < 4) continue;
                    uint g = ((uint)p[0] << 24) | ((uint)p[1] << 16) | ((uint)p[2] << 8) | p[3];
                    Route r;
                    if (!routes.TryGetValue(g, out r)) continue;
                    if (type == Msg.End || type == Msg.Err)
                    {
                        routes.TryRemove(g, out r);
                        uint ignore;
                        r.Conn.Map.TryRemove(r.Lsid, out ignore);
                    }
                    if (type != Msg.Data && type != Msg.End && type != Msg.Err) continue;
                    try { lock (r.Conn.Lock) LocalFrames.Write(r.Conn.Stream, type, r.Lsid, p, 4, p.Length - 4); }
                    catch (Exception) { try { r.Conn.Sock.Close(); } catch { } }
                }
            }
            catch (Exception) { }
            Stop();
        }

        void Stop()
        {
            if (stopped) return;
            stopped = true;
            try { listener.Stop(); } catch { }
            foreach (var r in routes.Values) try { r.Conn.Sock.Close(); } catch { }
            ch.Close();
            var h = Closed;
            if (h != null) h();
        }

        public void Dispose() { Stop(); }
    }
}
