using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Windows.Automation;

namespace eThangAgent.ComputerUse.Host;

/// <summary>The broker's real observation surface (task 18): the UIA walk over the
///     requested app's window (rows on the spec 3.1 element table, indices assigned in
///     walk order and cached for the element-op dispatcher), window listing, application
///     identity, and the PrintWindow raster with blank detection. Owner loss resets the
///     element cache (A3: a new controller starts from a fresh observation).</summary>
public sealed partial class RealBrokerObserver : IBrokerObserver
{
  private readonly Lock _gate = new();
  private readonly SurfaceTracker _surfaces = new();
  private readonly List<AutomationElement> _elementCache = [];

  /// <summary>The element-op seam for InputDispatch (R1): resolves a model index into
  ///     the walk's cached runtime element.</summary>
  public UiaElementOps CreateElementOps() => new(ResolveElement);

  private AutomationElement? ResolveElement(int index)
  {
    lock (_gate)
    {
      return index >= 0 && index < _elementCache.Count ? _elementCache[index] : null;
    }
  }

  public JsonElement? ListApplications()
  {
    List<object> rows = [];
    rows.AddRange(EnumerateTopLevelWindows()
      .Where(w => w.Pid > 0 && w.Title.Length > 0)
      .Select(window =>
      {
        AppIdentity identity = IdentityFor(window.Pid);
        return (object)new { pid = window.Pid, name = identity.ExeName, exe = identity.ExePath, aumid = identity.Aumid ?? string.Empty, active = false };
      }));

    return JsonSerializer.SerializeToElement(rows);
  }

  public JsonElement? ListWindows(JsonElement? parameters)
  {
    int pid = RequestPid(parameters);
    List<object> rows = [];
    foreach (Win32Window window in EnumerateTopLevelWindows().Where(w => pid <= 0 || w.Pid == pid))
    {
      rows.Add(new
      {
        title = window.Title,
        window_id = (int)window.Handle,
        bounds = new[] { window.Rect._left, window.Rect._top, window.Rect.Width, window.Rect.Height },
      });
    }

    return JsonSerializer.SerializeToElement(rows);
  }

  public JsonElement? CaptureApp(JsonElement? parameters)
  {
    int pid = RequestPid(parameters);
    bool includeScreenshot = parameters is { } p && p.TryGetProperty("include_screenshot", out JsonElement shotEl)
        && shotEl.ValueKind == JsonValueKind.True;
    string? reqName = null;
    string? reqAumid = null;
    int? reqWindowId = null;
    if (parameters is { } pr && pr.TryGetProperty("app_ref", out JsonElement ar2) && ar2.ValueKind == JsonValueKind.Object)
    {
      if (ar2.TryGetProperty("name", out JsonElement nm2) && nm2.ValueKind == JsonValueKind.String)
      {
        reqName = nm2.GetString();
      }

      if (ar2.TryGetProperty("aumid", out JsonElement am2) && am2.ValueKind == JsonValueKind.String)
      {
        reqAumid = am2.GetString();
      }

      if (ar2.TryGetProperty("window_id", out JsonElement wd2) && wd2.ValueKind == JsonValueKind.Number && wd2.TryGetInt32(out int wid2))
      {
        reqWindowId = wid2;
      }
    }

    List<Candidate> candidates = [.. EnumerateTopLevelWindows().Select(
        w => new Candidate(w.Handle, w.Pid, w.Title, ResolveAumid(w.Pid)))];
    bool resolved = AppRefResolver.TryResolve(
        candidates, pid, reqName, reqAumid, reqWindowId,
        out AppRefResolution resolution2, out AppRefResolutionFailure? resolutionFailure);
    if (!resolved)
    {
      // Typed resolution failures answer app_not_found / ambiguous_app per spec 2.2.
      string code = resolutionFailure!.Kind == AppRefFailureKind.Ambiguous ? "ambiguous_app" : "app_not_found";
      // M16: a vanished app's lifecycle is Closed (the tracker forgets the handle).
      lock (_gate)
      {
        _ = _surfaces.Observe(pid.ToString(System.Globalization.CultureInfo.InvariantCulture), 0,
          SurfaceKind.Window);
      }
      return JsonSerializer.SerializeToElement(new { error = new { code, message = resolutionFailure.Message } });
    }

    int resolvedPid = resolution2.Pid;
    nint target = resolution2.Window;

    string title = WindowTitle(target);
    NativeRect rect = WindowRect(target);
    SurfaceLifecycle lifecycle;
    lock (_gate)
    {
      lifecycle = _surfaces.Observe(
          resolvedPid.ToString(System.Globalization.CultureInfo.InvariantCulture), target,
          SurfaceClassifier.KindFor(WindowClassName(target), title));
    }

    WalkResult walk = WalkElements(target);
    lock (_gate)
    {
      _elementCache.Clear();
      _elementCache.AddRange(walk.Cache);
    }

    string stateId = NewStateId();
    JsonElement? screenshot = includeScreenshot ? CaptureRaster(target, rect) : null;

    var envelope = new
    {
      state_id = stateId,
      snapshot_mode = "full",
      app = AppObject(resolvedPid),
      window = WindowObject(target, title, rect, lifecycle),
      elements = walk.Rows,
      screenshot,
    };
    return JsonSerializer.SerializeToElement(envelope);
  }

