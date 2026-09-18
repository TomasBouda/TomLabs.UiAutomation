using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Reflection;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Input;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input;
using Avalonia.Input.Raw;
using Avalonia.Media.Imaging;
using Avalonia.Styling;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace TomLabs.UiAutomation;

/// <summary>
/// Loopback HTTP channel that lets a script (or Claude Code) drive this application without touching the user's
/// keyboard, mouse or screen: it renders the window through a <see cref="RenderTargetBitmap"/>, dumps the visual
/// tree and injects input straight into the Avalonia window through the same raw-input path the platform uses.
/// It works identically against the real desktop window and against the headless platform
/// (<see cref="UiAutomationAppBuilderExtensions.UsePlatformDetectOrHeadless"/>).
/// </summary>
/// <remarks>
/// Enabled only when the environment variable <c>&lt;APP&gt;_AUTOMATION</c> holds a port number; it only ever listens on 127.0.0.1.
/// Endpoints take GET or POST with query-string arguments and answer JSON, plain text (tree) or PNG (screenshot):
/// <code>
/// /                                 status: name, version, platform, windows
/// /screenshot?window=&amp;scale=&amp;path=  PNG of the window (default main; "top" = last opened); path also saves it
/// /tree?window=&amp;name=&amp;depth=&amp;all=1   visual tree with names, classes, bounds and text (hidden nodes with all=1)
/// /find?text=|name=|type=           matching visible controls with their bounds
/// /click?name=|text=|x=&amp;y=&amp;button=right&amp;count=2   synthetic pointer press + release at the control's centre
/// /key?gesture=Ctrl+Shift+K         key down + up on the focused element (Avalonia KeyGesture syntax)
/// /type?text=                       text input into the focused element
/// /get?path=SelectedApp.Name        read a property path on the window's DataContext
/// /set?path=Search&amp;value=drop        write a property on the DataContext (or name=Control&amp;property=Text)
/// /invoke?command=ToggleSigningCommand&amp;parameter=   execute an ICommand of the DataContext
/// /do/&lt;action&gt;?arg=                 app-specific action registered through <see cref="Actions"/>
/// /theme?variant=dark|light|default  switch the theme variant
/// /resize?width=&amp;height=            resize the main window
/// /wait?ms=                         let timers and layout settle
/// /quit                             shut the application down
/// </code>
/// </remarks>
public sealed class UiAutomationServer : IDisposable
{
    /// <summary>Environment variable that enables the server; its value is the loopback port.</summary>
    public static string VariableName(string appName) => appName.ToUpperInvariant() + "_AUTOMATION";

    private readonly UiAutomationHost _host;
    private readonly string _name;
    private readonly string _version;
    private readonly Action<string> _log;
    private readonly HttpListener _listener = new();
    private readonly CancellationTokenSource _cts = new();
    private readonly MouseDevice _mouse = new(new Avalonia.Input.Pointer(Avalonia.Input.Pointer.GetNextFreeId(), PointerType.Mouse, isPrimary: true));

    /// <summary>App-specific actions, reachable as <c>/do/{name}?arg=…</c>; run on the UI thread.</summary>
    public Dictionary<string, Func<Window, string?, object?>> Actions { get; } = new(StringComparer.OrdinalIgnoreCase);

    public int Port { get; }

    /// <summary>Milliseconds to let layout and bindings settle after an input-like command before answering.</summary>
    public int SettleMilliseconds { get; set; } = 150;

    public UiAutomationServer(UiAutomationHost host, int port, string name, string version, Action<string>? log = null)
    {
        _host = host;
        _name = name;
        _version = version;
        _log = log ?? (_ => { });
        Port = port;
    }

    /// <summary>Starts the server when <c>&lt;NAME&gt;_AUTOMATION</c> names a port; returns null otherwise.</summary>
    public static UiAutomationServer? StartIfEnabled(IClassicDesktopStyleApplicationLifetime lifetime, string name, string version, Action<string>? log = null) =>
        StartIfEnabled(UiAutomationHost.FromLifetime(lifetime), name, version, log);

