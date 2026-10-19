using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using CodexQuotaWidget.Core;

namespace CodexQuotaWidget;

internal static class LayoutCheckRunner
{
    private static readonly double[] Scales = [1, 1.25, 1.5, 2];

    public static async Task<int> RunAsync()
    {
        var settings = new Settings { FollowCodex = false, EnableAnimations = false };
        var app = (App)Application.Current;
        app.ConfigurePreviewTheme(settings);
        VerifyRingMapping();
        var monitor = new QuotaMonitor(settings, () => false);
        var full = new StatusWindow(monitor, () => Task.CompletedTask, () => { }, () => { }, () => { });
        var capsule = new CapsuleWindow(monitor, () => { }, () => { }, pointerProvider: () => null);
        try
        {
            full.Show();
            capsule.ShowPanel();
            await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.ApplicationIdle);
            var cases = 0;
            foreach (var scale in Scales)
            {
                settings.Theme = "Dark";
                app.ConfigurePreviewTheme(settings);
                await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                VerifyRaster(full, scale, "Full");
                cases++;
                full.ShowPreferencesForLayout();
                VerifyRaster(full, scale, "Full settings dark", scale == 1 && Environment.GetEnvironmentVariable("CQW_LAYOUT_CAPTURE") == "1");
                cases++;
                settings.Theme = "Light";
                app.ConfigurePreviewTheme(settings);
                await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                VerifyRaster(full, scale, "Full settings light", scale == 1 && Environment.GetEnvironmentVariable("CQW_LAYOUT_CAPTURE") == "1");
                cases++;
                settings.Theme = "Dark";
                app.ConfigurePreviewTheme(settings);
                foreach (var state in new[] { "Capsule collapsed", "Capsule expanded" })
                {
                    if (state == "Capsule expanded") capsule.SetExpandedForLayout(true);
                    else if (state == "Capsule collapsed") capsule.SetExpandedForLayout(false);
                    await Application.Current.Dispatcher.InvokeAsync(() => { }, DispatcherPriority.Render);
                    capsule.UpdateLayout();
                    VerifyRaster(capsule, scale, state, scale == 1 && Environment.GetEnvironmentVariable("CQW_LAYOUT_CAPTURE") == "1");
                    cases++;
                }
            }
            Console.WriteLine($"PASS layout-check: {cases} renders at 100%, 125%, 150%, 200%; current display={System.Windows.Forms.Screen.FromPoint(System.Windows.Forms.Cursor.Position).DeviceName}");
            return 0;
        }
        finally
        {
            full.Hide();
            capsule.HideImmediately();
            capsule.StopTimers();
            await monitor.DisposeAsync();
        }
    }

    private static void VerifyRaster(Window window, double scale, string state, bool capture = false)
    {
        var width = Math.Max(1, (int)Math.Ceiling(window.ActualWidth * scale));
        var height = Math.Max(1, (int)Math.Ceiling(window.ActualHeight * scale));
        var bitmap = new RenderTargetBitmap(width, height, 96 * scale, 96 * scale, PixelFormats.Pbgra32);
        bitmap.Render(window);
        var pixels = new byte[width * height * 4];
        bitmap.CopyPixels(pixels, width * 4, 0);
        var hasInk = false;
        for (var i = 3; i < pixels.Length; i += 4)
            if (pixels[i] != 0) { hasInk = true; break; }
        if (!hasInk || width <= 0 || height <= 0)
            throw new InvalidOperationException($"Blank or invalid {state} render at {scale:P0}");
        if (capture)
        {
            var path=Path.Combine(Path.GetTempPath(),"CQW-layout-"+state.Replace(' ','-')+".png");
            var encoder=new PngBitmapEncoder();encoder.Frames.Add(BitmapFrame.Create(bitmap));
            using var file=new FileStream(path,FileMode.Create,FileAccess.Write);encoder.Save(file);
            Console.WriteLine("  capture="+path);
        }
        Console.WriteLine($"  {scale:P0} {state}: {width}x{height}px");
    }

    private static void VerifyRingMapping()
    {
        var ring = new ConcentricQuotaRing { Width = 88, Height = 88, Track = Brushes.Black, Accent = Brushes.Lime };
        byte[] Render(double? five, double? week)
        {
            ring.Update(five, week, false);
            ring.Measure(new Size(88, 88));
            ring.Arrange(new Rect(0, 0, 88, 88));
            ring.UpdateLayout();
            var bitmap = new RenderTargetBitmap(88, 88, 96, 96, PixelFormats.Pbgra32);
            bitmap.Render(ring);
            var pixels = new byte[88 * 88 * 4];
            bitmap.CopyPixels(pixels, 88 * 4, 0);
            return pixels;
        }
        static bool Lit(byte[] pixels, int y)
        {
            var index = (y * 88 + 44) * 4;
            return pixels[index] + pixels[index + 1] + pixels[index + 2] > 100;
        }
        var outer = Render(100, 0);
        var inner = Render(0, 100);
        var absent = Render(null, null);
        if (!Lit(outer, 4) || Lit(outer, 14) || Lit(inner, 4) || !Lit(inner, 14) || Lit(absent, 4) || Lit(absent, 14))
            throw new InvalidOperationException("Concentric ring mapping or missing-data rendering failed");
        Console.WriteLine("PASS concentric-ring pixels: outer=5h, inner=weekly, missing data has no progress");
    }
}