  public void OnOwnerLost(int ownerConnectionId)
  {
    lock (_gate)
    {
      _elementCache.Clear();
    }
  }

  // ---- walk ----

  private sealed record WalkResult(List<object> Rows, List<AutomationElement> Cache);

  private static WalkResult WalkElements(nint window)
  {
    AutomationElement root = AutomationElement.FromHandle(window);
    List<object>? rows = null;
    List<AutomationElement>? cache = null;
    UiaTreeWalker walker = new(() =>
    {
      WalkResult result = WalkTree(root);
      rows = result.Rows;
      cache = result.Cache;
      return [];
    });
    _ = walker.WalkWithDeadline();
    return new WalkResult(rows ?? [], cache ?? []);
  }

  private static WalkResult WalkTree(AutomationElement root)
  {
    List<object> rows = [];
    List<AutomationElement> cache = [];
    WalkElement(root, rows, cache, 0);
    return new WalkResult(rows, cache);
  }

  private static void WalkElement(AutomationElement element, List<object> rows, List<AutomationElement> cache, int depth)
  {
    if (cache.Count >= 400)
    {
      return; // the sampling cap keeps huge trees bounded
    }

    int index = cache.Count;
    cache.Add(element);
    rows.Add(RowFor(index, element, depth));
    if (depth > 12)
    {
      return;
    }

    foreach (AutomationElement child in element.FindAll(TreeScope.Children, Condition.TrueCondition))
    {
      WalkElement(child, rows, cache, depth + 1);
    }
  }

  private static object RowFor(int index, AutomationElement element, int depth)
  {
    try
    {
      System.Windows.Rect bounds = element.Current.BoundingRectangle;
      string role = element.Current.ControlType?.ProgrammaticName?.Replace("ControlType.", string.Empty, StringComparison.Ordinal) ?? "unknown";
      string name = element.Current.Name ?? string.Empty;
      bool pressable = element.TryGetCurrentPattern(InvokePattern.Pattern, out _);
      bool toggleable = element.TryGetCurrentPattern(TogglePattern.Pattern, out _);
      string[] actions = (pressable, toggleable) switch
      {
        (true, true) => ["press", "toggle"],
        (true, false) => ["press"],
        (false, true) => ["toggle"],
        _ => [],
      };
      bool valueBacked = element.TryGetCurrentPattern(ValuePattern.Pattern, out _);
      string? value = null;
      if (valueBacked && element.TryGetCurrentPattern(ValuePattern.Pattern, out object? pattern) && pattern is ValuePattern vp)
      {
        try
        {
          value = vp.Current.Value;
        }
        catch (ElementNotAvailableException)
        {
          value = null;
        }
      }


      bool selected = element.TryGetCurrentPattern(SelectionItemPattern.Pattern, out _);
      bool hasMenu = element.Current.ControlType == ControlType.Menu;
      return new
      {
        index,
        role,
        kind = depth == 0 ? "window" : "control",
        title = name,
        value,
        bounds = new[] { (int)bounds.X, (int)bounds.Y, (int)bounds.Width, (int)bounds.Height },
        enabled = element.Current.IsEnabled,
        editable = valueBacked,
        actions,
        focused = element.Current.HasKeyboardFocus,
        selected,
        pressable = pressable || toggleable,
        has_menu = hasMenu,
        children_total = (int?)null,
        children_shown = (int?)null,
        children_offset = (int?)null,
      };
    }
    catch (ElementNotAvailableException)
    {
      return new
      {
        index,
        role = "unknown",
        kind = "control",
        title = string.Empty,
        value = (string?)null,
        bounds = new[] { 0, 0, 0, 0 },
        enabled = false,
        editable = false,
        actions = Array.Empty<string>(),
        focused = false,
        selected = false,
        pressable = false,
        has_menu = false,
        children_total = (int?)null,
        children_shown = (int?)null,
        children_offset = (int?)null,
      };
    }
  }