    /// <summary>Starts the server when <c>&lt;NAME&gt;_AUTOMATION</c> names a port; returns null otherwise.</summary>
    public static UiAutomationServer? StartIfEnabled(UiAutomationHost host, string name, string version, Action<string>? log = null)
    {
        var value = Environment.GetEnvironmentVariable(VariableName(name));
        if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var port) || port <= 0)
            return null;
        var server = new UiAutomationServer(host, port, name, version, log);
        server.Start();
        return server;
    }

    public void Start()
    {
        _listener.Prefixes.Add($"http://127.0.0.1:{Port}/");
        _listener.Start();
        _log($"UI automation listening on http://127.0.0.1:{Port}/");
        _ = Task.Run(AcceptLoopAsync);
    }

    private async Task AcceptLoopAsync()
    {
        while (!_cts.IsCancellationRequested)
        {
            HttpListenerContext context;
            try { context = await _listener.GetContextAsync(); }
            catch (Exception) when (_cts.IsCancellationRequested) { return; }
            catch (HttpListenerException) { continue; }
            _ = Task.Run(() => HandleAsync(context));
        }
    }

    private async Task HandleAsync(HttpListenerContext context)
    {
        var request = context.Request;
        var response = context.Response;
        var path = request.Url?.AbsolutePath.TrimEnd('/') ?? string.Empty;
        var query = request.QueryString;
        string? Arg(string key) => query[key];

        try
        {
            object? result;
            switch (path.ToLowerInvariant())
            {
                case "":
                    result = await OnUi(_ => Status());
                    break;
                case "/screenshot":
                    var png = await OnUi(w => Screenshot(w, Arg("scale")));
                    if (Arg("path") is { Length: > 0 } file)
                    {
                        // A relative or POSIX-style path (/c/Users/…) would land somewhere under the app's working directory.
                        if (!Path.IsPathFullyQualified(file))
                            throw new ArgumentException("path must be a fully qualified Windows path (C:\\...)");
                        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(file))!);
                        await File.WriteAllBytesAsync(file, png);
                    }
                    await WriteAsync(response, "image/png", png);
                    return;
                case "/tree":
                    var text = await OnUi(w => Tree(Root(w, Arg("name")), ParseInt(Arg("depth")) ?? 64, Arg("all") is "1" or "true"));
                    await WriteAsync(response, "text/plain; charset=utf-8", Encoding.UTF8.GetBytes(text));
                    return;
                case "/find":
                    result = await OnUi(w => Find(w, Arg("name"), Arg("text"), Arg("type")).Select(Describe).ToList());
                    break;
                case "/click":
                    result = await OnUi(w => Click(w, Arg("name"), Arg("text"), ParseDouble(Arg("x")), ParseDouble(Arg("y")), Arg("button"), ParseInt(Arg("count")) ?? 1), settle: true);
                    break;
                case "/key":
                    result = await OnUi(w => Key(w, Arg("gesture") ?? Arg("key") ?? throw new ArgumentException("gesture is required")), settle: true);
                    break;
                case "/type":
                    result = await OnUi(w => TypeText(w, Arg("text") ?? string.Empty), settle: true);
                    break;
                case "/get":
                    result = await OnUi(w => Serializable(GetPath(w.DataContext, Arg("path") ?? throw new ArgumentException("path is required"))));
                    break;
                case "/set":
                    result = await OnUi(w => Set(w, Arg("name"), Arg("property"), Arg("path"), Arg("value") ?? string.Empty), settle: true);
                    break;
                case "/invoke":
                    result = await OnUi(w => Invoke(w, Arg("command") ?? throw new ArgumentException("command is required"), Arg("parameter")), settle: true);
                    break;
                case "/theme":
                    result = await OnUi(w => Theme(Arg("variant") ?? "default"), settle: true);
                    break;
                case "/resize":
                    result = await OnUi(w => Resize(w, ParseDouble(Arg("width")), ParseDouble(Arg("height"))), settle: true);
                    break;
                case "/wait":
                    await Task.Delay(ParseInt(Arg("ms")) ?? SettleMilliseconds);
                    result = await OnUi(_ => "ok", settle: true);
                    break;
                case "/quit":
                    await WriteJsonAsync(response, new { ok = true });
                    await Dispatcher.UIThread.InvokeAsync(() => _host.Shutdown());
                    return;
                default:
                    if (path.StartsWith("/do/", StringComparison.OrdinalIgnoreCase) && Actions.TryGetValue(path[4..], out var action))
                    {
                        result = await OnUi(w => Serializable(action(w, Arg("arg"))), settle: true);
                        break;
                    }
                    response.StatusCode = 404;
                    await WriteJsonAsync(response, new { error = $"Unknown endpoint '{path}'" });
                    return;
            }
            await WriteJsonAsync(response, result ?? new { ok = true });
        }
        catch (Exception ex)
        {
            _log($"UI automation {path} failed: {ex.Message}");
            try
            {
                response.StatusCode = ex is ArgumentException or KeyNotFoundException ? 400 : 500;
                await WriteJsonAsync(response, new { error = ex.Message });
            }
            catch (Exception) { /* client went away */ }
        }

        Visual Root(Window window, string? name) => name is null ? window : FindByName(window, name) ?? throw new KeyNotFoundException($"No control named '{name}'");

        async Task<T> OnUi<T>(Func<Window, T> work, bool settle = false)
        {
            var result = await Dispatcher.UIThread.InvokeAsync(() =>
            {
                var window = TargetWindow(Arg("window"));
                // Bounds and hit testing need a current layout and a committed frame; a window shown a moment ago may
                // have had neither (the headless render timer only ticks on request outside a running main loop).
                window.UpdateLayout();
                RenderIfHeadless();
                return work(window);
            });
            if (settle)
                await SettleAsync();
            return result;
        }
    }

    /// <summary>Lets pending dispatcher work, animations and bindings run, then forces a layout pass.</summary>
    private async Task SettleAsync()
    {
        await Task.Delay(SettleMilliseconds);
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            foreach (var window in _host.Windows())
                window.UpdateLayout();
            RenderIfHeadless();
        }, DispatcherPriority.Background);
    }

    private static bool? s_headless;

    private static void RenderIfHeadless()
    {
        s_headless ??= AvaloniaLocator.Current.GetService<Avalonia.Platform.IWindowingPlatform>()?.GetType().Namespace == "Avalonia.Headless";
        if (s_headless == true)
            Avalonia.Headless.AvaloniaHeadlessPlatform.ForceRenderTimerTick();
    }

    // ----- commands (UI thread) -----

    private Window TargetWindow(string? selector)
    {
        var windows = _host.Windows();
        if (windows.Count == 0)
            throw new InvalidOperationException("No window is open");
        if (string.IsNullOrEmpty(selector) || selector == "main")
            return _host.MainWindow() ?? windows[0];
        if (selector == "top")
            return windows[^1];
        if (int.TryParse(selector, out var index) && index >= 0 && index < windows.Count)
            return windows[index];
        return windows.FirstOrDefault(w => string.Equals(w.Title, selector, StringComparison.OrdinalIgnoreCase))
            ?? throw new KeyNotFoundException($"No window '{selector}'; open: {string.Join(", ", windows.Select(w => w.Title))}");
    }

    private object Status() => new
    {
        name = _name,
        version = _version,
        platform = AvaloniaLocator.Current.GetService<Avalonia.Platform.IWindowingPlatform>()?.GetType().Namespace ?? "unknown",
        theme = Application.Current?.ActualThemeVariant.ToString(),
        windows = _host.Windows().Select((w, i) => new
        {
            index = i,
            title = w.Title,
            width = w.Bounds.Width,
            height = w.Bounds.Height,
            scaling = w.RenderScaling,
            isMain = ReferenceEquals(w, _host.MainWindow()),
            focused = (w.FocusManager?.GetFocusedElement() as Control)?.GetType().Name,
        }).ToList(),
    };

    private static byte[] Screenshot(Window window, string? scaleArg)
    {
        // Default to the real render scaling so a desktop screenshot matches what the user sees on their DPI;
        // the headless platform renders at 1.0.
        var scale = ParseDouble(scaleArg) ?? window.RenderScaling;
        var size = new PixelSize(Math.Max(1, (int)Math.Ceiling(window.Bounds.Width * scale)), Math.Max(1, (int)Math.Ceiling(window.Bounds.Height * scale)));
        using var bitmap = new RenderTargetBitmap(size, new Vector(96 * scale, 96 * scale));
        bitmap.Render(window);
        using var stream = new MemoryStream();
        bitmap.Save(stream);
        return stream.ToArray();
    }

    private static string Tree(Visual root, int maxDepth, bool includeHidden)
    {
        var builder = new StringBuilder();
        Walk(root, 0);
        return builder.ToString();

        void Walk(Visual visual, int depth)
        {
            if (depth > maxDepth)
                return;
            var hidden = !visual.IsVisible;
            if (hidden && !includeHidden)
                return;
            builder.Append(' ', depth * 2).Append(Line(visual));
            if (hidden)
                builder.Append(" (hidden)");
            builder.AppendLine();
            foreach (var child in visual.GetVisualChildren())
                Walk(child, depth + 1);
        }
    }

    private static string Line(Visual visual)
    {
        var builder = new StringBuilder(visual.GetType().Name);
        if (visual is StyledElement styled)
        {
            if (!string.IsNullOrEmpty(styled.Name))
                builder.Append(" #").Append(styled.Name);
            foreach (var cls in styled.Classes)
                builder.Append(cls.StartsWith(':') ? " " : " .").Append(cls);
        }
        var bounds = WindowBounds(visual);
        builder.Append(CultureInfo.InvariantCulture, $" [{bounds.X:0},{bounds.Y:0} {bounds.Width:0}x{bounds.Height:0}]");
        if (TextOf(visual) is { } text)
            builder.Append(" \"").Append(text.Replace("\r", string.Empty).Replace("\n", "\\n")).Append('"');
        return builder.ToString();
    }

    private static string? TextOf(Visual visual) => visual switch
    {
        TextBlock t => t.Text,
        TextBox t => t.Text,
        HeaderedContentControl h => h.Header as string,
        ContentControl { Content: string s } => s,
        Avalonia.Controls.Primitives.ToggleButton { IsChecked: { } c } => c.ToString(),
        _ => null,
    };

    private static Rect WindowBounds(Visual visual)
    {
        var root = visual.GetVisualRoot() as Visual;
        var origin = root is null ? null : visual.TranslatePoint(new Point(0, 0), root);
        return new Rect(origin ?? new Point(0, 0), visual.Bounds.Size);
    }

    private static object Describe(Control control)
    {
        var bounds = WindowBounds(control);
        return new
        {
            type = control.GetType().Name,
            name = control.Name,
            classes = control.Classes.Where(c => !c.StartsWith(':')).ToArray(),
            text = TextOf(control),
            x = bounds.X, y = bounds.Y, width = bounds.Width, height = bounds.Height,
            isEnabled = control.IsEffectivelyEnabled,
        };
    }

    private static Control? FindByName(Visual root, string name) =>
        root.GetSelfAndVisualDescendants().OfType<Control>().FirstOrDefault(c => string.Equals(c.Name, name, StringComparison.Ordinal));

    private static IEnumerable<Control> Find(Visual root, string? name, string? text, string? type)
    {
        if (name is null && text is null && type is null)
            throw new ArgumentException("name, text or type is required");
        // Exact text matches come first, so "Heap" picks the sidebar group before "Move to heap".
        return root.GetSelfAndVisualDescendants().OfType<Control>().Where(c =>
                c.IsEffectivelyVisible
                && (name is null || string.Equals(c.Name, name, StringComparison.Ordinal))
                && (type is null || string.Equals(c.GetType().Name, type, StringComparison.OrdinalIgnoreCase))
                && (text is null || (TextOf(c)?.Contains(text, StringComparison.OrdinalIgnoreCase) ?? false)))
            .OrderByDescending(c => text is not null && string.Equals(TextOf(c)?.Trim(), text, StringComparison.OrdinalIgnoreCase));
    }

    private object Click(Window window, string? name, string? text, double? x, double? y, string? button, int count)
    {
        Point point;
        object target;
        if (x is { } px && y is { } py)
        {
            point = new Point(px, py);
            target = new { x = px, y = py };
        }
        else
        {
            var control = Find(window, name, text, null).FirstOrDefault()
                ?? throw new KeyNotFoundException($"No visible control matches name='{name}' text='{text}'");
            point = WindowBounds(control).Center;
            target = Describe(control);
        }

        var (down, up) = button?.ToLowerInvariant() switch
        {
            "right" => (RawPointerEventType.RightButtonDown, RawPointerEventType.RightButtonUp),
            "middle" => (RawPointerEventType.MiddleButtonDown, RawPointerEventType.MiddleButtonUp),
            _ => (RawPointerEventType.LeftButtonDown, RawPointerEventType.LeftButtonUp),
        };
        var input = window.PlatformImpl?.Input ?? throw new InvalidOperationException("The window has no platform input");
        var timestamp = (ulong)Environment.TickCount64;
        input(new RawPointerEventArgs(_mouse, timestamp, window, RawPointerEventType.Move, point, RawInputModifiers.None));
        for (var i = 0; i < Math.Max(1, count); i++)
        {
            // Same timestamp keeps the presses inside the platform double-click interval, so ClickCount increments.
            input(new RawPointerEventArgs(_mouse, timestamp, window, down, point, RawInputModifiers.None));
            input(new RawPointerEventArgs(_mouse, timestamp, window, up, point, RawInputModifiers.None));
        }
        return new { clicked = target, x = point.X, y = point.Y };
    }

    private static object Key(Window window, string gesture)
    {
        // A "+" in a query string arrives as a space, so "Ctrl Shift K" is the same gesture as "Ctrl+Shift+K".
        var parsed = KeyGesture.Parse(gesture.Trim().Replace(' ', '+'));
        var input = window.PlatformImpl?.Input ?? throw new InvalidOperationException("The window has no platform input");
        var keyboard = AvaloniaLocator.Current.GetService<IKeyboardDevice>() ?? new KeyboardDevice();
        var modifiers = ToRaw(parsed.KeyModifiers);
        var timestamp = (ulong)Environment.TickCount64;
        input(new RawKeyEventArgs(keyboard, timestamp, window, RawKeyEventType.KeyDown, parsed.Key, modifiers, PhysicalKey.None, null));
        input(new RawKeyEventArgs(keyboard, timestamp, window, RawKeyEventType.KeyUp, parsed.Key, modifiers, PhysicalKey.None, null));
        return new { key = parsed.ToString() };
    }

    private static object TypeText(Window window, string text)
    {
        var input = window.PlatformImpl?.Input ?? throw new InvalidOperationException("The window has no platform input");
        var keyboard = AvaloniaLocator.Current.GetService<IKeyboardDevice>() ?? new KeyboardDevice();
        input(new RawTextInputEventArgs(keyboard, (ulong)Environment.TickCount64, window, text));
        return new { typed = text };
    }

    private static RawInputModifiers ToRaw(KeyModifiers modifiers)
    {
        var raw = RawInputModifiers.None;
        if (modifiers.HasFlag(KeyModifiers.Control)) raw |= RawInputModifiers.Control;
        if (modifiers.HasFlag(KeyModifiers.Shift)) raw |= RawInputModifiers.Shift;
        if (modifiers.HasFlag(KeyModifiers.Alt)) raw |= RawInputModifiers.Alt;
        if (modifiers.HasFlag(KeyModifiers.Meta)) raw |= RawInputModifiers.Meta;
        return raw;
    }

    private static object Set(Window window, string? name, string? property, string? path, string value)
    {
        if (name is not null)
        {
            var control = FindByName(window, name) ?? throw new KeyNotFoundException($"No control named '{name}'");
            SetProperty(control, property ?? "Text", value);
            return new { control = Describe(control) };
        }
        if (path is null)
            throw new ArgumentException("path or name is required");
        var (owner, member) = Resolve(window.DataContext, path);
        SetProperty(owner, member, value);
        return new { path, value = Serializable(GetPath(window.DataContext, path)) };
    }

    private static object Invoke(Window window, string commandName, string? parameter)
    {
        var (owner, member) = Resolve(window.DataContext, commandName);
        var command = owner.GetType().GetProperty(member, BindingFlags.Public | BindingFlags.Instance)?.GetValue(owner) as ICommand
            ?? throw new KeyNotFoundException($"'{commandName}' is not an ICommand on {owner.GetType().Name}");
        if (!command.CanExecute(parameter))
            return new { command = commandName, executed = false, reason = "CanExecute is false" };
        command.Execute(parameter);
        return new { command = commandName, executed = true };
    }

    private static object Theme(string variant)
    {
        if (Application.Current is not { } app)
            throw new InvalidOperationException("No application");
        app.RequestedThemeVariant = variant.ToLowerInvariant() switch
        {
            "dark" => ThemeVariant.Dark,
            "light" => ThemeVariant.Light,
            _ => ThemeVariant.Default,
        };
        return new { theme = app.ActualThemeVariant.ToString() };
    }

    private static object Resize(Window window, double? width, double? height)
    {
        if (width is { } w) window.Width = w;
        if (height is { } h) window.Height = h;
        return new { width = window.Width, height = window.Height };
    }

    // ----- reflection helpers -----

    /// <summary>Walks "A.B.C" and returns the owner of the last member plus its name.</summary>
    private static (object Owner, string Member) Resolve(object? context, string path)
    {
        var owner = context ?? throw new InvalidOperationException("The window has no DataContext");
        var parts = path.Split('.', StringSplitOptions.RemoveEmptyEntries);
        for (var i = 0; i < parts.Length - 1; i++)
            owner = ReadProperty(owner, parts[i]) ?? throw new KeyNotFoundException($"'{string.Join('.', parts.Take(i + 1))}' is null");
        return (owner, parts[^1]);
    }

    private static object? GetPath(object? context, string path)
    {
        var (owner, member) = Resolve(context, path);
        return ReadProperty(owner, member);
    }

    private static object? ReadProperty(object owner, string name)
    {
        var property = owner.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new KeyNotFoundException($"{owner.GetType().Name} has no property '{name}'");
        return property.GetValue(owner);
    }

    private static void SetProperty(object owner, string name, string value)
    {
        var property = owner.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance)
            ?? throw new KeyNotFoundException($"{owner.GetType().Name} has no property '{name}'");
        if (!property.CanWrite)
            throw new ArgumentException($"{owner.GetType().Name}.{name} is read-only");
        var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
        object? converted = type.IsEnum ? Enum.Parse(type, value, ignoreCase: true) : Convert.ChangeType(value, type, CultureInfo.InvariantCulture);
        property.SetValue(owner, converted);
    }

    /// <summary>Primitives and strings pass through; anything else is reduced to its public readable scalars.</summary>
    private static object? Serializable(object? value)
    {
        if (value is null || value is string || value.GetType().IsPrimitive || value is decimal || value is Enum || value is DateTimeOffset || value is DateTime || value is Guid)
            return value is Enum ? value.ToString() : value;
        if (value is System.Collections.IEnumerable enumerable)
            return enumerable.Cast<object?>().Take(200).Select(Serializable).ToList();
        var scalars = new Dictionary<string, object?>();
        foreach (var property in value.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance).Where(p => p.CanRead && p.GetIndexParameters().Length == 0))
        {
            var type = Nullable.GetUnderlyingType(property.PropertyType) ?? property.PropertyType;
            if (type.IsPrimitive || type == typeof(string) || type.IsEnum || type == typeof(decimal) || type == typeof(DateTimeOffset) || type == typeof(DateTime))
            {
                try { scalars[property.Name] = Serializable(property.GetValue(value)); }
                catch (Exception) { /* a getter that throws is not worth failing the whole answer for */ }
            }
        }
        if (!value.GetType().Name.StartsWith('<'))
            scalars["$type"] = value.GetType().Name;
        return scalars;
    }

    // ----- HTTP plumbing -----

    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true };

    private static Task WriteJsonAsync(HttpListenerResponse response, object value) =>
        WriteAsync(response, "application/json; charset=utf-8", JsonSerializer.SerializeToUtf8Bytes(value, JsonOptions));

    private static async Task WriteAsync(HttpListenerResponse response, string contentType, byte[] body)
    {
        response.ContentType = contentType;
        response.ContentLength64 = body.Length;
        await response.OutputStream.WriteAsync(body);
        response.Close();
    }

    private static int? ParseInt(string? value) => int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out var i) ? i : null;
    private static double? ParseDouble(string? value) => double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var d) ? d : null;

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch (ObjectDisposedException) { }
        _listener.Close();
    }
}
