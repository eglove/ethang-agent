using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Strictly parsed arguments for the pdf tool (task 2): a required action switch
///     (exactly one of read, render, create, manipulate, case-sensitive), per-action
///     required and optional keys, unknown keys rejected, page-range strings parsed
///     through <see cref="PdfPageRange.Parse"/> at input time (bad grammar is refused
///     here, never at execution), expectedVersion decoded through
///     <see cref="FileVersionToken.Decode"/>, and every range checked with NO clamping.
///     Every violation is a typed <see cref="DomainError"/> - never a throw, never a
///     coercion.</summary>
public sealed record PdfToolInput(
    string Action,
    string? Path,
    int Offset,
    int MaxPages,
    IReadOnlyList<int>? Pages,
    int Dpi,
    FileVersion? ExpectedVersion,
    IReadOnlyList<PdfOperation> Operations,
    string? Markdown)
{
  private const int DefaultPagesPerRead = 20;

  private const int MaxPagesPerRead = 100;

  private const int DefaultDpi = 150;

  private const int MaxDpi = 300;

  private const int MaxOperationsPerBatch = 50;

  private const string GrammarReminder =
      "Use 1-based, comma-separated segments: N, N-M, or the open tail N- (last only).";

  /// <summary>Parses raw JSON arguments into validated input on the strict
  ///     tool-parser contract: unknown keys, wrong kinds, missing required keys,
  ///     and out-of-range values are typed errors; nothing is coerced.</summary>
  public static Result<PdfToolInput> FromArguments(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error);
    }

    JsonElement json = baseParse.Value;
    if (!json.TryGetProperty("action", out JsonElement actionEl))
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'action' is required: exactly one of read, render, create, or manipulate (case-sensitive)."));
    }

    if (actionEl.ValueKind != JsonValueKind.String)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'action' must be a string, but got {actionEl.ValueKind}."));
    }

    string action = actionEl.GetString()!;
    if (action is not ("read" or "render" or "create" or "manipulate"))
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'action' must be exactly one of read, render, create, manipulate (case-sensitive), but got '{action}'."));
    }

    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, Allowed(action));
    return unknown is not null
      ? Fail(unknown)
      : ParseAction(action, json);
  }

  /// <summary>The allowed argument keys per action (timeoutSeconds belongs to
  ///     every tool call). The list is echoed verbatim on UnknownParameter errors.
  ///     </summary>
  private static string[] Allowed(string action) => action switch
  {
    "read" => ["action", ToolTimeout.ParameterName, "path", "offset", "maxPages"],
    "render" => ["action", ToolTimeout.ParameterName, "path", "pages", "dpi"],
    "create" => ["action", ToolTimeout.ParameterName, "path", "markdown"],
    "manipulate" => ["action", ToolTimeout.ParameterName, "path", "expectedVersion", "operations"],
    _ => throw new InvalidOperationException("unreachable: action is a validated member of the four"),
  };

  /// <summary>Dispatches to the per-action parser; every action first passes the
  ///     shared path requirement (present, a string, non-empty).</summary>
  private static Result<PdfToolInput> ParseAction(string action, JsonElement json)
  {
    Result<string> path = ToolArguments.RequireString(json, "path", "Every pdf action names the document file to operate on.");
    if (!path.IsSuccess)
    {
      return Fail(path.Error);
    }

    if (path.Value.Length == 0)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue, "'path' must be non-empty."));
    }

    Result<PdfToolInput> parsed = action switch
    {
      "read" => ParseRead(json, path.Value),
      "render" => ParseRender(json, path.Value),
      "create" => ParseCreate(json, path.Value),
      _ => ParseManipulate(json, path.Value),
    };
    return parsed;
  }

  /// <summary>read: optional offset (>= 0, default 0) and maxPages (1..100, default
  ///     20). Out-of-range values are typed errors, never clamped.</summary>
  private static Result<PdfToolInput> ParseRead(JsonElement json, string path)
  {
    Result<int?> offset = ToolArguments.OptionalInt(json, "offset");
    if (!offset.IsSuccess)
    {
      return Fail(offset.Error);
    }

    if (offset.Value is < 0)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'offset' must be >= 0 (got {offset.Value}); values are never clamped."));
    }

    Result<int?> maxPages = ToolArguments.OptionalInt(json, "maxPages");
    if (!maxPages.IsSuccess)
    {
      return Fail(maxPages.Error);
    }

    bool maxPagesInRange = maxPages.Value is null or (>= 1 and <= MaxPagesPerRead);
    return maxPagesInRange
      ? Result.Success(Base("read", path) with { Offset = offset.Value ?? 0, MaxPages = maxPages.Value ?? DefaultPagesPerRead })
      : Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'maxPages' must be 1..{MaxPagesPerRead} (got {maxPages.Value}); values are never clamped."));
  }

  /// <summary>render: pages required (a page-range string parsed at input time),
  ///     dpi 1..300 default 150. The open tail is refused here because the tool layer
  ///     has no page count to resolve it against (the ACL resolves open tails, not the
  ///     input parser).</summary>
  private static Result<PdfToolInput> ParseRender(JsonElement json, string path)
  {
    Result<string> pages = ToolArguments.RequireString(json, "pages",
        "The 'render' action requires pages: a page-range string (N, N-M, comma-separated).");
    if (!pages.IsSuccess)
    {
      return Fail(pages.Error);
    }

    Result<IReadOnlyList<int>> resolved = ResolvePages(pages.Value, "render");
    if (!resolved.IsSuccess)
    {
      return Fail(resolved.Error);
    }

    Result<int?> dpi = ToolArguments.OptionalInt(json, "dpi");
    if (!dpi.IsSuccess)
    {
      return Fail(dpi.Error);
    }

    bool dpiInRange = dpi.Value is null or (>= 1 and <= MaxDpi);
    return dpiInRange
      ? Result.Success(Base("render", path) with { Pages = resolved.Value, Dpi = dpi.Value ?? DefaultDpi })
      : Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'dpi' must be 1..{MaxDpi} (got {dpi.Value}); values are never clamped."));
  }

  /// <summary>create: markdown required and non-empty (the markdown subset itself is
  ///     validated by the create surface, not here).</summary>
  private static Result<PdfToolInput> ParseCreate(JsonElement json, string path)
  {
    Result<string> markdown = ToolArguments.RequireString(json, "markdown",
        "The 'create' action requires markdown: the document body in the pinned markdown subset.");
    if (!markdown.IsSuccess)
    {
      return Fail(markdown.Error);
    }

    bool empty = markdown.Value.Length == 0;
    return empty
      ? Fail(new DomainError(ToolErrorCodes.InvalidParameterValue, "'markdown' must be non-empty."))
      : Result.Success(Base("create", path) with { Markdown = markdown.Value });
  }

  /// <summary>manipulate: expectedVersion required (decoded strictly), operations
  ///     required as a non-empty array of at most 50 operation objects, each parsed and
  ///     validated before any mutation - the first invalid op refuses the batch.</summary>
  private static Result<PdfToolInput> ParseManipulate(JsonElement json, string path)
  {
    Result<FileVersion?> version = ExpectedVersionOf(json);
    if (!version.IsSuccess)
    {
      return Fail(version.Error);
    }

    if (!json.TryGetProperty("operations", out JsonElement opsEl))
    {
      return ToolArguments.Missing<PdfToolInput>("operations",
          "The 'manipulate' action requires operations: a non-empty array of at most 50 operation objects.");
    }

    if (opsEl.ValueKind != JsonValueKind.Array)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'operations' must be an array of operation objects, but got {opsEl.ValueKind}."));
    }

    int count = opsEl.GetArrayLength();
    if (count == 0)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'operations' must be a non-empty array of operation objects."));
    }

    if (count > MaxOperationsPerBatch)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'operations' must carry at most {MaxOperationsPerBatch} operations (got {count})."));
    }

    List<PdfOperation> operations = [];
    foreach (JsonElement element in opsEl.EnumerateArray())
    {
      Result<PdfOperation> operation = ParseOperation(element);
      if (!operation.IsSuccess)
      {
        return Fail(operation.Error);
      }

      operations.Add(operation.Value);
    }

    return Result.Success(Base("manipulate", path) with { ExpectedVersion = version.Value, Operations = operations });
  }

  /// <summary>expectedVersion: required for manipulate (MissingParameter when absent),
  ///     decoded strictly through <see cref="FileVersionToken.Decode"/> - garbage fails
  ///     InvalidParameterValue.</summary>
  private static Result<FileVersion?> ExpectedVersionOf(JsonElement json)
  {
    if (!json.TryGetProperty("expectedVersion", out JsonElement el))
    {
      return ToolArguments.Missing<FileVersion?>("expectedVersion",
          "Mutating calls require the version token the previous pdf read returned (the exact v<length>:<ticks> string) so stale edits are refused.");
    }

    if (el.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<FileVersion?>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'expectedVersion' must be a string, but got {el.ValueKind}."));
    }

    Result<FileVersion> decoded = FileVersionToken.Decode(el.GetString());
    return decoded.IsSuccess
      ? Result.Success<FileVersion?>(decoded.Value)
      : Result.Failure<FileVersion?>(decoded.Error);
  }

  /// <summary>Parses one operations element against its op discriminator. Unknown
  ///     keys inside the operation object fail UnknownParameter; unknown op values and
  ///     missing fields fail InvalidParameterValue naming the op and the field; page
  ///     ranges parse through <see cref="PdfPageRange.Parse"/> here, so bad grammar is
  ///     refused at input time.</summary>
  private static Result<PdfOperation> ParseOperation(JsonElement element)
  {
    if (element.ValueKind != JsonValueKind.Object)
    {
      return Result.Failure<PdfOperation>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"Every operations element must be an object, but got {element.ValueKind}."));
    }

    if (!element.TryGetProperty("op", out JsonElement opEl))
    {
      return Result.Failure<PdfOperation>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "Every operations element must carry 'op': exactly one of rotate, deletePages, reorderPages, extractPages, merge, watermarkText, or setMetadata (case-sensitive)."));
    }

    if (opEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<PdfOperation>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'op' must be a string, but got {opEl.ValueKind}."));
    }

    string op = opEl.GetString()!;
    return op switch
    {
      "rotate" => ParseRotate(element, op),
      "deletePages" => ParseDeletePages(element, op),
      "reorderPages" => ParseReorderPages(element, op),
      "extractPages" => ParseExtractPages(element, op),
      "merge" => ParseMerge(element, op),
      "watermarkText" => ParseWatermarkText(element, op),
      "setMetadata" => ParseSetMetadata(element),
      _ => Result.Failure<PdfOperation>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'op' must be exactly one of rotate, deletePages, reorderPages, extractPages, merge, watermarkText, or setMetadata (case-sensitive), but got '{op}'.")),
    };
  }

  private static Result<PdfOperation> ParseRotate(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "pages", "degrees");
    if (unknownKeys is not null)
    {
      return Result.Failure<PdfOperation>(unknownKeys);
    }

    Result<IReadOnlyList<int>> pages = RequireOpPages(op, opName);
    if (!pages.IsSuccess)
    {
      return Result.Failure<PdfOperation>(pages.Error);
    }

    Result<int> degrees = RequireOpInt(op, opName, "degrees");
    if (!degrees.IsSuccess)
    {
      return Result.Failure<PdfOperation>(degrees.Error);
    }

    bool valid = degrees.Value is 90 or 180 or 270;
    return valid
      ? Result.Success<PdfOperation>(new RotatePages(pages.Value, degrees.Value))
      : Result.Failure<PdfOperation>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'degrees' must be exactly 90, 180, or 270 (got {degrees.Value}); values are never clamped."));
  }

  private static Result<PdfOperation> ParseDeletePages(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "pages");
    if (unknownKeys is not null)
    {
      return Result.Failure<PdfOperation>(unknownKeys);
    }

    Result<IReadOnlyList<int>> pages = RequireOpPages(op, opName);
    return !pages.IsSuccess
      ? Result.Failure<PdfOperation>(pages.Error)
      : Result.Success<PdfOperation>(new DeletePages(pages.Value));
  }

  private static Result<PdfOperation> ParseReorderPages(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "order");
    if (unknownKeys is not null)
    {
      return Result.Failure<PdfOperation>(unknownKeys);
    }

    Result<IReadOnlyList<int>> order = RequireIntArray(op, opName, "order");
    return !order.IsSuccess
      ? Result.Failure<PdfOperation>(order.Error)
      : Result.Success<PdfOperation>(new ReorderPages(order.Value));
  }

  private static Result<PdfOperation> ParseExtractPages(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "pages", "outputPath");
    if (unknownKeys is not null)
    {
      return Result.Failure<PdfOperation>(unknownKeys);
    }

    Result<IReadOnlyList<int>> pages = RequireOpPages(op, opName);
    if (!pages.IsSuccess)
    {
      return Result.Failure<PdfOperation>(pages.Error);
    }

    Result<string> outputPath = RequireOpString(op, opName, "outputPath");
    if (!outputPath.IsSuccess)
    {
      return Result.Failure<PdfOperation>(outputPath.Error);
    }

    bool empty = outputPath.Value.Length == 0;
    return empty
      ? Result.Failure<PdfOperation>(new DomainError(ToolErrorCodes.InvalidParameterValue, "'outputPath' must be non-empty."))
      : Result.Success<PdfOperation>(new ExtractPages(pages.Value, outputPath.Value));
  }

  private static Result<PdfOperation> ParseMerge(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "sources");
    if (unknownKeys is not null)
    {
      return Result.Failure<PdfOperation>(unknownKeys);
    }

    Result<IReadOnlyList<string>> sources = RequireStringArray(op, opName, "sources");
    return !sources.IsSuccess
      ? Result.Failure<PdfOperation>(sources.Error)
      : Result.Success<PdfOperation>(new Merge(sources.Value));
  }

  private static Result<PdfOperation> ParseWatermarkText(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "text", "fontSize", "opacity", "degrees");
    if (unknownKeys is not null)
    {
      return Result.Failure<PdfOperation>(unknownKeys);
    }

    Result<string> text = RequireOpString(op, opName, "text");
    if (!text.IsSuccess)
    {
      return Result.Failure<PdfOperation>(text.Error);
    }

    if (text.Value.Length == 0)
    {
      return Result.Failure<PdfOperation>(new DomainError(ToolErrorCodes.InvalidParameterValue, "'text' must be non-empty."));
    }

    Result<double?> fontSize = OptionalOpDouble(op, "fontSize");
    if (!fontSize.IsSuccess)
    {
      return Result.Failure<PdfOperation>(fontSize.Error);
    }

    if (fontSize.Value is <= 0)
    {
      return Result.Failure<PdfOperation>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'fontSize' must be > 0 (got {fontSize.Value}); values are never clamped."));
    }

    Result<double?> opacity = OptionalOpDouble(op, "opacity");
    if (!opacity.IsSuccess)
    {
      return Result.Failure<PdfOperation>(opacity.Error);
    }

    if (opacity.Value is < 0 or > 1)
    {
      return Result.Failure<PdfOperation>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'opacity' must be 0.0..1.0 (got {opacity.Value}); values are never clamped."));
    }

    Result<int?> degrees = OptionalOpInt(op, "degrees");
    if (!degrees.IsSuccess)
    {
      return Result.Failure<PdfOperation>(degrees.Error);
    }

    bool outOfRange = degrees.Value is < -180 or > 180;
    return outOfRange
      ? Result.Failure<PdfOperation>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'degrees' must be -180..180 (got {degrees.Value}); values are never clamped."))
      : Result.Success<PdfOperation>(new WatermarkText(text.Value, fontSize.Value, opacity.Value, degrees.Value));
  }

  private static Result<PdfOperation> ParseSetMetadata(JsonElement op)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "title", "author", "subject");
    if (unknownKeys is not null)
    {
      return Result.Failure<PdfOperation>(unknownKeys);
    }

    Result<string?> title = ToolArguments.OptionalString(op, "title");
    if (!title.IsSuccess)
    {
      return Result.Failure<PdfOperation>(title.Error);
    }

    Result<string?> author = ToolArguments.OptionalString(op, "author");
    if (!author.IsSuccess)
    {
      return Result.Failure<PdfOperation>(author.Error);
    }

    Result<string?> subject = ToolArguments.OptionalString(op, "subject");
    if (!subject.IsSuccess)
    {
      return Result.Failure<PdfOperation>(subject.Error);
    }

    bool none = title.Value is null && author.Value is null && subject.Value is null;
    return none
      ? Result.Failure<PdfOperation>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "The 'setMetadata' operation requires at least one of title, author, or subject."))
      : Result.Success<PdfOperation>(new SetMetadata(title.Value, author.Value, subject.Value));
  }

  /// <summary>Reads a required operation pages field: a page-range string parsed
  ///     through <see cref="PdfPageRange.Parse"/> at input time and converted to the
  ///     explicit 1-based page list. The open tail is refused here - the tool layer has
  ///     no page count to resolve it against (the ACL resolves open tails, never the
  ///     domain parser).</summary>
  private static Result<IReadOnlyList<int>> RequireOpPages(JsonElement op, string opName)
  {
    Result<string> text = RequireOpString(op, opName, "pages");
    return !text.IsSuccess
      ? Result.Failure<IReadOnlyList<int>>(text.Error)
      : ResolvePages(text.Value, opName);
  }

  /// <summary>Parses page-range text and flattens it to the explicit 1-based page
  ///     list. Bad grammar flows out verbatim; an open tail is a typed InvalidPageRange
  ///     naming the tail.</summary>
  private static Result<IReadOnlyList<int>> ResolvePages(string text, string context)
  {
    Result<PdfPageRange> parsed = PdfPageRange.Parse(text);
    if (!parsed.IsSuccess)
    {
      return Result.Failure<IReadOnlyList<int>>(parsed.Error);
    }

    if (HasOpenTail(text))
    {
      return Result.Failure<IReadOnlyList<int>>(new DomainError(PdfErrorCodes.InvalidPageRange,
          $"The '{context}' pages cannot use the open tail '{text}' - the tool layer has no page count to resolve it against; use explicit page numbers. {GrammarReminder}"));
    }

    List<int> pages = [];
    foreach (PdfPageSegment segment in parsed.Value.Segments)
    {
      int last = segment.End ?? segment.Start;
      for (int page = segment.Start; page <= last; page++)
      {
        pages.Add(page);
      }
    }

    return Result.Success<IReadOnlyList<int>>(pages);
  }

  /// <summary>True when the range text carries the open tail 'N-' (a segment ending
  ///     in '-'). The grammar is validated first, so a trailing dash is exactly the open
  ///     tail form.</summary>
  private static bool HasOpenTail(string text) =>
      text.Split(',').Any(part => part.Trim().EndsWith('-'));

  /// <summary>Reads a required operation string field: the missing-field wording is
  ///     InvalidParameterValue naming the op and the field; a wrong JSON kind is
  ///     InvalidParameterType.</summary>
  private static Result<string> RequireOpString(JsonElement op, string opName, string field)
  {
    if (!op.TryGetProperty(field, out JsonElement el))
    {
      return Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"The '{opName}' operation requires '{field}'."));
    }

    Result<string> text = el.ValueKind == JsonValueKind.String
      ? Result.Success(el.GetString()!)
      : Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{field}' must be a string, but got {el.ValueKind}."));
    return text;
  }

  /// <summary>Reads a required operation integer field: the missing-field wording is
  ///     InvalidParameterValue naming the op and the field; a wrong JSON kind is
  ///     InvalidParameterType.</summary>
  private static Result<int> RequireOpInt(JsonElement op, string opName, string field)
  {
    if (!op.TryGetProperty(field, out JsonElement el))
    {
      return Result.Failure<int>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"The '{opName}' operation requires '{field}'."));
    }

    Result<int> value = IntOf(el, field);
    return value;
  }

  /// <summary>Reads an optional operation integer field: null when absent, a typed
  ///     error when present but not an integer.</summary>
  private static Result<int?> OptionalOpInt(JsonElement op, string field)
  {
    if (!op.TryGetProperty(field, out JsonElement el))
    {
      return Result.Success<int?>(null);
    }

    Result<int> value = IntOf(el, field);
    return !value.IsSuccess
      ? Result.Failure<int?>(value.Error)
      : Result.Success<int?>(value.Value);
  }

  /// <summary>Reads an optional operation number field: null when absent, a typed
  ///     error when present but not a number.</summary>
  private static Result<double?> OptionalOpDouble(JsonElement op, string field)
  {
    if (!op.TryGetProperty(field, out JsonElement el))
    {
      return Result.Success<double?>(null);
    }

    Result<double> value = el.ValueKind == JsonValueKind.Number && el.TryGetDouble(out double parsed)
      ? Result.Success(parsed)
      : Result.Failure<double>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{field}' must be a number, but got {el.ValueKind}."));
    return !value.IsSuccess
      ? Result.Failure<double?>(value.Error)
      : Result.Success<double?>(value.Value);
  }

  /// <summary>Reads a required non-empty integer array: every entry a 1-based page
  ///     number (>= 1). Wrong kinds and out-of-range entries are typed errors, never
  ///     clamped.</summary>
  private static Result<IReadOnlyList<int>> RequireIntArray(JsonElement op, string opName, string field)
  {
    if (!op.TryGetProperty(field, out JsonElement el))
    {
      return Result.Failure<IReadOnlyList<int>>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"The '{opName}' operation requires '{field}': a non-empty array of 1-based page numbers."));
    }

    if (el.ValueKind != JsonValueKind.Array)
    {
      return Result.Failure<IReadOnlyList<int>>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{field}' must be an array of integers, but got {el.ValueKind}."));
    }

    List<int> items = [];
    foreach (JsonElement item in el.EnumerateArray())
    {
      if (item.ValueKind != JsonValueKind.Number || !item.TryGetInt32(out int value))
      {
        return Result.Failure<IReadOnlyList<int>>(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'{field}' must contain only integers, but got {item.ValueKind}."));
      }

      if (value < 1)
      {
        return Result.Failure<IReadOnlyList<int>>(new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'{field}' entries must be >= 1 (got {value}); values are never clamped."));
      }

      items.Add(value);
    }

    return items.Count == 0
      ? Result.Failure<IReadOnlyList<int>>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{field}' must be a non-empty array."))
      : Result.Success<IReadOnlyList<int>>(items);
  }

  /// <summary>Reads a required non-empty string array: every entry a non-empty string.
  ///     </summary>
  private static Result<IReadOnlyList<string>> RequireStringArray(JsonElement op, string opName, string field)
  {
    if (!op.TryGetProperty(field, out JsonElement el))
    {
      return Result.Failure<IReadOnlyList<string>>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"The '{opName}' operation requires '{field}': a non-empty array of source paths."));
    }

    if (el.ValueKind != JsonValueKind.Array)
    {
      return Result.Failure<IReadOnlyList<string>>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{field}' must be an array of strings, but got {el.ValueKind}."));
    }

    List<string> items = [];
    foreach (JsonElement item in el.EnumerateArray())
    {
      if (item.ValueKind != JsonValueKind.String)
      {
        return Result.Failure<IReadOnlyList<string>>(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'{field}' must contain only strings, but got {item.ValueKind}."));
      }

      string value = item.GetString()!;
      if (value.Length == 0)
      {
        return Result.Failure<IReadOnlyList<string>>(new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'{field}' entries must be non-empty."));
      }

      items.Add(value);
    }

    return items.Count == 0
      ? Result.Failure<IReadOnlyList<string>>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'{field}' must be a non-empty array."))
      : Result.Success<IReadOnlyList<string>>(items);
  }

  /// <summary>Validates one element as a JSON integer (the shared ToolArguments
  ///     wording, verbatim).</summary>
  private static Result<int> IntOf(JsonElement el, string field) =>
      el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int value)
        ? Result.Success(value)
        : Result.Failure<int>(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'{field}' must be a integer, but got {el.ValueKind}."));

  /// <summary>The per-action default instance: every action starts from the shared
  ///     defaults and overrides only what it parses.</summary>
  private static PdfToolInput Base(string action, string path) =>
      new(action, path, 0, DefaultPagesPerRead, null, DefaultDpi, null, [], null);

  private static Result<PdfToolInput> Fail(DomainError error) => Result.Failure<PdfToolInput>(error);
}
