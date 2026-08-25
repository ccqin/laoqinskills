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
        ["list", "tree", "find", "click", "set", "select", "toggle", "expand", "keys", "wait"];

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
        if (o.Mode is ("find" or "click" or "set" or "select" or "toggle" or "expand" or "keys") && !hasElementSelector)
            throw new UsageException($"mode '{o.Mode}' requires an element selector: --name/--id/--class/--control");
        if (o.Mode == "set" && o.Value == null)
            throw new UsageException("mode 'set' requires --value <text>");
        if (o.Mode == "keys" && string.IsNullOrEmpty(o.Text))
            throw new UsageException("mode 'keys' requires --text <keystrokes>");

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

    /// <summary>Single attempt to resolve the target window; null if not found right now.</summary>
    private static AutomationElement? TryResolveWindow(Options o)
    {
        if (o.Hwnd != 0)
            return AutomationElement.FromHandle(new IntPtr(o.Hwnd));

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
        return candidates.FirstOrDefault(w => GetInfo(w)?.IsOffscreen == false)
            ?? candidates.FirstOrDefault();
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

    /// <summary>Resolve window (+element when selectors given), polling until timeout.</summary>
    private static (AutomationElement Win, ElementSnapshot? El) Resolve(Options o, bool needElement)
    {
        var sw = Stopwatch.StartNew();
        int timeoutMs = (int)(o.TimeoutSec * 1000);
        SearchContext? last = null;
        AutomationElement? win = null;

        while (true)
        {
            win = TryResolveWindow(o);
            if (win != null)
            {
                if (!needElement) return (win, null);
                last = SearchOnce(o, win);
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

    private static ElementSnapshot PickMatch(Options o, SearchContext ctx)
    {
        if (ctx.Matches.Count > 1)
        {
            Console.Error.WriteLine($"{ctx.Matches.Count} elements matched; using #{o.Index} (rerun with --index k to pick another):");
            for (int i = 0; i < Math.Min(ctx.Matches.Count, 10); i++)
                Console.Error.WriteLine($"  #{i + 1} {Describe(ctx.Matches[i].Element)}");
            if (ctx.Matches.Count > 10)
                Console.Error.WriteLine($"  ... and {ctx.Matches.Count - 10} more");
        }
        if (o.Index > ctx.Matches.Count)
            throw new InvalidOperationException($"--index {o.Index} out of range: only {ctx.Matches.Count} match(es)");
        return ctx.Matches[o.Index - 1];
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
                          $"hwnd=0x{(info?.NativeWindowHandle ?? 0).ToString("X", CultureInfo.InvariantCulture)}");

        var ctx = SearchOnce(o, win);
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
            if (win != null)
            {
                if (!hasElementSelector)
                {
                    Console.WriteLine($"window appeared: {Describe(win)}");
                    return 0;
                }
                var ctx = SearchOnce(o, win);
                if (ctx.Matches.Count > 0)
                {
                    Console.WriteLine($"element appeared: {Describe(ctx.Matches[0].Element)}");
                    return 0;
                }
            }
            if (sw.ElapsedMilliseconds >= timeoutMs) break;
            Thread.Sleep(PollIntervalMs);
        }
        throw new InvalidOperationException(
            $"timed out after {Fmt(o.TimeoutSec)}s waiting for " +
            (hasElementSelector ? $"element {ElementDesc(o)} in window {WindowDesc(o)}" : $"window {WindowDesc(o)}"));
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
          click    click via Invoke pattern (falls back to Select/Toggle)
          set      write text via Value pattern (needs --value)
          select   select via SelectionItem pattern (tab item / list item)
          toggle   toggle via Toggle pattern
          expand   expand via ExpandCollapse pattern (--collapse to collapse)
          keys     send keystrokes via SendKeys (needs --text; steals foreground focus!)
          wait     poll until the window/element appears (--timeout, default 3s)

        Window selectors (exactly one required except for list):
          --title <substring>     window title substring, case-insensitive
          --process <name>        process name, with or without .exe
          --pid <n>               process id
          --hwnd <n|0xHEX>        window handle

        Element selectors (any combination; required for find/click/set/select/toggle/expand/keys):
          --name <substring>      element Name substring, case-insensitive
          --id <AutomationId>     exact AutomationId (most stable)
          --class <ClassName>     exact class name
          --control <Type>        ControlType: Button, Edit, TabItem, ListItem, Text, ...
          --index <n>             pick the n-th match (1-based, default 1)

        Options:
          --value <text>          value for set
          --text <keys>           SendKeys syntax for keys, e.g. "hello{ENTER}" or "^a{DEL}"
          --collapse              for expand mode: collapse instead
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
}
