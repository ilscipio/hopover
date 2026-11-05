using System;
using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

namespace Hopover.Link
{
    /// <summary>
    /// Reads and makes junctions and symbolic links. Repos need them: pnpm and yarn workspaces keep junctions in
    /// node_modules, "npm link" leaves junctions in %APPDATA%\npm. Only these two tags move; cloud placeholders and
    /// app execution aliases do not.
    /// </summary>
    public static class Reparse
    {
        const uint TagMount = 0xA0000003, TagSymlink = 0xA000000C;
        const uint FsctlGet = 0x000900A8, FsctlSet = 0x000900A4;
        const uint ReadAttrs = 0x80, GenericWrite = 0x40000000, ShareAll = 7, OpenExisting = 3;
        const uint BackupSemantics = 0x02000000, OpenReparse = 0x00200000;

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern SafeFileHandle CreateFileW(string name, uint access, uint share, IntPtr sec, uint disposition, uint flags, IntPtr template);

        [DllImport("kernel32.dll", SetLastError = true)]
        static extern bool DeviceIoControl(SafeFileHandle h, uint code, byte[] inBuf, int inSize, byte[] outBuf, int outSize, out int returned, IntPtr overlapped);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateSymbolicLinkW(string link, string target, int flags);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern bool CreateDirectoryW(string name, IntPtr sec);

        /// <summary>"J|target", "S|target" (absolute link), "R|target" (relative link), or null for anything else.</summary>
        public static string Read(string path)
        {
            using (var h = CreateFileW(LinkCopy.Lp(path), ReadAttrs, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparse, IntPtr.Zero))
            {
                if (h.IsInvalid) return null;
                byte[] b = new byte[16 * 1024];
                int n;
                if (!DeviceIoControl(h, FsctlGet, null, 0, b, b.Length, out n, IntPtr.Zero) || n < 16) return null;
                uint tag = BitConverter.ToUInt32(b, 0);
                if (tag != TagMount && tag != TagSymlink) return null;
                int off = BitConverter.ToUInt16(b, 8), len = BitConverter.ToUInt16(b, 10);
                int start = tag == TagMount ? 16 : 20;
                if (start + off + len > n) return null;
                string t = Encoding.Unicode.GetString(b, start + off, len);
                if (t.StartsWith(@"\??\")) t = t.Substring(4);
                if (t.StartsWith(@"UNC\")) t = @"\\" + t.Substring(4);
                if (t.Length == 0 || t.IndexOf('\t') >= 0 || t.IndexOf('\n') >= 0) return null;
                if (tag == TagMount) return "J|" + t;
                return ((BitConverter.ToUInt32(b, 16) & 1) != 0 ? "R|" : "S|") + t;
            }
        }

        /// <summary>Makes the link from a Read() value. Throws Win32Exception on failure.</summary>
        public static void Create(string path, string value, bool isDir)
        {
            string kind = value.Substring(0, 1), target = value.Substring(2);
            string lp = LinkCopy.Lp(path);
            if (kind != "J")
            {
                // 2 = SYMBOLIC_LINK_FLAG_ALLOW_UNPRIVILEGED_CREATE. The engine runs elevated, so it works without Developer Mode too.
                if (!CreateSymbolicLinkW(lp, target, (isDir ? 1 : 0) | 2)) throw new Win32Exception();
                return;
            }
            if (!CreateDirectoryW(lp, IntPtr.Zero)) throw new Win32Exception();
            string subst = @"\??\" + target;
            byte[] s = Encoding.Unicode.GetBytes(subst), p = Encoding.Unicode.GetBytes(target);
            int data = 8 + s.Length + 2 + p.Length + 2;
            byte[] b = new byte[8 + data];
            BitConverter.GetBytes(TagMount).CopyTo(b, 0);
            BitConverter.GetBytes((ushort)data).CopyTo(b, 4);
            BitConverter.GetBytes((ushort)0).CopyTo(b, 8);
            BitConverter.GetBytes((ushort)s.Length).CopyTo(b, 10);
            BitConverter.GetBytes((ushort)(s.Length + 2)).CopyTo(b, 12);
            BitConverter.GetBytes((ushort)p.Length).CopyTo(b, 14);
            s.CopyTo(b, 16);
            p.CopyTo(b, 16 + s.Length + 2);
            using (var h = CreateFileW(lp, GenericWrite, ShareAll, IntPtr.Zero, OpenExisting, BackupSemantics | OpenReparse, IntPtr.Zero))
            {
                int n;
                if (h.IsInvalid || !DeviceIoControl(h, FsctlSet, b, b.Length, null, 0, out n, IntPtr.Zero))
                {
                    var e = new Win32Exception();
                    try { System.IO.Directory.Delete(lp); } catch (Exception) { }
                    throw e;
                }
            }
        }
    }
}
