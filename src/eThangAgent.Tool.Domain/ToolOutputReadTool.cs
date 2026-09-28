using System.Globalization;
using System.Text;
using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Reads one page of an archived tool result back (the store-and-read-back
///     context policy's read side). Handles appear in excerpts as
///     <c>[tool-output archived: arch:... | N chars omitted | read back with the
///     tool_output_read tool]</c>; pass the handle here with an offset to page
///     through the full content. Output format contract: an annotation line
///     <c>[tool-output-read arch:... chars 1-4000 of 20456 | lines 1-97]</c>, then
///     each content line prefixed with its global line number and <c>→</c> (the read
///     tool's gutter format; the number and arrow are never part of the content), and
///     a final <c>[more content follows — pass offset N to continue]</c> notice when
///     the page was capped. A successful page sets <see
///     cref="ToolResult.BypassesArchivePolicy"/> — the loop's store-and-read-back
///     policy never re-archives a read-back of archived content. Errors begin with
///     <c>Error [Code]:</c> — including <c>Error [ArchiveNotFound]:</c> for an
///     unknown handle.</summary>
public sealed class ToolOutputReadTool(IToolOutputArchive archive) : ITool
{
  /// <summary>Default page size in characters (~1k tokens), matching the excerpt head.</summary>
  public const int DefaultMaxChars = 4000;

  /// <summary>Upper bound for one read-back page: a page larger than this defeats the
  ///     policy that archived the content.</summary>
  public const int MaxCharsPerPage = 20000;

  private readonly IToolOutputArchive _archive = archive ?? throw new ArgumentNullException(nameof(archive));

  public ToolDefinition Definition { get; } = new(
      "tool_output_read",
      "Read one page of an archived tool result back. Oversized tool results enter the " +
      "conversation as a short excerpt whose first line is the marker " +
      "[tool-output archived: arch:... | N chars omitted | read back with the tool_output_read tool]; " +
      "pass the handle shown there (the arch:... string) to this tool to page through the full " +
      "content. Parameters: handle (required, the arch:... string from the marker), offset " +
      "(optional, 0-based character offset into the archived content; omit or 0 for the first " +
      "page), maxChars (optional, 1..20000, default 4000). Output begins with an annotation " +
      "line [tool-output-read <handle> chars <start>-<end> of <total> | lines <a>-<b>], then " +
      "each content line prefixed with its global line number and → (metadata, never part of " +
      "the content — never reproduce line numbers or arrows when quoting the content), and a " +
      "final [more content follows — pass offset N to continue] notice when the page was " +
      "capped. A successful read-back page is never re-archived: it is already a bounded " +
      "read of archived content, so the loop's store-and-read-back policy passes it " +
      "through untouched. Errors begin with Error [Code]: — including Error [ArchiveNotFound]: when no " +
      "archived content carries that handle in this workspace.",
      [
          new ToolParameter(ToolTimeout.ParameterName, ToolParameterType.WholeNumber, ToolTimeout.ParameterDescription, Minimum: 1),
          new ToolParameter("handle", ToolParameterType.Text,
              "The arch:... handle from the [tool-output archived: ...] marker line."),
          new ToolParameter("offset", ToolParameterType.WholeNumber,
              "0-based character offset into the archived content; omit or 0 for the first page.", Minimum: 0),
          new ToolParameter("maxChars", ToolParameterType.WholeNumber,
              $"Page size in characters, 1..{MaxCharsPerPage}; omit for the default ({DefaultMaxChars}).", Minimum: 1),
      ],
      ["timeoutSeconds", "handle"]);

  public Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default)
  {
    ArgumentNullException.ThrowIfNull(input);
    Result<ToolOutputReadArgs> args = ParseArguments(input.JsonArguments);
    if (!args.IsSuccess)
    {
      return Task.FromResult(Err(args.Error));
    }

    Result<ToolCallEnvelope> budget = ToolCallEnvelopeParser.Parse(input.Name, input.JsonArguments);
    return !budget.IsSuccess
      ? Task.FromResult(Err(budget.Error))
      : ToolExecution.RunAsync(input.Name, budget.Value.Timeout, token => ReadAsync(args.Value, token), ct);
  }

  private async Task<ToolResult> ReadAsync(ToolOutputReadArgs args, CancellationToken ct)
  {
    Result<ArchivePage> page = await _archive.ReadBackAsync(args.Handle, args.Offset, args.MaxChars, ct).ConfigureAwait(false);
    if (!page.IsSuccess)
    {
      return Err(page.Error);
    }

    ArchivePage read = page.Value;
    StringBuilder sb = new();
    _ = sb.AppendLine(CultureInfo.InvariantCulture,
        $"[tool-output-read {read.Handle} chars {read.Offset + 1}-{read.Offset + read.Text.Length} of {read.TotalChars} | lines {read.StartLine}-{read.EndLine}]");
    string[] lines = read.Text.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    int width = read.EndLine.ToString(CultureInfo.InvariantCulture).Length;
    for (int i = 0; i < lines.Length; i++)
    {
      _ = sb.AppendLine(CultureInfo.InvariantCulture,
          $"{(read.StartLine + i).ToString(CultureInfo.InvariantCulture).PadLeft(width)}→ {lines[i]}");
    }

    if (read.HasMore)
    {
      _ = sb.Append(CultureInfo.InvariantCulture,
          $"[more content follows — pass offset {read.Offset + read.Text.Length} to continue]");
    }
    else
    {
      sb.Length -= Environment.NewLine.Length; // trim trailing newline
    }

    // A read-back page is already a bounded read of archived content: the loop's
    // store-and-read-back policy must pass it through untouched (no nested archive).
    return new ToolResult(sb.ToString(), false) { BypassesArchivePolicy = true };
  }

  private static Result<ToolOutputReadArgs> ParseArguments(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Result.Failure<ToolOutputReadArgs>(baseParse.Error);
    }

    Result<TimeSpan> budget = ToolTimeout.Parse(baseParse.Value);
    if (!budget.IsSuccess)
    {
      return Result.Failure<ToolOutputReadArgs>(budget.Error);
    }

    string? handle = null;
    int offset = 0;
    int maxChars = DefaultMaxChars;
    List<string> unknown = [];
    foreach (JsonProperty property in baseParse.Value.EnumerateObject())
    {
      switch (property.Name)
      {
        case "handle":
          if (property.Value.ValueKind is not JsonValueKind.String)
          {
            return Result.Failure<ToolOutputReadArgs>(new DomainError(ToolErrorCodes.InvalidParameterType,
                "'handle' must be a string, but got " + property.Value.ValueKind + "."));
          }

          handle = property.Value.GetString();
          break;
        case "offset":
          if (property.Value.ValueKind is not JsonValueKind.Number ||
              !property.Value.TryGetInt32(out int parsedOffset) || parsedOffset < 0)
          {
            return Result.Failure<ToolOutputReadArgs>(new DomainError(ToolErrorCodes.InvalidParameterValue,
              "'offset' must be a non-negative whole number."));
          }

          offset = parsedOffset;
          break;
        case "maxChars":
          if (property.Value.ValueKind is not JsonValueKind.Number ||
              !property.Value.TryGetInt32(out int parsedMax) || parsedMax < 1 || parsedMax > MaxCharsPerPage)
          {
            return Result.Failure<ToolOutputReadArgs>(new DomainError(ToolErrorCodes.InvalidParameterValue,
              $"'maxChars' must be a whole number between 1 and {MaxCharsPerPage}."));
          }

          maxChars = parsedMax;
          break;
        case ToolTimeout.ParameterName:
          break;
        default:
          unknown.Add(property.Name);
          break;
      }
    }

    return unknown.Count > 0
      ? Result.Failure<ToolOutputReadArgs>(new DomainError(ToolErrorCodes.UnknownParameter,
          $"Unknown parameter(s): {string.Join(", ", unknown)}. " +
          "Supported: timeoutSeconds, handle, offset, maxChars."))
      : ParseHandle(handle, offset, maxChars);
  }

  private static Result<ToolOutputReadArgs> ParseHandle(string? handle, int offset, int maxChars)
  {
    return string.IsNullOrWhiteSpace(handle)
      ? Result.Failure<ToolOutputReadArgs>(new DomainError(ToolErrorCodes.MissingParameter,
          "Missing required parameter 'handle'. Pass the arch:... handle from the " +
          "[tool-output archived: ...] marker line."))
      : ValidateHandleShape(handle, maxChars, offset);
  }

  private static Result<ToolOutputReadArgs> ValidateHandleShape(string handle, int maxChars, int offset)
  {
    return ToolOutputArchiveFormat.IsWellFormedHandle(handle)
      ? Result.Success(new ToolOutputReadArgs(handle, offset, maxChars))
      : Result.Failure<ToolOutputReadArgs>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'handle' must be an arch: handle exactly as shown in the " +
          "[tool-output archived: ...] marker (arch: plus 16 hex characters)."));
  }

  private sealed record ToolOutputReadArgs(string Handle, int Offset, int MaxChars);

  private static ToolResult Err(DomainError error) => new($"Error [{error.Code}]: {error.Message}", true);
}
