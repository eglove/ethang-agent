using System.Globalization;
using System.Text;
using eThangAgent.ToolDomain;

namespace eThangAgent.AgentDomain;

/// <summary>The model-facing size budget for one tool result. A result whose text is at most
///     <see cref="MaxBytes"/> UTF-8 bytes is returned as the instance it arrived as; a
///     larger one is replaced by an error result naming the size, the limit and two
///     narrowing moves, so the model reads a refusal instead of a payload it cannot use.
///     Only text is measured: attached images never count against the budget.</summary>
public static class ToolResultBudget
{
  /// <summary>Largest tool-result text, in UTF-8 bytes, that enters the conversation.</summary>
  public const int MaxBytes = 2_097_152;

  /// <summary>Verbatim prefix of the error result that replaces an over-budget result.</summary>
  public const string RefusalPrefix = "Error [ToolResultTooLarge]: ";

  /// <summary>The result itself when its text is within the budget; otherwise an error
  ///     result whose content is <see cref="Refusal"/> for that result's byte count. The
  ///     replacement keeps the tool's display title and carries none of its images or cost.</summary>
  public static ToolResult Apply(ToolResult result)
  {
    ArgumentNullException.ThrowIfNull(result);
    string content = result.Content ?? string.Empty;
    int bytes = Encoding.UTF8.GetByteCount(content);
    return bytes <= MaxBytes
        ? result
        : new ToolResult(Refusal(bytes), true, result.Title);
  }

  /// <summary>The refusal text for a result of <paramref name="bytes"/> UTF-8 bytes: it
  ///     names the size, the limit, and the two narrowing moves — a line range for a read,
  ///     a LIMIT or fewer columns for a query.</summary>
  private static string Refusal(int bytes)
  {
    ArgumentOutOfRangeException.ThrowIfNegative(bytes);
    return RefusalPrefix + bytes.ToString(CultureInfo.InvariantCulture)
        + " UTF-8 bytes of text, over the "
        + MaxBytes.ToString(CultureInfo.InvariantCulture)
        + "-byte limit for one tool result. Narrow the call and run it again: read a line"
        + " range instead of the whole file, or a query with a LIMIT or fewer columns.";
  }
}