  // ---- raster ----

  private static JsonElement? CaptureRaster(nint target, NativeRect rect)
  {
    try
    {
      using Bitmap bmp = new(rect.Width, rect.Height, PixelFormat.Format32bppArgb);
      using Graphics graphics = Graphics.FromImage(bmp);
      nint hdc = graphics.GetHdc();
      try
      {
        _ = WindowCapture.PrintWindow(target, hdc, 0x00000002 /* PW_RENDERFULLCONTENT */);
      }
      finally
      {
        graphics.ReleaseHdc(hdc);
      }

      bool blank;
      byte[] pixels = new byte[rect.Width * rect.Height * 4];
      BitmapData data = bmp.LockBits(new Rectangle(0, 0, rect.Width, rect.Height), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
      try
      {
        Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);
      }
      finally
      {
        bmp.UnlockBits(data);
      }

      blank = WindowCapture.IsBlank(pixels, rect.Width * 4, rect.Height);
      using MemoryStream ms = new();
      bmp.Save(ms, ImageFormat.Png);
      return JsonSerializer.SerializeToElement(new
      {
        data = blank ? string.Empty : Convert.ToBase64String(ms.ToArray()),
        width = rect.Width,
        height = rect.Height,
        blank,
      });
    }
    catch (Exception ex) when (ex is ArgumentException or InvalidOperationException)
    {
      return null; // raster unavailable; the observation carries no screenshot key
    }
  }

  // ---- app identity ----

  private static JsonElement AppObject(int pid)
  {
    AppIdentity identity = IdentityFor(pid);
    return JsonSerializer.SerializeToElement(new
    {
      pid,
      name = identity.ExeName,
      exe = identity.ExePath,
      aumid = identity.Aumid ?? string.Empty,
    });
  }

