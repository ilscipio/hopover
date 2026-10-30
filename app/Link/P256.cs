using System;
using System.Numerics;
using System.Text;

namespace Hopover.Link
{
    /// <summary>NIST P-256 on System.Numerics.BigInteger. Variable time: fine for a one-time pairing code, not for a long-lived key.</summary>
    public static class P256
    {
        public static readonly BigInteger P = Hex("FFFFFFFF00000001000000000000000000000000FFFFFFFFFFFFFFFFFFFFFFFF");
        public static readonly BigInteger N = Hex("FFFFFFFF00000000FFFFFFFFFFFFFFFFBCE6FAADA7179E84F3B9CAC2FC632551");
        static readonly BigInteger B = Hex("5AC635D8AA3A93E7B3EBBD55769886BC651D06B0CC53B0F63BCE3C3E27D2604B");
        public static readonly Pt G = new Pt(Hex("6B17D1F2E12C4247F8BCE6E563A440F277037D812DEB33A0F4A13945D898C296"),
                                             Hex("4FE342E2FE1A7F9B8EE7EB4A7C0F9E162BCE33576B315ECECBB6406837BF51F5"));

        public sealed class Pt
        {
            public readonly BigInteger X, Y;
            public readonly bool Inf;
            public Pt(BigInteger x, BigInteger y) { X = x; Y = y; }
            Pt() { Inf = true; }
            public static readonly Pt Infinity = new Pt();
        }

        public static BigInteger Hex(string h) { return FromBytes(HexBytes(h)); }

        public static byte[] HexBytes(string h)
        {
            h = h.Replace(" ", "");
            byte[] r = new byte[h.Length / 2];
            for (int i = 0; i < r.Length; i++) r[i] = Convert.ToByte(h.Substring(2 * i, 2), 16);
            return r;
        }

        public static string ToHex(byte[] b)
        {
            var sb = new StringBuilder();
            foreach (byte x in b) sb.Append(x.ToString("x2"));
            return sb.ToString();
        }

        public static BigInteger FromBytes(byte[] be)
        {
            byte[] le = new byte[be.Length + 1];
            for (int i = 0; i < be.Length; i++) le[i] = be[be.Length - 1 - i];
            return new BigInteger(le);
        }

        public static byte[] ToBytes32(BigInteger v)
        {
            byte[] le = v.ToByteArray();
            byte[] r = new byte[32];
            for (int i = 0; i < 32 && i < le.Length; i++) r[31 - i] = le[i];
            return r;
        }

        static BigInteger Mod(BigInteger a) { a %= P; return a.Sign < 0 ? a + P : a; }
        static BigInteger Inv(BigInteger a) { return BigInteger.ModPow(Mod(a), P - 2, P); }

        public static bool OnCurve(Pt p)
        {
            if (p.Inf) return false;
            if (p.X.Sign < 0 || p.X >= P || p.Y.Sign < 0 || p.Y >= P) return false;
            return Mod(p.Y * p.Y) == Mod(p.X * p.X * p.X - 3 * p.X + B);
        }

        // Jacobian coordinates: (X, Y, Z) is the affine point (X/Z^2, Y/Z^3); Z = 0 is infinity.
        struct J { public BigInteger X, Y, Z; }

        static J Zero() { return new J { X = 1, Y = 1, Z = 0 }; }
        static J ToJ(Pt p) { return p.Inf ? Zero() : new J { X = p.X, Y = p.Y, Z = 1 }; }

        static Pt FromJ(J j)
        {
            if (j.Z.IsZero) return Pt.Infinity;
            BigInteger zi = Inv(j.Z), zi2 = Mod(zi * zi);
            return new Pt(Mod(j.X * zi2), Mod(j.Y * zi2 * zi));
        }

