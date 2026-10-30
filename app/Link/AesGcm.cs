using System;
using System.Runtime.InteropServices;
using System.Text;

namespace Hopover.Link
{
    /// <summary>AES-GCM through the Windows CNG (bcrypt.dll). .NET Framework 4.8 has no AesGcm class.</summary>
    public sealed class AesGcm : IDisposable
    {
        const uint STATUS_AUTH_TAG_MISMATCH = 0xC000A002;

        [StructLayout(LayoutKind.Sequential)]
        struct AuthInfo
        {
            public int cbSize;
            public int dwInfoVersion;
            public IntPtr pbNonce;
            public int cbNonce;
            public IntPtr pbAuthData;
            public int cbAuthData;
            public IntPtr pbTag;
            public int cbTag;
            public IntPtr pbMacContext;
            public int cbMacContext;
            public int cbAAD;
            public long cbData;
            public int dwFlags;
        }

        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern uint BCryptOpenAlgorithmProvider(out IntPtr h, string alg, string impl, uint flags);
        [DllImport("bcrypt.dll", CharSet = CharSet.Unicode)]
        static extern uint BCryptSetProperty(IntPtr h, string prop, byte[] input, int len, uint flags);
        [DllImport("bcrypt.dll")]
        static extern uint BCryptGenerateSymmetricKey(IntPtr alg, out IntPtr key, IntPtr obj, int objLen, byte[] secret, int secretLen, uint flags);
        [DllImport("bcrypt.dll")]
        static extern uint BCryptEncrypt(IntPtr key, IntPtr input, int inLen, ref AuthInfo info, IntPtr iv, int ivLen, IntPtr output, int outLen, out int result, uint flags);
        [DllImport("bcrypt.dll")]
        static extern uint BCryptDecrypt(IntPtr key, IntPtr input, int inLen, ref AuthInfo info, IntPtr iv, int ivLen, IntPtr output, int outLen, out int result, uint flags);
        [DllImport("bcrypt.dll")]
        static extern uint BCryptDestroyKey(IntPtr key);
        [DllImport("bcrypt.dll")]
        static extern uint BCryptCloseAlgorithmProvider(IntPtr h, uint flags);

        IntPtr alg, key;

        public AesGcm(byte[] keyBytes)
        {
            Check(BCryptOpenAlgorithmProvider(out alg, "AES", null, 0));
            byte[] mode = Encoding.Unicode.GetBytes("ChainingModeGCM\0");
            Check(BCryptSetProperty(alg, "ChainingMode", mode, mode.Length, 0));
            Check(BCryptGenerateSymmetricKey(alg, out key, IntPtr.Zero, 0, keyBytes, keyBytes.Length, 0));
        }

        static void Check(uint status)
        {
            if (status != 0) throw new InvalidOperationException("bcrypt error 0x" + status.ToString("x8"));
        }

        static IntPtr At(GCHandle h, byte[] a, int off) { return Marshal.UnsafeAddrOfPinnedArrayElement(a, off); }

        /// <summary>Encrypts src[so..so+len) into dst[doff..doff+len) and writes the 16-byte tag to tag[toff..].</summary>
        public void Encrypt(byte[] nonce, byte[] aad, byte[] src, int so, int len, byte[] dst, int doff, byte[] tag, int toff)
        {
            Run(true, nonce, aad, src, so, len, dst, doff, tag, toff);
        }

        /// <summary>Decrypts and checks the tag. Returns false when the tag does not match.</summary>
        public bool Decrypt(byte[] nonce, byte[] aad, byte[] src, int so, int len, byte[] dst, int doff, byte[] tag, int toff)
        {
            return Run(false, nonce, aad, src, so, len, dst, doff, tag, toff);
        }

        bool Run(bool enc, byte[] nonce, byte[] aad, byte[] src, int so, int len, byte[] dst, int doff, byte[] tag, int toff)
        {
            // A zero-length array has no element to pin: use one spare byte.
            if (src.Length == 0) src = new byte[1];
            if (dst.Length == 0) dst = new byte[1];
            if (aad == null) aad = new byte[0];
            byte[] aadp = aad.Length == 0 ? new byte[1] : aad;
            var hs = GCHandle.Alloc(src, GCHandleType.Pinned);
            var hd = GCHandle.Alloc(dst, GCHandleType.Pinned);
            var hn = GCHandle.Alloc(nonce, GCHandleType.Pinned);
            var ha = GCHandle.Alloc(aadp, GCHandleType.Pinned);
            var ht = GCHandle.Alloc(tag, GCHandleType.Pinned);
            try
            {
                var info = new AuthInfo
                {
                    cbSize = Marshal.SizeOf(typeof(AuthInfo)),
                    dwInfoVersion = 1,
                    pbNonce = hn.AddrOfPinnedObject(), cbNonce = nonce.Length,
                    pbAuthData = aad.Length == 0 ? IntPtr.Zero : ha.AddrOfPinnedObject(), cbAuthData = aad.Length,
                    pbTag = Marshal.UnsafeAddrOfPinnedArrayElement(tag, toff), cbTag = 16,
                };
                int got;
                IntPtr ip = len == 0 ? hs.AddrOfPinnedObject() : Marshal.UnsafeAddrOfPinnedArrayElement(src, so);
                IntPtr op = len == 0 ? hd.AddrOfPinnedObject() : Marshal.UnsafeAddrOfPinnedArrayElement(dst, doff);
                uint st = enc ? BCryptEncrypt(key, ip, len, ref info, IntPtr.Zero, 0, op, len, out got, 0)
                              : BCryptDecrypt(key, ip, len, ref info, IntPtr.Zero, 0, op, len, out got, 0);
                if (st == STATUS_AUTH_TAG_MISMATCH && !enc) return false;
                Check(st);
                return true;
            }
            finally { hs.Free(); hd.Free(); hn.Free(); ha.Free(); ht.Free(); }
        }

        public void Dispose()
        {
            if (key != IntPtr.Zero) { BCryptDestroyKey(key); key = IntPtr.Zero; }
            if (alg != IntPtr.Zero) { BCryptCloseAlgorithmProvider(alg, 0); alg = IntPtr.Zero; }
        }
    }
}
