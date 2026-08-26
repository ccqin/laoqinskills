#:property TargetFramework=net10.0-windows
#:property UseWindowsForms=true
#:property UseWPF=true
#:property PublishTrimmed=false

// uia.cs - read window/control content and operate them via UI Automation.
// Run:  dotnet run --file uia.cs -- --mode list
// No screenshot features here; pair with csharp-screenshot for pixels:
//   uia --mode find --process app --name X     -> gives Bounds
//   screenshot --process app --region x,y,w,h  -> crops that control

using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Windows.Automation;

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

internal sealed class UsageException(string message) : Exception(message);

internal static class App
{
    private const int DefaultTimeoutMs = 3000;
    private const int DefaultDepth = 30;
    private const int DefaultMaxNodes = 4000;
    private const int PollIntervalMs = 150;

    private static readonly string[] Modes =
        ["list", "tree", "find", "click", "set", "select", "toggle", "expand", "keys", "wait", "scroll", "menu"];

    internal static int Run(string[] args)
    {
        if (!OperatingSystem.IsWindows())
        {
            Console.Error.WriteLine("ERROR: this tool only runs on Windows.");
            return 1;
        }

        // Physical-pixel coordinates for BoundingRectangle, matching csharp-screenshot.
        if (!SetProcessDpiAwarenessContext(new IntPtr(-4))) // PER_MONITOR_AWARE_V2
            SetProcessDPIAware();

        try { Console.OutputEncoding = Encoding.UTF8; } catch { /* piped without console */ }

        Options o = ParseArgs(args);
        if (o.ShowHelp)
        {
            PrintHelp();
            return 0;
        }

        return o.Mode switch
        {
            "list" => RunList(),
            "tree" => RunTree(o),
            "find" => RunFind(o),
            "click" => RunClick(o),
            "set" => RunSet(o),
            "select" => RunSelect(o),
            "toggle" => RunToggle(o),
            "expand" => RunExpand(o),
            "keys" => RunKeys(o),
            "wait" => RunWait(o),
            "scroll" => RunScroll(o),
            "menu" => RunMenu(o),
            _ => throw new UsageException($"unknown mode '{o.Mode}'"),
        };
    }

    // ---------- options ----------

    internal sealed class Options
    {
        public string Mode = "list";
        public string? Title;
        public string? ProcessName;
        public int Pid;
        public long Hwnd;
        public string? Name;        // element Name substring, ignore case
        public string? Id;          // AutomationId, exact
        public string? ClassName;   // ClassName, exact
        public string? Control;     // ControlType name, e.g. Button
        public int Index = 1;       // 1-based pick among matches
        public string? Value;       // for set
        public string? Text;        // for keys
        public bool Collapse;
        public bool Gone;          // wait: wait for disappearance instead of appearance
        public bool AnyWindow;     // search all top-level windows of the target, not just the first
        public bool Real;          // click: real mouse click at coordinates (moves the user's cursor)
        public bool Right;         // click/menu: right mouse button
        public bool Double;        // click: double click
        public bool Hover;         // click: move pointer over the element, no button
        public string? MenuText;   // menu: menu item text to find after right-clicking
        public double TimeoutSec = DefaultTimeoutMs / 1000.0;
        public string View = "content";   // content | control | raw
        public int Depth = DefaultDepth;
        public int MaxNodes = DefaultMaxNodes;
        public string Format = "tree";    // tree | json (tree mode)
        public string? Out;
        public bool ShowHelp;
    }

    internal static Options ParseArgs(string[] args)
    {
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
            string t = tokens[i];
            string Next() => ++i < tokens.Count ? tokens[i] : throw new UsageException($"missing value for {t}");
            switch (t)
            {
                case "--mode" or "-m": o.Mode = Next(); break;
                case "--title": o.Title = Next(); break;
                case "--process" or "--pname": o.ProcessName = Next(); break;
                case "--pid": o.Pid = ParseInt(Next(), t); break;
                case "--hwnd": o.Hwnd = ParseLong(Next(), t); break;
                case "--name": o.Name = Next(); break;
                case "--id": o.Id = Next(); break;
                case "--class": o.ClassName = Next(); break;
                case "--control": o.Control = Next(); break;
                case "--index": o.Index = ParseInt(Next(), t); break;
                case "--value": o.Value = Next(); break;
                case "--text": o.Text = Next(); break;
                case "--collapse": o.Collapse = true; break;
                case "--gone": o.Gone = true; break;
                case "--any-window": o.AnyWindow = true; break;
                case "--real": o.Real = true; break;
                case "--right": o.Right = true; break;
                case "--double": o.Double = true; break;
                case "--hover": o.Hover = true; break;
                case "--menu": o.MenuText = Next(); break;
                case "--timeout": o.TimeoutSec = ParseDouble(Next(), t); break;
                case "--view": o.View = Next(); break;
                case "--depth": o.Depth = ParseInt(Next(), t); break;
                case "--max-nodes": o.MaxNodes = ParseInt(Next(), t); break;
                case "--format": o.Format = Next(); break;
                case "--out" or "-o": o.Out = Next(); break;
                case "--help" or "-h": o.ShowHelp = true; break;
                default: throw new UsageException($"unknown option '{t}'");
            }
        }

