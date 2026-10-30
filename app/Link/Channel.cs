using System;
using System.IO;
using System.Net.Sockets;
using System.Text;

namespace Hopover.Link
{
    public class LinkException : Exception
    {
        public LinkException(string m) : base(m) { }
        public LinkException(string m, Exception inner) : base(m, inner) { }
    }

    public enum Hs { Ok, NotHopover, WrongCode, Io }

    /// <summary>Mux message types inside the encrypted channel. The payload starts with a 4-byte stream id (big endian).</summary>
    public static class Msg
    {
        public const byte Req = 1, Data = 2, End = 3, Err = 4, Cancel = 5, Use = 0xF0;
    }

    /// <summary>
    /// An authenticated, encrypted link over one TCP connection.
    /// Handshake: SPAKE2 (RFC 9382) with the pairing code, key confirmation both ways.
    /// Frames: u32 len | AES-GCM(type u8 + payload) | tag 16. The length is the AAD, one key per direction,
    /// nonce = 4-byte direction + 64-bit counter. A tag failure drops the link.
    /// </summary>
    public sealed class Channel : IDisposable
    {
        public const int MaxFrame = 1 << 20;
        static readonly byte[] Magic = Encoding.ASCII.GetBytes("HOPOVER1");

        readonly Socket sock;
        readonly Stream stream;
        readonly AesGcm sendKey, recvKey;
        readonly byte[] sendNonce, recvNonce;
        ulong sendCtr, recvCtr;
        readonly object wlock = new object();
        bool closed;

        /// <summary>Set by the caller: the name of the other PC and the route ("lan" or "internet").</summary>
        public string PeerName = "";
        public string Path = "";

        Channel(Socket s, byte[] ke, bool isA)
        {
            sock = s;
            stream = new NetworkStream(s, false);
            byte[] a2b = Spake2.Hkdf(ke, null, Encoding.ASCII.GetBytes("hopover-link-v1 A2B"), 32);
            byte[] b2a = Spake2.Hkdf(ke, null, Encoding.ASCII.GetBytes("hopover-link-v1 B2A"), 32);
            sendKey = new AesGcm(isA ? a2b : b2a);
            recvKey = new AesGcm(isA ? b2a : a2b);
            sendNonce = new byte[12]; recvNonce = new byte[12];
            sendNonce[0] = (byte)(isA ? 1 : 2);
            recvNonce[0] = (byte)(isA ? 2 : 1);
        }

        /// <summary>Test constructor: a channel from a shared key without the handshake.</summary>
        internal static Channel FromKey(Socket s, byte[] ke, bool isA) { return new Channel(s, ke, isA); }

        public System.Net.EndPoint RemoteEndPoint { get { try { return sock.RemoteEndPoint; } catch { return null; } } }
        public bool IsClosed { get { return closed; } }

        static void ReadExact(Stream s, byte[] b, int off, int len)
        {
            while (len > 0)
            {
                int n = s.Read(b, off, len);
                if (n <= 0) throw new LinkException("connection closed");
                off += n; len -= n;
            }
        }

        /// <summary>
        /// Runs the handshake on a connected socket. A is the new PC ("join"), B the old PC ("host").
        /// WrongCode means the peer spoke the protocol and its confirmation did not match: the code is burnt.
        /// </summary>
        public static Channel Open(Socket s, bool isA, byte[] w, int timeoutMs, out Hs result)
        {
            try
            {
                s.NoDelay = true;
                s.ReceiveTimeout = timeoutMs;
                s.SendTimeout = timeoutMs;
                var sp = new Spake2(isA, w, Encoding.ASCII.GetBytes("hopover-new"), Encoding.ASCII.GetBytes("hopover-old"));
                var ns = new NetworkStream(s, false);
                byte[] hello = new byte[74];
                Array.Copy(Magic, hello, 8);
                hello[8] = (byte)(isA ? 'A' : 'B');
                Array.Copy(sp.Share, 0, hello, 9, 65);
                ns.Write(hello, 0, hello.Length);
                byte[] peer = new byte[74];
                ReadExact(ns, peer, 0, 74);
                for (int i = 0; i < 8; i++) if (peer[i] != Magic[i]) { result = Hs.NotHopover; return null; }
                if (peer[8] != (byte)(isA ? 'B' : 'A')) { result = Hs.NotHopover; return null; }
                byte[] ps = new byte[65];
                Array.Copy(peer, 9, ps, 0, 65);
                if (!sp.Finish(ps)) { result = Hs.NotHopover; return null; }
                ns.Write(sp.ConfirmOut, 0, 32);
                byte[] conf = new byte[32];
                ReadExact(ns, conf, 0, 32);
                if (!sp.CheckConfirm(conf)) { result = Hs.WrongCode; return null; }
                s.ReceiveTimeout = 0;
                s.SendTimeout = 0;
                result = Hs.Ok;
                return new Channel(s, sp.Ke, isA);
            }
            catch (Exception)
            {
                result = Hs.Io;
                return null;
            }
        }

