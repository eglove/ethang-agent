using eThangAgent.ModelDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Composition;

/// <summary>The ticked tool names that resolved on this machine, plus the raw stored value
///     they were resolved from.
///     <para><see cref="List"/> is what the prompt announces. <see cref="Stored"/> rides the
///     settings JSON so the conversation host - which builds the prompt for every production
///     turn - re-resolves through the SAME rule rather than trusting a list computed in
///     another process.</para></summary>
public sealed record ResolvedAvailableTools(IReadOnlyList<string> List, string? Stored)
{
  /// <summary>Resolves the stored ticked names against THIS machine's PATH.</summary>
  public static ResolvedAvailableTools Resolve(string? stored) =>
      Resolve(stored, MachinePath());

  /// <summary>Resolves the stored ticked names against <paramref name="pathDirectories" />,
  ///     keeping only the names whose first PATH match is a console program or a script, in
  ///     stored order.
  ///     <para>A stored value that does not parse resolves NOTHING and keeps its raw text on
  ///     <see cref="Stored" />: a bad value never half-announces a partially understood set,
  ///     and the raw text still rides the settings so a host re-reads rather than trusts.</para></summary>
  public static ResolvedAvailableTools Resolve(string? stored, IEnumerable<string> pathDirectories)
  {
    ArgumentNullException.ThrowIfNull(pathDirectories);

    if (PathToolPreferences.Parse(stored) is not { IsSuccess: true } parsed)
    {
      return new([], stored);
    }

    if (parsed.Value.Count == 0)
    {
      return new([], stored);
    }

    string[] directories = [.. pathDirectories];
    List<string> available = [];
    foreach (PathToolResolution resolution in PathToolDiscovery.Resolve(parsed.Value, directories))
    {
      if (resolution.IsResolved)
      {
        available.Add(resolution.Name);
      }
    }

    return new(available, stored);
  }

  /// <summary>The directories this process's PATH names, in PATH order. The one place the
  ///     environment is read: the overload above takes the list explicitly so the rule is
  ///     testable without touching the machine.</summary>
  public static IReadOnlyList<string> MachinePath() =>
      (Environment.GetEnvironmentVariable("PATH") ?? string.Empty)
          .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries);
}

/// <summary>Names the command-line tools the user ticked and this machine can actually run.
/// <para>THE ANNOUNCEMENT IS ONLY WORTH MAKING IF IT IS TRUE: a name is announced because it
/// resolved against the PATH at construction, so every tool the prompt names is one
/// <c>Shell</c> can start. A ticked name that no longer resolves is omitted with no trace -
/// naming an unrunnable tool is the failure this capability exists to end.</para>
/// <para>Bare names only: no versions, no descriptions, no per-tool annotation. Probing a
/// version would be one process spawn per tool per session start, and what a model needs is
/// which verbs exist, not what they report.</para>
/// <para>Renders EMPTY when nothing is ticked or nothing ticked resolves, so the composite
/// prompt drops the entry and a user who never opens the Tools page keeps a byte-identical
/// prompt.</para>
/// <para>PURITY: the resolved list is snapshotted at CONSTRUCTION, so two calls return the
/// same bytes and no <c>Build</c> call touches the file system.</para></summary>
public sealed class AvailableToolsPromptProvider(IReadOnlyList<string> available)
    : ISystemPromptProvider
{
  private readonly IReadOnlyList<string> _available =
      available ?? throw new ArgumentNullException(nameof(available));

  public string Build() =>
      _available.Count == 0 ? string.Empty : Render();

  /// <summary>One sentence naming the tools, then the rule that keeps the model from
  ///     reading the list as exhaustive.</summary>
  private string Render() =>
      "Command-line tools on this machine: " +
      string.Join(", ", _available) +
      " are on the PATH and can be run directly through exec's Shell. Other tools may exist " +
      "on this machine but are not listed here.";
}