        static J Dbl(J p)
        {
            if (p.Z.IsZero || p.Y.IsZero) return Zero();
            BigInteger delta = Mod(p.Z * p.Z), gamma = Mod(p.Y * p.Y), beta = Mod(p.X * gamma);
            BigInteger alpha = Mod(3 * (p.X - delta) * (p.X + delta));
            BigInteger x3 = Mod(alpha * alpha - 8 * beta);
            BigInteger z3 = Mod((p.Y + p.Z) * (p.Y + p.Z) - gamma - delta);
            BigInteger y3 = Mod(alpha * (4 * beta - x3) - 8 * gamma * gamma);
            return new J { X = x3, Y = y3, Z = z3 };
        }

        static J AddJ(J a, J b)
        {
            if (a.Z.IsZero) return b;
            if (b.Z.IsZero) return a;
            BigInteger z1z1 = Mod(a.Z * a.Z), z2z2 = Mod(b.Z * b.Z);
            BigInteger u1 = Mod(a.X * z2z2), u2 = Mod(b.X * z1z1);
            BigInteger s1 = Mod(a.Y * b.Z * z2z2), s2 = Mod(b.Y * a.Z * z1z1);
            BigInteger h = Mod(u2 - u1), r = Mod(s2 - s1);
            if (h.IsZero) return r.IsZero ? Dbl(a) : Zero();
            BigInteger i = Mod(4 * h * h), j = Mod(h * i);
            r = Mod(2 * r);
            BigInteger v = Mod(u1 * i);
            BigInteger x3 = Mod(r * r - j - 2 * v);
            BigInteger y3 = Mod(r * (v - x3) - 2 * s1 * j);
            BigInteger z3 = Mod(((a.Z + b.Z) * (a.Z + b.Z) - z1z1 - z2z2) * h);
            return new J { X = x3, Y = y3, Z = z3 };
        }

        public static Pt Mul(BigInteger k, Pt p)
        {
            k %= N;
            if (k.Sign < 0) k += N;
            J acc = Zero(), q = ToJ(p);
            byte[] bytes = ToBytes32(k);
            for (int i = 0; i < 256; i++)
            {
                acc = Dbl(acc);
                if (((bytes[i >> 3] >> (7 - (i & 7))) & 1) != 0) acc = AddJ(acc, q);
            }
            return FromJ(acc);
        }

        public static Pt Add(Pt a, Pt b) { return FromJ(AddJ(ToJ(a), ToJ(b))); }
        public static Pt Neg(Pt a) { return a.Inf ? a : new Pt(a.X, Mod(-a.Y)); }

        /// <summary>SEC1 uncompressed encoding (65 bytes).</summary>
        public static byte[] Encode(Pt p)
        {
            byte[] r = new byte[65];
            r[0] = 4;
            Array.Copy(ToBytes32(p.X), 0, r, 1, 32);
            Array.Copy(ToBytes32(p.Y), 0, r, 33, 32);
            return r;
        }

        /// <summary>Decodes a SEC1 point (uncompressed or compressed) and checks it is on the curve. Returns null otherwise.</summary>
        public static Pt Decode(byte[] b)
        {
            if (b == null) return null;
            if (b.Length == 65 && b[0] == 4)
            {
                byte[] x = new byte[32], y = new byte[32];
                Array.Copy(b, 1, x, 0, 32);
                Array.Copy(b, 33, y, 0, 32);
                var p = new Pt(FromBytes(x), FromBytes(y));
                return OnCurve(p) ? p : null;
            }
            if (b.Length == 33 && (b[0] == 2 || b[0] == 3))
            {
                byte[] x = new byte[32];
                Array.Copy(b, 1, x, 0, 32);
                BigInteger xv = FromBytes(x);
                if (xv >= P) return null;
                BigInteger rhs = Mod(xv * xv * xv - 3 * xv + B);
                BigInteger y = BigInteger.ModPow(rhs, (P + 1) / 4, P);
                if (Mod(y * y) != rhs) return null;
                if (((int)(y & 1)) != (b[0] & 1)) y = Mod(-y);
                return new Pt(xv, y);
            }
            return null;
        }
    }
}
