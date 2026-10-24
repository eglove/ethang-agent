using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>Names the session's user-approved external roots in the system prompt
/// (issue #96). The approval is USELESS to the model unless it knows about it: a root
/// approved for reading is expressed in the path resolver's refusal, but nothing told
/// the model to try it in the first place, so the recurring workaround - raw
/// <c>System.IO</c> in <c>exec</c> - was what the model reached for instead.
/// <para>A few dozen tokens, and only when something is approved: with no roots the
/// provider renders EMPTY, the composite prompt drops empty entries, and the prompt is
/// byte-identical to a build without this capability.</para>
/// <para>PURITY (issue #267): the list is snapshotted at CONSTRUCTION, so two calls
/// return the same bytes. An approval changed mid-session takes effect on the next
/// session - a session's roots are a container-build decision, exactly like the session
/// files and the skill directories.</para></summary>
public sealed class TrustedDirectoriesPromptProvider(IReadOnlyList<TrustedDirectory> trusted)
    : ISystemPromptProvider
{
  private readonly IReadOnlyList<TrustedDirectory> _trusted =
      trusted ?? throw new ArgumentNullException(nameof(trusted));

  public string Build() =>
      _trusted.Count == 0 ? string.Empty : Render();

  /// <summary>One line per root with its permission, plus the rule that keeps the model
  ///     from treating an approval as a licence beyond it.</summary>
  private string Render() =>
      "Trusted directories: the user approved these external roots for this workspace, so " +
      "the file tools reach them directly - no exec workaround is needed and none should " +
      "be used. " +
      string.Join("; ", _trusted.Select(t =>
          $"'{t.Root}' ({(t.Editable ? "read and write" : "read only")})")) +
      ". Their paths are absolute: pass them to read/edit/write/find_files as-is. A root " +
      "marked read only accepts every reading tool and refuses writes with " +
      "Error [TrustedRootReadOnly]; nothing outside the workspace and these roots is " +
      "reachable by the mutating tools.";
}
