using System;
using System.IO;
using System.Numerics;
using System.Security.Cryptography;
using System.Text;

namespace Hopover.Link
{
    /// <summary>
    /// SPAKE2 per RFC 9382, ciphersuite P256-SHA256-HKDF-HMAC, with the M and N of the RFC.
    /// A (the new PC, "join") uses M; B (the old PC, "host") uses N.
    /// </summary>
    public sealed class Spake2
    {
        static readonly P256.Pt M = P256.Decode(P256.HexBytes("02886e2f97ace46e55ba9dd7242579f2993b64e16ef3dcab95afd497333d8fa12f"));
        static readonly P256.Pt Nn = P256.Decode(P256.HexBytes("03d8bbd6c639c62937b04d997f38c3770719c629d7014d49a24b4f98baa1292b49"));

        readonly bool isA;
        readonly BigInteger w;
        readonly byte[] wBytes, idA, idB;
        readonly BigInteger secret;
        readonly byte[] myShare;
        byte[] confirmExpected;

        /// <summary>The shared key Ke (16 bytes), after Finish.</summary>
        public byte[] Ke { get; private set; }
        /// <summary>The confirmation MAC this side sends, after Finish.</summary>
        public byte[] ConfirmOut { get; private set; }
        /// <summary>The confirmation MAC the peer must send, after Finish (test vectors).</summary>
        public byte[] ConfirmExpected { get { return confirmExpected; } }
        public byte[] Transcript { get; private set; }

        public Spake2(bool isA, byte[] w32, byte[] idA, byte[] idB, byte[] fixedSecret32 = null)
        {
            this.isA = isA;
            wBytes = w32;
            w = P256.FromBytes(w32);
            this.idA = idA ?? new byte[0];
            this.idB = idB ?? new byte[0];
            secret = fixedSecret32 != null ? P256.FromBytes(fixedSecret32) : RandomScalar();
            myShare = P256.Encode(P256.Add(P256.Mul(secret, P256.G), P256.Mul(w, isA ? M : Nn)));
        }

        public byte[] Share { get { return myShare; } }

        static BigInteger RandomScalar()
        {
            using (var rng = new RNGCryptoServiceProvider())
            {
                byte[] b = new byte[48];
                rng.GetBytes(b);
                // 128 bits more than the order: no bias after the reduction.
                return BigInteger.Remainder(P256.FromBytes(b), P256.N - 1) + 1;
            }
        }

        /// <summary>w from the pairing code: PBKDF2-HMAC-SHA256 (40 bytes), mod the group order, 32 bytes big endian.</summary>
        public static byte[] DeriveW(string code)
        {
            byte[] raw = Pbkdf2(Encoding.UTF8.GetBytes(code), Encoding.ASCII.GetBytes("hopover-link-v1 pairing code"), 20000, 40);
            return P256.ToBytes32(P256.FromBytes(raw) % P256.N);
        }

        static byte[] Pbkdf2(byte[] pw, byte[] salt, int iterations, int len)
        {
            using (var h = new HMACSHA256(pw))
            {
                var outp = new MemoryStream();
                for (uint block = 1; outp.Length < len; block++)
                {
                    byte[] s = new byte[salt.Length + 4];
                    Array.Copy(salt, s, salt.Length);
                    s[salt.Length] = (byte)(block >> 24); s[salt.Length + 1] = (byte)(block >> 16);
                    s[salt.Length + 2] = (byte)(block >> 8); s[salt.Length + 3] = (byte)block;
                    byte[] u = h.ComputeHash(s), t = (byte[])u.Clone();
                    for (int i = 1; i < iterations; i++)
                    {
                        u = h.ComputeHash(u);
                        for (int k = 0; k < t.Length; k++) t[k] ^= u[k];
                    }
                    outp.Write(t, 0, t.Length);
                }
                byte[] r = new byte[len];
                Array.Copy(outp.ToArray(), r, len);
                return r;
            }
        }

        static void Field(MemoryStream ms, byte[] v)
        {
            long n = v.Length;
            for (int i = 0; i < 8; i++) ms.WriteByte((byte)(n >> (8 * i)));
            ms.Write(v, 0, v.Length);
        }

        /// <summary>Takes the peer's share. Returns false when it is not a valid curve point.</summary>
        public bool Finish(byte[] peerShare)
        {
            if (peerShare == null || peerShare.Length != 65 || P256.Decode(peerShare) == null) return false;
            var peer = P256.Decode(peerShare);
            // K = h*x*(pB - w*N) for A, h*y*(pA - w*M) for B; the cofactor h of P-256 is 1.
            var other = P256.Add(peer, P256.Neg(P256.Mul(w, isA ? Nn : M)));
            if (other.Inf) return false;
            var k = P256.Mul(secret, other);
            if (k.Inf) return false;
            byte[] pA = isA ? myShare : peerShare, pB = isA ? peerShare : myShare;
            var tt = new MemoryStream();
            Field(tt, idA); Field(tt, idB); Field(tt, pA); Field(tt, pB); Field(tt, P256.Encode(k)); Field(tt, wBytes);
            byte[] t = tt.ToArray();
            Transcript = t;
            byte[] hash;
            using (var sha = SHA256.Create()) hash = sha.ComputeHash(t);
            byte[] ke = new byte[16], ka = new byte[16];
            Array.Copy(hash, 0, ke, 0, 16);
            Array.Copy(hash, 16, ka, 0, 16);
            byte[] kc = Hkdf(ka, null, Encoding.ASCII.GetBytes("ConfirmationKeys"), 32);
            byte[] kcA = new byte[16], kcB = new byte[16];
            Array.Copy(kc, 0, kcA, 0, 16);
            Array.Copy(kc, 16, kcB, 0, 16);
            Ke = ke;
            KcA = kcA; KcB = kcB;
            using (var ma = new HMACSHA256(kcA))
            using (var mb = new HMACSHA256(kcB))
            {
                byte[] cA = ma.ComputeHash(t), cB = mb.ComputeHash(t);
                ConfirmOut = isA ? cA : cB;
                confirmExpected = isA ? cB : cA;
            }
            return true;
        }

        public byte[] KcA, KcB;

        public bool CheckConfirm(byte[] peerConfirm)
        {
            return confirmExpected != null && peerConfirm != null && Const.Equal(confirmExpected, peerConfirm);
        }

        public static byte[] Hkdf(byte[] ikm, byte[] salt, byte[] info, int len)
        {
            byte[] prk;
            using (var h = new HMACSHA256(salt ?? new byte[32])) prk = h.ComputeHash(ikm);
            var res = new MemoryStream();
            byte[] t = new byte[0];
            using (var h = new HMACSHA256(prk))
                for (byte i = 1; res.Length < len; i++)
                {
                    var m = new MemoryStream();
                    m.Write(t, 0, t.Length); m.Write(info, 0, info.Length); m.WriteByte(i);
                    t = h.ComputeHash(m.ToArray());
                    res.Write(t, 0, t.Length);
                }
            byte[] r = new byte[len];
            Array.Copy(res.ToArray(), r, len);
            return r;
        }
    }

    public static class Const
    {
        public static bool Equal(byte[] a, byte[] b)
        {
            if (a.Length != b.Length) return false;
            int d = 0;
            for (int i = 0; i < a.Length; i++) d |= a[i] ^ b[i];
            return d == 0;
        }
    }
}
