using System.Collections.Immutable;
using eThangAgent.ToolDomain;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace eThangAgent.Roslyn.ACL;

/// <summary>The exec engine's native-interop gate (issue #282).
///
/// <para>THE DEFECT IT CLOSES: an exec script could declare
/// <c>[DllImport("user32.dll")] static extern bool SetCursorPos(int, int);</c> and drive the
/// user's real desktop — move their window, steal focus, dispatch hardware-level clicks —
/// entirely around the ComputerUse ACL. That ACL exists to supply four guarantees the raw
/// surface has none of: killable containment (a Job Object around a broker process, where an
/// exec script runs INSIDE the app process and cannot be killed), an audit trail, a
/// foreground/focus policy, and a grant surface (<c>computer_use_enabled</c> defaults to OFF).
/// A user who deliberately left computer use disabled still had their cursor moved, because a
/// disabled tool is not a gate when <c>exec</c> reaches the same native surface ungated.</para>
///
/// <para>WHY A SYNTAX WALK AND NOT A SEMANTIC ONE: this runs on the same
/// <see cref="Compilation"/> the engine already builds for validation, so it costs no
/// additional compilation and — critically — loads no assembly, which keeps the engine's leak
/// contract intact (see the drain contract in <see cref="CSharpScriptExecEngine"/>). It also
/// means the gate fires on the DECLARATION SHAPE rather than on a successfully resolved
/// symbol, so a script that misspells an extern is refused for the right reason instead of
/// reporting a confusing name-resolution error.</para>
///
/// <para>THE ALLOWLIST IS EMPTY, BY NAMED DECISION. The issue offered a narrow allowlist for
/// "genuinely harmless externs" (a kernel32 query, a file-lock probe). It is deliberately not
/// taken: an allowlist keyed on a library name cannot distinguish <c>GetTickCount64</c> from
/// <c>SetForegroundWindow</c> — they are the same declaration shape against the same DLL — so
/// a library-keyed carve-out would have to enumerate entry points, and every entry point added
/// is a permanent hole somebody widens later. Process-level needs are already covered by
/// <c>Shell</c>; desktop needs are covered by the <c>computer</c> tool. The empty set is pinned
/// by a test so that populating it forces a documented decision rather than a quiet edit.</para>
///
/// <para>WHAT IS BLOCKED: the three declaration shapes that reach native code
/// (<c>DllImport</c>, <c>LibraryImport</c>, and an <c>extern</c> method), plus the two managed
/// escape hatches that reach the same place without an attribute
/// (<c>NativeLibrary.Load</c>/<c>GetExport</c> and
/// <c>Marshal.GetDelegateForFunctionPointer</c>). Blocking only the attributes would leave the
/// hole open — <c>NativeLibrary</c> plus a function-pointer delegate IS the same
/// <c>mouse_event</c> call — and a gate that a one-line rewrite defeats is not a gate.</para>
/// </summary>
public static class NativeInteropPolicy
{
  /// <summary>The stable code every violation carries, so the model can act on it without
  ///     parsing prose.</summary>
  public const string ErrorCode = "NativeInteropBlocked";

  /// <summary>The entry points this gate permits. Intentionally EMPTY — see the type's
  ///     named decision. Exposed rather than inlined so a test can pin the emptiness: adding
  ///     an entry point is a product decision that must update the test and the AGENTS.md
  ///     Exec ACL row in the same change, never a quiet widening.</summary>
  public static IReadOnlyCollection<string> AllowedEntryPoints => [];

  /// <summary>The attribute names that declare a native call. Matched on the RIGHTMOST
  ///     identifier with any <c>Attribute</c> suffix stripped, so <c>DllImport</c>,
  ///     <c>DllImportAttribute</c>, <c>System.Runtime.InteropServices.DllImport</c> and the
  ///     <c>global::</c>-qualified form are one case.</summary>
  private static readonly ImmutableHashSet<string> BlockedAttributeNames =
      ImmutableHashSet.Create(StringComparer.Ordinal, "DllImport", "LibraryImport");

  /// <summary>The <c>NativeLibrary</c> members that load native code or resolve an export.
  ///     <c>TryLoad</c>/<c>TryGetExport</c> are the non-throwing twins and reach exactly the
  ///     same surface, so they are blocked alongside the throwing forms.</summary>
  private static readonly ImmutableHashSet<string> BlockedNativeLibraryMembers =
      ImmutableHashSet.Create(StringComparer.Ordinal,
          "Load", "LoadLibrary", "GetExport", "TryLoad", "TryGetExport");

  /// <summary>The <c>Marshal</c> member that turns a native address into a callable
  ///     delegate — the second half of the <c>NativeLibrary</c> route.</summary>
  private const string BlockedMarshalMember = "GetDelegateForFunctionPointer";

  /// <summary>Every native-interop violation in <paramref name="compilation"/>, in source
  ///     order. Empty means the program declares no native call and the gate is silent.</summary>
  public static IReadOnlyList<ExecParseError> Analyze(Compilation compilation, CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(compilation);
    List<ExecParseError> violations = [];
    foreach (SyntaxTree tree in compilation.SyntaxTrees)
    {
      ct.ThrowIfCancellationRequested();
      SyntaxNode root = tree.GetRoot(ct);
      foreach (SyntaxNode node in root.DescendantNodes())
      {
        if (Describe(node) is { } message)
        {
          violations.Add(At(node, message));
        }
      }
    }

    return violations;
  }

