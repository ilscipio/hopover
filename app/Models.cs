using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text;
using System.Web.Script.Serialization;

namespace Hopover.App
{
    public abstract class Bindable : INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler PropertyChanged;

        protected void Set<T>(ref T field, T value, [CallerMemberName] string name = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value)) return;
            field = value;
            Raise(name);
        }

        protected void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>One top-level folder on the scan page (all chunks of C:\java are one row).</summary>
    public class GroupRow : Bindable
    {
        bool include = true;
        double barRatio;
        public bool Include { get => include; set => Set(ref include, value); }
        public string Path { get; set; }
        public string Name { get; set; }
        public string Why { get; set; }
        public long Bytes { get; set; }
        public long Files { get; set; }
        public double BarRatio { get => barRatio; set => Set(ref barRatio, value); }
        public string SizeText => Fmt.Size(Bytes);
        /// <summary>The crate code: HOP- and a running number.</summary>
        public string Code { get; set; }
        public string Sub => WhyText + "  ·  " + Path;

        public string WhyText
        {
            get
            {
                switch (Why)
                {
                    case "profile": return "Profile";
                    case "appdata-roaming":
                    case "appdata-local": return "App data";
                    case "c-root": return "Folder on C:";
                    case "drive": return "Drive";
                    case "keepass": return "KeePass";
                    case "program": return "Program without installer";
                    case "programdata": return "Program data";
                    case "cache": return "Cache (only time)";
                    case "extra": return "Folder";
                    default: return Why ?? "";
                }
            }
        }
    }

    public class StepRow : Bindable
    {
        string state = "pending";
        string detail = "";
        public string Key { get; set; }
        public string No { get; set; }
        public string Label { get; set; }
        public bool IsLast { get; set; }
        public string State { get => state; set => Set(ref state, value); }
        public string Detail { get => detail; set => Set(ref detail, value); }
    }

    public class ActiveRow
    {
        public int Index { get; set; }
        public string Path { get; set; }
        public string SizeText { get; set; }
        public string Code => "HOP-" + (Index + 1).ToString("0000");
    }

    /// <summary>One finished app install, from the engine event "##APP id|name".</summary>
    public class AppRow
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Check { get; set; }
        /// <summary>The Start menu shortcut, or null when the app cannot be found: then there is no Open button.</summary>
        public string Launch { get; set; }
        public bool CanOpen => Launch != null;
    }

    public class ReceiptLine
    {
        public string What { get; set; }
        public string N { get; set; }
    }

    public class CheckRow : Bindable
    {
        bool done;
        public string Text { get; set; }
        public string Detail { get; set; }
        public bool HasDetail => !string.IsNullOrEmpty(Detail);
        public bool IsHeader { get; set; }
        public bool Done { get => done; set => Set(ref done, value); }
    }

    public static class Fmt
    {
        public static string Size(double bytes)
        {
            if (bytes >= 1L << 40) return (bytes / (1L << 40)).ToString("0.0") + " TB";
            if (bytes >= 1L << 30) return (bytes / (1L << 30)).ToString(bytes >= 100.0 * (1L << 30) ? "0" : "0.0") + " GB";
            if (bytes >= 1L << 20) return (bytes / (1L << 20)).ToString("0") + " MB";
            if (bytes >= 1L << 10) return (bytes / (1L << 10)).ToString("0") + " KB";
            return ((long)bytes) + " B";
        }

        public static string Duration(double seconds)
        {
            if (double.IsNaN(seconds) || double.IsInfinity(seconds) || seconds < 0) return "";
            if (seconds < 90) return "about a minute";
            if (seconds < 3600) return "about " + Math.Round(seconds / 60) + " min";
            double h = seconds / 3600;
            return "about " + h.ToString(h < 10 ? "0.0" : "0") + " h";
        }
    }

    /// <summary>Small JSON access over JavaScriptSerializer (part of .NET Framework).</summary>
    public static class Json
    {
        static readonly JavaScriptSerializer Ser = new JavaScriptSerializer { MaxJsonLength = int.MaxValue, RecursionLimit = 256 };

        public static object Read(string path)
        {
            try
            {
                if (!File.Exists(path)) return null;
                string text = File.ReadAllText(path, Encoding.UTF8).Trim('\uFEFF', ' ', '\r', '\n', '\t');
                return text.Length == 0 ? null : Ser.DeserializeObject(text);
            }
            catch { return null; }
        }

        public static object Parse(byte[] utf8)
        {
            try
            {
                string text = Encoding.UTF8.GetString(utf8).Trim('\uFEFF', ' ', '\r', '\n', '\t');
                return text.Length == 0 ? null : Ser.DeserializeObject(text);
            }
            catch { return null; }
        }

        public static void Write(string path, object value)
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(path));
            File.WriteAllText(path, Ser.Serialize(value), new UTF8Encoding(false));
        }

        public static object Get(object o, string key) =>
            o is IDictionary<string, object> d && d.TryGetValue(key, out var v) ? v : null;

        public static string Str(object o, string key) => Get(o, key)?.ToString();

        public static long Long(object o, string key)
        {
            var v = Get(o, key);
            if (v == null) return 0;
            try { return Convert.ToInt64(v); } catch { return 0; }
        }

        public static IEnumerable<object> List(object o)
        {
            if (o is IEnumerable e && !(o is string) && !(o is IDictionary<string, object>))
                foreach (var x in e) yield return x;
            else if (o != null) yield return o;
        }
    }
}
