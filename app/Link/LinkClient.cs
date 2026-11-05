using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Hopover.Link
{
    /// <summary>
    /// The meeting point and TCP hole punching. The meeting point only swaps addresses, it never carries a byte of a file.
    /// Protocol: one JSON line each way (see CLAUDE.md, "Link"). Both PCs then connect to each other from the same
    /// local port (simultaneous open) and race; the first connection that passes the SPAKE2 handshake wins.
    /// </summary>
    public static class Rendezvous
    {
        public sealed class Holder
        {
            Socket s;
            readonly object lk = new object();
            public void Set(Socket x) { lock (lk) s = x; }
            public void Close() { Socket x; lock (lk) { x = s; s = null; } if (x != null) try { x.Close(); } catch { } }
        }

        public sealed class Result
        {
            public string You, Peer;
            public string[] PeerLocal = new string[0];
            public int LocalPort;
        }

        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer();

        public static IPEndPoint ParseEp(string hostPort)
        {
            string hp = hostPort;
            if (hp.StartsWith("http://")) { hp = hp.Substring(7).TrimEnd('/'); if (hp.IndexOf(':') < 0) hp += ":80"; }
            int i = hp.LastIndexOf(':');
            if (i < 0) throw new FormatException("host:port expected");
            string host = hp.Substring(0, i);
            int port = int.Parse(hp.Substring(i + 1));
            IPAddress ip;
            if (!IPAddress.TryParse(host, out ip))
            {
                var all = Dns.GetHostAddresses(host);
                ip = all.FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork);
                if (ip == null) throw new SocketException((int)SocketError.HostNotFound);
            }
            return new IPEndPoint(ip, port);
        }

        /// <summary>Registers (op "host" or "join") and waits for the peer's addresses. Returns null when the meeting point cannot be reached or says no.</summary>
        public static Result Meet(string hostPort, string op, string room, int waitSeconds, Holder holder, out string error)
        {
            error = null;
            Socket s = null;
            try
            {
                s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                s.Bind(new IPEndPoint(IPAddress.Any, 0));
                int port = ((IPEndPoint)s.LocalEndPoint).Port;
                holder.Set(s);
                var ar = s.BeginConnect(ParseEp(hostPort), null, null);
                if (!ar.AsyncWaitHandle.WaitOne(10000)) { error = "the meeting point does not answer"; return null; }
                s.EndConnect(ar);
                var locals = Net.LocalIPv4().Select(a => a + ":" + port).ToArray();
                string body = Ser.Serialize(new Dictionary<string, object> { { "v", 1 }, { "op", op }, { "room", room }, { "local", locals } });
                string hostHeader = hostPort.StartsWith("http://") ? hostPort.Substring(7).TrimEnd('/') : hostPort;
                byte[] bb = Encoding.UTF8.GetBytes(body);
                string head = "POST /v1/meet HTTP/1.1\r\nHost: " + hostHeader + "\r\nContent-Type: application/json\r\nContent-Length: " + bb.Length + "\r\nConnection: close\r\n\r\n";
                s.Send(Encoding.ASCII.GetBytes(head));
                s.Send(bb);
                s.ReceiveTimeout = waitSeconds * 1000;
                var raw = new List<byte>();
                var buf = new byte[4096];
                while (raw.Count < 16384)
                {
                    int n = s.Receive(buf);
                    if (n <= 0) break;
                    for (int k = 0; k < n; k++) raw.Add(buf[k]);
                }
                string resp = Encoding.UTF8.GetString(raw.ToArray());
                int split = resp.IndexOf("\r\n\r\n");
                if (split < 0) { error = "the meeting point closed the connection"; return null; }
                var all = Encoding.UTF8.GetBytes(resp.Substring(split + 4).Trim()).ToList();
                if (all.Count == 0) { error = "the meeting point sent no answer"; return null; }
                var d = (Dictionary<string, object>)Ser.DeserializeObject(Encoding.UTF8.GetString(all.ToArray()));
                if (d.ContainsKey("error")) { error = Convert.ToString(d["error"]); return null; }
                var r = new Result { You = Convert.ToString(d["you"]), Peer = Convert.ToString(d["peer"]), LocalPort = port };
                if (d.ContainsKey("peer_local") && d["peer_local"] is System.Collections.IEnumerable)
                    r.PeerLocal = ((System.Collections.IEnumerable)d["peer_local"]).Cast<object>().Select(x => Convert.ToString(x)).ToArray();
                return r;
            }
            catch (Exception e) { error = e.Message; return null; }
            finally { holder.Set(null); if (s != null) try { s.Close(); } catch { } }
        }

        /// <summary>Tells the meeting point once how the move connected ("lan", "internet" or "none"). Fire and forget: 5 s timeout, never throws.</summary>
        public static void ReportResult(string hostPort, string room, string path)
        {
            var th = new Thread(() =>
            {
                Socket s = null;
                try
                {
                    s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                    s.SendTimeout = 5000; s.ReceiveTimeout = 5000;
                    var ar = s.BeginConnect(ParseEp(hostPort), null, null);
                    if (!ar.AsyncWaitHandle.WaitOne(5000)) return;
                    s.EndConnect(ar);
                    byte[] bb = Encoding.UTF8.GetBytes(Ser.Serialize(new Dictionary<string, object> { { "v", 1 }, { "room", room }, { "path", path } }));
                    string hostHeader = hostPort.StartsWith("http://") ? hostPort.Substring(7).TrimEnd('/') : hostPort;
                    s.Send(Encoding.ASCII.GetBytes("POST /v1/result HTTP/1.1\r\nHost: " + hostHeader + "\r\nContent-Type: application/json\r\nContent-Length: " + bb.Length + "\r\nConnection: close\r\n\r\n"));
                    s.Send(bb);
                    s.Receive(new byte[512]);
                }
                catch (Exception) { }
                finally { if (s != null) try { s.Close(); } catch { } }
            }) { IsBackground = true };
            th.Start();
        }

        /// <summary>
        /// Simultaneous open for about <paramref name="ms"/> ms: listens on the punch port and connects to every candidate
        /// from the same port. Every connected socket goes to <paramref name="onSocket"/>.
        /// </summary>
        public static void Punch(Result r, int ms, Action<Socket> onSocket, Func<bool> stop)
        {
            var cands = r.PeerLocal.Concat(new[] { r.Peer }).Where(x => !string.IsNullOrEmpty(x)).Distinct().ToList();
            DateTime until = DateTime.UtcNow.AddMilliseconds(ms);
            var lis = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            lis.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
            lis.Bind(new IPEndPoint(IPAddress.Any, r.LocalPort));
            lis.Listen(8);
            var threads = new List<Thread>();
            var acc = new Thread(() =>
            {
                try
                {
                    while (DateTime.UtcNow < until && !stop())
                        if (lis.Poll(200000, SelectMode.SelectRead)) onSocket(lis.Accept());
                }
                catch (Exception) { }
            }) { IsBackground = true };
            threads.Add(acc);
            foreach (string cand in cands)
            {
                string c = cand;
                threads.Add(new Thread(() =>
                {
                    IPEndPoint ep;
                    try { ep = ParseEp(c); } catch { return; }
                    while (DateTime.UtcNow < until && !stop())
                    {
                        var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
                        try
                        {
                            s.SetSocketOption(SocketOptionLevel.Socket, SocketOptionName.ReuseAddress, true);
                            s.Bind(new IPEndPoint(IPAddress.Any, r.LocalPort));
                            var ar = s.BeginConnect(ep, null, null);
                            if (ar.AsyncWaitHandle.WaitOne(1500) && s.Connected) { s.EndConnect(ar); onSocket(s); return; }
                        }
                        catch (Exception) { }
                        try { s.Close(); } catch { }
                        Thread.Sleep(150);
                    }
                }) { IsBackground = true });
            }
            foreach (var t in threads) t.Start();
            foreach (var t in threads) t.Join();
            try { lis.Close(); } catch { }
        }
    }

    public sealed class ConnectResult
    {
        public Channel Channel;
        public string Path;        // "lan" or "internet"
        public string Error;       // "wrong-code", "no-route", "bad-code", "cancelled"
        public string Detail;
    }

    /// <summary>The new PC: finds the old PC (LAN broadcast, or meeting point and hole punch) and pairs with the typed code.</summary>
    public static class LinkClient
    {
        public static bool LooksLikeCode(string c)
        {
            string[] p = LinkServer.Normalize(c).Split('-');
            return p.Length == 4 && p[0].Length == 1 && char.IsDigit(p[0][0]) && p.Skip(1).All(w => Array.IndexOf(Words.List, w) >= 0);
        }

        static bool IsPrivate(IPAddress a)
        {
            byte[] b = a.GetAddressBytes();
            return IPAddress.IsLoopback(a) || b[0] == 10 || (b[0] == 172 && b[1] >= 16 && b[1] < 32) || (b[0] == 192 && b[1] == 168) || (b[0] == 169 && b[1] == 254);
        }

        static Channel Finish(Socket s, byte[] w, string path, out Hs hs)
        {
            var ch = Channel.Open(s, true, w, 10000, out hs);
            if (ch == null) { try { s.Close(); } catch { } return null; }
            return ch;
        }

        static void Announce(Channel ch, string path)
        {
            ch.Send(Msg.Use, Encoding.UTF8.GetBytes(Environment.MachineName));
            Net.KeepAlive(Sock(ch));
            ch.Path = path;
        }

        static Socket Sock(Channel ch) { return ch.RawSocket; }

        /// <summary>mode: "lan", "internet" or "addr" (then <paramref name="addr"/> is host:port). status gets short progress texts.</summary>
        public static ConnectResult Connect(string code, string mode, string addr, string rendezvous, int udpPort, Action<string> status, Rendezvous.Holder cancel, int lanSeconds = 25)
        {
            code = LinkServer.Normalize(code);
            if (!LooksLikeCode(code)) return new ConnectResult { Error = "bad-code" };
            status = status ?? delegate { };
            byte[] w = Spake2.DeriveW(code);
            if (mode == "addr") return ConnectDirect(Rendezvous.ParseEp(addr), w, status);
            if (mode == "internet") return ConnectInternet(code, w, rendezvous, status, cancel);
            return ConnectLan(code, w, udpPort, status, lanSeconds, cancel);
        }

        static ConnectResult ConnectDirect(IPEndPoint ep, byte[] w, Action<string> status)
        {
            var s = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            try
            {
                var ar = s.BeginConnect(ep, null, null);
                if (!ar.AsyncWaitHandle.WaitOne(8000)) { s.Close(); return new ConnectResult { Error = "no-route" }; }
                s.EndConnect(ar);
            }
            catch (Exception e) { s.Close(); return new ConnectResult { Error = "no-route", Detail = e.Message }; }
            Hs hs;
            var ch = Finish(s, w, "lan", out hs);
            if (hs == Hs.WrongCode) return new ConnectResult { Error = "wrong-code" };
            if (ch == null) return new ConnectResult { Error = "no-route", Detail = "handshake " + hs };
            string path = IsPrivate(ep.Address) ? "lan" : "internet";
            try { Announce(ch, path); }
            catch (Exception e) { ch.Close(); return new ConnectResult { Error = "no-route", Detail = e.Message }; }
            return new ConnectResult { Channel = ch, Path = path };
        }

        static ConnectResult ConnectLan(string code, byte[] w, int udpPort, Action<string> status, int seconds, Rendezvous.Holder cancel)
        {
            string room = LinkServer.RoomOf(code);
            byte[] probe = Encoding.ASCII.GetBytes("HOPOVER-DISCOVER1 " + room);
            var socks = new List<Socket>();
            var bcast = new List<IPAddress> { IPAddress.Broadcast };
            try
            {
                foreach (var ni in System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.OperationalStatus != System.Net.NetworkInformation.OperationalStatus.Up || ni.NetworkInterfaceType == System.Net.NetworkInformation.NetworkInterfaceType.Loopback) continue;
                    foreach (var ua in ni.GetIPProperties().UnicastAddresses)
                    {
                        if (ua.Address.AddressFamily != AddressFamily.InterNetwork || ua.IPv4Mask == null) continue;
                        byte[] ip = ua.Address.GetAddressBytes(), m = ua.IPv4Mask.GetAddressBytes(), b = new byte[4];
                        for (int i = 0; i < 4; i++) b[i] = (byte)(ip[i] | ~m[i]);
                        try
                        {
                            var s = new Socket(AddressFamily.InterNetwork, SocketType.Dgram, ProtocolType.Udp);
                            s.EnableBroadcast = true;
                            s.Bind(new IPEndPoint(ua.Address, 0));
                            socks.Add(s);
                            var bip = new IPAddress(b);
                            if (!bcast.Any(x => x.Equals(bip))) bcast.Add(bip);
                        }
                        catch { }
                    }
                }
                if (socks.Count == 0) return new ConnectResult { Error = "no-route", Detail = "no network connection" };
                status("Looking for the old PC on this network");
                var tried = new HashSet<string>();
                DateTime until = DateTime.UtcNow.AddSeconds(seconds);
                while (DateTime.UtcNow < until)
                {
                    foreach (var s in socks)
                        foreach (var b in bcast)
                            try { s.SendTo(probe, new IPEndPoint(b, udpPort)); } catch { }
                    DateTime round = DateTime.UtcNow.AddMilliseconds(1200);
                    while (DateTime.UtcNow < round)
                    {
                        foreach (var s in socks)
                        {
                            if (!s.Poll(40000, SelectMode.SelectRead)) continue;
                            byte[] buf = new byte[512];
                            EndPoint from = new IPEndPoint(IPAddress.Any, 0);
                            int n;
                            try { n = s.ReceiveFrom(buf, ref from); } catch { continue; }
                            string[] p = Encoding.UTF8.GetString(buf, 0, n).Split(new[] { ' ' }, 3);
                            int tcpPort;
                            if (p.Length < 2 || p[0] != "HOPOVER-HERE1" || !int.TryParse(p[1], out tcpPort)) continue;
                            var ep = new IPEndPoint(((IPEndPoint)from).Address, tcpPort);
                            status("Found " + (p.Length > 2 ? p[2] : ep.Address.ToString()) + ", checking the code");
                            var r = ConnectDirect(ep, w, status);
                            if (r.Channel != null || r.Error == "wrong-code") return r;
                        }
                    }
                }
                return new ConnectResult { Error = "no-route" };
            }
            finally { foreach (var s in socks) try { s.Close(); } catch { } }
        }

        static ConnectResult ConnectInternet(string code, byte[] w, string rendezvous, Action<string> status, Rendezvous.Holder cancel)
        {
            if (string.IsNullOrEmpty(rendezvous)) return new ConnectResult { Error = "no-route", Detail = "no meeting point configured" };
            status("Asking the meeting point for the old PC");
            string err;
            var res = Rendezvous.Meet(rendezvous, "join", LinkServer.RoomOf(code), 600, cancel ?? new Rendezvous.Holder(), out err);
            if (res == null) return new ConnectResult { Error = err == "room_busy" ? "no-route" : "no-route", Detail = err };
            status("Connecting straight to the old PC");
            object lk = new object();
            Channel winner = null;
            bool wrong = false;
            int pending = 0;
            Action<Socket> onSocket = s =>
            {
                Interlocked.Increment(ref pending);
                var sock = s;
                new Thread(() =>
                {
                    try
                    {
                        Hs hs;
                        var ch = Channel.Open(sock, true, w, 10000, out hs);
                        if (hs == Hs.WrongCode) { wrong = true; }
                        if (ch == null) { try { sock.Close(); } catch { } return; }
                        lock (lk)
                        {
                            if (winner == null)
                            {
                                winner = ch;
                                var rep = (sock.RemoteEndPoint as IPEndPoint);
                                Announce(ch, rep != null && IsPrivate(rep.Address) ? "lan" : "internet");
                            }
                            else ch.Close();
                        }
                    }
                    catch (Exception) { try { sock.Close(); } catch { } }
                    finally { Interlocked.Decrement(ref pending); }
                }) { IsBackground = true }.Start();
            };
            Rendezvous.Punch(res, 12000, onSocket, () => winner != null || wrong);
            DateTime end = DateTime.UtcNow.AddSeconds(12);
            while (winner == null && !wrong && Volatile.Read(ref pending) > 0 && DateTime.UtcNow < end) Thread.Sleep(50);
            string room = LinkServer.RoomOf(code);
            if (winner != null) { Rendezvous.ReportResult(rendezvous, room, winner.Path); return new ConnectResult { Channel = winner, Path = winner.Path }; }
            if (!wrong) Rendezvous.ReportResult(rendezvous, room, "none");
            return new ConnectResult { Error = wrong ? "wrong-code" : "no-route", Detail = "no direct connection" };
        }
    }
}
