using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace Hopover.Link
{
    /// <summary>Headless "Hopover.exe --link VERB": serve, proxy, copy, info, selftest, stub. Runs before any WPF code.</summary>
    public static class LinkMain
    {
        [System.Runtime.InteropServices.DllImport("kernel32.dll")]
        static extern uint SetThreadExecutionState(uint flags);

        /// <summary>No sleep while this thread lives: a move takes hours and network traffic does not count as activity.
        /// Call it on a thread that lives as long as the process (the UI thread, or Main).</summary>
        public static void KeepAwake() { SetThreadExecutionState(0x80000001); } // ES_CONTINUOUS | ES_SYSTEM_REQUIRED

        public static string DefaultRendezvous()
        {
            try
            {
                using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("engine/config/include.json"))
                using (var r = new StreamReader(s, Encoding.UTF8))
                {
                    var d = new JavaScriptSerializer().DeserializeObject(r.ReadToEnd()) as Dictionary<string, object>;
                    var link = d["link"] as Dictionary<string, object>;
                    return Convert.ToString(link["rendezvous"]);
                }
            }
            catch { return "http://hop.ilscipio.com"; }
        }

        static string Opt(string[] a, string name)
        {
            int i = Array.IndexOf(a, name);
            return i >= 0 && i + 1 < a.Length ? a[i + 1] : null;
        }

        public static int Run(string[] a)
        {
            var o = new StreamWriter(Console.OpenStandardOutput(), new UTF8Encoding(false)) { AutoFlush = true };
            var lk = new object();
            Action<string> say = line => { lock (lk) o.WriteLine(line); };
            string verb = a.Length > 0 ? a[0] : "";
            try
            {
                switch (verb)
                {
                    case "selftest": return SelfTest.Run(say);
                    case "copy": return LinkCopy.Run(a.Skip(1).ToArray(), o);
                    case "info":
                        using (var m = MuxClient.ConnectFromEnv()) say(m.Info());
                        return 0;
                    case "refresh":
                        using (var m = MuxClient.ConnectFromEnv()) return m.Refresh(say);
                    case "serve": return Serve(a, say);
                    case "proxy": return Proxy(a, say);
                    case "stub": return Stub.Run(int.Parse(Opt(a, "--port") ?? "47900"), say);
                    default:
                        say("usage: Hopover.exe --link selftest|serve|proxy|copy|info|refresh|stub");
                        return 2;
                }
            }
            catch (Exception e)
            {
                say("ERROR " + e.Message);
                return 16;
            }
        }

        static int Serve(string[] a, Action<string> say)
        {
            var so = new ServerOptions { Event = say, Rendezvous = DefaultRendezvous() };
            if (Opt(a, "--store") != null) so.Store = Opt(a, "--store");
            so.Engine = Opt(a, "--engine");
            if (Opt(a, "--code") != null) so.FixedCode = Opt(a, "--code");
            if (Opt(a, "--tcp") != null) so.TcpPort = int.Parse(Opt(a, "--tcp"));
            if (Opt(a, "--udp") != null) so.UdpPort = int.Parse(Opt(a, "--udp"));
            if (Opt(a, "--name") != null) so.Name = Opt(a, "--name");
            string rv = Opt(a, "--rendezvous");
            if (rv != null) so.Rendezvous = rv == "none" ? null : rv;
            for (int i = 0; i < a.Length - 1; i++)
                if (a[i] == "--root") { string[] kv = a[i + 1].Split(new[] { '=' }, 2); so.Roots[kv[0]] = kv[1]; }
            var srv = new LinkServer(so);
            srv.Start();
            say("##LINK listening " + srv.TcpPort);
            KeepAwake();
            Thread.Sleep(Timeout.Infinite);
            return 0;
        }

        static int Proxy(string[] a, Action<string> say)
        {
            string rv = Opt(a, "--rendezvous") ?? DefaultRendezvous();
            var r = LinkClient.Connect(Opt(a, "--code"), Opt(a, "--mode") ?? "lan", Opt(a, "--addr"), rv,
                                       int.Parse(Opt(a, "--udp") ?? "47801"), s => say("##LINK status " + s), new Rendezvous.Holder());
            if (r.Channel == null) { say("##LINK path none"); say("##LINK failed " + r.Error + " " + r.Detail); return 1; }
            var px = new MuxProxy(r.Channel);
            var done = new ManualResetEvent(false);
            px.Closed += () => done.Set();
            px.Start();
            say("##LINK path " + r.Path);
            say("##LINK proxy " + px.Port + " " + px.Token);
            KeepAwake();
            done.WaitOne();
            return 0;
        }
    }

    /// <summary>A tiny stand-in for the meeting point (the real one is the Go server in rendezvous\). Test helper only.</summary>
    static class Stub
    {
        sealed class Waiter { public Socket S; public string Ip; public string[] Local; public ManualResetEvent Done = new ManualResetEvent(false); }

        public static int Run(int port, Action<string> say)
        {
            var rooms = new Dictionary<string, Dictionary<string, Waiter>>();
            var ser = new JavaScriptSerializer();
            var lis = new TcpListener(IPAddress.Loopback, port);
            lis.Start();
            say("##STUB listening " + port);
            while (true)
            {
                var s = lis.AcceptSocket();
                new Thread(() =>
                {
                    try
                    {
                        var raw = new List<byte>();
                        var one = new byte[1];
                        while (true)
                        {
                            if (s.Receive(one) != 1) return;
                            raw.Add(one[0]);
                            int n = raw.Count;
                            if (n >= 4 && raw[n - 4] == 13 && raw[n - 3] == 10 && raw[n - 2] == 13 && raw[n - 1] == 10) break;
                        }
                        string head = Encoding.ASCII.GetString(raw.ToArray());
                        if (head.StartsWith("GET /v1/ping")) { Reply(s, "{\"v\":1,\"pong\":true}"); return; }
                        int cl = 0;
                        foreach (string line in head.Split('\n'))
                            if (line.ToLowerInvariant().StartsWith("content-length:")) cl = int.Parse(line.Substring(15).Trim());
                        byte[] body = new byte[cl];
                        int got = 0;
                        while (got < cl) { int k = s.Receive(body, got, cl - got, SocketFlags.None); if (k <= 0) return; got += k; }
                        var req = (Dictionary<string, object>)ser.DeserializeObject(Encoding.UTF8.GetString(body));
                        string op = (string)req["op"], room = (string)req["room"];
                        var ep = (IPEndPoint)s.RemoteEndPoint;
                        var w = new Waiter { S = s, Ip = ep.Address + ":" + ep.Port, Local = ((System.Collections.IEnumerable)req["local"]).Cast<object>().Select(x => (string)x).ToArray() };
                        Waiter other = null;
                        string peerOp = op == "host" ? "join" : "host";
                        lock (rooms)
                        {
                            Dictionary<string, Waiter> r;
                            if (!rooms.TryGetValue(room, out r)) rooms[room] = r = new Dictionary<string, Waiter>();
                            if (r.ContainsKey(op)) { Reply(s, "{\"v\":1,\"error\":\"room_busy\"}"); return; }
                            if (r.TryGetValue(peerOp, out other)) rooms.Remove(room); else r[op] = w;
                        }
                        if (other == null)
                        {
                            w.Done.WaitOne(10 * 60 * 1000);
                            return;
                        }
                        Reply(w.S, Answer(ser, w, other));
                        Reply(other.S, Answer(ser, other, w));
                        other.Done.Set();
                    }
                    catch (Exception) { }
                    finally { try { s.Close(); } catch { } }
                }) { IsBackground = true }.Start();
            }
        }

        static string Answer(JavaScriptSerializer ser, Waiter me, Waiter peer)
        {
            return ser.Serialize(new Dictionary<string, object> { { "v", 1 }, { "you", me.Ip }, { "peer", peer.Ip }, { "peer_local", peer.Local } });
        }

        static void Reply(Socket s, string json)
        {
            byte[] b = Encoding.UTF8.GetBytes(json);
            string head = "HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: " + b.Length + "\r\nConnection: close\r\n\r\n";
            s.Send(Encoding.ASCII.GetBytes(head));
            s.Send(b);
            try { s.Shutdown(SocketShutdown.Both); } catch { }
        }
    }

    static class SelfTest
    {
        static int fails;

        static void Check(Action<string> say, string name, bool ok)
        {
            say((ok ? "PASS " : "FAIL ") + name);
            if (!ok) fails++;
        }

        static readonly string[][] Vectors =
        {
            new[] { "server", "client", "2ee57912099d31560b3a44b1184b9b4866e904c49d12ac5042c97dca461b1a5f", "43dd0fd7215bdcb482879fca3220c6a968e66d70b1356cac18bb26c84a78d729", "dcb60106f276b02606d8ef0a328c02e4b629f84f89786af5befb0bc75b6e66be", "04a56fa807caaa53a4d28dbb9853b9815c61a411118a6fe516a8798434751470f9010153ac33d0d5f2047ffdb1a3e42c9b4e6be662766e1eeb4116988ede5f912c", "0406557e482bd03097ad0cbaa5df82115460d951e3451962f1eaf4367a420676d09857ccbc522686c83d1852abfa8ed6e4a1155cf8f1543ceca528afb591a1e0b7", "0412af7e89717850671913e6b469ace67bd90a4df8ce45c2af19010175e37eed69f75897996d539356e2fa6a406d528501f907e04d97515fbe83db277b715d3325", "06000000000000007365727665720600000000000000636c69656e74410000000000000004a56fa807caaa53a4d28dbb9853b9815c61a411118a6fe516a8798434751470f9010153ac33d0d5f2047ffdb1a3e42c9b4e6be662766e1eeb4116988ede5f912c41000000000000000406557e482bd03097ad0cbaa5df82115460d951e3451962f1eaf4367a420676d09857ccbc522686c83d1852abfa8ed6e4a1155cf8f1543ceca528afb591a1e0b741000000000000000412af7e89717850671913e6b469ace67bd90a4df8ce45c2af19010175e37eed69f75897996d539356e2fa6a406d528501f907e04d97515fbe83db277b715d332520000000000000002ee57912099d31560b3a44b1184b9b4866e904c49d12ac5042c97dca461b1a5f", "58ad4aa88e0b60d5061eb6b5dd93e80d9c4f00d127c65b3b35b1b5281fee38f0", "d3e2e547f1ae04f2dbdbf0fc4b79f8ecff2dff314b5d32fe9fcef2fb26dc459b" },
            new[] { "", "client", "0548d8729f730589e579b0475a582c1608138ddf7054b73b5381c7e883e2efae", "403abbe3b1b4b9ba17e3032849759d723939a27a27b9d921c500edde18ed654b", "903023b6598908936ea7c929bd761af6039577a9c3f9581064187c3049d87065", "04a897b769e681c62ac1c2357319a3d363f610839c4477720d24cbe32f5fd85f44fb92ba966578c1b712be6962498834078262caa5b441ecfa9d4a9485720e918a", "04e0f816fd1c35e22065d5556215c097e799390d16661c386e0ecc84593974a61b881a8c82327687d0501862970c64565560cb5671f696048050ca66ca5f8cc7fc", "048f83ec9f6e4f87cc6f9dc740bdc2769725f923364f01c84148c049a39a735ebda82eac03e00112fd6a5710682767cff5361f7e819e53d8d3c3a2922e0d837aa6", "00000000000000000600000000000000636c69656e74410000000000000004a897b769e681c62ac1c2357319a3d363f610839c4477720d24cbe32f5fd85f44fb92ba966578c1b712be6962498834078262caa5b441ecfa9d4a9485720e918a410000000000000004e0f816fd1c35e22065d5556215c097e799390d16661c386e0ecc84593974a61b881a8c82327687d0501862970c64565560cb5671f696048050ca66ca5f8cc7fc4100000000000000048f83ec9f6e4f87cc6f9dc740bdc2769725f923364f01c84148c049a39a735ebda82eac03e00112fd6a5710682767cff5361f7e819e53d8d3c3a2922e0d837aa620000000000000000548d8729f730589e579b0475a582c1608138ddf7054b73b5381c7e883e2efae", "47d29e6666af1b7dd450d571233085d7a9866e4d49d2645e2df975489521232b", "3313c5cefc361d27fb16847a91c2a73b766ffa90a4839122a9b70a2f6bd1d6df" },
            new[] { "server", "", "626e0cdc7b14c9db3e52a0b1b3a768c98e37852d5db30febe0497b14eae8c254", "07adb3db6bc623d3399726bfdbfd3d15a58ea776ab8a308b00392621291f9633", "b6a4fc8dbb629d4ba51d6f91ed1532cf87adec98f25dd153a75accafafedec16", "04f88fb71c99bfffaea370966b7eb99cd4be0ff1a7d335caac4211c4afd855e2e15a873b298503ad8ba1d9cbb9a392d2ba309b48bfd7879aefd0f2cea6009763b0", "040c269d6be017dccb15182ac6bfcd9e2a14de019dd587eaf4bdfd353f031101e7cca177f8eb362a6e83e7d5e729c0732e1b528879c086f39ba0f31a9661bd34db", "0445ee233b8ecb51ebd6e7da3f307e88a1616bae2166121221fdc0dadb986afaf3ec8a988dc9c626fa3b99f58a7ca7c9b844bb3e8dd9554aafc5b53813504c1cbe", "06000000000000007365727665720000000000000000410000000000000004f88fb71c99bfffaea370966b7eb99cd4be0ff1a7d335caac4211c4afd855e2e15a873b298503ad8ba1d9cbb9a392d2ba309b48bfd7879aefd0f2cea6009763b04100000000000000040c269d6be017dccb15182ac6bfcd9e2a14de019dd587eaf4bdfd353f031101e7cca177f8eb362a6e83e7d5e729c0732e1b528879c086f39ba0f31a9661bd34db41000000000000000445ee233b8ecb51ebd6e7da3f307e88a1616bae2166121221fdc0dadb986afaf3ec8a988dc9c626fa3b99f58a7ca7c9b844bb3e8dd9554aafc5b53813504c1cbe2000000000000000626e0cdc7b14c9db3e52a0b1b3a768c98e37852d5db30febe0497b14eae8c254", "bc9f9bbe99f26d0b2260e6456e05a86196a3307ec6663a18bf6ac825736533b2", "c2370e1bf813b086dff0d834e74425a06e6390f48f5411900276dcccc5a297ec" },
            new[] { "", "", "7bf46c454b4c1b25799527d896508afd5fc62ef4ec59db1efb49113063d70cca", "8cef65df64bb2d0f83540c53632de911b5b24b3eab6cc74a97609fd659e95473", "d7a66f64074a84652d8d623a92e20c9675c61cb5b4f6a0063e4648a2fdc02d53", "04a65b367a3f613cf9f0654b1b28a1e3a8a40387956c8ba6063e8658563890f46ca1ef6a676598889fc28de2950ab8120b79a5ef1ea4c9f44bc98f585634b46d66", "04589f13218822710d98d8b2123a079041052d9941b9cf88c6617ddb2fcc0494662eea8ba6b64692dc318250030c6af045cb738bc81ba35b043c3dcb46adf6f58d", "041a3c03d51b452537ca2a1fea6110353c6d5ed483c4f0f86f4492ca3f378d40a994b4477f93c64d928edbbcd3e85a7c709b7ea73ee97986ce3d1438e135543772", "00000000000000000000000000000000410000000000000004a65b367a3f613cf9f0654b1b28a1e3a8a40387956c8ba6063e8658563890f46ca1ef6a676598889fc28de2950ab8120b79a5ef1ea4c9f44bc98f585634b46d66410000000000000004589f13218822710d98d8b2123a079041052d9941b9cf88c6617ddb2fcc0494662eea8ba6b64692dc318250030c6af045cb738bc81ba35b043c3dcb46adf6f58d4100000000000000041a3c03d51b452537ca2a1fea6110353c6d5ed483c4f0f86f4492ca3f378d40a994b4477f93c64d928edbbcd3e85a7c709b7ea73ee97986ce3d1438e13554377220000000000000007bf46c454b4c1b25799527d896508afd5fc62ef4ec59db1efb49113063d70cca", "dfb4db8d48ae5a675963ea5e6c19d98d4ea028d8e898dad96ea19a80ade95dca", "d0f0609d1613138d354f7e95f19fb556bf52d751947241e8c7118df5ef0ae175" },
        };

        public static int Run(Action<string> say)
        {
            fails = 0;
            Spake(say);
            Gcm(say);
            Session(say);
            say(fails == 0 ? "SELFTEST OK" : "SELFTEST FAILED (" + fails + ")");
            return fails == 0 ? 0 : 1;
        }

        static byte[] H(string h) { return P256.HexBytes(h); }

        static void Spake(Action<string> say)
        {
            int n = 0;
            foreach (var v in Vectors)
            {
                n++;
                byte[] w = H(v[2]);
                var A = new Spake2(true, w, Encoding.ASCII.GetBytes(v[0]), Encoding.ASCII.GetBytes(v[1]), H(v[3]));
                var B = new Spake2(false, w, Encoding.ASCII.GetBytes(v[0]), Encoding.ASCII.GetBytes(v[1]), H(v[4]));
                bool ok = P256.ToHex(A.Share) == v[5] && P256.ToHex(B.Share) == v[6];
                ok &= A.Finish(B.Share) && B.Finish(A.Share);
                ok &= P256.ToHex(A.Transcript) == v[8] && P256.ToHex(B.Transcript) == v[8];
                ok &= P256.ToHex(A.Ke) == P256.ToHex(B.Ke);
                ok &= P256.ToHex(A.ConfirmOut) == v[9] && P256.ToHex(B.ConfirmOut) == v[10];
                ok &= A.CheckConfirm(B.ConfirmOut) && B.CheckConfirm(A.ConfirmOut);
                Check(say, "RFC 9382 P-256 test vector " + n + " (shares, TT, Ke, confirmations)", ok);
            }
            var bad = new Spake2(false, Spake2.DeriveW("1-a-b-c"), null, null);
            Check(say, "SPAKE2 rejects an off-curve share", !bad.Finish(new byte[65]));
            var a1 = new Spake2(true, Spake2.DeriveW("1-alpha-bravo-cafe"), null, null);
            var b1 = new Spake2(false, Spake2.DeriveW("1-alpha-bravo-cafe2"), null, null);
            a1.Finish(b1.Share); b1.Finish(a1.Share);
            Check(say, "SPAKE2 wrong password gives different confirmations", !a1.CheckConfirm(b1.ConfirmOut));
        }

        static void Gcm(Action<string> say)
        {
            // McGrew and Viega, GCM spec, test cases 1 to 4 (AES-128), also in NIST SP 800-38D examples.
            Func<string, string, string, string, string, string, bool> one = (key, iv, aad, pt, ct, tag) =>
            {
                using (var g = new AesGcm(H(key)))
                {
                    byte[] p = H(pt), c = new byte[p.Length], t = new byte[16], a = H(aad);
                    g.Encrypt(H(iv), a, p, 0, p.Length, c, 0, t, 0);
                    bool ok = P256.ToHex(c) == ct && P256.ToHex(t) == tag;
                    byte[] back = new byte[p.Length];
                    ok &= g.Decrypt(H(iv), a, c, 0, c.Length, back, 0, t, 0) && P256.ToHex(back) == pt;
                    if (p.Length > 0) { c[0] ^= 1; ok &= !g.Decrypt(H(iv), a, c, 0, c.Length, back, 0, t, 0); }
                    return ok;
                }
            };
            string z = "00000000000000000000000000000000";
            Check(say, "AES-GCM test case 1 (empty)", one(z, "000000000000000000000000", "", "", "", "58e2fccefa7e3061367f1d57a4e7455a"));
            Check(say, "AES-GCM test case 2", one(z, "000000000000000000000000", "", z, "0388dace60b6a392f328c2b971b2fe78", "ab6e47d42cec13bdf53a67b21257bddf"));
            string k3 = "feffe9928665731c6d6a8f9467308308", iv3 = "cafebabefacedbaddecaf888";
            string p3 = "d9313225f88406e5a55909c5aff5269a86a7a9531534f7da2e4c303d8a318a721c3c0c95956809532fcf0e2449a6b525b16aedf5aa0de657ba637b391aafd255";
            Check(say, "AES-GCM test case 3", one(k3, iv3, "", p3,
                "42831ec2217774244b7221b784d0d49ce3aa212f2c02a4e035c17e2329aca12e21d514b25466931c7d8f6a5aac84aa051ba30b396a0aac973d58e091473f5985",
                "4d5c2af327cd64a62cf35abd2ba6fab4"));
            string p4 = p3.Substring(0, 120);
            Check(say, "AES-GCM test case 4 (with AAD)", one(k3, iv3, "feedfacedeadbeeffeedfacedeadbeefabaddad2", p4,
                "42831ec2217774244b7221b784d0d49ce3aa212f2c02a4e035c17e2329aca12e21d514b25466931c7d8f6a5aac84aa051ba30b396a0aac973d58e091",
                "5bc94fbc3221a5db94fae95ae7121a47"));
        }

        static Socket[] Pair()
        {
            var l = new TcpListener(IPAddress.Loopback, 0);
            l.Start();
            var c = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
            c.Connect(l.LocalEndpoint);
            var s = l.AcceptSocket();
            l.Stop();
            return new[] { c, s };
        }

        static void Session(Action<string> say)
        {
            // Good code: both sides open, data flows both ways.
            byte[] w = Spake2.DeriveW("3-alpha-bravo-cafe");
            var p = Pair();
            Hs hb = Hs.Io;
            Channel b = null;
            var t = new Thread(() => { b = Channel.Open(p[1], false, w, 5000, out hb); });
            t.Start();
            Hs ha;
            var a = Channel.Open(p[0], true, w, 5000, out ha);
            t.Join();
            bool ok = ha == Hs.Ok && hb == Hs.Ok;
            if (ok)
            {
                a.Send(Msg.Data, new byte[] { 1, 2, 3 });
                byte[] got;
                ok &= b.Receive(out got) == Msg.Data && got.Length == 3 && got[2] == 3;
                b.Send(Msg.Data, new byte[0]);
                ok &= a.Receive(out got) == Msg.Data && got.Length == 0;
            }
            Check(say, "session: right code opens an encrypted link, both directions", ok);
            if (a != null) a.Close();
            if (b != null) b.Close();

            // Wrong code: both sides refuse.
            p = Pair();
            byte[] w2 = Spake2.DeriveW("3-alpha-bravo-cafx");
            t = new Thread(() => { Channel x = Channel.Open(p[1], false, w2, 5000, out hb); });
            t.Start();
            Channel y = Channel.Open(p[0], true, w, 5000, out ha);
            t.Join();
            Check(say, "session: wrong code fails on both sides", y == null && ha == Hs.WrongCode && hb == Hs.WrongCode);

            // Tamper: a relay flips one ciphertext byte after the handshake; the receiver drops the link.
            var left = Pair();   // client <-> relay
            var right = Pair();  // relay <-> server
            bool flip = false;
            Action<Socket, Socket, bool> pump = (from, to, tamper) =>
            {
                try
                {
                    byte[] buf = new byte[4096];
                    while (true)
                    {
                        int n = from.Receive(buf);
                        if (n <= 0) break;
                        if (tamper && flip && n > 6) { buf[6] ^= 0x01; flip = false; }
                        to.Send(buf, 0, n, SocketFlags.None);
                    }
                }
                catch (Exception) { }
                try { to.Close(); } catch { }
            };
            new Thread(() => pump(left[1], right[0], true)) { IsBackground = true }.Start();
            new Thread(() => pump(right[0], left[1], false)) { IsBackground = true }.Start();
            Channel sb = null;
            t = new Thread(() => { sb = Channel.Open(right[1], false, w, 5000, out hb); });
            t.Start();
            var cl = Channel.Open(left[0], true, w, 5000, out ha);
            t.Join();
            bool dropped = false;
            if (cl != null && sb != null)
            {
                cl.Send(Msg.Data, new byte[] { 9, 9, 9, 9, 9, 9, 9, 9 });
                byte[] g;
                sb.Receive(out g);   // one clean frame first, so the counters have moved
                flip = true;
                cl.Send(Msg.Data, new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 });
                try { sb.Receive(out g); } catch (LinkException) { dropped = sb.IsClosed; }
            }
            Check(say, "tamper: a flipped byte drops the link", dropped);

            // The server burns the code on a wrong guess.
            var so = new ServerOptions { FixedCode = "4-alpha-bravo-cafe", TcpPort = 0, UdpPort = 47899, Rendezvous = null };
            var srv = new LinkServer(so);
            srv.Start();
            string addr = "127.0.0.1:" + srv.TcpPort;
            var bad = LinkClient.Connect("4-alpha-bravo-cairo", "addr", addr, null, 0, null, null);
            bool burned = bad.Error == "wrong-code";
            for (int i = 0; i < 100 && srv.Code == "4-alpha-bravo-cafe"; i++) Thread.Sleep(100);
            string fresh = srv.Code;
            burned &= fresh != "4-alpha-bravo-cafe";
            var again = LinkClient.Connect("4-alpha-bravo-cafe", "addr", addr, null, 0, null, null);
            Check(say, "wrong guess ends the session: the old code is dead, a new code exists", burned && again.Channel == null);
            // The second wrong guess burns the code too, and making a code takes a while on a busy PC.
            for (int i = 0; i < 100 && srv.Code == fresh; i++) Thread.Sleep(100);
            var good = LinkClient.Connect(srv.Code, "addr", addr, null, 0, null, null);
            Check(say, "the fresh code pairs", good.Channel != null);
            if (good.Channel != null) good.Channel.Close();
            srv.Stop();
        }
    }
}
