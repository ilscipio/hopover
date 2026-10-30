using System;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Windows;
using System.Windows.Markup;

namespace Hopover.App
{
    static class Win32
    {
        [DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int value, int size);

        public static bool IsAdmin()
        {
            using (var id = WindowsIdentity.GetCurrent())
                return new WindowsPrincipal(id).IsInRole(WindowsBuiltInRole.Administrator);
        }

        /// <summary>Windows 11: rounded corners, dark frame. Older Windows ignores the calls.</summary>
        public static void StyleWindow(IntPtr hwnd)
        {
            int dark = 0, round = 1, caption = 0x00DDDCDC, border = 0x00131211;
            DwmSetWindowAttribute(hwnd, 20, ref dark, 4);     // DWMWA_USE_IMMERSIVE_DARK_MODE
            DwmSetWindowAttribute(hwnd, 33, ref round, 4);    // DWMWA_WINDOW_CORNER_PREFERENCE
            DwmSetWindowAttribute(hwnd, 34, ref border, 4);   // DWMWA_BORDER_COLOR
            DwmSetWindowAttribute(hwnd, 35, ref caption, 4);  // DWMWA_CAPTION_COLOR
        }

        public static bool IsLocalFixedPath(string path)
        {
            try
            {
                if (path.StartsWith(@"\\")) return false;
                return new DriveInfo(Path.GetPathRoot(path)).DriveType == DriveType.Fixed;
            }
            catch { return false; }
        }
    }

    static class Res
    {
        static string fontDir;

        public static T Load<T>(string name) where T : class
        {
            using (var s = Assembly.GetExecutingAssembly().GetManifestResourceStream("ui/" + name))
            {
                if (name != "Theme.xaml") return (T)XamlReader.Load(s);
                // Theme.xaml names its fonts by folder; the folder is known only at run time.
                if (fontDir == null) fontDir = Fonts.Install();
                string xaml = new StreamReader(s, System.Text.Encoding.UTF8).ReadToEnd().Replace("@FONTDIR@", fontDir);
                return (T)XamlReader.Parse(xaml);
            }
        }
    }
}