        byte[] sendBuf, recvBuf;
        readonly byte[] sendAad = new byte[4];

        public Socket RawSocket { get { return sock; } }

        public void Send(byte type, byte[] payload, int off, int len)
        {
            int total = 1 + len;
            if (total > MaxFrame + 1) throw new LinkException("frame too large");
            lock (wlock)
            {
                if (closed) throw new LinkException("link closed");
                int need = 4 + total + 16;
                if (sendBuf == null || sendBuf.Length < need) sendBuf = new byte[Math.Max(need, 70000)];
                sendAad[0] = sendBuf[0] = (byte)(total >> 24); sendAad[1] = sendBuf[1] = (byte)(total >> 16);
                sendAad[2] = sendBuf[2] = (byte)(total >> 8); sendAad[3] = sendBuf[3] = (byte)total;
                sendBuf[4] = type;
                if (len > 0) Buffer.BlockCopy(payload, off, sendBuf, 5, len);
                for (int i = 0; i < 8; i++) sendNonce[11 - i] = (byte)(sendCtr >> (8 * i));
                // In place: CNG allows the same buffer for input and output.
                sendKey.Encrypt(sendNonce, sendAad, sendBuf, 4, total, sendBuf, 4, sendBuf, 4 + total);
                sendCtr++;
                try { stream.Write(sendBuf, 0, need); }
                catch (Exception e) { Close(); throw new LinkException("send failed", e); }
            }
        }

        public void Send(byte type, byte[] payload) { Send(type, payload, 0, payload == null ? 0 : payload.Length); }

        /// <summary>Reads one frame (one reader thread only). A bad length or a bad tag drops the link and throws.</summary>
        public byte Receive(out byte[] payload)
        {
            byte[] hdr = new byte[4];
            try { ReadExact(stream, hdr, 0, 4); }
            catch (Exception e) { Close(); throw new LinkException("receive failed", e); }
            long total = ((long)hdr[0] << 24) | ((long)hdr[1] << 16) | ((long)hdr[2] << 8) | hdr[3];
            if (total < 1 || total > MaxFrame + 1) { Close(); throw new LinkException("bad frame length"); }
            if (recvBuf == null || recvBuf.Length < total + 16) recvBuf = new byte[Math.Max(total + 16, 70000)];
            try { ReadExact(stream, recvBuf, 0, (int)total + 16); }
            catch (Exception e) { Close(); throw new LinkException("receive failed", e); }
            for (int i = 0; i < 8; i++) recvNonce[11 - i] = (byte)(recvCtr >> (8 * i));
            if (!recvKey.Decrypt(recvNonce, hdr, recvBuf, 0, (int)total, recvBuf, 0, recvBuf, (int)total))
            {
                Close();
                throw new LinkException("authentication failed: link dropped");
            }
            recvCtr++;
            payload = new byte[total - 1];
            Buffer.BlockCopy(recvBuf, 1, payload, 0, payload.Length);
            return recvBuf[0];
        }

        public void Close()
        {
            closed = true;
            try { sock.Close(); } catch { }
        }

        public void Dispose()
        {
            Close();   // the keys stay: another thread may still be inside Send or Receive
        }
    }
}