  private static string? ResolveAumid(int pid)
  {
    try
    {
      using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);
      return IdentityFor(pid).Aumid;
    }
#pragma warning disable CA1031 // Named decision: AUMID is best-effort; failure degrades to null.
    catch
    {
      return null;
    }
#pragma warning restore CA1031
  }

  private static AppIdentity IdentityFor(int pid)
  {
    string path = ProcessPath(pid);
    return AppIdentity.Resolve(pid, path, _ => null);
  }

  private static string ProcessPath(int pid)
  {
    try
    {
      using System.Diagnostics.Process process = System.Diagnostics.Process.GetProcessById(pid);
      return process.MainModule?.FileName ?? string.Empty;
    }
    catch (Exception ex) when (ex is ArgumentException or System.ComponentModel.Win32Exception or InvalidOperationException)
    {
      return string.Empty;
    }
  }

  private static JsonElement WindowObject(nint handle, string title, NativeRect rect, SurfaceLifecycle lifecycle) =>
    JsonSerializer.SerializeToElement(new
    {
      title,
      window_id = (int)handle,
      bounds = new[] { rect._left, rect._top, rect.Width, rect.Height },
      surface_kind = SurfaceClassifier.KindFor(WindowClassName(handle), title) switch
      {
        SurfaceKind.Dialog => "attached_dialog",
        SurfaceKind.OpenPanel => "open_panel",
        SurfaceKind.SavePanel => "save_panel",
        SurfaceKind.Window => "window",
        _ => "window",
      },
      surface_lifecycle = lifecycle switch
      {
        SurfaceLifecycle.Replaced => "replaced",
        SurfaceLifecycle.Closed => "closed",
        SurfaceLifecycle.Stable => "stable",
        _ => "stable",
      },
    });

  private static string NewStateId() => "s-" + DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
      .ToString(System.Globalization.CultureInfo.InvariantCulture);

  // ---- win32 ----

  private static int RequestPid(JsonElement? parameters) =>
    parameters is { } p && p.TryGetProperty("app_ref", out JsonElement appRef) && appRef.ValueKind == JsonValueKind.Object
      && appRef.TryGetProperty("pid", out JsonElement pidEl) && pidEl.ValueKind == JsonValueKind.Number
      && pidEl.TryGetInt32(out int pid) ? pid : -1;

  internal sealed record Win32Window(nint Handle, int Pid, string Title, NativeRect Rect);

  private static List<Win32Window> EnumerateTopLevelWindows()
  {
    List<Win32Window> windows = [];
    bool Handler(nint hwnd, nint lParam)
    {
      _ = lParam;
      // A failing window (dead mid-enum, hung, protected) is SKIPPED: an exception in the
      // native callback would silently abort the whole enumeration.
      try
      {
        if (!IsWindowVisible(hwnd) || GetWindow(hwnd, GwOwner) != 0)
        {
          return true;
        }

        _ = GetWindowThreadProcessId(hwnd, out int pid);
        windows.Add(new Win32Window(hwnd, pid, WindowTitle(hwnd), WindowRect(hwnd)));
      }
#pragma warning disable CA1031 // Named decision: one dead window must not abort the enumeration.
      catch (Exception)
      {
        // skip the failing window and continue
      }
#pragma warning restore CA1031

      return true;
    }

    _ = EnumWindows(Handler, 0);
    return windows;
  }

  private static string WindowTitle(nint hwnd)
  {
    int length = GetWindowTextLength(hwnd);
    if (length == 0)
    {
      return string.Empty;
    }

    StringBuilder sb = new(length + 1);
    _ = GetWindowText(hwnd, sb, sb.Capacity);
    return sb.ToString();
  }

  private static NativeRect WindowRect(nint hwnd)
  {
    _ = GetWindowRect(hwnd, out NativeRect rect);
    return rect;
  }

  private static string WindowClassName(nint hwnd)
  {
    StringBuilder sb = new(256);
    _ = GetClassName(hwnd, sb, sb.Capacity);
    return sb.ToString();
  }

  private const uint GwOwner = 4;

  [StructLayout(LayoutKind.Sequential)]
  internal struct NativeRect
  {
    public int _left;
    public int _top;
    public int _right;
    public int _bottom;
    public readonly int Width => _right - _left;
    public readonly int Height => _bottom - _top;
  }

  private delegate bool EnumWindowsProc(nint hwnd, nint lParam);

  [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool EnumWindows(EnumWindowsProc handler, nint lParam);

  [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool IsWindowVisible(nint hwnd);

  [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  [return: MarshalAs(UnmanagedType.Bool)]
  private static partial bool GetWindowRect(nint hwnd, out NativeRect rect);

  [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static partial nint GetWindow(nint hwnd, uint relationship);

  [LibraryImport("user32.dll"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static partial uint GetWindowThreadProcessId(nint hwnd, out int pid);

#pragma warning disable SYSLIB1054, CA1838 // Named decision (T12-13 precedent): LibraryImport cannot marshal StringBuilder; DllImport with CharSet.Unicode plus a char buffer marshals identically here.
  [DllImport("user32.dll", EntryPoint = "GetWindowTextW", CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern int GetWindowText(nint hwnd, [Out] StringBuilder text, int maxCount);
#pragma warning restore SYSLIB1054

  [LibraryImport("user32.dll", EntryPoint = "GetWindowTextLengthW"), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static partial int GetWindowTextLength(nint hwnd);

#pragma warning disable SYSLIB1054, CA1838 // Named decision (T12-13 precedent): LibraryImport cannot marshal StringBuilder; DllImport with CharSet.Unicode plus a char buffer marshals identically here.
  [DllImport("user32.dll", EntryPoint = "GetClassNameW", CharSet = CharSet.Unicode), DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
  private static extern int GetClassName(nint hwnd, [Out] StringBuilder text, int maxCount);
#pragma warning restore SYSLIB1054
}
