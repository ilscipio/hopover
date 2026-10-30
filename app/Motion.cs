using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Path = System.IO.Path;
using ShapePath = System.Windows.Shapes.Path;

namespace Hopover.App
{
    /// <summary>Motion rules of the design: crouch, lift, land. One ease for every move. No animation when Windows asks for reduced motion.</summary>
    static class Motion
    {
        /// <summary>False when the user switched animations off in Windows (reduced motion).</summary>
        public static bool Enabled => SystemParameters.ClientAreaAnimation;

        public static readonly KeySpline Ease = new KeySpline(0.45, 0, 0.2, 1);

        public static readonly Color Ink = Color.FromRgb(0x11, 0x12, 0x13);
        public static readonly Color Signal = Color.FromRgb(0xFF, 0x5A, 0x1F);
        public static readonly Color Ground = Color.FromRgb(0xDC, 0xDD, 0xDD);
        public static readonly Color Steel = Color.FromRgb(0x54, 0x57, 0x5B);

        public static SplineDoubleKeyFrame KF(double sec, double v) =>
            new SplineDoubleKeyFrame(v, KeyTime.FromTimeSpan(TimeSpan.FromSeconds(sec)), Ease);

        /// <summary>Hard-edged diagonal stripes: ink and signal, half a period each.</summary>
        public static LinearGradientBrush Hazard(double period = 28)
        {
            double d = period / Math.Sqrt(2);
            var b = new LinearGradientBrush
            {
                MappingMode = BrushMappingMode.Absolute,
                SpreadMethod = GradientSpreadMethod.Repeat,
                StartPoint = new Point(0, 0),
                EndPoint = new Point(d, d),
            };
            b.GradientStops.Add(new GradientStop(Ink, 0));
            b.GradientStops.Add(new GradientStop(Ink, 0.5));
            b.GradientStops.Add(new GradientStop(Signal, 0.5));
            b.GradientStops.Add(new GradientStop(Signal, 1));
            return b;
        }

        static KeyTime Ms(double ms) => KeyTime.FromTimeSpan(TimeSpan.FromMilliseconds(ms));