        if (!Modes.Contains(o.Mode, StringComparer.Ordinal))
            throw new UsageException($"unknown mode '{o.Mode}' (valid: {string.Join("|", Modes)})");
        if (o.View is not ("content" or "control" or "raw"))
            throw new UsageException($"unknown view '{o.View}' (valid: content|control|raw)");
        if (o.Format is not ("tree" or "json"))
            throw new UsageException($"unknown format '{o.Format}' (valid: tree|json)");
        if (o.Index < 1) throw new UsageException("--index must be >= 1");
        if (o.Depth < 1) throw new UsageException("--depth must be >= 1");
        if (o.MaxNodes < 1) throw new UsageException("--max-nodes must be >= 1");
        if (o.TimeoutSec < 0) throw new UsageException("--timeout must be >= 0");

        int windowSelectors = Count(o.Title, o.ProcessName, o.Pid, o.Hwnd);
        if (windowSelectors > 1)
            throw new UsageException("window selectors (--title/--process/--pid/--hwnd) are mutually exclusive");
        bool needsWindow = o.Mode is not "list";
        if (needsWindow && windowSelectors == 0)
            throw new UsageException($"mode '{o.Mode}' requires a window selector: --title/--process/--pid/--hwnd");

        bool hasElementSelector = o.Name != null || o.Id != null || o.ClassName != null || o.Control != null;
        if (o.Mode is ("find" or "click" or "set" or "select" or "toggle" or "expand" or "keys" or "scroll" or "menu") && !hasElementSelector)
            throw new UsageException($"mode '{o.Mode}' requires an element selector: --name/--id/--class/--control");
        if (o.Mode == "set" && o.Value == null)
            throw new UsageException("mode 'set' requires --value <text>");
        if (o.Mode == "keys" && string.IsNullOrEmpty(o.Text))
            throw new UsageException("mode 'keys' requires --text <keystrokes>");
        if (o.Mode == "menu" && o.MenuText == null)
            throw new UsageException("mode 'menu' requires --menu <item text>");
        if (o.MenuText != null && o.Mode != "menu")
            throw new UsageException("--menu is only valid with --mode menu");
        if (o.Gone && o.Mode != "wait")
            throw new UsageException("--gone is only valid with --mode wait");
        if ((o.Real || o.Right || o.Double || o.Hover) && o.Mode is not ("click" or "menu"))
            throw new UsageException("--real/--right/--double/--hover are only valid with --mode click or menu");

