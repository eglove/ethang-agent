using eThangAgent.CapabilityDomain;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>The native-interop gate (issue #282). Every test here drives the REAL engine
///     over REAL Roslyn compilation — no fake compilation, because the whole point of the
///     gate is that it reads the same syntax tree the compiler does.</summary>
public class NativeInteropGateTests
{
  private static CSharpScriptExecEngine CreateEngine()
      => new(CapabilityRegistry.Create([]), workspaceRoot: () => AppContext.BaseDirectory);

  /// <summary>Validates <paramref name="program"/> and returns the violation messages.
  ///     A gate failure is a VALIDATION failure (the program never runs), which is what
  ///     makes it mechanical rather than advisory.</summary>
  private static async Task<IReadOnlyList<string>> ViolationsAsync(string program)
  {
    CSharpScriptExecEngine engine = CreateEngine();
    Result<IReadOnlyList<ExecParseError>> result =
        await engine.ValidateAsync(new ExecProgram(program), TestContext.Current.CancellationToken).ConfigureAwait(false);
    Assert.True(result.IsSuccess, "validation itself must not fault");
    return [.. result.Value.Select(e => e.Message)];
  }

  /// <summary>Asserts the program is refused by the native-interop gate specifically, and
  ///     that the refusal names the computer tool — the redirect is the point, not the
  ///     refusal.</summary>
  private static async Task AssertBlockedAsync(string program)
  {
    IReadOnlyList<string> violations = await ViolationsAsync(program).ConfigureAwait(false);
    Assert.NotEmpty(violations);
    string all = string.Join("\n", violations);
    Assert.Contains(NativeInteropPolicy.ErrorCode, all, StringComparison.Ordinal);
    Assert.Contains("computer", all, StringComparison.Ordinal);
  }

  [Fact]
  public Task DllImport_SetCursorPos_IsBlocked() => AssertBlockedAsync("""
      using System.Runtime.InteropServices;
      [DllImport("user32.dll")]
      static extern bool SetCursorPos(int x, int y);
      return SetCursorPos(0, 0);
      """);

  [Fact]
  public Task DllImport_SetForegroundWindow_IsBlocked() => AssertBlockedAsync("""
      using System.Runtime.InteropServices;
      [DllImport("user32.dll")]
      static extern bool SetForegroundWindow(IntPtr hWnd);
      return SetForegroundWindow(IntPtr.Zero);
      """);

  [Fact]
  public Task DllImport_MouseEvent_IsBlocked() => AssertBlockedAsync("""
      using System.Runtime.InteropServices;
      [DllImport("user32.dll")]
      static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, IntPtr dwExtraInfo);
      mouse_event(0x0002, 0, 0, 0, IntPtr.Zero);
      return 0;
      """);

  [Fact]
  public Task DllImport_Gdi32Capture_IsBlocked() => AssertBlockedAsync("""
      using System.Runtime.InteropServices;
      [DllImport("gdi32.dll")]
      static extern IntPtr CreateCompatibleDC(IntPtr hdc);
      return CreateCompatibleDC(IntPtr.Zero);
      """);

  [Fact]
  public Task DllImport_Kernel32_IsAlsoBlocked() => AssertBlockedAsync("""
      using System.Runtime.InteropServices;
      [DllImport("kernel32.dll")]
      static extern ulong GetTickCount64();
      return GetTickCount64();
      """);

  [Fact]
  public Task LibraryImport_IsBlocked() => AssertBlockedAsync("""
      using System.Runtime.InteropServices;
      [LibraryImport("user32.dll")]
      static partial bool SetCursorPos(int x, int y);
      return 0;
      """);

  [Fact]
  public Task ExternMethod_IsBlocked() => AssertBlockedAsync("""
      static extern bool SetCursorPos(int x, int y);
      return 0;
      """);

  [Fact]
  public Task NativeLibraryLoad_IsBlocked() => AssertBlockedAsync("""
      using System.Runtime.InteropServices;
      IntPtr lib = NativeLibrary.Load("user32.dll");
      return lib;
      """);

  [Fact]
  public Task NativeLibraryGetExport_IsBlocked() => AssertBlockedAsync("""
      using System.Runtime.InteropServices;
      IntPtr lib = NativeLibrary.Load("user32.dll");
      IntPtr fn = NativeLibrary.GetExport(lib, "SetCursorPos");
      return fn;
      """);

  [Fact]
  public Task MarshalGetDelegateForFunctionPointer_IsBlocked() => AssertBlockedAsync("""
      using System.Runtime.InteropServices;
      IntPtr fn = IntPtr.Zero;
      Delegate d = Marshal.GetDelegateForFunctionPointer(fn, typeof(Action));
      return d;
      """);

  [Fact]
  public Task FullyQualifiedDllImport_IsBlocked() => AssertBlockedAsync("""
      [System.Runtime.InteropServices.DllImport("user32.dll")]
      static extern bool SetCursorPos(int x, int y);
      return 0;
      """);

  [Fact]
  public Task DllImportAttributeSuffix_IsBlocked() => AssertBlockedAsync("""
      using System.Runtime.InteropServices;
      [DllImportAttribute("user32.dll")]
      static extern bool SetCursorPos(int x, int y);
      return 0;
      """);

  [Fact]
  public async Task OrdinaryProgram_IsNotBlocked()
  {
    IReadOnlyList<string> violations = await ViolationsAsync("return 1 + 1;").ConfigureAwait(true);
    Assert.Empty(violations);
  }

  [Fact]
  public async Task ProgramUsingMarshalSizeOf_IsNotBlocked()
  {
    // Marshal is a broad type; only the function-pointer binder is gated. A program that
    // merely measures a struct must keep working.
    IReadOnlyList<string> violations = await ViolationsAsync("""
      using System.Runtime.InteropServices;
      return Marshal.SizeOf<int>();
      """).ConfigureAwait(true);
    Assert.Empty(violations);
  }

  [Fact]
  public async Task ExecuteAsync_AlsoRefusesNativeInterop()
  {
    // Defence in depth: a caller that skips ValidateAsync must not be able to run a
    // native call. The run path re-checks the same compilation.
    CSharpScriptExecEngine engine = CreateEngine();
    ExecRunResult run = await engine.ExecuteAsync(new ExecProgram("""
      using System.Runtime.InteropServices;
      [DllImport("user32.dll")]
      static extern bool SetCursorPos(int x, int y);
      return SetCursorPos(0, 0);
      """), ct: TestContext.Current.CancellationToken).ConfigureAwait(true);

    Assert.NotEmpty(run.ErrorLines);
    Assert.Contains(NativeInteropPolicy.ErrorCode, string.Join("\n", run.ErrorLines), StringComparison.Ordinal);
  }

  // THE PIN. Populating this set is a product decision that must update this test and
  // the AGENTS.md Exec ACL row in the same change — never a quiet widening.
  [Fact]
  public void Allowlist_IsEmpty() => Assert.Empty(NativeInteropPolicy.AllowedEntryPoints);

  [Fact]
  public async Task EveryBlockedShape_IsRefused()
  {
    // Fails when an un-listed P/Invoke shape is added: the corpus below is the complete
    // set of declaration shapes the gate claims to cover, and each must be refused.
    string[] corpus =
    [
        "[DllImport(\"user32.dll\")] static extern bool SetCursorPos(int x, int y);",
        "[LibraryImport(\"user32.dll\")] static partial bool SetCursorPos(int x, int y);",
        "static extern bool SetCursorPos(int x, int y);",
    ];

    foreach (string declaration in corpus)
    {
      IReadOnlyList<string> violations = await ViolationsAsync(declaration + "\nreturn 0;").ConfigureAwait(true);
      Assert.NotEmpty(violations);
    }
  }
}
