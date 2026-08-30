using System.Text.Json;

namespace eThangAgent.Desktop.ViewModels;

/// <summary>Formats tool-call arguments for the transcript cards: pretty JSON when
///     the raw text parses as JSON, otherwise verbatim; short single-line previews
///     for the collapsed card header.</summary>
internal static class ToolArgsFormatter
{
  private static readonly JsonSerializerOptions Pretty = new() { WriteIndented = true };

  public static string Indent(string rawArguments)
  {
    if (string.IsNullOrWhiteSpace(rawArguments))
    {
      return rawArguments;
    }

    try
    {
      using JsonDocument document = JsonDocument.Parse(rawArguments);
      return JsonSerializer.Serialize(document.RootElement, Pretty);
    }
    catch (JsonException)
    {
      return rawArguments; // not JSON - show verbatim, never lose content
    }
  }

  public static string Preview(string rawArguments, int maxChars = 96)
  {
    string single = rawArguments.Replace("\n", " ", StringComparison.Ordinal)
        .Replace("\r", " ", StringComparison.Ordinal);
    return single.Length <= maxChars ? single : single[..maxChars] + "…";
  }
}
