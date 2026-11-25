using System.Text.Json;
using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain;

/// <summary>Strictly parsed arguments for the pptx tool: a required action switch (exactly
///     one of read, edit, create, comments, extract, case-sensitive), per-action required
///     and optional keys, unknown keys rejected, every address parsed through
///     <see cref="PptxAddress.Parse"/> at input time, expectedVersion decoded through
///     <see cref="FileVersionToken.Decode"/>, every slide number and geometry value
///     checked with NO clamping, and every operation object parsed into a typed
///     <see cref="PptxOperation"/> before the ACL is called. Every violation is a typed
///     <see cref="DomainError"/> - never a throw, never a coercion.</summary>
public sealed record PptxToolInput(
    string Action,
    string? Path,
    int Slide,
    FileVersion? ExpectedVersion,
    IReadOnlyList<PptxOperation> Operations,
    string? Markdown,
    string? CommentAction,
    int CommentId,
    string? Address,
    string? Author,
    string? Text,
    IReadOnlyList<string>? ImageNames)
{
  /// <summary>The batch cap, mirroring the docx tool's.</summary>
  private const int MaxOperations = 50;

  /// <summary>The address kinds a text replacement reaches: a shape's own text body, or one
  ///     table cell's.</summary>
  private static readonly PptxAddressKind[] TextTargets = [PptxAddressKind.Shape, PptxAddressKind.TableCell];

  /// <summary>The address kinds an operation that acts on one shape reaches.</summary>
  private static readonly PptxAddressKind[] ShapeTargets = [PptxAddressKind.Shape];

  /// <summary>Parses raw JSON arguments into validated input on the strict tool-parser
  ///     contract: unknown keys, wrong kinds, missing required keys, and out-of-range
  ///     values are typed errors; nothing is coerced.</summary>
  /// <param name="jsonArguments">The call's raw JSON arguments.</param>
  public static Result<PptxToolInput> FromArguments(string jsonArguments)
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
    return unknown is not null ? Fail(unknown) : ParseAction(action.Value, json);
  }

  /// <summary>Reads the required 'action' discriminator: present, a string, and a validated
  ///     member of the five actions. The refusal names all five, which is the only place a
  ///     model learns the vocabulary.</summary>
  private static Result<string> ParseActionName(JsonElement json)
  {
    if (!json.TryGetProperty("action", out JsonElement actionEl))
    {
      return Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'action' is required: exactly one of read, edit, create, comments, or extract (case-sensitive)."));
    }

    if (actionEl.ValueKind != JsonValueKind.String)
    {
      return Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'action' must be a string, but got {actionEl.ValueKind}."));
    }

    string action = actionEl.GetString()!;
    return action is not ("read" or "edit" or "create" or "comments" or "extract")
      ? Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'action' must be exactly one of read, edit, create, comments, extract (case-sensitive), but got '{action}'."))
      : Result.Success(action);
  }

  /// <summary>The allowed argument keys per action (timeoutSeconds belongs to every tool
  ///     call). The list is echoed verbatim on UnknownParameter errors.</summary>
  private static string[] Allowed(string action) => action switch
  {
    "read" => ["action", ToolTimeout.ParameterName, "path", "slide"],
    "edit" => ["action", ToolTimeout.ParameterName, "path", "expectedVersion", "operations"],
    "create" => ["action", ToolTimeout.ParameterName, "path", "markdown"],
    "comments" => ["action", ToolTimeout.ParameterName, "path", "commentAction", "address", "author", "text", "commentId", "expectedVersion"],
    "extract" => ["action", ToolTimeout.ParameterName, "path", "names"],
    _ => throw new InvalidOperationException("unreachable: action is a validated member of the five"),
  };

  /// <summary>Dispatches to the per-action parser; every action first passes the shared path
  ///     requirement (present, a string, non-empty).</summary>
  private static Result<PptxToolInput> ParseAction(string action, JsonElement json)
  {
    Result<string> path = ToolArguments.RequireString(json, "path", "Every pptx action names the presentation file to operate on.");
    return path.IsSuccess ? DispatchAction(action, json, path.Value) : Fail(path.Error);
  }

  /// <summary>Refuses an empty path, then dispatches on the validated action.</summary>
  private static Result<PptxToolInput> DispatchAction(string action, JsonElement json, string path) =>
      path.Length == 0
        ? Fail(new DomainError(ToolErrorCodes.InvalidParameterValue, "'path' must be non-empty."))
        : action switch
        {
          "read" => ParseRead(json, path),
          "edit" => ParseEdit(json, path),
          "create" => ParseCreate(json, path),
          "comments" => ParseComments(json, path),
          _ => ParseExtract(json, path),
        };

  /// <summary>read: optional slide (>= 1). Without it the outline is returned; with it one
  ///     slide is returned. There is no offset or maxBlocks - a slide is already the
  ///     pagination unit.</summary>
  private static Result<PptxToolInput> ParseRead(JsonElement json, string path)
  {
    Result<int?> slide = ToolArguments.OptionalInt(json, "slide");
    return slide.IsSuccess ? ReadInput(path, slide.Value) : Fail(slide.Error);
  }

  /// <summary>Builds a read input from the optional slide number; without one the outline is
  ///     what the action returns.</summary>
  private static Result<PptxToolInput> ReadInput(string path, int? slide) =>
      slide is null or >= 1
        ? Result.Success(Input("read", path) with { Slide = slide ?? 0 })
        : Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'slide' must be >= 1 (got {slide}); values are never clamped."));

  /// <summary>edit: expectedVersion required (decoded strictly), operations required as a
  ///     non-empty array of at most 50 operation objects, each parsed and validated before
  ///     any mutation - the first invalid op refuses the batch.</summary>
  private static Result<PptxToolInput> ParseEdit(JsonElement json, string path)
  {
    Result<FileVersion?> version = ExpectedVersionOf(json, required: true);
    if (!version.IsSuccess)
    {
      return Fail(version.Error);
    }

    if (!json.TryGetProperty("operations", out JsonElement opsEl))
    {
      return ToolArguments.Missing<PptxToolInput>("operations",
          "The 'edit' action requires operations: a non-empty array of at most 50 operation objects.");
    }

    if (opsEl.ValueKind != JsonValueKind.Array)
    {
      return Fail(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'operations' must be an array of operation objects, but got {opsEl.ValueKind}."));
    }

    Result<IReadOnlyList<PptxOperation>> operations = ParseOperations(opsEl);
    return !operations.IsSuccess
      ? Fail(operations.Error)
      : Result.Success(Input("edit", path) with { ExpectedVersion = version.Value, Operations = operations.Value });
  }

  /// <summary>Parses every element of the operations array, refusing the whole batch on the
  ///     first invalid operation. The refusal names the operation's position.</summary>
  private static Result<IReadOnlyList<PptxOperation>> ParseOperations(JsonElement opsEl)
  {
    int count = opsEl.GetArrayLength();
    if (count == 0)
    {
      return Result.Failure<IReadOnlyList<PptxOperation>>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "'operations' must be a non-empty array of operation objects."));
    }

    if (count > MaxOperations)
    {
      return Result.Failure<IReadOnlyList<PptxOperation>>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'operations' must carry at most {MaxOperations} operations (got {count})."));
    }

    List<PptxOperation> operations = [];
    foreach (JsonElement element in opsEl.EnumerateArray())
    {
      Result<PptxOperation> operation = ParseOperation(element);
      if (!operation.IsSuccess)
      {
        return Result.Failure<IReadOnlyList<PptxOperation>>(new DomainError(operation.Error.Code,
            $"operations[{operations.Count}]: {operation.Error.Message}"));
      }

      operations.Add(operation.Value);
    }

    return Result.Success<IReadOnlyList<PptxOperation>>(operations);
  }

  /// <summary>create: markdown required - the pinned subset is the only way a deck is
  ///     authored, so an absent source is refused here rather than at the seam.</summary>
  private static Result<PptxToolInput> ParseCreate(JsonElement json, string path)
  {
    Result<string> markdown = ToolArguments.RequireString(json, "markdown",
        "The 'create' action requires markdown: the pinned-subset source (a line of exactly three hyphens separates slides).");
    return !markdown.IsSuccess
      ? Fail(markdown.Error)
      : Result.Success(Input("create", path) with { Markdown = markdown.Value });
  }

  /// <summary>comments: commentAction required (list|add|delete); add requires a shape
  ///     address, author and text plus expectedVersion; delete requires commentId >= 1 plus
  ///     expectedVersion.</summary>
  private static Result<PptxToolInput> ParseComments(JsonElement json, string path)
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
    return version.IsSuccess
      ? DispatchCommentAction(commentAction.Value, json, path, version.Value)
      : Fail(version.Error);
  }

  /// <summary>Dispatches a validated commentAction; only list runs unguarded.</summary>
  private static Result<PptxToolInput> DispatchCommentAction(string commentAction, JsonElement json,
      string path, FileVersion? version) => commentAction switch
      {
        "list" => Result.Success(Input("comments", path) with { CommentAction = "list", ExpectedVersion = version }),
        "add" => ParseCommentAdd(json, path, version),
        _ => ParseCommentDelete(json, path, version),
      };

  /// <summary>comments add: the anchor must be a shape address (a comment attaches to a
  ///     shape, never to a slide), and author and text must be non-empty.</summary>
  private static Result<PptxToolInput> ParseCommentAdd(JsonElement json, string path, FileVersion? version)
  {
    Result<string> address = ToolArguments.RequireString(json, "address",
        "The 'comments add' action requires address: the shape the comment anchors to (s<N>.sh<M>).");
    if (!address.IsSuccess)
    {
      return Fail(address.Error);
    }

    Result<PptxAddress> parsed = PptxAddress.Parse(address.Value);
    if (!parsed.IsSuccess)
    {
      return Fail(parsed.Error);
    }

    if (parsed.Value.Kind != PptxAddressKind.Shape)
    {
      return Fail(new DomainError(PptxErrorCodes.InvalidAddress,
          $"'address' must name a shape (s<N>.sh<M>) for a comment anchor; got '{address.Value}', which addresses a {parsed.Value.Kind}."));
    }

    Result<string> author = RequireNonEmpty(json, "author", "The 'comments add' action requires author: the comment author's name, non-empty.");
    if (!author.IsSuccess)
    {
      return Fail(author.Error);
    }

    Result<string> text = RequireNonEmpty(json, "text", "The 'comments add' action requires text: the comment body, non-empty.");
    return !text.IsSuccess
      ? Fail(text.Error)
      : Result.Success(Input("comments", path) with
      {
        CommentAction = "add",
        ExpectedVersion = version,
        Address = address.Value,
        Author = author.Value,
        Text = text.Value,
      });
  }

  /// <summary>comments delete: commentId required, >= 1.</summary>
  private static Result<PptxToolInput> ParseCommentDelete(JsonElement json, string path, FileVersion? version)
  {
    Result<int> commentId = ToolArguments.RequireInt(json, "commentId",
        "The 'comments delete' action requires commentId: the c<id> a comments list returned (integer >= 1).");
    return commentId.IsSuccess ? DeleteCommentInput(path, version, commentId.Value) : Fail(commentId.Error);
  }

  /// <summary>Builds a comment-delete input from the id a list reported.</summary>
  private static Result<PptxToolInput> DeleteCommentInput(string path, FileVersion? version, int commentId) =>
      commentId >= 1
      ? Result.Success(Input("comments", path) with
      {
        CommentAction = "delete",
        ExpectedVersion = version,
        CommentId = commentId,
      })
      : Fail(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'commentId' must be >= 1 (got {commentId}); values are never clamped."));

  /// <summary>extract: optional names array; entries must be non-empty media names.</summary>
  private static Result<PptxToolInput> ParseExtract(JsonElement json, string path)
  {
    Result<IReadOnlyList<string>?> names = ToolArguments.OptionalStringArray(json, "names");
    if (!names.IsSuccess)
    {
      return Fail(names.Error);
    }

    bool hasEmptyName = names.Value is not null && names.Value.Any(name => name.Length == 0);
    return hasEmptyName
      ? Fail(new DomainError(ToolErrorCodes.InvalidParameterValue, "'names' entries must be non-empty media names."))
      : Result.Success(Input("extract", path) with { ImageNames = names.Value });
  }

  /// <summary>expectedVersion: required for mutating calls (MissingParameter when absent),
  ///     decoded strictly through <see cref="FileVersionToken.Decode"/> - garbage fails
  ///     InvalidParameterValue.</summary>
  private static Result<FileVersion?> ExpectedVersionOf(JsonElement json, bool required)
  {
    if (!json.TryGetProperty("expectedVersion", out JsonElement el))
    {
      return required
        ? ToolArguments.Missing<FileVersion?>("expectedVersion",
            "Mutating calls require the version token the previous pptx read returned (the exact v<length>:<ticks> string) so stale edits are refused.")
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

  /// <summary>Parses one operations element against its op discriminator. Unknown keys inside
  ///     the operation object fail UnknownParameter; unknown op values and missing fields
  ///     fail InvalidParameterValue naming the op and the field; address fields parse here,
  ///     so bad grammar is refused at input time.</summary>
  private static Result<PptxOperation> ParseOperation(JsonElement element)
  {
    if (element.ValueKind != JsonValueKind.Object)
    {
      return Result.Failure<PptxOperation>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"Every operations element must be an object, but got {element.ValueKind}."));
    }

    Result<string> opName = ParseOpName(element);
    return opName.IsSuccess
      ? DispatchOperation(element, opName.Value)
      : Result.Failure<PptxOperation>(opName.Error);
  }

  /// <summary>Reads the required 'op' discriminator: present and a string.</summary>
  private static Result<string> ParseOpName(JsonElement element)
  {
    return element.TryGetProperty("op", out JsonElement opEl)
      ? OpNameOf(opEl)
      : Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "Every operations element must carry 'op': exactly one of replaceText, setNotes, insertSlide, deleteSlide, moveSlide, setSlideLayout, replacePicture, insertPicture, insertTable, insertTextBox, setChartData, setAnimation, or removeAnimation (case-sensitive)."));
  }

  /// <summary>Validates the 'op' value's JSON kind.</summary>
  private static Result<string> OpNameOf(JsonElement opEl) =>
      opEl.ValueKind == JsonValueKind.String
        ? Result.Success(opEl.GetString()!)
        : Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'op' must be a string, but got {opEl.ValueKind}."));

  /// <summary>Dispatches a validated op name to its per-op parser.</summary>
  private static Result<PptxOperation> DispatchOperation(JsonElement element, string op) => op switch
  {
    "replaceText" => ParseReplaceText(element, op),
    "setNotes" => ParseSetNotes(element, op),
    "insertSlide" => ParseInsertSlide(element, op),
    "deleteSlide" => ParseDeleteSlide(element, op),
    "moveSlide" => ParseMoveSlide(element, op),
    _ => DispatchStructureOperation(element, op),
  };

  /// <summary>Dispatches the layout and media operations.</summary>
  private static Result<PptxOperation> DispatchStructureOperation(JsonElement element, string op) => op switch
  {
    "setSlideLayout" => ParseSetSlideLayout(element, op),
    "replacePicture" => ParseReplacePicture(element, op),
    "insertPicture" => ParseInsertPicture(element, op),
    "insertTable" => ParseInsertTable(element, op),
    _ => DispatchContentOperation(element, op),
  };

  /// <summary>Dispatches the remaining content operations; an op outside the vocabulary is
  ///     refused with the whole list, never ignored.</summary>
  private static Result<PptxOperation> DispatchContentOperation(JsonElement element, string op) => op switch
  {
    "insertTextBox" => ParseInsertTextBox(element, op),
    "setChartData" => ParseSetChartData(element, op),
    "setAnimation" => ParseSetAnimation(element, op),
    "removeAnimation" => ParseRemoveAnimation(element, op),
    _ => Result.Failure<PptxOperation>(new DomainError(ToolErrorCodes.InvalidParameterValue,
        $"'op' must be exactly one of replaceText, setNotes, insertSlide, deleteSlide, moveSlide, setSlideLayout, replacePicture, insertPicture, insertTable, insertTextBox, setChartData, setAnimation, or removeAnimation (case-sensitive), but got '{op}'.")),
  };

  private static Result<PptxOperation> ParseReplaceText(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "address", "text");
    if (unknownKeys is not null)
    {
      return Result.Failure<PptxOperation>(unknownKeys);
    }

    Result<string> address = RequireOpAddress(op, opName, "address", TextTargets);
    if (!address.IsSuccess)
    {
      return Result.Failure<PptxOperation>(address.Error);
    }

    Result<string> text = RequireOpString(op, opName, "text");
    return !text.IsSuccess
      ? Result.Failure<PptxOperation>(text.Error)
      : Result.Success<PptxOperation>(new ReplaceText(address.Value, text.Value));
  }

  private static Result<PptxOperation> ParseSetNotes(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "slide", "text");
    if (unknownKeys is not null)
    {
      return Result.Failure<PptxOperation>(unknownKeys);
    }

    Result<int> slide = RequireSlide(op, opName, "slide");
    if (!slide.IsSuccess)
    {
      return Result.Failure<PptxOperation>(slide.Error);
    }

    Result<string> text = RequireOpString(op, opName, "text");
    return !text.IsSuccess
      ? Result.Failure<PptxOperation>(text.Error)
      : Result.Success<PptxOperation>(new SetNotes(slide.Value, text.Value));
  }

  private static Result<PptxOperation> ParseInsertSlide(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "afterSlide", "layout");
    if (unknownKeys is not null)
    {
      return Result.Failure<PptxOperation>(unknownKeys);
    }

    Result<int> afterSlide = RequireOpInt(op, opName, "afterSlide");
    if (!afterSlide.IsSuccess)
    {
      return Result.Failure<PptxOperation>(afterSlide.Error);
    }

    if (afterSlide.Value < 0)
    {
      return Result.Failure<PptxOperation>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'afterSlide' must be >= 0, where 0 inserts at the front (got {afterSlide.Value}); values are never clamped."));
    }

    Result<string> layout = RequireNonEmptyOp(op, opName, "layout");
    return !layout.IsSuccess
      ? Result.Failure<PptxOperation>(layout.Error)
      : Result.Success<PptxOperation>(new InsertSlide(afterSlide.Value, layout.Value));
  }

  private static Result<PptxOperation> ParseDeleteSlide(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "slide");
    if (unknownKeys is not null)
    {
      return Result.Failure<PptxOperation>(unknownKeys);
    }

    Result<int> slide = RequireSlide(op, opName, "slide");
    return !slide.IsSuccess
      ? Result.Failure<PptxOperation>(slide.Error)
      : Result.Success<PptxOperation>(new DeleteSlide(slide.Value));
  }

  private static Result<PptxOperation> ParseMoveSlide(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "from", "to");
    if (unknownKeys is not null)
    {
      return Result.Failure<PptxOperation>(unknownKeys);
    }

    Result<int> from = RequireSlide(op, opName, "from");
    if (!from.IsSuccess)
    {
      return Result.Failure<PptxOperation>(from.Error);
    }

    Result<int> to = RequireSlide(op, opName, "to");
    return !to.IsSuccess
      ? Result.Failure<PptxOperation>(to.Error)
      : Result.Success<PptxOperation>(new MoveSlide(from.Value, to.Value));
  }

  private static Result<PptxOperation> ParseSetSlideLayout(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "slide", "layout");
    if (unknownKeys is not null)
    {
      return Result.Failure<PptxOperation>(unknownKeys);
    }

    Result<int> slide = RequireSlide(op, opName, "slide");
    if (!slide.IsSuccess)
    {
      return Result.Failure<PptxOperation>(slide.Error);
    }

    Result<string> layout = RequireNonEmptyOp(op, opName, "layout");
    return !layout.IsSuccess
      ? Result.Failure<PptxOperation>(layout.Error)
      : Result.Success<PptxOperation>(new SetSlideLayout(slide.Value, layout.Value));
  }

  private static Result<PptxOperation> ParseReplacePicture(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "address", "image");
    if (unknownKeys is not null)
    {
      return Result.Failure<PptxOperation>(unknownKeys);
    }

    Result<string> address = RequireOpAddress(op, opName, "address", ShapeTargets);
    if (!address.IsSuccess)
    {
      return Result.Failure<PptxOperation>(address.Error);
    }

    Result<string> image = RequireNonEmptyOp(op, opName, "image");
    return !image.IsSuccess
      ? Result.Failure<PptxOperation>(image.Error)
      : Result.Success<PptxOperation>(new ReplacePicture(address.Value, image.Value));
  }

  private static Result<PptxOperation> ParseInsertPicture(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "slide", "image", "x", "y", "width", "height");
    if (unknownKeys is not null)
    {
      return Result.Failure<PptxOperation>(unknownKeys);
    }

    Result<int> slide = RequireSlide(op, opName, "slide");
    if (!slide.IsSuccess)
    {
      return Result.Failure<PptxOperation>(slide.Error);
    }

    Result<string> image = RequireNonEmptyOp(op, opName, "image");
    if (!image.IsSuccess)
    {
      return Result.Failure<PptxOperation>(image.Error);
    }

    Result<PptxBox> box = RequireBox(op, opName);
    return !box.IsSuccess
      ? Result.Failure<PptxOperation>(box.Error)
      : Result.Success<PptxOperation>(new InsertPicture(slide.Value, image.Value, box.Value.X, box.Value.Y, box.Value.Width, box.Value.Height));
  }

  private static Result<PptxOperation> ParseInsertTable(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "slide", "rows", "cols", "cells", "x", "y", "width", "height");
    if (unknownKeys is not null)
    {
      return Result.Failure<PptxOperation>(unknownKeys);
    }

    Result<int> slide = RequireSlide(op, opName, "slide");
    if (!slide.IsSuccess)
    {
      return Result.Failure<PptxOperation>(slide.Error);
    }

    Result<int> rows = RequirePositive(op, opName, "rows");
    if (!rows.IsSuccess)
    {
      return Result.Failure<PptxOperation>(rows.Error);
    }

    Result<int> cols = RequirePositive(op, opName, "cols");
    if (!cols.IsSuccess)
    {
      return Result.Failure<PptxOperation>(cols.Error);
    }

    Result<IReadOnlyList<IReadOnlyList<string>>> cells = RequireCellGrid(op, opName, rows.Value, cols.Value);
    if (!cells.IsSuccess)
    {
      return Result.Failure<PptxOperation>(cells.Error);
    }

    Result<PptxBox> box = RequireBox(op, opName);
    return !box.IsSuccess
      ? Result.Failure<PptxOperation>(box.Error)
      : Result.Success<PptxOperation>(new InsertTable(slide.Value, rows.Value, cols.Value, cells.Value,
          box.Value.X, box.Value.Y, box.Value.Width, box.Value.Height));
  }

  private static Result<PptxOperation> ParseInsertTextBox(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "slide", "text", "x", "y", "width", "height");
    if (unknownKeys is not null)
    {
      return Result.Failure<PptxOperation>(unknownKeys);
    }

    Result<int> slide = RequireSlide(op, opName, "slide");
    if (!slide.IsSuccess)
    {
      return Result.Failure<PptxOperation>(slide.Error);
    }

    Result<string> text = RequireOpString(op, opName, "text");
    if (!text.IsSuccess)
    {
      return Result.Failure<PptxOperation>(text.Error);
    }

    Result<PptxBox> box = RequireBox(op, opName);
    return !box.IsSuccess
      ? Result.Failure<PptxOperation>(box.Error)
      : Result.Success<PptxOperation>(new InsertTextBox(slide.Value, text.Value,
          box.Value.X, box.Value.Y, box.Value.Width, box.Value.Height));
  }

  private static Result<PptxOperation> ParseSetChartData(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "address", "series", "categories", "values");
    if (unknownKeys is not null)
    {
      return Result.Failure<PptxOperation>(unknownKeys);
    }

    Result<string> address = RequireOpAddress(op, opName, "address", ShapeTargets);
    if (!address.IsSuccess)
    {
      return Result.Failure<PptxOperation>(address.Error);
    }

    Result<IReadOnlyList<string>?> series = ToolArguments.OptionalStringArray(op, "series");
    if (!series.IsSuccess)
    {
      return Result.Failure<PptxOperation>(series.Error);
    }

    Result<IReadOnlyList<string>?> categories = ToolArguments.OptionalStringArray(op, "categories");
    if (!categories.IsSuccess)
    {
      return Result.Failure<PptxOperation>(categories.Error);
    }

    Result<IReadOnlyList<IReadOnlyList<double>>?> values = OptionalNumberGrid(op, "values");
    return values.IsSuccess
      ? ChartData(address.Value, series.Value, categories.Value, values.Value)
      : Result.Failure<PptxOperation>(values.Error);
  }

  /// <summary>Builds a chart-data operation, refusing a request that names neither series,
  ///     categories, nor values - a chart change that changes nothing is a mistake, not a
  ///     no-op.</summary>
  private static Result<PptxOperation> ChartData(string address, IReadOnlyList<string>? series,
      IReadOnlyList<string>? categories, IReadOnlyList<IReadOnlyList<double>>? values)
  {
    return series is null && categories is null && values is null
      ? Result.Failure<PptxOperation>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          "The 'setChartData' operation requires at least one of 'series', 'categories', or 'values'; it changes a chart, so an empty request changes nothing."))
      : Result.Success<PptxOperation>(new SetChartData(address, series, categories, values));
  }

  private static Result<PptxOperation> ParseSetAnimation(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "slide", "effectIndex", "effect", "trigger", "durationMilliseconds");
    if (unknownKeys is not null)
    {
      return Result.Failure<PptxOperation>(unknownKeys);
    }

    Result<int> slide = RequireSlide(op, opName, "slide");
    if (!slide.IsSuccess)
    {
      return Result.Failure<PptxOperation>(slide.Error);
    }

    Result<int> effectIndex = RequirePositive(op, opName, "effectIndex");
    if (!effectIndex.IsSuccess)
    {
      return Result.Failure<PptxOperation>(effectIndex.Error);
    }

    Result<string?> effect = ToolArguments.OptionalString(op, "effect");
    if (!effect.IsSuccess)
    {
      return Result.Failure<PptxOperation>(effect.Error);
    }

    Result<string?> trigger = ToolArguments.OptionalString(op, "trigger");
    if (!trigger.IsSuccess)
    {
      return Result.Failure<PptxOperation>(trigger.Error);
    }

    Result<int?> duration = ToolArguments.OptionalInt(op, "durationMilliseconds");
    return duration.IsSuccess
      ? AnimationChange(slide.Value, effectIndex.Value, effect.Value, trigger.Value, duration.Value)
      : Result.Failure<PptxOperation>(duration.Error);
  }

  /// <summary>Builds an animation change, refusing a negative duration - a retime takes a
  ///     whole number of milliseconds, zero or more.</summary>
  private static Result<PptxOperation> AnimationChange(int slide, int effectIndex, string? effect,
      string? trigger, int? duration) =>
      duration is null or >= 0
        ? Result.Success<PptxOperation>(new SetAnimation(slide, effectIndex, effect, trigger, duration))
        : Result.Failure<PptxOperation>(new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'durationMilliseconds' must be >= 0 (got {duration}); values are never clamped."));

  private static Result<PptxOperation> ParseRemoveAnimation(JsonElement op, string opName)
  {
    DomainError? unknownKeys = ToolArguments.RejectUnknownParameters(op, "op", "slide", "effectIndex");
    if (unknownKeys is not null)
    {
      return Result.Failure<PptxOperation>(unknownKeys);
    }

    Result<int> slide = RequireSlide(op, opName, "slide");
    if (!slide.IsSuccess)
    {
      return Result.Failure<PptxOperation>(slide.Error);
    }

    Result<int> effectIndex = RequirePositive(op, opName, "effectIndex");
    return !effectIndex.IsSuccess
      ? Result.Failure<PptxOperation>(effectIndex.Error)
      : Result.Success<PptxOperation>(new RemoveAnimation(slide.Value, effectIndex.Value));
  }

  /// <summary>The four geometry values an insertion carries, in English Metric Units.</summary>
  private sealed record PptxBox(long X, long Y, long Width, long Height);

  /// <summary>Reads the geometry of an insertion: x and y are offsets (>= 0), width and
  ///     height are extents (>= 1). Nothing is clamped.</summary>
  private static Result<PptxBox> RequireBox(JsonElement op, string opName)
  {
    Result<long> x = RequireOpLong(op, opName, "x");
    if (!x.IsSuccess)
    {
      return Result.Failure<PptxBox>(x.Error);
    }

    Result<long> y = RequireOpLong(op, opName, "y");
    if (!y.IsSuccess)
    {
      return Result.Failure<PptxBox>(y.Error);
    }

    Result<long> width = RequireOpLong(op, opName, "width");
    if (!width.IsSuccess)
    {
      return Result.Failure<PptxBox>(width.Error);
    }

    Result<long> height = RequireOpLong(op, opName, "height");
    if (!height.IsSuccess)
    {
      return Result.Failure<PptxBox>(height.Error);
    }

    bool inRange = x.Value >= 0 && y.Value >= 0 && width.Value >= 1 && height.Value >= 1;
    return inRange
      ? Result.Success(new PptxBox(x.Value, y.Value, width.Value, height.Value))
      : Result.Failure<PptxBox>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"'x' and 'y' must be >= 0 and 'width' and 'height' must be >= 1 English Metric Units (got x={x.Value}, y={y.Value}, width={width.Value}, height={height.Value}); values are never clamped."));
  }

  /// <summary>Reads a required operation string field: the missing-field wording names the
  ///     op and the field; a wrong JSON kind is InvalidParameterType.</summary>
  private static Result<string> RequireOpString(JsonElement op, string opName, string field)
  {
    return op.TryGetProperty(field, out JsonElement el)
      ? StringField(el, field)
      : MissingOpField<string>(opName, field);
  }

  /// <summary>Validates one operation field's JSON kind as a string.</summary>
  private static Result<string> StringField(JsonElement el, string field) =>
      el.ValueKind == JsonValueKind.String
        ? Result.Success(el.GetString()!)
        : Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'{field}' must be a string, but got {el.ValueKind}."));

  /// <summary>The refusal an operation states when a field it needs is absent.</summary>
  private static Result<T> MissingOpField<T>(string opName, string field) =>
      Result.Failure<T>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"The '{opName}' operation requires '{field}'."));

  /// <summary>Reads a required non-empty operation string field.</summary>
  private static Result<string> RequireNonEmptyOp(JsonElement op, string opName, string field)
  {
    Result<string> text = RequireOpString(op, opName, field);
    return text.IsSuccess ? NonEmptyOpValue(text.Value, opName, field) : text;
  }

  /// <summary>Refuses an empty value for a field an operation cannot act on empty.</summary>
  private static Result<string> NonEmptyOpValue(string value, string opName, string field) =>
      value.Length > 0
        ? Result.Success(value)
        : Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"The '{opName}' operation requires a non-empty '{field}'."));

  /// <summary>Reads a required operation integer field.</summary>
  private static Result<int> RequireOpInt(JsonElement op, string opName, string field)
  {
    return op.TryGetProperty(field, out JsonElement el)
      ? IntField(el, field)
      : MissingOpField<int>(opName, field);
  }

  /// <summary>Validates one operation field's JSON kind as an integer.</summary>
  private static Result<int> IntField(JsonElement el, string field) =>
      el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out int value)
        ? Result.Success(value)
        : Result.Failure<int>(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'{field}' must be an integer, but got {el.ValueKind}."));

  /// <summary>Reads a required operation integer field that must be at least 1.</summary>
  private static Result<int> RequirePositive(JsonElement op, string opName, string field)
  {
    Result<int> value = RequireOpInt(op, opName, field);
    return value.IsSuccess ? AtLeastOne(value.Value, field) : value;
  }

  /// <summary>Refuses a 1-based index that is not one; nothing is clamped.</summary>
  private static Result<int> AtLeastOne(int value, string field) =>
      value >= 1
        ? Result.Success(value)
        : Result.Failure<int>(new DomainError(ToolErrorCodes.InvalidParameterValue,
            $"'{field}' must be >= 1 (got {value}); values are never clamped."));

  /// <summary>Reads a required operation slide number: an integer at least 1.</summary>
  private static Result<int> RequireSlide(JsonElement op, string opName, string field) => RequirePositive(op, opName, field);

  /// <summary>Reads a required operation integer field as a 64-bit value (a geometry
  ///     coordinate in English Metric Units).</summary>
  private static Result<long> RequireOpLong(JsonElement op, string opName, string field)
  {
    return op.TryGetProperty(field, out JsonElement el)
      ? LongField(el, field)
      : MissingOpField<long>(opName, field);
  }

  /// <summary>Validates one operation field's JSON kind as a 64-bit integer.</summary>
  private static Result<long> LongField(JsonElement el, string field) =>
      el.ValueKind == JsonValueKind.Number && el.TryGetInt64(out long value)
        ? Result.Success(value)
        : Result.Failure<long>(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'{field}' must be an integer, but got {el.ValueKind}."));

  /// <summary>Reads a required operation address field: the string requirement applies
  ///     first, then the pinned grammar through <see cref="PptxAddress.Parse"/>, then the
  ///     kind the operation can act on. Its InvalidAddress error flows out verbatim.</summary>
  private static Result<string> RequireOpAddress(JsonElement op, string opName, string field, PptxAddressKind[] kinds)
  {
    Result<string> text = RequireOpString(op, opName, field);
    if (!text.IsSuccess)
    {
      return text;
    }

    Result<PptxAddress> parsed = PptxAddress.Parse(text.Value);
    return parsed.IsSuccess
      ? CheckAddressKind(parsed.Value, text.Value, field, kinds)
      : Result.Failure<string>(parsed.Error);
  }

  /// <summary>Accepts an address only when its kind is one the operation acts on; the
  ///     refusal states both the kinds it takes and the kind it was given.</summary>
  private static Result<string> CheckAddressKind(PptxAddress parsed, string text, string field, PptxAddressKind[] kinds) =>
      kinds.Contains(parsed.Kind)
        ? Result.Success(text)
        : Result.Failure<string>(new DomainError(PptxErrorCodes.InvalidAddress,
            $"'{field}' must address {Describe(kinds)}; got '{text}', which addresses a {parsed.Kind}."));

  /// <summary>The address kinds an operation accepts, as the refusal states them.</summary>
  private static string Describe(PptxAddressKind[] kinds) =>
      kinds.Contains(PptxAddressKind.TableCell)
          ? "a shape (s<N>.sh<M>) or a table cell (s<N>.sh<M>.t<R>.c<C>)"
          : "a shape (s<N>.sh<M>)";

  /// <summary>Reads the cell grid of an inserted table: an array of rows, each an array of
  ///     strings, carrying exactly rows x cols entries. A mismatch is InvalidAddress and the
  ///     message names the expected dimensions.</summary>
  private static Result<IReadOnlyList<IReadOnlyList<string>>> RequireCellGrid(JsonElement op, string opName, int rows, int cols)
  {
    if (!op.TryGetProperty("cells", out JsonElement grid))
    {
      return Result.Failure<IReadOnlyList<IReadOnlyList<string>>>(new DomainError(ToolErrorCodes.InvalidParameterValue,
          $"The '{opName}' operation requires 'cells': an array of {rows} row arrays, each carrying {cols} strings."));
    }

    Result<IReadOnlyList<IReadOnlyList<string>>> parsed = ReadStringGrid(grid);
    if (!parsed.IsSuccess)
    {
      return parsed;
    }

    int count = parsed.Value.Sum(row => row.Count);
    return count == rows * cols
      ? parsed
      : Result.Failure<IReadOnlyList<IReadOnlyList<string>>>(new DomainError(PptxErrorCodes.InvalidAddress,
          $"'cells' must carry exactly rows x cols ({rows} x {cols} = {rows * cols}) entries; got {count}."));
  }

  /// <summary>Reads a JSON array of arrays of strings.</summary>
  private static Result<IReadOnlyList<IReadOnlyList<string>>> ReadStringGrid(JsonElement grid)
  {
    if (grid.ValueKind != JsonValueKind.Array)
    {
      return Result.Failure<IReadOnlyList<IReadOnlyList<string>>>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'cells' must be an array of row arrays, but got {grid.ValueKind}."));
    }

    List<IReadOnlyList<string>> rows = [];
    foreach (JsonElement row in grid.EnumerateArray())
    {
      Result<IReadOnlyList<string>> parsed = ReadStringRow(row);
      if (!parsed.IsSuccess)
      {
        return Result.Failure<IReadOnlyList<IReadOnlyList<string>>>(parsed.Error);
      }

      rows.Add(parsed.Value);
    }

    return Result.Success<IReadOnlyList<IReadOnlyList<string>>>(rows);
  }

  /// <summary>Reads one row of a cell grid: an array of strings.</summary>
  private static Result<IReadOnlyList<string>> ReadStringRow(JsonElement row)
  {
    if (row.ValueKind != JsonValueKind.Array)
    {
      return Result.Failure<IReadOnlyList<string>>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'cells' rows must be arrays of strings, but got {row.ValueKind}."));
    }

    List<string> cells = [];
    foreach (JsonElement cell in row.EnumerateArray())
    {
      if (cell.ValueKind != JsonValueKind.String)
      {
        return Result.Failure<IReadOnlyList<string>>(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'cells' must contain only strings, but got {cell.ValueKind}."));
      }

      cells.Add(cell.GetString()!);
    }

    return Result.Success<IReadOnlyList<string>>(cells);
  }

  /// <summary>Reads an optional chart value grid: an array of series arrays of numbers.
  ///     Absent is null; present but not a grid of numbers is InvalidParameterType.</summary>
  private static Result<IReadOnlyList<IReadOnlyList<double>>?> OptionalNumberGrid(JsonElement op, string field)
  {
    if (!op.TryGetProperty(field, out JsonElement grid))
    {
      return Result.Success<IReadOnlyList<IReadOnlyList<double>>?>(null);
    }

    if (grid.ValueKind != JsonValueKind.Array)
    {
      return Result.Failure<IReadOnlyList<IReadOnlyList<double>>?>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{field}' must be an array of series arrays of numbers, but got {grid.ValueKind}."));
    }

    List<IReadOnlyList<double>> series = [];
    foreach (JsonElement row in grid.EnumerateArray())
    {
      Result<IReadOnlyList<double>> parsed = ReadNumberRow(row, field);
      if (!parsed.IsSuccess)
      {
        return Result.Failure<IReadOnlyList<IReadOnlyList<double>>?>(parsed.Error);
      }

      series.Add(parsed.Value);
    }

    return Result.Success<IReadOnlyList<IReadOnlyList<double>>?>(series);
  }

  /// <summary>Reads one series of chart values: an array of numbers.</summary>
  private static Result<IReadOnlyList<double>> ReadNumberRow(JsonElement row, string field)
  {
    if (row.ValueKind != JsonValueKind.Array)
    {
      return Result.Failure<IReadOnlyList<double>>(new DomainError(ToolErrorCodes.InvalidParameterType,
          $"'{field}' rows must be arrays of numbers, but got {row.ValueKind}."));
    }

    List<double> values = [];
    foreach (JsonElement value in row.EnumerateArray())
    {
      if (value.ValueKind != JsonValueKind.Number)
      {
        return Result.Failure<IReadOnlyList<double>>(new DomainError(ToolErrorCodes.InvalidParameterType,
            $"'{field}' must contain only numbers, but got {value.ValueKind}."));
      }

      values.Add(value.GetDouble());
    }

    return Result.Success<IReadOnlyList<double>>(values);
  }

  /// <summary>Reads a required non-empty top-level string parameter.</summary>
  private static Result<string> RequireNonEmpty(JsonElement json, string name, string requirement)
  {
    Result<string> text = ToolArguments.RequireString(json, name, requirement);
    return text.IsSuccess ? NonEmptyValue(text.Value, name) : text;
  }

  /// <summary>Refuses an empty value for a required non-empty parameter.</summary>
  private static Result<string> NonEmptyValue(string value, string name) =>
      value.Length > 0
        ? Result.Success(value)
        : Result.Failure<string>(new DomainError(ToolErrorCodes.InvalidParameterValue, $"'{name}' must be non-empty."));

  /// <summary>A parsed input carrying only the action and its path; every per-action parser
  ///     fills in the members its action uses.</summary>
  private static PptxToolInput Input(string action, string path) =>
      new(action, path, 0, null, [], null, null, 0, null, null, null, null);

  private static Result<PptxToolInput> Fail(DomainError error) => Result.Failure<PptxToolInput>(error);
}
