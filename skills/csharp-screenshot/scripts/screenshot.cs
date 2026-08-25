#:package System.Drawing.Common@10.0.11

// screenshot.cs - Windows screenshot tool as a .NET 10 File-Based App.
// Run:  dotnet run --file screenshot.cs -- --mode full --out shot.png
// Docs: https://learn.microsoft.com/en-us/dotnet/core/sdk/file-based-apps

using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;

[assembly: SupportedOSPlatform("windows")]

try
{
    return App.Run(args);
}
catch (UsageException ex)
{
    Console.Error.WriteLine($"Usage error: {ex.Message}");
    Console.Error.WriteLine("Run with --help for usage.");
    return 2;
}
catch (Exception ex)
{
    Console.Error.WriteLine($"ERROR: {ex.Message}");
    return 1;
}

internal static class App
{
    private const uint SRCCOPY = 0x00CC0020;
    private const uint CAPTUREBLT = 0x40000000;
    private const uint PW_RENDERFULLCONTENT = 0x00000002;
    private const int DWMWA_EXTENDED_FRAME_BOUNDS = 9;
    private const int DWMWA_CLOAKED = 14;
    private const uint CURSOR_SHOWING = 0x00000001;
    private const uint DI_NORMAL = 0x0003;

    internal static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("ERROR: this tool only runs on Windows.");
            return 1;
        }

        // DPI awareness must be set before any GDI call, otherwise coordinates
        // are virtualized and captures are stretched on high-DPI displays.
        if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) // PER_MONITOR_AWARE_V2
            SetProcessDPIAware();

        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* piped without console */ }

        Options o = ParseArgs(args);
        if (o.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        // A window selector implies window mode even if --mode was omitted.
        // Together with --region the window is rendered via PrintWindow and
        // cropped to that screen-absolute rectangle (occlusion-proof control
        // shots; the rect typically comes from `uia --mode find`).
        bool hasWindowSelector = o.Title != null || o.Hwnd != 0 || o.ProcessName != null || o.Pid != 0;
        if (hasWindowSelector && o.Mode == "region" && o.Region != null)
            o.Mode = "window";
        if (hasWindowSelector && o.Mode == "full")
            o.Mode = "window";
        if (hasWindowSelector && o.Mode != "window")
            throw new UsageException("window selectors (--title/--hwnd/--process/--pid) require --mode window");

        if (o.DelayMs > 0)
            Thread.Sleep(Math.Min(o.DelayMs, 60_000));

        Bitmap bmp;
        switch (o.Mode)
        {
            case "list":
                ListMonitors();
                return 0;
            case "full":
                bmp = CaptureRect(VirtualScreen(), o.Cursor);
                break;
            case "monitor":
                bmp = CaptureRect(MonitorRect(o.MonitorIndex), o.Cursor);
                break;
            case "region":
                bmp = CaptureRect(ClampToVirtualScreen(RequireRegion(o)), o.Cursor);
                break;
            case "window":
                IntPtr hwnd = ResolveWindow(o);
                bmp = CaptureWindow(hwnd);
                if (o.Region != null)
                    bmp = CropToRegion(bmp, WindowBounds(hwnd), o.Region.Value);
                break;
            default:
                throw new UsageException($"unknown mode '{o.Mode}'");
        }

        using (bmp)
        {
            string format = ResolveFormat(o);
            string outPath = ResolveOutPath(o, format);
            SaveImage(bmp, outPath, format, o.Quality);
            Console.WriteLine(Path.GetFullPath(outPath));
        }
        return 0;
    }

    // ---------- argument parsing ----------

    internal static Options ParseArgs(string[] args)
    {
        // Normalize "--key=value" into separate tokens.
        var tokens = new List<string>();
        foreach (string a in args)
        {
            if (a.StartsWith("--", StringComparison.Ordinal) && a.Contains('='))
                tokens.AddRange(a.Split('=', 2));
            else
                tokens.Add(a);
        }

        var o = new Options();
        for (int i = 0; i < tokens.Count; i++)
        {
            string a = tokens[i];
            string Value()
            {
                if (i + 1 >= tokens.Count)
                    throw new UsageException($"missing value for {a}");
                return tokens[++i];
            }
            int Int()
            {
                string v = Value();
                return int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out int n)
                    ? n : throw new UsageException($"invalid integer '{v}' for {a}");
            }

            switch (a)
            {
                case "--mode": o.Mode = Value().ToLowerInvariant(); break;
                case "--index":
                case "--monitor": o.MonitorIndex = Int(); break;
                case "--region":
                    o.Region = ParseRegion(Value()) ?? throw new UsageException($"invalid region for {a}, expected x,y,w,h");
                    break;
                case "--title": o.Title = Value(); break;
                case "--hwnd": o.Hwnd = ParseHwnd(Value()); break;
                case "--process":
                case "--pname": o.ProcessName = Value(); break;
                case "--pid": o.Pid = Int(); break;
                case "--out":
                case "-o": o.Out = Value(); break;
                case "--format":
                case "-f": o.Format = Value().ToLowerInvariant(); break;
                case "--quality":
                case "-q": o.Quality = Int(); break;
                case "--delay":
                    string dv = Value();
                    o.DelayMs = double.TryParse(dv, NumberStyles.Float, CultureInfo.InvariantCulture, out double secs) && secs > 0
                        ? (int)Math.Min(secs * 1000, 60_000) : 0;
                    break;
                case "--cursor": o.Cursor = true; break;
                case "--help":
                case "-h": o.ShowHelp = true; break;
                default:
                    throw new UsageException($"unknown option '{a}'");
            }
        }
        return o;
    }

    private static RECT? ParseRegion(string s)
    {
        string[] parts = s.Split(',');
        if (parts.Length != 4)
            return null;
        var vals = new int[4];
        for (int i = 0; i < 4; i++)
            if (!int.TryParse(parts[i].Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out vals[i]))
                return null;
        return new RECT { Left = vals[0], Top = vals[1], Right = vals[0] + vals[2], Bottom = vals[1] + vals[3] };
    }

    private static RECT RequireRegion(Options o)
        => o.Region ?? throw new UsageException("--mode region requires --region x,y,w,h");

    private static IntPtr ResolveWindow(Options o)
    {
        int selectors = (o.Title != null ? 1 : 0) + (o.Hwnd != 0 ? 1 : 0)
                      + (o.ProcessName != null ? 1 : 0) + (o.Pid != 0 ? 1 : 0);
        if (selectors == 0)
            throw new UsageException("--mode window requires one of --title, --hwnd, --process, --pid");
        if (selectors > 1)
            throw new UsageException("window selectors are mutually exclusive; use only one of --title, --hwnd, --process, --pid");

        if (o.Title != null)
            return FindWindowByTitle(o.Title)
                ?? throw new InvalidOperationException($"no visible top-level window title contains '{o.Title}'");

        if (o.Hwnd != 0)
        {
            IntPtr h = new IntPtr(o.Hwnd);
            if (!IsWindow(h))
                throw new InvalidOperationException($"invalid window handle {o.Hwnd}");
            return h;
        }

        if (o.Pid != 0)
            return MainWindowByPid(o.Pid);
        return MainWindowByProcess(o.ProcessName!);
    }

    private static long ParseHwnd(string s)
    {
        s = s.Trim();
        long v = 0;
        bool ok = s.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            ? long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out v)
            : long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out v);
        if (!ok || v == 0)
            throw new UsageException($"invalid window handle '{s}' (decimal or 0x-hex expected)");
        return v;
    }

    private static string ResolveFormat(Options o)
    {
        string f = o.Format;
        if (f.Length == 0 && o.Out.Length > 0)
            f = (Path.GetExtension(o.Out) ?? "").TrimStart('.').ToLowerInvariant();
        return f switch
        {
            "" or "png" => "png",
            "jpg" or "jpeg" => "jpeg",
            "bmp" => "bmp",
            _ => throw new UsageException($"unsupported format '{f}' (png, jpeg, bmp)")
        };
    }

    private static string ResolveOutPath(Options o, string format)
    {
        if (o.Out.Length == 0)
            return $"screenshot-{DateTime.Now:yyyyMMdd-HHmmss-fff}.{ExtFor(format)}";
        if (!Path.HasExtension(o.Out))
            return o.Out + "." + ExtFor(format);
        return o.Out;
    }

    private static string ExtFor(string format)
        => format == "jpeg" ? "jpg" : format;

    // ---------- capture ----------

    private static RECT VirtualScreen() => new()
    {
        Left = GetSystemMetrics(76),  // SM_XVIRTUALSCREEN
        Top = GetSystemMetrics(77),   // SM_YVIRTUALSCREEN
        Right = GetSystemMetrics(76) + GetSystemMetrics(78),  // + SM_CXVIRTUALSCREEN
        Bottom = GetSystemMetrics(77) + GetSystemMetrics(79), // + SM_CYVIRTUALSCREEN
    };

    private static RECT ClampToVirtualScreen(RECT r)
    {
        RECT vs = VirtualScreen();
        int left = Math.Max(r.Left, vs.Left);
        int top = Math.Max(r.Top, vs.Top);
        int right = Math.Min(r.Right, vs.Right);
        int bottom = Math.Min(r.Bottom, vs.Bottom);
        if (right - left <= 0 || bottom - top <= 0)
            throw new InvalidOperationException($"region ({r.Left},{r.Top},{r.Right - r.Left},{r.Bottom - r.Top}) does not intersect the virtual screen ({vs.Left},{vs.Top},{vs.Right - vs.Left},{vs.Bottom - vs.Top})");
        return new RECT { Left = left, Top = top, Right = right, Bottom = bottom };
    }

    private static Bitmap CaptureRect(RECT r, bool withCursor)
    {
        int w = r.Right - r.Left, h = r.Bottom - r.Top;
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException($"invalid capture rectangle {w}x{h}");

        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = CreateCompatibleBitmap(screenDc, w, h);
        IntPtr oldObj = SelectObject(memDc, hBitmap);
        try
        {
            if (!BitBlt(memDc, 0, 0, w, h, screenDc, r.Left, r.Top, SRCCOPY | CAPTUREBLT))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "BitBlt failed");

            Bitmap bmp = Image.FromHbitmap(hBitmap);
            if (withCursor)
                DrawCursor(bmp, r.Left, r.Top);
            return bmp;
        }
        finally
        {
            SelectObject(memDc, oldObj);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
    }

    private static Bitmap CaptureWindow(IntPtr hwnd)
    {
        if (IsIconic(hwnd))
            throw new InvalidOperationException("target window is minimized; restore it first");

        GetWindowRect(hwnd, out RECT win);
        int w = win.Right - win.Left, h = win.Bottom - win.Top;
        if (w <= 0 || h <= 0)
            throw new InvalidOperationException("window has no visible bounds");

        Bitmap? rendered = null;
        IntPtr screenDc = GetDC(IntPtr.Zero);
        IntPtr memDc = CreateCompatibleDC(screenDc);
        IntPtr hBitmap = CreateCompatibleBitmap(screenDc, w, h);
        IntPtr oldObj = SelectObject(memDc, hBitmap);
        try
        {
            // PrintWindow renders the window even when occluded. It paints the FULL
            // GetWindowRect area (including the invisible resize border) at (0,0),
            // so the bitmap is sized to GetWindowRect, not the visible frame.
            if (PrintWindow(hwnd, memDc, PW_RENDERFULLCONTENT))
                rendered = Image.FromHbitmap(hBitmap);
        }
        finally
        {
            SelectObject(memDc, oldObj);
            DeleteObject(hBitmap);
            DeleteDC(memDc);
            ReleaseDC(IntPtr.Zero, screenDc);
        }
        // Fallback for windows PrintWindow cannot handle: blit the screen area.
        rendered ??= CaptureRect(win, withCursor: false);

        // Trim the invisible borders so the bitmap origin equals the DWM extended
        // (visible) frame origin — the same grid UIA BoundingRectangle uses —
        // keeping --region cropping aligned with `uia --mode find` rects.
        return TrimToExtendedBounds(rendered, hwnd, win);
    }

    /// <summary>Crop the invisible resize border off a GetWindowRect-sized capture
    /// so the result lines up with the DWM extended frame bounds.</summary>
    private static Bitmap TrimToExtendedBounds(Bitmap bmp, IntPtr hwnd, RECT win)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT ext, Marshal.SizeOf<RECT>()) != 0)
            return bmp; // no DWM info; keep as-is
        int x = Math.Clamp(ext.Left - win.Left, 0, bmp.Width);
        int y = Math.Clamp(ext.Top - win.Top, 0, bmp.Height);
        int w = Math.Clamp(ext.Right - ext.Left, 0, bmp.Width - x);
        int h = Math.Clamp(ext.Bottom - ext.Top, 0, bmp.Height - y);
        if (w <= 0 || h <= 0 || (x == 0 && y == 0 && w == bmp.Width && h == bmp.Height))
            return bmp;
        Bitmap cropped = bmp.Clone(new Rectangle(x, y, w, h), bmp.PixelFormat);
        bmp.Dispose();
        return cropped;
    }

    private static RECT WindowBounds(IntPtr hwnd)
    {
        if (DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS, out RECT r, Marshal.SizeOf<RECT>()) != 0)
            GetWindowRect(hwnd, out r);
        return r;
    }

    /// <summary>
    /// Crop a full-window capture to a screen-absolute region (physical px).
    /// The bitmap's origin is windowRect, so shift and intersect there.
    /// </summary>
    private static Bitmap CropToRegion(Bitmap windowBmp, RECT windowRect, RECT region)
    {
        int left = Math.Max(region.Left, windowRect.Left) - windowRect.Left;
        int top = Math.Max(region.Top, windowRect.Top) - windowRect.Top;
        int right = Math.Min(region.Right, windowRect.Right) - windowRect.Left;
        int bottom = Math.Min(region.Bottom, windowRect.Bottom) - windowRect.Top;
        if (right - left <= 0 || bottom - top <= 0)
            throw new InvalidOperationException(
                $"region ({region.Left},{region.Top} {region.Right - region.Left}x{region.Bottom - region.Top}) " +
                $"does not intersect the window ({windowRect.Left},{windowRect.Top} {windowRect.Right - windowRect.Left}x{windowRect.Bottom - windowRect.Top})");
        Bitmap cropped = windowBmp.Clone(new Rectangle(left, top, right - left, bottom - top), windowBmp.PixelFormat);
        windowBmp.Dispose();
        return cropped;
    }

    private static void DrawCursor(Bitmap bmp, int originX, int originY)
    {
        var ci = new CURSORINFO { Size = Marshal.SizeOf<CURSORINFO>() };
        if (!GetCursorInfo(ref ci) || (ci.Flags & CURSOR_SHOWING) == 0 || ci.Handle == IntPtr.Zero)
            return;
        using Graphics g = Graphics.FromImage(bmp);
        IntPtr hdc = g.GetHdc();
        try
        {
            DrawIconEx(hdc, ci.Position.X - originX, ci.Position.Y - originY, ci.Handle, 0, 0, 0, IntPtr.Zero, DI_NORMAL);
        }
        finally
        {
            g.ReleaseHdc();
        }
    }

    // ---------- monitors ----------

    private static List<MonitorEntry> GetMonitors()
    {
        var list = new List<MonitorEntry>();
        EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr hMonitor, IntPtr hdcMonitor, ref RECT rect, IntPtr data) =>
        {
            var mi = new MONITORINFOEXW { Size = (uint)Marshal.SizeOf<MONITORINFOEXW>() };
            if (GetMonitorInfoW(hMonitor, ref mi))
            {
                uint dpi = 0;
                try { if (GetDpiForMonitor(hMonitor, 0, out uint dx, out _) == 0) dpi = dx; } catch { /* pre-Win10 */ }
                list.Add(new MonitorEntry
                {
                    Handle = hMonitor,
                    Device = mi.DeviceName,
                    Rect = mi.Monitor,
                    Primary = (mi.Flags & 1) != 0,
                    Dpi = dpi,
                });
            }
            return true;
        }, IntPtr.Zero);
        return list;
    }

    private static void ListMonitors()
    {
        var monitors = GetMonitors();
        Console.WriteLine("Monitors:");
        for (int i = 0; i < monitors.Count; i++)
        {
            var m = monitors[i];
            string dpi = m.Dpi > 0 ? m.Dpi.ToString(CultureInfo.InvariantCulture) : "n/a";
            Console.WriteLine($"  [{i}] {(m.Primary ? "PRIMARY" : "       ")} {m.Rect.Right - m.Rect.Left}x{m.Rect.Bottom - m.Rect.Top} at ({m.Rect.Left}, {m.Rect.Top})  DPI {dpi}  {m.Device}");
        }
        RECT vs = VirtualScreen();
        Console.WriteLine($"Virtual screen: ({vs.Left}, {vs.Top}) size {vs.Right - vs.Left}x{vs.Bottom - vs.Top}");
        ListWindows();
    }

    private static void ListWindows()
    {
        Console.WriteLine("Windows:");
        int shown = 0;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || IsIconic(hWnd))
                return true;
            if (GetWindowTextLengthW(hWnd) == 0 || IsCloaked(hWnd))
                return true;
            var sb = new StringBuilder(256);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            var cls = new StringBuilder(256);
            GetClassNameW(hWnd, cls, cls.Capacity);
            GetWindowThreadProcessId(hWnd, out uint pid);
            string proc = "?";
            try { proc = Process.GetProcessById((int)pid).ProcessName; } catch { /* exited */ }
            RECT r = WindowBounds(hWnd);
            Console.WriteLine($"  [{shown + 1}] \"{sb}\" pid={(int)pid} proc={proc} class={cls} " +
                              $"hwnd=0x{hWnd.ToString("X", CultureInfo.InvariantCulture)} " +
                              $"rect={r.Left},{r.Top} {r.Right - r.Left}x{r.Bottom - r.Top}");
            shown++;
            return true;
        }, IntPtr.Zero);
        Console.WriteLine($"total: {shown} visible top-level windows (minimized/untitled hidden)");
    }

    /// <summary>UWP/hosted windows that are "cloaked" (not really on screen).</summary>
    private static bool IsCloaked(IntPtr hWnd)
    {
        try { return DwmGetWindowAttributeInt(hWnd, DWMWA_CLOAKED, out int cloaked, sizeof(int)) == 0 && cloaked != 0; }
        catch { return false; } // no DWM (e.g. Server Core)
    }

    private static RECT MonitorRect(int index)
    {
        var monitors = GetMonitors();
        if (index < 0 || index >= monitors.Count)
            throw new InvalidOperationException($"monitor index {index} out of range (0..{monitors.Count - 1}); run --mode list to see monitors");
        return monitors[index].Rect;
    }

    // ---------- saving ----------

    private static void SaveImage(Image img, string path, string format, int quality)
    {
        string? dir = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(dir))
            Directory.CreateDirectory(dir);

        switch (format)
        {
            case "jpeg":
                ImageCodecInfo jpeg = ImageCodecInfo.GetImageEncoders()
                    .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                using (var eps = new EncoderParameters(1))
                {
                    eps.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, (long)Math.Clamp(quality, 1, 100));
                    img.Save(path, jpeg, eps);
                }
                break;
            case "bmp":
                img.Save(path, ImageFormat.Bmp);
                break;
            default:
                img.Save(path, ImageFormat.Png);
                break;
        }
    }

    // ---------- window lookup ----------

    private static IntPtr? FindWindowByTitle(string substring)
    {
        IntPtr? found = null;
        EnumWindows((hWnd, _) =>
        {
            // Skip cloaked ghosts so title lookup picks the window that is
            // actually on screen (matches csharp-uia's resolution).
            if (!IsWindowVisible(hWnd) || IsCloaked(hWnd))
                return true;
            int len = GetWindowTextLengthW(hWnd);
            if (len == 0)
                return true;
            var sb = new StringBuilder(len + 1);
            GetWindowTextW(hWnd, sb, sb.Capacity);
            if (sb.ToString().Contains(substring, StringComparison.OrdinalIgnoreCase))
            {
                found = hWnd;
                return false; // stop enumeration
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static IntPtr MainWindowByProcess(string name)
    {
        string normalized = name.Trim();
        if (normalized.EndsWith(".exe", StringComparison.OrdinalIgnoreCase))
            normalized = normalized[..^4];
        if (normalized.Length == 0)
            throw new UsageException("empty process name");

        Process[] processes = Process.GetProcessesByName(normalized);
        if (processes.Length == 0)
            throw new InvalidOperationException($"no running process named '{normalized}'");

        var pids = new HashSet<int>();
        IntPtr main = IntPtr.Zero;
        foreach (Process p in processes)
        {
            pids.Add(p.Id);
            if (main == IntPtr.Zero && p.MainWindowHandle != IntPtr.Zero && IsWindow(p.MainWindowHandle))
                main = p.MainWindowHandle;
        }
        if (main != IntPtr.Zero)
            return main;

        // Fallback: some apps expose no main window; enumerate top-level
        // windows belonging to any of these processes (topmost in Z-order).
        return FindWindowByPidSet(pids)
            ?? throw new InvalidOperationException($"process '{normalized}' has no capturable top-level window (all windows hidden or minimized to tray?)");
    }

    private static IntPtr MainWindowByPid(int pid)
    {
        Process p;
        try { p = Process.GetProcessById(pid); }
        catch (ArgumentException) { throw new InvalidOperationException($"no process with pid {pid}"); }

        if (p.MainWindowHandle != IntPtr.Zero && IsWindow(p.MainWindowHandle))
            return p.MainWindowHandle;
        return FindWindowByPidSet([pid])
            ?? throw new InvalidOperationException($"process pid {pid} ({p.ProcessName}) has no capturable top-level window");
    }

    private static IntPtr? FindWindowByPidSet(HashSet<int> pids)
    {
        IntPtr? found = null;
        EnumWindows((hWnd, _) =>
        {
            if (!IsWindowVisible(hWnd) || IsCloaked(hWnd))
                return true;
            GetWindowThreadProcessId(hWnd, out uint pid);
            if (pids.Contains((int)pid))
            {
                found = hWnd;
                return false; // topmost match in Z-order
            }
            return true;
        }, IntPtr.Zero);
        return found;
    }

    private static void PrintHelp()
    {
        Console.WriteLine("""
            screenshot.cs - Windows screenshot tool (.NET file-based app)

            Usage:
              dotnet run --file screenshot.cs -- [options]

            Options:
              --mode <full|monitor|region|window|list>  Capture mode (default: full)
                  full       entire virtual screen (all monitors combined)
                  monitor    one monitor (select with --index)
                  region     rectangle on the virtual screen
                  window     a top-level window (select with --title)
                  list       list monitors and visible top-level windows
              --index, --monitor <n>       Monitor index for --mode monitor (default 0)
              --region <x,y,w,h>           Region in physical pixels, e.g. 100,100,800,600
                                                   alone: rectangle on the screen (--mode region)
                                                   with a window selector: crop inside the rendered
                                                   window (occlusion-proof control shots; the rect
                                                   usually comes from `uia --mode find`)
              --title <substring>          Case-insensitive substring of the window title
              --hwnd <handle>              Window handle, decimal or 0x-hex
              --process, --pname <name>    Process name, with or without .exe (e.g. notepad)
              --pid <pid>                  Process id
                                           (the four window selectors are mutually exclusive;
                                           providing any of them implies --mode window)
              --out, -o <path>             Output file (default: screenshot-<timestamp>.png)
              --format, -f <png|jpeg|bmp>  Image format (default: --out extension, else png)
              --quality, -q <1-100>        JPEG quality (default 90)
              --delay <seconds>            Wait before capturing, e.g. 1.5
              --cursor                     Draw the mouse cursor into the image
              --help, -h                   Show this help

            Notes:
              * Coordinates are physical pixels. Monitors left of / above the primary
                screen have negative coordinates. Run --mode list to inspect the layout.
              * Window capture renders the target window even when it is occluded by
                other windows; a minimized window cannot be captured - restore it first.
              * Control-level shots: get the rect from csharp-uia
                (`uia --mode find --process X --name Y` prints rect=L,T WxH), then
                pass it as --region together with the same window selector.
              * On success the absolute path of the saved file is printed to stdout.
              * Exit codes: 0 ok, 1 runtime error, 2 usage error.
            """);
    }

    // ---------- P/Invoke: user32 / gdi32 ----------

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern IntPtr GetDC(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern int ReleaseDC(IntPtr hWnd, IntPtr hDC);

    [DllImport("user32.dll")]
    private static extern int GetSystemMetrics(int nIndex);

    [DllImport("user32.dll")]
    private static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);

    private delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdcMonitor, ref RECT rect, IntPtr data);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetMonitorInfoW(IntPtr hMonitor, ref MONITORINFOEXW info);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hMonitor, int dpiType, out uint dpiX, out uint dpiY);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc proc, IntPtr lParam);

    private delegate bool EnumWindowsProc(IntPtr hWnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsIconic(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool IsWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint processId);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextLengthW(IntPtr hWnd);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowTextW(IntPtr hWnd, StringBuilder lpString, int nMaxCount);

    [DllImport("user32.dll")]
    private static extern bool GetWindowRect(IntPtr hWnd, out RECT rect);

    [DllImport("user32.dll")]
    private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);

    [DllImport("user32.dll")]
    private static extern bool GetCursorInfo(ref CURSORINFO pci);

    [DllImport("user32.dll")]
    private static extern bool DrawIconEx(IntPtr hdc, int xLeft, int yTop, IntPtr hIcon, int cxWidth, int cyHeight, int istepIfAniCur, IntPtr hbrFlickerFreeDraw, uint diFlags);

    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attr, out RECT rect, int cbAttribute);

    [DllImport("dwmapi.dll", EntryPoint = "DwmGetWindowAttribute")]
    private static extern int DwmGetWindowAttributeInt(IntPtr hwnd, int attr, out int value, int cbAttribute);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetClassNameW(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleBitmap(IntPtr hdc, int width, int height);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdcDest, int xDest, int yDest, int width, int height, IntPtr hdcSrc, int xSrc, int ySrc, uint rop);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr obj);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);
}

internal sealed class Options
{
    public string Mode = "full";
    public int MonitorIndex;
    public RECT? Region;
    public string? Title;
    public long Hwnd;
    public string? ProcessName;
    public int Pid;
    public string Out = "";
    public string Format = "";
    public int Quality = 90;
    public int DelayMs;
    public bool Cursor;
    public bool ShowHelp;
}

internal sealed class UsageException(string message) : Exception(message);

internal sealed class MonitorEntry
{
    public IntPtr Handle;
    public string Device = "";
    public RECT Rect;
    public bool Primary;
    public uint Dpi;
}

[StructLayout(LayoutKind.Sequential)]
internal struct RECT
{
    public int Left, Top, Right, Bottom;
}

[StructLayout(LayoutKind.Sequential)]
internal struct POINT
{
    public int X, Y;
}

[StructLayout(LayoutKind.Sequential)]
internal struct CURSORINFO
{
    public int Size;
    public uint Flags;
    public IntPtr Handle;
    public POINT Position;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
internal struct MONITORINFOEXW
{
    public uint Size;
    public RECT Monitor;
    public RECT WorkArea;
    public uint Flags;
    [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
    public string DeviceName;
}
