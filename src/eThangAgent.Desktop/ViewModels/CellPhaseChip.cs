namespace eThangAgent.Desktop.ViewModels;

/// <summary>The phase chip's FIXED vocabulary (issue #331): five values, so the same agent state
///     always reads the same way.
///     <para>WHY A CLOSED SET RATHER THAN THE EVENT'S OWN LABEL: <c>AgentProgressEvent</c> carries
///     wire vocabulary (<c>turn-start</c>, <c>iteration</c>, <c>tool:NAME</c>). A chip that
///     sometimes showed that text and sometimes a rendering of it would be worse than one that
///     is occasionally vague, so an unrecognised label falls back to <see cref="Thinking"/>
///     rather than being echoed.</para></summary>
internal static class CellPhaseChip
{
  /// <summary>The cell exists and no progress beat has arrived yet.</summary>
  public const string Starting = "Starting";

  /// <summary>A model call is in flight: the <c>turn-start</c> and <c>iteration</c> labels,
  ///     and the fallback for anything this vocabulary does not name.</summary>
  public const string Thinking = "Thinking";

  /// <summary>The settled fact arrived and the agent's report has not been read yet.</summary>
  public const string Settling = "Settling";

  /// <summary>The run is over: the final read completed, or failed and was reported.</summary>
  public const string Finished = "Finished";

  /// <summary>The label prefix a tool dispatch beat carries.</summary>
  private const string ToolPrefix = "tool:";

  /// <summary>The chip for a tool dispatch: the tool's own name, exactly as the model sees it.
  ///     <para>THE NAME IS NOT RE-CASED. The model names its tools, and a chip that rendered
  ///     <c>exec</c> as <c>Exec</c> would be the UI inventing a spelling the transcript does not
  ///     use.</para></summary>
  /// <param name="toolName">The name after the <c>tool:</c> prefix.</param>
  public static string Running(string toolName) => "Running " + toolName;

  /// <summary>The chip for a progress beat's label.</summary>
  /// <param name="label">The beat's short label, or null.</param>
  public static string FromLabel(string? label)
  {
    if (string.IsNullOrWhiteSpace(label))
    {
      return Thinking;
    }

    if (!label.StartsWith(ToolPrefix, StringComparison.Ordinal))
    {
      // turn-start, iteration, and anything this vocabulary does not name.
      return Thinking;
    }

    string tool = label[ToolPrefix.Length..];
    return tool.Length == 0 ? Thinking : Running(tool);
  }
}
