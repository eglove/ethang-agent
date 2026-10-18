using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Strictly parsed arguments for the docx tool (task 2): a required action switch
///     (exactly one of read, edit, create, comments, revisions, extract, case-sensitive),
///     per-action required and optional keys, unknown keys rejected, every address
///     field parsed through <see cref="DocxAddress.Parse"/> at input time (bad grammar
///     is refused here, never at execution), expectedVersion decoded through
///     <see cref="FileVersionToken.Decode"/>, and every range checked with NO
///     clamping. Every violation is a typed <see cref="DomainError"/> - never a throw,
///     never a coercion.</summary>
public sealed record DocxToolInput(
    string Action,
    string? Path,
    int Offset,
    int MaxBlocks,
    FileVersion? ExpectedVersion,
    IReadOnlyList<DocxOperation> Operations,
    string? Markdown,
    string? CommentAction,
    int CommentId,
    string? Address,
    string? Author,
    string? Text,
    string? RevisionAction,
    DocxRevisionScope? Scope,
    IReadOnlyList<string>? ImageNames)
{
  private const int DefaultMaxBlocks = 200;

  private const int MaxBlocksCap = 400;

  private const int MaxOperations = 50;

  /// <summary>Parses raw JSON arguments into validated input on the strict
  ///     tool-parser contract: unknown keys, wrong kinds, missing required keys,
  ///     and out-of-range values are typed errors; nothing is coerced.</summary>
  public static Result<DocxToolInput> FromArguments(string jsonArguments)
  {
    Result<JsonElement> baseParse = ToolArguments.ParseObject(jsonArguments);
    if (!baseParse.IsSuccess)
    {
      return Fail(baseParse.Error);
    }

    JsonElement json = baseParse.Value;
    Result<string> action = ParseActionName(json);
    if (!action.IsSuccess)
    {
      return Fail(action.Error);
    }

    DomainError? unknown = ToolArguments.RejectUnknownParameters(json, Allowed(action.Value));
    return unknown is not null
      ? Fail(unknown)
      : ParseAction(action.Value, json);
  }

  /// <summary>Reads the required 'action' discriminator: present, a string, and a
  ///     validated member of the six actions.</summary>
  private static Result<string> ParseActionName(JsonElement json)
  {
    if (!json.TryGetProperty("action", out JsonElement actionEl))
    {
      return Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'action' is required: exactly one of read, edit, create, comments, revisions, or extract (case-sensitive)."));
    }

    if (actionEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'action' must be a string, but got {actionEl.ValueKind}."));
    }

    string action = actionEl.GetString()!;
    return action is not ("read" or "edit" or "create" or "comments" or "revisions" or "extract")
      ? Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'action' must be exactly one of read, edit, create, comments, revisions, extract (case-sensitive), but got '{action}'."))
      : Result.Success(action);
  }

  /// <summary>The allowed argument keys per action (timeoutSeconds belongs to
  ///     every tool call). The list is echoed verbatim on UnknownParameter errors.
  ///     </summary>
  private static string[] Allowed(string action) => action switch
  {
    "read" => ["action", ToolTimeout.ParameterName, "path", "offset", "maxBlocks"],
    "edit" => ["action", ToolTimeout.ParameterName, "path", "expectedVersion", "operations"],
    "create" => ["action", ToolTimeout.ParameterName, "path", "markdown"],
    "comments" => ["action", ToolTimeout.ParameterName, "path", "commentAction", "address", "author", "text", "commentId", "expectedVersion"],
    "revisions" => ["action", ToolTimeout.ParameterName, "path", "revisionAction", "scope", "expectedVersion"],
    "extract" => ["action", ToolTimeout.ParameterName, "path", "names"],
    _ => throw new InvalidOperationException("unreachable: action is a validated member of the six"),
  };

  /// <summary>Dispatches to the per-action parser; every action first passes the
  ///     shared path requirement (present, a string, non-empty).</summary>
  private static Result<DocxToolInput> ParseAction(string action, JsonElement json)
  {
    Result<string> path = ToolArguments.RequireString(json, "path", "Every docx action names the document file to operate on.");
    if (!path.IsSuccess)
    {
      return Fail(path.Error);
    }

    if (path.Value.Length == 0)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue, "'path' must be non-empty."));
    }

    Result<DocxToolInput> parsed = action switch
    {
      "read" => ParseRead(json, path.Value),
      "edit" => ParseEdit(json, path.Value),
      "create" => ParseCreate(json, path.Value),
      "comments" => ParseComments(json, path.Value),
      "revisions" => ParseRevisions(json, path.Value),
      "extract" => ParseExtract(json, path.Value),
      _ => throw new InvalidOperationException("unreachable: action is a validated member of the six"),
    };
    return parsed;
  }

  /// <summary>read: optional offset (>= 0, default 0) and maxBlocks (1..400, default
  ///     200). Out-of-range values are typed errors, never clamped.</summary>
  private static Result<DocxToolInput> ParseRead(JsonElement json, string path)
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

    Result<int?> maxBlocks = ToolArguments.OptionalInt(json, "maxBlocks");
    if (!maxBlocks.IsSuccess)
    {
      return Fail(maxBlocks.Error);
    }

    bool maxBlocksInRange = maxBlocks.Value is null or (>= 1 and <= MaxBlocksCap);
    return maxBlocksInRange
      ? Result.Success(new DocxToolInput("read", path, offset.Value ?? 0, maxBlocks.Value ?? DefaultMaxBlocks,
        null, [], null, null, 0, null, null, null, null, null, null))
      : Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'maxBlocks' must be 1..{MaxBlocksCap} (got {maxBlocks.Value}); values are never clamped."));
  }

  /// <summary>edit: expectedVersion required (decoded strictly), operations required
  ///     as a non-empty array of at most 50 operation objects, each parsed and
  ///     validated before any mutation - the first invalid op refuses the batch.</summary>
  private static Result<DocxToolInput> ParseEdit(JsonElement json, string path)
  {
    Result<FileVersion?> version = ExpectedVersionOf(json, required: true);
    if (!version.IsSuccess)
    {
      return Fail(version.Error);
    }

    if (!json.TryGetProperty("operations", out JsonElement opsEl))
    {
      return ToolArguments.Missing<DocxToolInput>("operations",
          "The 'edit' action requires operations: a non-empty array of at most 50 operation objects.");
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

    if (count > MaxOperations)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'operations' must carry at most {MaxOperations} operations (got {count})."));
    }

    List<DocxOperation> operations = [];
    foreach (JsonElement element in opsEl.EnumerateArray())
    {
      Result<DocxOperation> operation = ParseOperation(element);
      if (!operation.IsSuccess)
      {
        return Fail(operation.Error);
      }

      operations.Add(operation.Value);
    }

    return Result.Success(new DocxToolInput("edit", path, 0, DefaultMaxBlocks, version.Value, operations,
        null, null, 0, null, null, null, null, null, null));
  }

  /// <summary>create: optional markdown content (the markdown subset itself is
  ///     validated by the create surface, not here).</summary>
  private static Result<DocxToolInput> ParseCreate(JsonElement json, string path)
  {
    Result<string?> markdown = ToolArguments.OptionalString(json, "markdown");
    return !markdown.IsSuccess
      ? Fail(markdown.Error)
      : Result.Success(new DocxToolInput("create", path, 0, DefaultMaxBlocks, null, [],
          markdown.Value, null, 0, null, null, null, null, null, null));
  }

  /// <summary>comments: commentAction required (list|add|delete); add requires
  ///     address (grammar-parsed), author, and text (both non-empty) plus
  ///     expectedVersion; delete requires commentId >= 1 plus expectedVersion.</summary>
  private static Result<DocxToolInput> ParseComments(JsonElement json, string path)
  {
    Result<string> commentAction = ToolArguments.RequireString(json, "commentAction",
        "The 'comments' action requires commentAction: exactly one of list, add, or delete (case-sensitive).");
    if (!commentAction.IsSuccess)
    {
      return Fail(commentAction.Error);
    }

    if (commentAction.Value is not ("list" or "add" or "delete"))
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'commentAction' must be exactly one of list, add, delete (case-sensitive), but got '{commentAction.Value}'."));
    }

    Result<FileVersion?> version = ExpectedVersionOf(json, required: commentAction.Value != "list");
    if (!version.IsSuccess)
    {
      return Fail(version.Error);
    }

    Result<DocxToolInput> parsed = commentAction.Value switch
    {
      "list" => Result.Success(new DocxToolInput("comments", path, 0, DefaultMaxBlocks, version.Value, [],
          null, "list", 0, null, null, null, null, null, null)),
      "add" => ParseCommentAdd(json, path, version.Value),
      _ => ParseCommentDelete(json, path, version.Value),
    };
    return parsed;
  }

  /// <summary>comments add: address grammar-parsed at input time; author and text
  ///     required non-empty.</summary>
  private static Result<DocxToolInput> ParseCommentAdd(JsonElement json, string path, FileVersion? version)
  {
    Result<string> address = ToolArguments.RequireString(json, "address",
        "The 'comments add' action requires address: the block the comment anchors to (p<N>, t<N>, t<N>.r<R>.c<C>, sec<S>.header.p<N>, or sec<S>.footer.p<N>).");
    if (!address.IsSuccess)
    {
      return Fail(address.Error);
    }

    Result<DocxAddress> parsedAddress = DocxAddress.Parse(address.Value);
    if (!parsedAddress.IsSuccess)
    {
      return Fail(parsedAddress.Error);
    }

    Result<string> author = ToolArguments.RequireString(json, "author",
        "The 'comments add' action requires author: the comment author's name, non-empty.");
    if (!author.IsSuccess)
    {
      return Fail(author.Error);
    }

    if (author.Value.Length == 0)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue, "'author' must be non-empty."));
    }

    Result<string> text = ToolArguments.RequireString(json, "text",
        "The 'comments add' action requires text: the comment body, non-empty.");
    if (!text.IsSuccess)
    {
      return Fail(text.Error);
    }

    if (text.Value.Length == 0)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue, "'text' must be non-empty."));
    }

    DocxToolInput input = new("comments", path, 0, DefaultMaxBlocks, version, [],
        null, "add", 0, address.Value, author.Value, text.Value, null, null, null);
    return Result.Success(input);
  }

  /// <summary>comments delete: commentId required, >= 1.</summary>
  private static Result<DocxToolInput> ParseCommentDelete(JsonElement json, string path, FileVersion? version)
  {
    Result<int> commentId = ToolArguments.RequireInt(json, "commentId",
        "The 'comments delete' action requires commentId: the id of the comment to delete (integer >= 1).");
    if (!commentId.IsSuccess)
    {
      return Fail(commentId.Error);
    }

    if (commentId.Value < 1)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'commentId' must be >= 1 (got {commentId.Value}); values are never clamped."));
    }

    DocxToolInput input = new("comments", path, 0, DefaultMaxBlocks, version, [],
        null, "delete", commentId.Value, null, null, null, null, null, null);
    return Result.Success(input);
  }

  /// <summary>revisions: revisionAction required (list|accept|reject); accept/reject
  ///     require expectedVersion and take an optional scope object (default All);
  ///     the scope carries exactly one selector.</summary>
  private static Result<DocxToolInput> ParseRevisions(JsonElement json, string path)
  {
    Result<string> revisionAction = ToolArguments.RequireString(json, "revisionAction",
        "The 'revisions' action requires revisionAction: exactly one of list, accept, or reject (case-sensitive).");
    if (!revisionAction.IsSuccess)
    {
      return Fail(revisionAction.Error);
    }

    if (revisionAction.Value is not ("list" or "accept" or "reject"))
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'revisionAction' must be exactly one of list, accept, reject (case-sensitive), but got '{revisionAction.Value}'."));
    }

    bool mutating = revisionAction.Value != "list";
    Result<FileVersion?> version = ExpectedVersionOf(json, required: mutating);
    if (!version.IsSuccess)
    {
      return Fail(version.Error);
    }

    DocxRevisionScope? scope = null;
    if (Has(json, "scope"))
    {
      Result<DocxRevisionScope> parsedScope = ParseScope(json);
      if (!parsedScope.IsSuccess)
      {
        return Fail(parsedScope.Error);
      }

      scope = parsedScope.Value;
    }
    else if (mutating)
    {
      scope = new DocxRevisionScope(true, null, null);
    }

    return Result.Success(new DocxToolInput("revisions", path, 0, DefaultMaxBlocks, version.Value, [],
        null, null, 0, null, null, null, revisionAction.Value, scope, null));
  }

  /// <summary>Scope: object with at most one selector - all=true, a non-empty author,
  ///     or a grammar-valid address. The record constructor refuses more than one;
  ///     this parser refuses first, as a typed error.</summary>
  private static Result<DocxRevisionScope> ParseScope(JsonElement json)
  {
    JsonElement scope = json.GetProperty("scope");
    if (scope.ValueKind != JsonValueKind.Object)
    {
      return Result.Failure<DocxRevisionScope>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'scope' must be an object with at most one of all, author, or address, but got {scope.ValueKind}."));
    }

    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(scope, "all", "author", "address");
    if (unknownKeys is not null)
    {
      return Result.Failure<DocxRevisionScope>(unknownKeys);
    }

    Result<ScopeSelectors> selectors = ReadScopeSelectors(scope);
    return selectors.IsSuccess
      ? BuildScope(selectors.Value)
      : Result.Failure<DocxRevisionScope>(selectors.Error);
  }

  /// <summary>The three optional scope selectors, read but not yet cross-validated.</summary>
  private sealed record ScopeSelectors(bool? All, string? Author, string? Address);

  /// <summary>Reads the three optional selector fields; a wrong kind on any of them
  ///     is a typed error.</summary>
  private static Result<ScopeSelectors> ReadScopeSelectors(JsonElement scope)
  {
    Result<bool?> all = ToolArguments.OptionalBool(scope, "all");
    if (!all.IsSuccess)
    {
      return Result.Failure<ScopeSelectors>(all.Error);
    }

    Result<string?> author = ToolArguments.OptionalString(scope, "author");
    if (!author.IsSuccess)
    {
      return Result.Failure<ScopeSelectors>(author.Error);
    }

    Result<string?> address = ToolArguments.OptionalString(scope, "address");
    return !address.IsSuccess
      ? Result.Failure<ScopeSelectors>(address.Error)
      : Result.Success(new ScopeSelectors(all.Value, author.Value, address.Value));
  }

  /// <summary>Cross-validates the selectors - exactly one of them, a non-empty
  ///     author, and a grammar-valid address - then constructs the scope through the
  ///     record constructor, whose ArgumentException re-surfaces as a typed
  ///     InvalidParameterValue.</summary>
  private static Result<DocxRevisionScope> BuildScope(ScopeSelectors selectors)
  {
    bool allSelected = selectors.All == true;
    int count = (allSelected ? 1 : 0) + (selectors.Author is not null ? 1 : 0) + (selectors.Address is not null ? 1 : 0);
    if (count != 1)
    {
      return Result.Failure<DocxRevisionScope>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'scope' must carry exactly one selector: all=true, a non-empty author, or a non-empty address; got {count}."));
    }

    if (selectors.Author is not null && selectors.Author.Length == 0)
    {
      return Result.Failure<DocxRevisionScope>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'scope.author' must be non-empty when given."));
    }

    if (selectors.Address is not null)
    {
      Result<DocxAddress> parsedAddress = DocxAddress.Parse(selectors.Address);
      if (!parsedAddress.IsSuccess)
      {
        return Result.Failure<DocxRevisionScope>(parsedAddress.Error);
      }
    }

    try
    {
      return Result.Success(new DocxRevisionScope(allSelected, selectors.Author, selectors.Address));
    }
    catch (ArgumentException ex)
    {
      return Result.Failure<DocxRevisionScope>(new DomainError(ToolErrorCodes.InvalidParameterValue, ex.Message));
    }
  }

  /// <summary>extract: optional names array; entries must be non-empty media names.
  ///     </summary>
  private static Result<DocxToolInput> ParseExtract(JsonElement json, string path)
  {
    Result<IReadOnlyList<string>?> names = ToolArguments.OptionalStringArray(json, "names");
    if (!names.IsSuccess)
    {
      return Fail(names.Error);
    }

    bool hasEmptyName = names.Value is not null && names.Value.Any(name => name.Length == 0);
    return hasEmptyName
      ? Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'names' entries must be non-empty media names."))
      : Result.Success(new DocxToolInput("extract", path, 0, DefaultMaxBlocks, null, [],
        null, null, 0, null, null, null, null, null, names.Value));
  }

  /// <summary>expectedVersion: required for mutating calls (MissingParameter when
  ///     absent), optional-but-validated elsewhere; decoded strictly through
  ///     <see cref="FileVersionToken.Decode"/> - garbage fails InvalidParameterValue.
  ///     </summary>
  private static Result<FileVersion?> ExpectedVersionOf(JsonElement json, bool required)
  {
    if (!json.TryGetProperty("expectedVersion", out JsonElement el))
    {
      return required
        ? ToolArguments.Missing<FileVersion?>("expectedVersion",
            "Mutating calls require the version token the previous docx read returned (the exact v<length>:<ticks> string) so stale edits are refused.")
        : Result.Success<FileVersion?>(null);
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
  ///     keys inside the operation object fail UnknownParameter (the ComputerToolInput
  ///     discipline); unknown op values and missing fields fail InvalidParameterValue
  ///     naming the op and the field; address fields parse through
  ///     <see cref="DocxAddress.Parse"/> here, so bad grammar is refused at input time.
  ///     </summary>
  private static Result<DocxOperation> ParseOperation(JsonElement element)
  {
    if (element.ValueKind != JsonValueKind.Object)
    {
      return Result.Failure<DocxOperation>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"Every operations element must be an object, but got {element.ValueKind}."));
    }

    Result<string> opName = ParseOpName(element);
    return opName.IsSuccess
      ? DispatchOperation(element, opName.Value)
      : Result.Failure<DocxOperation>(opName.Error);
  }

  /// <summary>Reads the required 'op' discriminator: present and a string.</summary>
  private static Result<string> ParseOpName(JsonElement element)
  {
    if (!element.TryGetProperty("op", out JsonElement opEl))
    {
      return Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "Every operations element must carry 'op': exactly one of replaceParagraphText, setParagraphStyle, insertParagraph, deleteParagraph, setCellText, insertTableRow, deleteTableRow, applyRunFormatting, or findReplace (case-sensitive)."));
    }

    Result<string> op = opEl.ValueKind == JsonValueKind.String
      ? Result.Success(opEl.GetString()!)
      : Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'op' must be a string, but got {opEl.ValueKind}."));
    return op;
  }

  /// <summary>Dispatches a validated op name to its per-op parser.</summary>
  private static Result<DocxOperation> DispatchOperation(JsonElement element, string op)
    => op switch
    {
      "replaceParagraphText" => ParseReplaceParagraphText(element, op),
      "setParagraphStyle" => ParseSetParagraphStyle(element, op),
      "insertParagraph" => ParseInsertParagraph(element, op),
      "deleteParagraph" => ParseAddressOnly(element, op),
      "setCellText" => ParseSetCellText(element, op),
      _ => DispatchRemainingOperation(element, op),
    };

  /// <summary>Dispatches the remaining validated op names to their per-op parsers.</summary>
  private static Result<DocxOperation> DispatchRemainingOperation(JsonElement element, string op)
    => op switch
    {
      "insertTableRow" => ParseInsertTableRow(element, op),
      "deleteTableRow" => ParseDeleteTableRow(element, op),
      "applyRunFormatting" => ParseApplyRunFormatting(element, op),
      "findReplace" => ParseFindReplace(element, op),
      _ => Result.Failure<DocxOperation>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'op' must be exactly one of replaceParagraphText, setParagraphStyle, insertParagraph, deleteParagraph, setCellText, insertTableRow, deleteTableRow, applyRunFormatting, or findReplace (case-sensitive), but got '{op}'.")),
    };

  private static Result<DocxOperation> ParseReplaceParagraphText(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "address", "text");
    if (unknownKeys is not null)
    {
      return Result.Failure<DocxOperation>(unknownKeys);
    }

    Result<string> address = RequireOpAddress(op, opName, "address");
    if (!address.IsSuccess)
    {
      return Result.Failure<DocxOperation>(address.Error);
    }

    Result<string> text = RequireOpString(op, opName, "text");
    return !text.IsSuccess
      ? Result.Failure<DocxOperation>(text.Error)
      : Result.Success<DocxOperation>(new ReplaceParagraphText(address.Value, text.Value));
  }

  private static Result<DocxOperation> ParseSetParagraphStyle(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "address", "style");
    if (unknownKeys is not null)
    {
      return Result.Failure<DocxOperation>(unknownKeys);
    }

    Result<string> address = RequireOpAddress(op, opName, "address");
    if (!address.IsSuccess)
    {
      return Result.Failure<DocxOperation>(address.Error);
    }

    Result<string> style = RequireOpString(op, opName, "style");
    return !style.IsSuccess
      ? Result.Failure<DocxOperation>(style.Error)
      : Result.Success<DocxOperation>(new SetParagraphStyle(address.Value, style.Value));
  }

  private static Result<DocxOperation> ParseInsertParagraph(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "afterAddress", "text", "style");
    if (unknownKeys is not null)
    {
      return Result.Failure<DocxOperation>(unknownKeys);
    }

    Result<string> afterAddress = RequireOpAddress(op, opName, "afterAddress", admitsP0Sentinel: true);
    if (!afterAddress.IsSuccess)
    {
      return Result.Failure<DocxOperation>(afterAddress.Error);
    }

    Result<string> text = RequireOpString(op, opName, "text");
    if (!text.IsSuccess)
    {
      return Result.Failure<DocxOperation>(text.Error);
    }

    Result<string?> style = ToolArguments.OptionalString(op, "style");
    return !style.IsSuccess
      ? Result.Failure<DocxOperation>(style.Error)
      : Result.Success<DocxOperation>(new InsertParagraph(afterAddress.Value, text.Value, style.Value));
  }

  private static Result<DocxOperation> ParseAddressOnly(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "address");
    if (unknownKeys is not null)
    {
      return Result.Failure<DocxOperation>(unknownKeys);
    }

    Result<string> address = RequireOpAddress(op, opName, "address");
    return !address.IsSuccess
      ? Result.Failure<DocxOperation>(address.Error)
      : Result.Success<DocxOperation>(new DeleteParagraph(address.Value));
  }

  private static Result<DocxOperation> ParseSetCellText(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "address", "text");
    if (unknownKeys is not null)
    {
      return Result.Failure<DocxOperation>(unknownKeys);
    }

    Result<string> address = RequireOpAddress(op, opName, "address");
    if (!address.IsSuccess)
    {
      return Result.Failure<DocxOperation>(address.Error);
    }

    Result<string> text = RequireOpString(op, opName, "text");
    return !text.IsSuccess
      ? Result.Failure<DocxOperation>(text.Error)
      : Result.Success<DocxOperation>(new SetCellText(address.Value, text.Value));
  }

  private static Result<DocxOperation> ParseInsertTableRow(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "tableAddress", "afterRow");
    if (unknownKeys is not null)
    {
      return Result.Failure<DocxOperation>(unknownKeys);
    }

    Result<string> tableAddress = RequireOpAddress(op, opName, "tableAddress");
    if (!tableAddress.IsSuccess)
    {
      return Result.Failure<DocxOperation>(tableAddress.Error);
    }

    Result<int> afterRow = RequireOpInt(op, opName, "afterRow");
    return !afterRow.IsSuccess
      ? Result.Failure<DocxOperation>(afterRow.Error)
      : Result.Success<DocxOperation>(new InsertTableRow(tableAddress.Value, afterRow.Value));
  }

  private static Result<DocxOperation> ParseDeleteTableRow(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "tableAddress", "row");
    if (unknownKeys is not null)
    {
      return Result.Failure<DocxOperation>(unknownKeys);
    }

    Result<string> tableAddress = RequireOpAddress(op, opName, "tableAddress");
    if (!tableAddress.IsSuccess)
    {
      return Result.Failure<DocxOperation>(tableAddress.Error);
    }

    Result<int> row = RequireOpInt(op, opName, "row");
    return !row.IsSuccess
      ? Result.Failure<DocxOperation>(row.Error)
      : Result.Success<DocxOperation>(new DeleteTableRow(tableAddress.Value, row.Value));
  }

  private static Result<DocxOperation> ParseApplyRunFormatting(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "address", "format");
    if (unknownKeys is not null)
    {
      return Result.Failure<DocxOperation>(unknownKeys);
    }

    Result<string> address = RequireOpAddress(op, opName, "address");
    if (!address.IsSuccess)
    {
      return Result.Failure<DocxOperation>(address.Error);
    }

    Result<DocxRunFormat> format = ParseRunFormat(op, opName);
    return !format.IsSuccess
      ? Result.Failure<DocxOperation>(format.Error)
      : Result.Success<DocxOperation>(new ApplyRunFormatting(address.Value, format.Value));
  }

  private static Result<DocxOperation> ParseFindReplace(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "find", "replace");
    if (unknownKeys is not null)
    {
      return Result.Failure<DocxOperation>(unknownKeys);
    }

    Result<string> find = RequireOpString(op, opName, "find");
    if (!find.IsSuccess)
    {
      return Result.Failure<DocxOperation>(find.Error);
    }

    if (find.Value.Length == 0)
    {
      return Result.Failure<DocxOperation>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'find' must be non-empty (the '{opName}' operation matches literal text)."));
    }

    Result<string> replace = RequireOpString(op, opName, "replace");
    return !replace.IsSuccess
      ? Result.Failure<DocxOperation>(replace.Error)
      : Result.Success<DocxOperation>(new FindReplace(find.Value, replace.Value));
  }

  /// <summary>format: object with at least one of bold, italic, underline,
  ///     fontSizeHalfPoints, or colorHex; colorHex is validated by the
  ///     <see cref="DocxRunFormat"/> constructor, whose ArgumentException re-surfaces
  ///     as a typed InvalidParameterValue (the ComputerToolInput discipline).</summary>
  private static Result<DocxRunFormat> ParseRunFormat(JsonElement op, string opName)
  {
    if (!op.TryGetProperty("format", out JsonElement el))
    {
      return Result.Failure<DocxRunFormat>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"The '{opName}' operation requires 'format': an object with at least one of bold, italic, underline, fontSizeHalfPoints, or colorHex."));
    }

    if (el.ValueKind != JsonValueKind.Object)
    {
      return Result.Failure<DocxRunFormat>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'format' must be an object, but got {el.ValueKind}."));
    }

    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(el, "bold", "italic", "underline", "fontSizeHalfPoints", "colorHex");
    if (unknownKeys is not null)
    {
      return Result.Failure<DocxRunFormat>(unknownKeys);
    }

    Result<RunFormatFields> fields = ReadRunFormatFields(el);
    return fields.IsSuccess
      ? ConstructRunFormat(fields.Value)
      : Result.Failure<DocxRunFormat>(fields.Error);
  }

  /// <summary>The five optional run-format fields, read but not yet cross-validated.</summary>
  private sealed record RunFormatFields(bool? Bold, bool? Italic, bool? Underline, int? FontSizeHalfPoints, string? ColorHex);

  /// <summary>Reads the five optional run-format fields; a wrong kind on any of them
  ///     is a typed error.</summary>
  private static Result<RunFormatFields> ReadRunFormatFields(JsonElement el)
  {
    Result<bool?> bold = ToolArguments.OptionalBool(el, "bold");
    if (!bold.IsSuccess)
    {
      return Result.Failure<RunFormatFields>(bold.Error);
    }

    Result<bool?> italic = ToolArguments.OptionalBool(el, "italic");
    if (!italic.IsSuccess)
    {
      return Result.Failure<RunFormatFields>(italic.Error);
    }

    Result<bool?> underline = ToolArguments.OptionalBool(el, "underline");
    if (!underline.IsSuccess)
    {
      return Result.Failure<RunFormatFields>(underline.Error);
    }

    Result<int?> fontSize = ToolArguments.OptionalInt(el, "fontSizeHalfPoints");
    if (!fontSize.IsSuccess)
    {
      return Result.Failure<RunFormatFields>(fontSize.Error);
    }

    Result<string?> colorHex = ToolArguments.OptionalString(el, "colorHex");
    return !colorHex.IsSuccess
      ? Result.Failure<RunFormatFields>(colorHex.Error)
      : Result.Success(new RunFormatFields(bold.Value, italic.Value, underline.Value, fontSize.Value, colorHex.Value));
  }

  /// <summary>Requires at least one selected field, then constructs the format
  ///     through the record constructor, whose ArgumentException re-surfaces as a
  ///     typed InvalidParameterValue.</summary>
  private static Result<DocxRunFormat> ConstructRunFormat(RunFormatFields fields)
  {
    if (fields.Bold is null && fields.Italic is null && fields.Underline is null && fields.FontSizeHalfPoints is null && fields.ColorHex is null)
    {
      return Result.Failure<DocxRunFormat>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'format' must carry at least one of bold, italic, underline, fontSizeHalfPoints, or colorHex; got none."));
    }

    try
    {
      return Result.Success(new DocxRunFormat(fields.Bold, fields.Italic, fields.Underline, fields.FontSizeHalfPoints, fields.ColorHex));
    }
    catch (ArgumentException ex)
    {
      return Result.Failure<DocxRunFormat>(new DomainError(ToolErrorCodes.InvalidParameterValue, ex.Message));
    }
  }

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

  /// <summary>Reads a required operation address field: the string requirement
  ///     applies first, then the pinned grammar through <see cref="DocxAddress.Parse"/>
  ///     - its InvalidAddress error flows out verbatim.</summary>
  private static Result<string> RequireOpAddress(JsonElement op, string opName, string field, bool admitsP0Sentinel = false)
  {
    Result<string> text = RequireOpString(op, opName, field);
    if (!text.IsSuccess)
    {
      return text;
    }

    // The pinned insertParagraph sentinel: afterAddress "p0" inserts before the
    // first body block. The grammar itself is 1-based, so the input layer admits
    // exactly this one 0-indexed address for exactly this field.
    if (admitsP0Sentinel && text.Value == "p0")
    {
      return text;
    }

    Result<DocxAddress> parsed = DocxAddress.Parse(text.Value);
    return parsed.IsSuccess ? text : ResultFailure(parsed.Error);
  }

  /// <summary>Reads a required operation integer field: the missing-field wording is
  ///     InvalidParameterValue naming the op and the field; a wrong JSON kind is
  ///     InvalidParameterType (the shared ToolArguments wording, verbatim).</summary>
  private static Result<int> RequireOpInt(JsonElement op, string opName, string field)
  {
    if (!op.TryGetProperty(field, out JsonElement el))
    {
      return Result.Failure<int>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"The '{opName}' operation requires '{field}'."));
    }

    Result<int> parsed = el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int value)
      ? Result.Success(value)
      : Result.Failure<int>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{field}' must be an integer, but got {el.ValueKind}."));
    return parsed;
  }

  private static bool Has(JsonElement json, string name) => json.TryGetProperty(name, out _);

  private static Result<string> ResultFailure(DomainError error) => Result.Failure<string>(error);

  private static Result<DocxToolInput> Fail(DomainError error) => Result.Failure<DocxToolInput>(error);
}
