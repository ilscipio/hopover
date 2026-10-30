using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace Hopover.App
{
    /// <summary>The coffee pledge: signed on first start, paid on the honour system. Stored in %LOCALAPPDATA%\Hopover\pledge.json.</summary>
    static class Pledge
    {
        static readonly string FilePath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hopover", "pledge.json");

        /// <summary>The screenshot run sets this: sample state, no file access.</summary>
        public static bool Sample;

        public static bool Signed, Paid;
        public static string SignedAt = "", PaidAt = "";

        public static void Load()
        {
            if (Sample) return;
            var j = Json.Read(FilePath);
            Signed = Json.Get(j, "signed") is bool s && s;
            Paid = Json.Get(j, "paid") is bool p && p;
            SignedAt = Json.Str(j, "signedAt") ?? "";
            PaidAt = Json.Str(j, "paidAt") ?? "";
        }

        static void Save()
        {
            if (Sample) return;
            try
            {
                Json.Write(FilePath, new Dictionary<string, object> { ["signed"] = Signed, ["signedAt"] = SignedAt, ["paid"] = Paid, ["paidAt"] = PaidAt });
            }
            catch { }
        }

        public static void Sign() { Signed = true; SignedAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"); Save(); }
        public static void MarkPaid() { Paid = true; PaidAt = DateTime.Now.ToString("yyyy-MM-dd HH:mm"); Save(); }

        /// <summary>A ticket number that stays the same for this PC.</summary>
        public static string TicketNo
        {
            get
            {
                using (var md5 = MD5.Create())
                {
                    var h = md5.ComputeHash(Encoding.UTF8.GetBytes(Environment.MachineName));
                    return "HOP-" + DateTime.Now.Year + "-" + (((h[0] << 8) | h[1]) % 10000).ToString("0000");
                }
            }
        }

        /// <summary>Opens a URL. A placeholder (starts with "[") opens nothing and returns false.</summary>
        public static bool OpenUrl(string url, string what, Action<string> log)
        {
            if (string.IsNullOrEmpty(url) || url.StartsWith("["))
            {
                log?.Invoke("Branding: " + what + " is not set yet (" + url + "); nothing opened.");
                return false;
            }
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); return true; }
            catch (Exception ex) { log?.Invoke("Could not open " + what + ": " + ex.Message); return false; }
        }
    }
}