        return o;
    }

    private static int Count(params object?[] values) => values.Count(v => v switch
    {
        null => false,
        string s => s.Length > 0,
        long l => l != 0,
        int i => i != 0,
        _ => true,
    });

    private static int ParseInt(string s, string opt) =>
        int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out int v)
            ? v : throw new UsageException($"invalid integer '{s}' for {opt}");

    private static long ParseLong(string s, string opt)
    {
        if (s.StartsWith("0x", StringComparison.OrdinalIgnoreCase))
            return long.TryParse(s[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture, out long h)
                ? h : throw new UsageException($"invalid hwnd '{s}' for {opt}");
        return ParseInt(s, opt);
    }

    private static double ParseDouble(string s, string opt) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out double v)
            ? v : throw new UsageException($"invalid number '{s}' for {opt}");

    // ---------- element info snapshot (one fetch, cheap reuse) ----------

    private sealed record ElInfo(
        string? Name, string AutomationId, string ClassName, string TypeName,
        int ProcessId, int NativeWindowHandle, System.Windows.Rect Rect,
        bool IsEnabled, bool IsOffscreen, bool IsPassword);

    /// <summary>Fetch the common properties in one go; null if the element vanished.</summary>
    private static ElInfo? GetInfo(AutomationElement e)
    {
        try
        {
            var c = e.Current;
            return new ElInfo(
                c.Name, c.AutomationId ?? "", c.ClassName ?? "",
                c.ControlType.ProgrammaticName["ControlType.".Length..],
                c.ProcessId, c.NativeWindowHandle, c.BoundingRectangle,
                c.IsEnabled, c.IsOffscreen, c.IsPassword);
        }
        catch (ElementNotAvailableException) { return null; }
    }

    // ---------- window / element resolution ----------

    private static List<AutomationElement> TopLevelWindows()
    {
        var root = AutomationElement.RootElement;
        var wins = new List<AutomationElement>();
        foreach (AutomationElement w in root.FindAll(TreeScope.Children,
                     new PropertyCondition(AutomationElement.ControlTypeProperty, ControlType.Window)))
        {
            wins.Add(w);
        }
        return wins;
    }

    /// <summary>All windows matching the selectors right now, on-screen ones first.</summary>
    private static List<AutomationElement> TryResolveWindows(Options o)
    {
        if (o.Hwnd != 0)
            return [AutomationElement.FromHandle(new IntPtr(o.Hwnd))];

        var candidates = TopLevelWindows();
        if (o.Pid != 0)
            candidates = candidates.Where(w => GetInfo(w)?.ProcessId == o.Pid).ToList();
        else if (o.ProcessName != null)
        {
            var pids = new HashSet<int>(Process.GetProcessesByName(
                o.ProcessName.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)
                    ? o.ProcessName[..^4] : o.ProcessName).Select(p => p.Id));
            candidates = candidates.Where(w => pids.Contains(GetInfo(w)?.ProcessId ?? -1)).ToList();
        }
        else if (o.Title != null)
            candidates = candidates.Where(w => GetInfo(w)?.Name?.Contains(o.Title, StringComparison.OrdinalIgnoreCase) == true).ToList();

        // Prefer windows actually shown on screen (skips UWP cloaked ghosts).
        var onScreen = candidates.Where(w => GetInfo(w)?.IsOffscreen == false).ToList();
        return onScreen.Count > 0 ? onScreen : candidates;
    }

    /// <summary>Single attempt to resolve the target window; null if not found right now.</summary>
    private static AutomationElement? TryResolveWindow(Options o)
    {
        var wins = TryResolveWindows(o);
        return wins.Count > 0 ? wins[0] : null;
    }

    private sealed record ElementSnapshot(AutomationElement Element, int Depth);

    private sealed class SearchContext(Options o)
    {
        public TreeWalker Walker = o.View switch
        {
            "raw" => TreeWalker.RawViewWalker,
            "control" => TreeWalker.ControlViewWalker,
            _ => TreeWalker.ContentViewWalker,
        };
        public List<ElementSnapshot> Matches = [];
        public int Visited;
        public bool Truncated;
    }

    private static bool Matches(Options o, AutomationElement e)
    {
        var info = GetInfo(e);
        if (info == null) return false;
        if (o.Id != null && !string.Equals(info.AutomationId, o.Id, StringComparison.Ordinal)) return false;
        if (o.ClassName != null && !string.Equals(info.ClassName, o.ClassName, StringComparison.Ordinal)) return false;
        if (o.Control != null && !string.Equals(info.TypeName, o.Control, StringComparison.OrdinalIgnoreCase)) return false;
        if (o.Name != null && (info.Name == null || !info.Name.Contains(o.Name, StringComparison.OrdinalIgnoreCase))) return false;
        return true;
    }

    private static void Walk(SearchContext ctx, Options o, AutomationElement e, int depth)
    {
        if (ctx.Visited >= o.MaxNodes) { ctx.Truncated = true; return; }
        ctx.Visited++;
        if (Matches(o, e))
            ctx.Matches.Add(new ElementSnapshot(e, depth));
        if (depth >= o.Depth) return;

        AutomationElement? child = null;
        try { child = ctx.Walker.GetFirstChild(e); }
        catch (ElementNotAvailableException) { }
        while (child != null)
        {
            Walk(ctx, o, child, depth + 1);
            if (ctx.Visited >= o.MaxNodes) { ctx.Truncated = true; return; }
            AutomationElement? next = null;
            try { next = ctx.Walker.GetNextSibling(child); }
            catch (ElementNotAvailableException) { }
            child = next;
        }
    }

    private static SearchContext SearchOnce(Options o, AutomationElement win)
    {
        var ctx = new SearchContext(o);
        Walk(ctx, o, win, 0);
        return ctx;
    }

    /// <summary>Resolve window (+element when selectors given), polling until timeout.
    /// --any-window: search every matching top-level window (popups/combobox dropdowns
    /// are separate HWNDs), not just the first.</summary>
    private static (AutomationElement Win, ElementSnapshot? El) Resolve(Options o, bool needElement)
    {
        var sw = Stopwatch.StartNew();
        int timeoutMs = (int)(o.TimeoutSec * 1000);
        SearchContext? last = null;
        AutomationElement? win = null;

        while (true)
        {
            var wins = TryResolveWindows(o);
            var targets = o.AnyWindow ? wins : wins.Take(1).ToList();
            if (targets.Count > 0)
            {
                win = targets[0];
                if (!needElement) return (win, null);
                last = MergeSearch(o, targets);
                if (last.Matches.Count > 0) return (win, PickMatch(o, last));
            }
            if (sw.ElapsedMilliseconds >= timeoutMs) break;
            Thread.Sleep(PollIntervalMs);
        }

        if (win == null)
            throw new InvalidOperationException($"no window matched {WindowDesc(o)} within {Fmt(o.TimeoutSec)}s");
        if (last != null && last.Matches.Count == 0)
            throw new InvalidOperationException(
                $"no element matched {ElementDesc(o)} in window '{GetInfo(win)?.Name}' " +
                $"(visited {last.Visited} nodes, waited {Fmt(o.TimeoutSec)}s)");
        throw new InvalidOperationException($"no element matched {ElementDesc(o)} within {Fmt(o.TimeoutSec)}s");
    }

    private static SearchContext MergeSearch(Options o, List<AutomationElement> windows)
    {
        var merged = new SearchContext(o);
        foreach (var w in windows)
        {
            var ctx = SearchOnce(o, w);
            merged.Matches.AddRange(ctx.Matches);
            merged.Visited += ctx.Visited;
            merged.Truncated |= ctx.Truncated;
        }
        return merged;
    }

    /// <summary>Patterns the current mode can act on; empty for pure-read modes.</summary>
    private static AutomationPattern[] NeededPatterns(Options o) => o.Mode switch
    {
        "click" => [InvokePattern.Pattern, SelectionItemPattern.Pattern, TogglePattern.Pattern],
        "set" => [ValuePattern.Pattern],
        "select" => [SelectionItemPattern.Pattern],
        "toggle" => [TogglePattern.Pattern],
        "expand" => [ExpandCollapsePattern.Pattern],
        "scroll" => [ScrollItemPattern.Pattern],
        _ => [],
    };

    private static ElementSnapshot PickMatch(Options o, SearchContext ctx)
    {
        // Operate modes: prefer matches that actually support the needed pattern.
        // WPF bridges popup content (combobox dropdowns, context menus) into the
        // main-window tree as ghost copies without any pattern; the real element
        // lives in the popup HWND (or further down the match list).
        var usable = ctx.Matches;
        var needed = NeededPatterns(o);
        if (needed.Length > 0)
        {
            var withPattern = ctx.Matches.Where(m => needed.Any(p => m.Element.TryGetCurrentPattern(p, out _))).ToList();
            if (withPattern.Count > 0)
            {
                if (withPattern.Count < ctx.Matches.Count)
                    Console.Error.WriteLine($"skipped {ctx.Matches.Count - withPattern.Count} pattern-less ghost match(es); picking among {withPattern.Count} usable");
                usable = withPattern;
            }
        }
        if (usable.Count > 1)
        {
            Console.Error.WriteLine($"{usable.Count} elements matched; using #{o.Index} (rerun with --index k to pick another):");
            for (int i = 0; i < Math.Min(usable.Count, 10); i++)
                Console.Error.WriteLine($"  #{i + 1} {Describe(usable[i].Element)}");
            if (usable.Count > 10)
                Console.Error.WriteLine($"  ... and {usable.Count - 10} more");
        }
        if (o.Index > usable.Count)
            throw new InvalidOperationException($"--index {o.Index} out of range: only {usable.Count} match(es)");
        return usable[o.Index - 1];
    }

    private static string WindowDesc(Options o) =>
        o.Title != null ? $"--title '{o.Title}'" :
        o.ProcessName != null ? $"--process '{o.ProcessName}'" :
        o.Pid != 0 ? $"--pid {o.Pid}" : $"--hwnd {o.Hwnd}";

    private static string ElementDesc(Options o)
    {
        var parts = new List<string>();
        if (o.Name != null) parts.Add($"--name '{o.Name}'");
        if (o.Id != null) parts.Add($"--id '{o.Id}'");
        if (o.ClassName != null) parts.Add($"--class '{o.ClassName}'");
        if (o.Control != null) parts.Add($"--control {o.Control}");
        return string.Join(" ", parts);
    }

    // ---------- element description ----------

    private static string Describe(AutomationElement e)
    {
        var info = GetInfo(e);
        if (info == null) return "<gone>";
        string? value = ValueOf(e);
        var sb = new StringBuilder();
        sb.Append(info.TypeName);
        sb.Append($" \"{Trim(info.Name)}\"");
        if (info.AutomationId.Length > 0) sb.Append($" id='{info.AutomationId}'");
        if (info.ClassName.Length > 0) sb.Append($" class='{info.ClassName}'");
        if (value != null) sb.Append($" value=\"{Trim(value)}\"");
        if (!info.Rect.IsEmpty)
            sb.Append($" rect={R(info.Rect.Left)},{R(info.Rect.Top)} {R(info.Rect.Width)}x{R(info.Rect.Height)}");
        if (!info.IsEnabled) sb.Append(" [disabled]");
        if (info.IsOffscreen) sb.Append(" [offscreen]");
        return sb.ToString();
    }

    private static string? ValueOf(AutomationElement e)
    {
        if (GetInfo(e) is not { } info) return null;
        if (!e.TryGetCurrentPattern(ValuePattern.Pattern, out object? p)) return null;
        if (info.IsPassword) return "********";
        try { return ((ValuePattern)p).Current.Value; }
        catch (ElementNotAvailableException) { return null; }
    }

    private static string PatternsOf(AutomationElement e)
    {
        var names = new List<string>();
        foreach (var (pattern, label) in new (AutomationPattern, string)[]
                 {
                     (InvokePattern.Pattern, "Invoke"),
                     (ValuePattern.Pattern, "Value"),
                     (SelectionItemPattern.Pattern, "SelectionItem"),
                     (TogglePattern.Pattern, "Toggle"),
                     (ExpandCollapsePattern.Pattern, "ExpandCollapse"),
                     (TextPattern.Pattern, "Text"),
                     (ScrollItemPattern.Pattern, "ScrollItem"),
                 })
        {
            if (e.TryGetCurrentPattern(pattern, out _)) names.Add(label);
        }
        return names.Count > 0 ? string.Join(",", names) : "-";
    }

    private static string Trim(string? s, int max = 120) =>
        string.IsNullOrEmpty(s) ? "" :
        s.Length <= max ? s : s[..(max - 1)] + "…";

    private static int R(double d) => (int)Math.Round(d);

    private static string Fmt(double d) => d.ToString(CultureInfo.InvariantCulture);

    // ---------- modes: read ----------

    private static int RunList()
    {
        var wins = TopLevelWindows();
        Console.WriteLine("Windows:");
        int shown = 0;
        foreach (var w in wins)
        {
            var info = GetInfo(w);
            if (info == null || info.IsOffscreen || string.IsNullOrEmpty(info.Name)) continue;
            string proc = "?";
            try { proc = Process.GetProcessById(info.ProcessId).ProcessName; } catch { /* exited */ }
            // Minimized windows report an empty/garbage rect; show a marker instead.
            bool minimized = info.Rect.IsEmpty || info.Rect.Width <= 0 || info.Rect.Height <= 0
                             || info.Rect.X > 100_000 || info.Rect.Y > 100_000;
            string rect = minimized ? "minimized"
                : $"rect={R(info.Rect.Left)},{R(info.Rect.Top)} {R(info.Rect.Width)}x{R(info.Rect.Height)}";
            Console.WriteLine($"  [{shown + 1}] \"{Trim(info.Name, 80)}\" pid={info.ProcessId} proc={proc} " +
                              $"hwnd=0x{info.NativeWindowHandle.ToString("X", CultureInfo.InvariantCulture)} {rect}");
            shown++;
        }
        Console.WriteLine($"total: {shown} visible top-level windows");
        return 0;
    }

    private static int RunTree(Options o)
    {
        var (win, _) = Resolve(o, needElement: false);
        bool json = o.Format == "json";
        var ctx = new SearchContext(o);
        var sb = new StringBuilder();
        byte[] jsonBytes = [];

        if (json)
        {
            using var ms = new MemoryStream();
            using (var jw = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true, Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                jw.WriteStartObject();
                EmitNode(null, jw, ctx, o, win, 0);
                jw.WriteEndObject();
            }
            jsonBytes = ms.ToArray();
        }
        else
        {
            EmitNode(sb, null, ctx, o, win, 0);
            if (ctx.Truncated)
                sb.AppendLine($"... (truncated at {o.MaxNodes} nodes; raise --max-nodes for more)");
        }

        if (o.Out != null)
        {
            if (json) File.WriteAllBytes(o.Out, jsonBytes);
            else File.WriteAllText(o.Out, sb.ToString());
            Console.WriteLine(Path.GetFullPath(o.Out));
        }
        else if (json)
            Console.OpenStandardOutput().Write(jsonBytes);
        else
            Console.Write(sb.ToString());
        return 0;
    }

    private static void EmitNode(StringBuilder? sb, Utf8JsonWriter? jw, SearchContext ctx, Options o, AutomationElement e, int depth)
    {
        ctx.Visited++;
        var info = GetInfo(e);
        if (info == null) return;

        string? value = ValueOf(e);

        if (sb != null)
        {
            sb.Append(' ', depth * 2);
            sb.AppendLine(Describe(e));
        }
        if (jw != null)
        {
            jw.WriteString("type", info.TypeName);
            jw.WriteString("name", info.Name ?? "");
            if (info.AutomationId.Length > 0) jw.WriteString("automationId", info.AutomationId);
            if (info.ClassName.Length > 0) jw.WriteString("className", info.ClassName);
            if (value != null) jw.WriteString("value", value);
            if (!info.Rect.IsEmpty) jw.WriteString("rect", $"{R(info.Rect.Left)},{R(info.Rect.Top)} {R(info.Rect.Width)}x{R(info.Rect.Height)}");
            if (!info.IsEnabled) jw.WriteBoolean("enabled", false);
            if (info.IsOffscreen) jw.WriteBoolean("offscreen", true);
        }

        if (depth >= o.Depth || ctx.Visited >= o.MaxNodes)
        {
            ctx.Truncated = true;
            return;
        }

        if (jw != null) jw.WriteStartArray("children");
        AutomationElement? child = null;
        try { child = ctx.Walker.GetFirstChild(e); }
        catch (ElementNotAvailableException) { }
        while (child != null)
        {
            if (jw != null) jw.WriteStartObject();
            EmitNode(sb, jw, ctx, o, child, depth + 1);
            if (jw != null) jw.WriteEndObject();
            if (ctx.Visited >= o.MaxNodes) { ctx.Truncated = true; break; }
            AutomationElement? next = null;
            try { next = ctx.Walker.GetNextSibling(child); }
            catch (ElementNotAvailableException) { }
            child = next;
        }
        if (jw != null) jw.WriteEndArray();
    }

    private static int RunFind(Options o)
    {
        var (win, _) = Resolve(o, needElement: false);
        var info = GetInfo(win);
        Console.WriteLine($"window: \"{Trim(info?.Name, 80)}\" pid={info?.ProcessId} " +
                          $"hwnd=0x{(info?.NativeWindowHandle ?? 0).ToString("X", CultureInfo.InvariantCulture)}" +
                          (o.AnyWindow ? " (+all matching windows)" : ""));

        var ctx = o.AnyWindow ? MergeSearch(o, TryResolveWindows(o)) : SearchOnce(o, win);
        if (ctx.Matches.Count == 0)
            throw new InvalidOperationException(
                $"no element matched {ElementDesc(o)} (visited {ctx.Visited} nodes" +
                (ctx.Truncated ? $", truncated at {o.MaxNodes}" : "") + ")");
        for (int i = 0; i < ctx.Matches.Count; i++)
        {
            var e = ctx.Matches[i].Element;
            Console.WriteLine($"#{i + 1} {Describe(e)} patterns={PatternsOf(e)}");
        }
        if (ctx.Truncated)
            Console.Error.WriteLine($"note: search truncated at {o.MaxNodes} nodes; raise --max-nodes if the element may be deeper");
        return 0;
    }

    // ---------- modes: operate ----------

    private static int RunClick(Options o)
    {
        var (win, el) = Resolve(o, needElement: true);
        var e = el!.Element;
        if (o.Real || o.Right || o.Double || o.Hover)
            return RealMouse(o, e);
        if (e.TryGetCurrentPattern(InvokePattern.Pattern, out object? p))
        {
            ((InvokePattern)p).Invoke();
            Console.WriteLine($"invoked: {Describe(e)}");
        }
        else if (e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out p))
        {
            ((SelectionItemPattern)p).Select();
            Console.WriteLine($"selected (no Invoke pattern): {Describe(e)}");
        }
        else if (e.TryGetCurrentPattern(TogglePattern.Pattern, out p))
        {
            ((TogglePattern)p).Toggle();
            Console.WriteLine($"toggled (no Invoke pattern): {Describe(e)}");
        }
        else
            throw new InvalidOperationException($"element has no clickable pattern (Invoke/SelectionItem/Toggle): {Describe(e)}; patterns={PatternsOf(e)}");
        return 0;
    }

    private static int RunSet(Options o)
    {
        var (win, el) = Resolve(o, needElement: true);
        var e = el!.Element;
        if (!e.TryGetCurrentPattern(ValuePattern.Pattern, out object? p))
            throw new InvalidOperationException($"element has no Value pattern (cannot set text): {Describe(e)}; patterns={PatternsOf(e)}");
        var vp = (ValuePattern)p;
        if (vp.Current.IsReadOnly)
            throw new InvalidOperationException($"element is read-only: {Describe(e)}");
        vp.SetValue(o.Value!);
        Console.WriteLine($"set value \"{o.Value}\" on: {Describe(e)}");
        return 0;
    }

    private static int RunSelect(Options o)
    {
        var (win, el) = Resolve(o, needElement: true);
        var e = el!.Element;
        if (!e.TryGetCurrentPattern(SelectionItemPattern.Pattern, out object? p))
            throw new InvalidOperationException($"element has no SelectionItem pattern: {Describe(e)}; patterns={PatternsOf(e)}");
        ((SelectionItemPattern)p).Select();
        Console.WriteLine($"selected: {Describe(e)}");
        return 0;
    }

    private static int RunToggle(Options o)
    {
        var (win, el) = Resolve(o, needElement: true);
        var e = el!.Element;
        if (!e.TryGetCurrentPattern(TogglePattern.Pattern, out object? p))
            throw new InvalidOperationException($"element has no Toggle pattern: {Describe(e)}; patterns={PatternsOf(e)}");
        var tp = (TogglePattern)p;
        tp.Toggle();
        Console.WriteLine($"toggled to {tp.Current.ToggleState}: {Describe(e)}");
        return 0;
    }

    private static int RunExpand(Options o)
    {
        var (win, el) = Resolve(o, needElement: true);
        var e = el!.Element;
        if (!e.TryGetCurrentPattern(ExpandCollapsePattern.Pattern, out object? p))
            throw new InvalidOperationException($"element has no ExpandCollapse pattern: {Describe(e)}; patterns={PatternsOf(e)}");
        var ec = (ExpandCollapsePattern)p;
        if (o.Collapse) ec.Collapse(); else ec.Expand();
        Console.WriteLine($"{(o.Collapse ? "collapsed" : "expanded")} (state={ec.Current.ExpandCollapseState}): {Describe(e)}");
        return 0;
    }

    private static int RunKeys(Options o)
    {
        var (win, el) = Resolve(o, needElement: true);
        var e = el!.Element;
        var info = GetInfo(win);
        if (info != null && info.NativeWindowHandle != 0)
            SetForegroundWindow(new IntPtr(info.NativeWindowHandle));
        Thread.Sleep(150);
        e.SetFocus();
        Thread.Sleep(50);
        System.Windows.Forms.SendKeys.SendWait(o.Text!);
        Console.WriteLine($"sent keys to: {Describe(e)}");
        return 0;
    }

    private static int RunWait(Options o)
    {
        bool hasElementSelector = o.Name != null || o.Id != null || o.ClassName != null || o.Control != null;
        var sw = Stopwatch.StartNew();
        int timeoutMs = (int)(o.TimeoutSec * 1000);
        while (true)
        {
            var win = TryResolveWindow(o);
            List<ElementSnapshot> live = [];
            if (win != null && hasElementSelector)
                live = ValueMatches(o, SearchOnce(o, win).Matches);

            if (o.Gone)
            {
                if (win == null)
                {
                    Console.WriteLine($"window gone: {WindowDesc(o)}");
                    return 0;
                }
                if (hasElementSelector && live.Count == 0)
                {
                    Console.WriteLine("element gone: " + ElementDesc(o)
                        + (o.Value != null ? $" (value~'{o.Value}')" : ""));
                    return 0;
                }
            }
            else if (win != null)
            {
                if (!hasElementSelector)
                {
                    Console.WriteLine($"window appeared: {Describe(win)}");
                    return 0;
                }
                if (live.Count > 0)
                {
                    Console.WriteLine($"element appeared: {Describe(live[0].Element)}");
                    return 0;
                }
            }
            if (sw.ElapsedMilliseconds >= timeoutMs) break;
            Thread.Sleep(PollIntervalMs);
        }
        if (o.Gone)
            throw new InvalidOperationException($"timed out after {Fmt(o.TimeoutSec)}s; still present: " +
                (hasElementSelector ? $"element {ElementDesc(o)}" : $"window {WindowDesc(o)}"));
        throw new InvalidOperationException(
            $"timed out after {Fmt(o.TimeoutSec)}s waiting for " +
            (hasElementSelector ? $"element {ElementDesc(o)} in window {WindowDesc(o)}" : $"window {WindowDesc(o)}"));
    }

    /// <summary>wait --value &lt;substring&gt;: a match only counts when its Value (or Name for
    /// text blocks without ValuePattern) contains the substring. Lets callers wait for a
    /// status line to change, e.g. from "connecting" to "connected".</summary>
    private static List<ElementSnapshot> ValueMatches(Options o, List<ElementSnapshot> matches)
    {
        if (o.Value == null) return matches;
        return matches.Where(m =>
        {
            string s = ValueOf(m.Element) ?? GetInfo(m.Element)?.Name ?? "";
            return s.Contains(o.Value, StringComparison.OrdinalIgnoreCase);
        }).ToList();
    }

    private static int RunScroll(Options o)
    {
        var (win, el) = Resolve(o, needElement: true);
        var e = el!.Element;

        // With --value the element selector means the CONTAINER (List/Tree/Table):
        // scroll through it in large increments until an item whose Name/Value
        // contains the substring materializes (virtualized rows are absent from
        // the UIA tree until scrolled into view), then bring that item into view.
        if (o.Value != null)
        {
            if (!e.TryGetCurrentPattern(ScrollPattern.Pattern, out object? sp))
                throw new InvalidOperationException($"scroll-search (--value) requires a container with ScrollPattern: {Describe(e)}; patterns={PatternsOf(e)}");
            var scroll = (ScrollPattern)sp;
            var so = new Options { Name = o.Value };
            // Rewind to the top, then sweep downward screen by screen: virtualized
            // items outside the viewport are absent from the tree, so one full
            // top-to-bottom pass materializes everything regardless of the
            // container's current scroll position.
            try { scroll.SetScrollPercent(ScrollPattern.NoScroll, 0); Thread.Sleep(150); }
            catch (InvalidOperationException) { /* not scrollable right now */ }
            for (int round = 0; round < 300; round++)
            {
                var ctx = new SearchContext(so);
                Walk(ctx, so, e, 0);
                if (ctx.Matches.Count > 0)
                {
                    var item = ctx.Matches[0].Element;
                    if (item.TryGetCurrentPattern(ScrollItemPattern.Pattern, out object? ip))
                    {
                        ((ScrollItemPattern)ip).ScrollIntoView();
                        Thread.Sleep(150);
                    }
                    Console.WriteLine($"found and scrolled into view: {Describe(item)}");
                    return 0;
                }
                double hBefore = scroll.Current.HorizontalScrollPercent;
                double vBefore = scroll.Current.VerticalScrollPercent;
                if (scroll.Current.VerticallyScrollable)
                    scroll.ScrollVertical(ScrollAmount.LargeIncrement);
                if (scroll.Current.HorizontallyScrollable)
                    scroll.ScrollHorizontal(ScrollAmount.LargeIncrement);
                Thread.Sleep(120);
                if (scroll.Current.VerticalScrollPercent == vBefore &&
                    scroll.Current.HorizontalScrollPercent == hBefore)
                    break; // reached the end without a match
            }
            throw new InvalidOperationException(
                $"no item matching '{o.Value}' found in the container even after scrolling to the end: {Describe(e)}");
        }

        if (!e.TryGetCurrentPattern(ScrollItemPattern.Pattern, out object? p))
            throw new InvalidOperationException($"element has no ScrollItem pattern (cannot scroll into view): {Describe(e)}; patterns={PatternsOf(e)}");
        ((ScrollItemPattern)p).ScrollIntoView();
        Thread.Sleep(150); // let the container settle after scrolling
        Console.WriteLine($"scrolled into view: {Describe(e)}");
        return 0;
    }

    private static int RunMenu(Options o)
    {
        // Right-click the target element, then find and invoke a menu item in the
        // popup (a separate top-level HWND of the same process), all in one run:
        // the popup usually closes before a second process could search it.
        var (win, el) = Resolve(o, needElement: true);
        Console.Error.WriteLine($"right-clicking target: {Describe(el!.Element)}");
        RealMouse(new Options { Mode = "click", Right = true }, el.Element);
        Thread.Sleep(150);

        var mo = new Options { Name = o.MenuText, Control = "MenuItem" };
        var sw = Stopwatch.StartNew();
        int timeoutMs = (int)(o.TimeoutSec * 1000);
        while (true)
        {
            foreach (var w in TryResolveWindows(o))
            {
                var ctx = new SearchContext(mo);
                Walk(ctx, mo, w, 0);
                if (ctx.Matches.Count > 0)
                {
                    var item = ctx.Matches[Math.Min(o.Index, ctx.Matches.Count) - 1].Element;
                    if (item.TryGetCurrentPattern(InvokePattern.Pattern, out object? p))
                    {
                        ((InvokePattern)p).Invoke();
                        Console.WriteLine($"menu invoked: {Describe(item)}");
                    }
                    else
                    {
                        Console.Error.WriteLine("menu item has no Invoke pattern; coordinate-clicking it");
                        RealMouse(new Options(), item);
                    }
                    return 0;
                }
            }
            if (sw.ElapsedMilliseconds >= timeoutMs) break;
            Thread.Sleep(80);
        }
        throw new InvalidOperationException(
            $"menu item '{o.MenuText}' did not appear within {Fmt(o.TimeoutSec)}s after right-click");
    }

    /// <summary>Real mouse action on the element's rect center. Moves the user's cursor
    /// and takes focus; use for controls without UIA patterns (ghost popup items,
    /// custom-drawn buttons) or for right-click/double-click/hover semantics.</summary>
    private static int RealMouse(Options o, AutomationElement e)
    {
        var info = GetInfo(e) ?? throw new InvalidOperationException("element vanished before mouse action");
        if (info.Rect.IsEmpty || info.Rect.Width <= 0 || info.Rect.Height <= 0)
            throw new InvalidOperationException($"element has no on-screen rect: {Describe(e)}");
        int x = (int)Math.Round(info.Rect.Left + info.Rect.Width / 2);
        int y = (int)Math.Round(info.Rect.Top + info.Rect.Height / 2);
        string what = o.Hover ? "hover" : o.Right ? "right-click" : o.Double ? "double-click" : "click";
        // Real clicks land on whatever is topmost at that screen point; raise the
        // owning window first so an occluded target actually receives them.
        if (info.NativeWindowHandle != 0)
        {
            SetForegroundWindow(new IntPtr(info.NativeWindowHandle));
            Thread.Sleep(150);
        }
        SetCursorPos(x, y);
        Thread.Sleep(80);
        if (!o.Hover)
        {
            if (o.Right)
            {
                mouse_event(MOUSEEVENTF_RIGHTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_RIGHTUP, 0, 0, 0, UIntPtr.Zero);
            }
            else
            {
                mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                if (o.Double)
                {
                    Thread.Sleep(60);
                    mouse_event(MOUSEEVENTF_LEFTDOWN, 0, 0, 0, UIntPtr.Zero);
                    mouse_event(MOUSEEVENTF_LEFTUP, 0, 0, 0, UIntPtr.Zero);
                }
            }
        }
        Console.WriteLine($"{what} at {x},{y} on: {Describe(e)}");
        return 0;
    }

    // ---------- help ----------

    private static void PrintHelp()
    {
        Console.WriteLine("""
        uia.cs - read window content and operate controls via UI Automation (no screenshots).

        Usage: dotnet run --file uia.cs -- --mode <mode> [window selector] [element selectors] [options]

        Modes:
          list     list visible top-level windows (no window selector needed)
          tree     dump the UIA tree of the target window (read)
          find     search elements in the target window and print them with Bounds (read)
          click    click via Invoke pattern (falls back to Select/Toggle);
                   --real/--right/--double/--hover: real mouse at the element's
                   rect center (moves the cursor, needed for ghost popup items)
          set      write text via Value pattern (needs --value)
          select   select via SelectionItem pattern (tab item / list item)
          toggle   toggle via Toggle pattern
          expand   expand via ExpandCollapse pattern (--collapse to collapse)
          keys     send keystrokes via SendKeys (needs --text; steals foreground focus!)
          wait     poll until the window/element appears (--timeout, default 3s);
                   --gone: wait for disappearance; --value <sub>: only count matches
                   whose Value (or Name) contains the substring (status changes)
          scroll   bring the element into view via ScrollItem pattern (for
                   offscreen items in long/virtualized lists)
          menu     right-click the element, then find --menu <text> item in the
                   popup and invoke it, all in one run (context menus)

        Window selectors (exactly one required except for list):
          --title <substring>     window title substring, case-insensitive
          --process <name>        process name, with or without .exe
          --pid <n>               process id
          --hwnd <n|0xHEX>        window handle
          --any-window            search ALL matching top-level windows of the target,
                                   not just the first (popups/dropdowns are separate HWNDs)

        Element selectors (any combination; required for find/click/set/select/toggle/expand/keys/scroll/menu):
          --name <substring>      element Name substring, case-insensitive
          --id <AutomationId>     exact AutomationId (most stable)
          --class <ClassName>     exact class name
          --control <Type>        ControlType: Button, Edit, TabItem, ListItem, Text, ...
          --index <n>             pick the n-th match (1-based, default 1)

        Options:
          --value <text>          value for set; substring filter for wait
          --text <keys>           SendKeys syntax for keys, e.g. "hello{ENTER}" or "^a{DEL}"
          --menu <text>           menu item text for --mode menu
          --collapse              for expand mode: collapse instead
          --gone                  for wait mode: wait until gone instead of appeared
          --real/--right/--double/--hover  for click/menu: real mouse at coordinates
          --timeout <seconds>     find/wait polling timeout (default 3)
          --view <content|control|raw>   tree walk view (default content)
          --depth <n>             max tree depth (default 30)
          --max-nodes <n>         max visited nodes (default 4000)
          --format <tree|json>    tree output format (default tree)
          --out, -o <path>        write tree output to a file instead of stdout
          --help, -h              this help

        Exit codes: 0 ok, 1 runtime error, 2 usage error. stdout is UTF-8.

        Pairing with csharp-screenshot (this tool never screenshots):
          uia --mode find --process app --name "OK"            # gives rect=L,T WxH
          screenshot --process app --region L,T,W,H           # crops that control
        """);
    }

    // ---------- win32 ----------

    [DllImport("user32.dll")]
    private static extern bool SetProcessDpiAwarenessContext(IntPtr value);

    [DllImport("user32.dll")]
    private static extern bool SetProcessDPIAware();

    [DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr hWnd);

    [DllImport("user32.dll")]
    private static extern bool SetCursorPos(int x, int y);

    [DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, UIntPtr dwExtraInfo);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;
}
