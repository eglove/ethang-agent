using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Resolves a tool's path by the call's ACTION (issue #96). Container-format
/// tools (docx, pdf) resolve ONE path before dispatching on an action argument, and
/// those actions differ in what they do to the file: <c>docx read</c> only reads while
/// <c>docx edit</c> rewrites the document; <c>pdf render</c> rasterizes while
/// <c>pdf manipulate</c> deletes pages. One resolver cannot be right for both - a
/// trusted root approved for reading would silently become writable through
/// <c>docx edit</c>, and enforcing the write policy on every action would refuse a
/// read the user did approve.
/// <para>So a READ-ONLY action resolves through the resolver's read view (the trusted
/// directory resolver with its write policy switched off, see
/// <c>TrustedDirectoryPathResolver.ForReads</c>); every other action - including
/// an unknown or absent one, which no tool can prove read-only - keeps the strict
/// resolver. DENY BY DEFAULT: an action a tool adds later is treated as mutating until
/// it is named in that tool's read-only set.</para>
/// <para>The action is read from the SAME JSON the tool parsed. Arguments that do not
/// parse, or an action that is absent or not a string, fall through to the strict
/// resolver rather than failing here: reporting a malformed call is the TOOL's job with
/// its own precise code, and a stale error from this layer would mask it.</para></summary>
public static class ActionScopedPathResolver
{
  /// <summary>Resolves <paramref name="path"/> under the policy the call's action earns.</summary>
  /// <param name="resolver">The tool's strict resolver (typically trust-aware).</param>
  /// <param name="path">The model-supplied path argument.</param>
  /// <param name="arguments">The call's raw JSON arguments, read for the action.</param>
  /// <param name="readOnlyActions">The actions this tool performs without writing.</param>
  /// <param name="actionParameter">The JSON member naming the action.</param>
  public static Result<string> Resolve(IPathResolver resolver, string path, string? arguments,
      IReadOnlySet<string> readOnlyActions, string actionParameter = "action")
  {
    ArgumentNullException.ThrowIfNull(resolver);
    ArgumentNullException.ThrowIfNull(readOnlyActions);

    IPathResolver effective = IsReadOnlyAction(arguments, readOnlyActions, actionParameter)
        ? ReadView(resolver)
        : resolver;
    return effective.Resolve(path);
  }

  /// <summary>The resolver's READ view: a trust-aware resolver stops enforcing the
  ///     per-root write policy. Anything else needs no relaxation (a workspace-only
  ///     resolver is exactly what it was) and is returned unchanged.</summary>
  private static IPathResolver ReadView(IPathResolver resolver) =>
      resolver is TrustedDirectoryPathResolver trusted ? trusted.AsReadOnlyResolver() : resolver;

  /// <summary>True when the call's action is one of <paramref name="readOnlyActions"/>.
  ///     Unparseable arguments, a non-object root, and an absent or non-string action
  ///     all answer FALSE (the strict path).</summary>
  private static bool IsReadOnlyAction(string? arguments, IReadOnlySet<string> readOnlyActions,
      string actionParameter)
  {
    if (string.IsNullOrWhiteSpace(arguments))
    {
      return false;
    }

    try
    {
      using JsonDocument document = JsonDocument.Parse(arguments);
      return document.RootElement.ValueKind is JsonValueKind.Object
          && document.RootElement.TryGetProperty(actionParameter, out JsonElement action)
          && action.ValueKind is JsonValueKind.String
          && readOnlyActions.Contains(action.GetString()!);
    }
    catch (JsonException)
    {
      return false;
    }
  }
}