  /// <summary>The violation message for one node, or null when the node declares no native
  ///     call. Every message names the <c>computer</c> tool as the replacement, because the
  ///     point of the gate is to redirect the model to the sanctioned path rather than to
  ///     simply refuse it.</summary>
  private static string? Describe(SyntaxNode node) => node switch
  {
    AttributeSyntax attribute => DescribeAttribute(attribute),
    MethodDeclarationSyntax method => DescribeExtern(method),
    InvocationExpressionSyntax invocation => DescribeInvocation(invocation),
    _ => null,
  };

  private static string? DescribeAttribute(AttributeSyntax attribute)
  {
    string? name = SimpleName(attribute.Name);
    if (name is null)
    {
      return null;
    }

    string bare = name.EndsWith("Attribute", StringComparison.Ordinal)
        ? name[..^"Attribute".Length]
        : name;
    return BlockedAttributeNames.Contains(bare)
        ? $"Error [{ErrorCode}]: native interop is not available in exec — '[{name}]' declares a native call, and the native desktop surface belongs to the computer tool (use computer with action observe/click). Remove the declaration."
        : null;
  }

  private static string? DescribeExtern(MethodDeclarationSyntax method) =>
      method.Modifiers.Any(SyntaxKind.ExternKeyword)
          ? $"Error [{ErrorCode}]: native interop is not available in exec — the extern method '{method.Identifier.Text}' declares a native call, and the native desktop surface belongs to the computer tool (use computer with action observe/click). Remove the declaration."
          : null;

  private static string? DescribeInvocation(InvocationExpressionSyntax invocation) =>
      invocation.Expression is MemberAccessExpressionSyntax access
          ? DescribeMemberCall(access)
          : null;

  /// <summary>Classifies one <c>receiver.member(...)</c> call. Split from
  ///     <see cref="DescribeInvocation"/> so each shape test is its own single ternary:
  ///     the style analyzers in this repo forbid an <c>if</c> before a <c>return</c>
  ///     (IDE0046) and forbid a nested ternary (S3358), so the two routes are separate
  ///     expressions rather than one nested chain.</summary>
  private static string? DescribeMemberCall(MemberAccessExpressionSyntax access)
  {
    string member = access.Name.Identifier.Text;
    return RightmostName(access.Expression) is { } receiver
        ? DescribeNativeLibraryCall(receiver, member) ?? DescribeMarshalCall(receiver, member)
        : null;
  }

  private static string? DescribeNativeLibraryCall(string receiver, string member) =>
      string.Equals(receiver, "NativeLibrary", StringComparison.Ordinal)
          && BlockedNativeLibraryMembers.Contains(member)
          ? $"Error [{ErrorCode}]: native interop is not available in exec — 'NativeLibrary.{member}' loads native code, and the native desktop surface belongs to the computer tool (use computer with action observe/click)."
          : null;

  private static string? DescribeMarshalCall(string receiver, string member) =>
      string.Equals(receiver, "Marshal", StringComparison.Ordinal)
          && string.Equals(member, BlockedMarshalMember, StringComparison.Ordinal)
          ? $"Error [{ErrorCode}]: native interop is not available in exec — 'Marshal.{member}' binds a native function pointer, and the native desktop surface belongs to the computer tool (use computer with action observe/click)."
          : null;

  /// <summary>The rightmost identifier of a name, so a qualified reference matches its bare
  ///     form. <c>System.Runtime.InteropServices.DllImport</c> yields <c>DllImport</c>.</summary>
  private static string? SimpleName(NameSyntax name) => name switch
  {
    IdentifierNameSyntax identifier => identifier.Identifier.Text,
    QualifiedNameSyntax qualified => SimpleName(qualified.Right),
    AliasQualifiedNameSyntax alias => SimpleName(alias.Name),
    GenericNameSyntax generic => generic.Identifier.Text,
    _ => null,
  };

  /// <summary>The rightmost identifier of an expression's receiver, so
  ///     <c>System.Runtime.InteropServices.NativeLibrary.Load(...)</c> is recognised as a
  ///     <c>NativeLibrary</c> call. A receiver that is not a name (a field access, a call
  ///     result) yields null and is not gated here — it cannot name a framework type.</summary>
  private static string? RightmostName(ExpressionSyntax expression) => expression switch
  {
    IdentifierNameSyntax identifier => identifier.Identifier.Text,
    MemberAccessExpressionSyntax member => member.Name.Identifier.Text,
    AliasQualifiedNameSyntax alias => alias.Name.Identifier.Text,
    _ => null,
  };

  /// <summary>Locates a violation at its own source position, using the same 1-based
  ///     line/column convention the engine's compile diagnostics use so the rendered
  ///     'line N, col M:' prefix is consistent across both error sources.</summary>
  private static ExecParseError At(SyntaxNode node, string message)
  {
    FileLinePositionSpan span = node.GetLocation().GetMappedLineSpan();
    return new ExecParseError(
        span.StartLinePosition.Line + 1,
        span.StartLinePosition.Character + 1,
        message);
  }
}