        /// <summary>The stamp lands: 380 ms, overshoot only here. The receipt shakes once.</summary>
        public static void DropStamp(FrameworkElement stamp, FrameworkElement receipt, double restAngle, double receiptAngle)
        {
            var scale = new ScaleTransform(1, 1);
            var rot = new RotateTransform(restAngle);
            var g = new TransformGroup();
            g.Children.Add(scale);
            g.Children.Add(rot);
            stamp.RenderTransformOrigin = new Point(0.5, 0.5);
            stamp.RenderTransform = g;
            var shake = new TranslateTransform();
            if (receipt != null)
            {
                var rg = new TransformGroup();
                rg.Children.Add(new RotateTransform(receiptAngle));
                rg.Children.Add(shake);
                receipt.RenderTransformOrigin = new Point(0.5, 0.5);
                receipt.RenderTransform = rg;
            }
            if (!Enabled) return;

            var stampEase = new KeySpline(0.2, 1.4, 0.4, 1);
            SplineDoubleKeyFrame SK(double ms, double v) => new SplineDoubleKeyFrame(v, Ms(ms), stampEase);
            var sc = new DoubleAnimationUsingKeyFrames();
            sc.KeyFrames.Add(new DiscreteDoubleKeyFrame(2.4, Ms(0)));
            sc.KeyFrames.Add(SK(230, 0.96));
            sc.KeyFrames.Add(SK(300, 1.02));
            sc.KeyFrames.Add(SK(380, 1));
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, sc);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, sc.Clone());
            var an = new DoubleAnimationUsingKeyFrames();
            an.KeyFrames.Add(new DiscreteDoubleKeyFrame(restAngle + 10, Ms(0)));
            an.KeyFrames.Add(SK(230, restAngle));
            an.KeyFrames.Add(SK(380, restAngle));
            rot.BeginAnimation(RotateTransform.AngleProperty, an);
            var op = new DoubleAnimationUsingKeyFrames();
            op.KeyFrames.Add(new DiscreteDoubleKeyFrame(0, Ms(0)));
            op.KeyFrames.Add(new LinearDoubleKeyFrame(1, Ms(120)));
            stamp.BeginAnimation(UIElement.OpacityProperty, op);
            if (receipt != null)
            {
                var sx = new DoubleAnimationUsingKeyFrames();
                sx.KeyFrames.Add(new LinearDoubleKeyFrame(0, Ms(230)));
                sx.KeyFrames.Add(new LinearDoubleKeyFrame(-3, Ms(260)));
                sx.KeyFrames.Add(new LinearDoubleKeyFrame(2, Ms(300)));
                sx.KeyFrames.Add(new LinearDoubleKeyFrame(0, Ms(340)));
                var sy = new DoubleAnimationUsingKeyFrames();
                sy.KeyFrames.Add(new LinearDoubleKeyFrame(0, Ms(230)));
                sy.KeyFrames.Add(new LinearDoubleKeyFrame(2, Ms(260)));
                sy.KeyFrames.Add(new LinearDoubleKeyFrame(-1, Ms(300)));
                sy.KeyFrames.Add(new LinearDoubleKeyFrame(0, Ms(340)));
                shake.BeginAnimation(TranslateTransform.XProperty, sx);
                shake.BeginAnimation(TranslateTransform.YProperty, sy);
            }
        }
    }

    /// <summary>Static font files are unpacked from the exe once; WPF loads them by file URI.</summary>
    static class Fonts
    {
        public static string Dir =>
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Hopover", "fonts");

        /// <summary>Returns the font folder as a file URI that ends in a slash, for Theme.xaml.</summary>
        public static string Install()
        {
            string dir = Dir;
            try
            {
                Directory.CreateDirectory(dir);
                var asm = Assembly.GetExecutingAssembly();
                foreach (var name in asm.GetManifestResourceNames())
                {
                    if (!name.StartsWith("fonts/")) continue;
                    string target = Path.Combine(dir, name.Substring(6));
                    using (var s = asm.GetManifestResourceStream(name))
                    {
                        var bytes = new byte[s.Length];
                        int got = 0;
                        while (got < bytes.Length) { int n = s.Read(bytes, got, bytes.Length - got); if (n <= 0) break; got += n; }
                        if (File.Exists(target) && File.ReadAllBytes(target).SequenceEqual(bytes)) continue;
                        // A second running copy may hold the file: then the old one is fine.
                        try { File.WriteAllBytes(target, bytes); } catch { }
                    }
                }
            }
            catch { }
            return new Uri(dir + "\\").AbsoluteUri;
        }
    }

    /// <summary>The mark with its ground line: the stem stays, the arch hops, the shadow shrinks at the top.</summary>
    public class HopMark : Viewbox
    {
        public Brush Stem { get; set; } = new SolidColorBrush(Motion.Ink);
        public Brush Arch { get; set; } = new SolidColorBrush(Motion.Signal);
        public Brush Shadow { get; set; } = new SolidColorBrush(Motion.Ink);
        public bool Animate { get; set; }

        bool built;

        public HopMark() { Loaded += (s, e) => Build(); }

        void Build()
        {
            if (built) return;
            built = true;
            Stretch = Stretch.Uniform;
            // viewBox "0 -20 100 120": the hop has 20 units of room above the stem.
            var c = new Canvas { Width = 100, Height = 120 };
            var stem = new Rectangle { Width = 18, Height = 84, Fill = Stem };
            Canvas.SetLeft(stem, 14); Canvas.SetTop(stem, 28);
            var shadow = new Rectangle { Width = 49, Height = 3, Fill = Shadow, Opacity = 0.45, RenderTransformOrigin = new Point(0.5, 0.5) };
            var shadowScale = new ScaleTransform(1, 1);
            shadow.RenderTransform = shadowScale;
            Canvas.SetLeft(shadow, 38); Canvas.SetTop(shadow, 110);
            var arch = new ShapePath { Data = Geometry.Parse("M45,104 V76 A17,17 0 0 1 79,76 V104"), Stroke = Arch, StrokeThickness = 16 };
            var scale = new ScaleTransform(1, 1, 62, 104);
            var move = new TranslateTransform();
            var g = new TransformGroup();
            g.Children.Add(scale);
            g.Children.Add(move);
            arch.RenderTransform = g;
            c.Children.Add(stem); c.Children.Add(shadow); c.Children.Add(arch);
            Child = c;
            if (!Animate || !Motion.Enabled) return;

            var dur = TimeSpan.FromSeconds(1.4);
            double[] t = { 0, 0.196, 0.42, 0.7, 1.008, 1.176, 1.4 };
            double[] y = { 8, 8, -6, -16, 2, 8, 8 };
            double[] sx = { 1.06, 1.08, 0.96, 1, 1, 1.06, 1.06 };
            double[] sy = { 0.9, 0.86, 1.05, 1, 1.02, 0.9, 0.9 };
            var ty = new DoubleAnimationUsingKeyFrames { Duration = dur, RepeatBehavior = RepeatBehavior.Forever };
            var ax = new DoubleAnimationUsingKeyFrames { Duration = dur, RepeatBehavior = RepeatBehavior.Forever };
            var ay = new DoubleAnimationUsingKeyFrames { Duration = dur, RepeatBehavior = RepeatBehavior.Forever };
            for (int i = 0; i < t.Length; i++)
            {
                ty.KeyFrames.Add(Motion.KF(t[i], y[i]));
                ax.KeyFrames.Add(Motion.KF(t[i], sx[i]));
                ay.KeyFrames.Add(Motion.KF(t[i], sy[i]));
            }
            move.BeginAnimation(TranslateTransform.YProperty, ty);
            scale.BeginAnimation(ScaleTransform.ScaleXProperty, ax);
            scale.BeginAnimation(ScaleTransform.ScaleYProperty, ay);

            var shx = new DoubleAnimationUsingKeyFrames { Duration = dur, RepeatBehavior = RepeatBehavior.Forever };
            var sho = new DoubleAnimationUsingKeyFrames { Duration = dur, RepeatBehavior = RepeatBehavior.Forever };
            double[] st = { 0, 0.196, 0.7, 1.176, 1.4 };
            double[] ssx = { 1, 1, 0.55, 1, 1 };
            double[] sso = { 0.45, 0.45, 0.15, 0.45, 0.45 };
            for (int i = 0; i < st.Length; i++)
            {
                shx.KeyFrames.Add(Motion.KF(st[i], ssx[i]));
                sho.KeyFrames.Add(Motion.KF(st[i], sso[i]));
            }
            shadowScale.BeginAnimation(ScaleTransform.ScaleXProperty, shx);
            shadow.BeginAnimation(UIElement.OpacityProperty, sho);
        }
    }

    /// <summary>Diagonal ink and signal stripes that slide while data moves. Still when motion is reduced.</summary>
    public class HazardBar : Border
    {
        public double Period { get; set; } = 28;

        bool started;

        public HazardBar() { Loaded += (s, e) => Start(); }

        void Start()
        {
            if (started) return;
            started = true;
            var b = Motion.Hazard(Period);
            var tt = new TranslateTransform();
            b.Transform = tt;
            Background = b;
            if (Motion.Enabled)
                tt.BeginAnimation(TranslateTransform.XProperty,
                    new DoubleAnimation(0, Period * Math.Sqrt(2), TimeSpan.FromMilliseconds(900)) { RepeatBehavior = RepeatBehavior.Forever });
        }
    }

    /// <summary>The hop line: a dashed arc between two stems. One crate rides it every 2.4 s. Reduced motion: a plain bar.</summary>
    public class HopLoader : Grid
    {
        public Brush StemFrom { get; set; } = new SolidColorBrush(Motion.Ink);
        public Brush Dash { get; set; } = new SolidColorBrush(Motion.Steel);

        bool built;

        public HopLoader() { Loaded += (s, e) => Build(); }

        void Build()
        {
            if (built) return;
            built = true;
            if (!Motion.Enabled)
            {
                Children.Add(new Border { Height = 8, Background = Motion.Hazard(12), VerticalAlignment = VerticalAlignment.Center });
                return;
            }
            var vb = new Viewbox { Stretch = Stretch.Uniform };
            var c = new Canvas { Width = 520, Height = 120 };
            var from = new Rectangle { Width = 16, Height = 32, Fill = StemFrom };
            Canvas.SetLeft(from, 8); Canvas.SetTop(from, 80);
            var to = new Rectangle { Width = 16, Height = 32, Fill = new SolidColorBrush(Motion.Signal) };
            Canvas.SetLeft(to, 496); Canvas.SetTop(to, 80);
            var arc = new ShapePath { Data = Geometry.Parse("M24,92 Q260,-40 496,92"), Stroke = Dash, StrokeThickness = 2, StrokeDashArray = new DoubleCollection { 5, 5 } };
            var ground = new Line { X1 = 0, X2 = 520, Y1 = 112, Y2 = 112, Stroke = Dash, StrokeThickness = 1 };
            var crate = new Rectangle { Width = 18, Height = 18, Fill = new SolidColorBrush(Motion.Signal), RenderTransformOrigin = new Point(0.5, 0.5) };
            Canvas.SetLeft(crate, -9); Canvas.SetTop(crate, -9);
            var rot = new RotateTransform();
            var pos = new TranslateTransform();
            var g = new TransformGroup();
            g.Children.Add(rot);
            g.Children.Add(pos);
            crate.RenderTransform = g;
            c.Children.Add(ground); c.Children.Add(arc); c.Children.Add(from); c.Children.Add(to); c.Children.Add(crate);
            vb.Child = c;
            Children.Add(vb);

            arc.BeginAnimation(Shape.StrokeDashOffsetProperty,
                new DoubleAnimation(0, -10, TimeSpan.FromSeconds(1)) { RepeatBehavior = RepeatBehavior.Forever });

            // One loop of 2.4 s: the crate rides for 70 % of it and waits for the rest.
            var path = PathGeometry.CreateFromGeometry(Geometry.Parse("M24,92 Q260,-40 496,92"));
            var ride = TimeSpan.FromSeconds(1.68);
            DoubleAnimationUsingPath Ride(PathAnimationSource src) =>
                new DoubleAnimationUsingPath { PathGeometry = path, Source = src, Duration = ride, AccelerationRatio = 0.5, DecelerationRatio = 0.5 };
            var ax = Ride(PathAnimationSource.X);
            var ay = Ride(PathAnimationSource.Y);
            var aa = Ride(PathAnimationSource.Angle);
            var sb = new Storyboard { Duration = TimeSpan.FromSeconds(2.4), RepeatBehavior = RepeatBehavior.Forever };
            Storyboard.SetTarget(ax, crate); Storyboard.SetTargetProperty(ax, new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.X)"));
            Storyboard.SetTarget(ay, crate); Storyboard.SetTargetProperty(ay, new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[1].(TranslateTransform.Y)"));
            Storyboard.SetTarget(aa, crate); Storyboard.SetTargetProperty(aa, new PropertyPath("(UIElement.RenderTransform).(TransformGroup.Children)[0].(RotateTransform.Angle)"));
            sb.Children.Add(ax); sb.Children.Add(ay); sb.Children.Add(aa);
            sb.Begin();
        }
    }
}
