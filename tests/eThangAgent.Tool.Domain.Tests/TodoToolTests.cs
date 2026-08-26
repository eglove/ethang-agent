using eThangAgent.SharedKernel;

namespace eThangAgent.ToolDomain.Tests;

public class TodoToolTests
{
  private const string StoreKey = "todo/list";

  private static (TodoTool Tool, FakeTodoListStore State) MakeTool(
      string? storedValue = null, int storedVersion = 1)
  {
    FakeTodoListStore state = storedValue is null
            ? new FakeTodoListStore()
            : new FakeTodoListStore(StoreKey, storedValue, storedVersion);
    return (new TodoTool(state), state);
  }

  private static string Json(params (int Id, string Description, string Status)[] items) =>
      "[" + string.Join(",", items.Select(i =>
          $"{{\"id\":{i.Id},\"description\":\"{i.Description}\",\"status\":\"{i.Status}\"}}")) + "]";

  // ---- Group 1: add to empty store ----

  [Fact]
  public async Task Add_ToEmptyStore_PersistsItemOnePending_ExactJsonAndOutput()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool();

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Add","description":"First task."}"""));

    Assert.False(result.IsError);
    Assert.Equal("[todo] added #1", result.Content);

    (string Key, string Value, int? ExpectedVersion) = Assert.Single(state.SetCalls);
    Assert.Equal(StoreKey, Key);
    Assert.Equal(/*lang=json,strict*/ """[{"id":1,"description":"First task.","status":"Pending"}]""", Value);
    Assert.Null(ExpectedVersion);
    Assert.Equal([StoreKey], state.GetCalls);
  }

  // ---- Group 2: id sequencing across gaps ----

  [Fact]
  public async Task Add_AfterGap_UsesMaxPlusOne()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool(
        storedValue: Json((2, "Survivor", "Pending")));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Add","description":"Next task"}"""));

    Assert.False(result.IsError);
    Assert.Equal("[todo] added #3", result.Content);

    (string _, string Value, int? _) = Assert.Single(state.SetCalls);
    Assert.Equal(
                             /*lang=json,strict*/
                             """[{"id":2,"description":"Survivor","status":"Pending"},{"id":3,"description":"Next task","status":"Pending"}]""",
        Value);
  }

  [Fact]
  public async Task Add_SequenceContinues_AfterRemovals_AndChainsCasVersions()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool();

    _ = await tool.ExecuteAsync(new RawToolInput("todo",
                             /*lang=json,strict*/
                             """{"timeoutSeconds":120,"action":"Add","description":"One"}"""));
    _ = await tool.ExecuteAsync(new RawToolInput("todo",
                             /*lang=json,strict*/
                             """{"timeoutSeconds":120,"action":"Add","description":"Two"}"""));
    _ = await tool.ExecuteAsync(new RawToolInput("todo",
                             /*lang=json,strict*/
                             """{"timeoutSeconds":120,"action":"Add","description":"Three"}"""));
    _ = await tool.ExecuteAsync(new RawToolInput("todo", /*lang=json,strict*/ """{"timeoutSeconds":120,"action":"Remove","id":1}"""));
    _ = await tool.ExecuteAsync(new RawToolInput("todo", /*lang=json,strict*/ """{"timeoutSeconds":120,"action":"Remove","id":2}"""));
    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Add","description":"Four"}"""));

    Assert.False(result.IsError);
    Assert.Equal("[todo] added #4", result.Content);

    Assert.Equal(6, state.SetCalls.Count);
    Assert.Null(state.SetCalls[0].ExpectedVersion);
    Assert.Equal(1, state.SetCalls[1].ExpectedVersion);
    Assert.Equal(2, state.SetCalls[2].ExpectedVersion);
    Assert.Equal(5, state.SetCalls[5].ExpectedVersion);
    Assert.Equal(
                             /*lang=json,strict*/
                             """[{"id":3,"description":"Three","status":"Pending"},{"id":4,"description":"Four","status":"Pending"}]""",
        state.SetCalls[5].Value);
  }

  // ---- Group 3: update ----

  [Fact]
  public async Task Update_Description_PersistsChange_ExactOutput()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool(
        storedValue: Json((1, "Old text", "Pending")));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Update","id":1,"description":"New text"}"""));

    Assert.False(result.IsError);
    Assert.Equal("[todo] updated #1", result.Content);

    (string _, string Value, int? _) = Assert.Single(state.SetCalls);
    Assert.Equal(/*lang=json,strict*/ """[{"id":1,"description":"New text","status":"Pending"}]""", Value);
  }

  [Fact]
  public async Task Update_Status_PersistsInProgress()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool(
        storedValue: Json((1, "Task", "Pending")));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Update","id":1,"status":"InProgress"}"""));

    Assert.False(result.IsError);
    Assert.Equal("[todo] updated #1", result.Content);

    (string _, string Value, int? _) = Assert.Single(state.SetCalls);
    Assert.Equal(/*lang=json,strict*/ """[{"id":1,"description":"Task","status":"InProgress"}]""", Value);
  }

  [Fact]
  public async Task Update_UnknownId_TodoNotFound_NoWrite()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool(
        storedValue: Json((1, "Task", "Pending")));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Update","id":9,"description":"Nope"}"""));

    Assert.True(result.IsError);
    Assert.Contains("TodoNotFound", result.Content, StringComparison.Ordinal);
    Assert.Contains("9", result.Content, StringComparison.Ordinal);
    Assert.Empty(state.SetCalls);
  }

  // ---- Group 4: complete ----

  [Fact]
  public async Task Complete_Existing_MarksCompleted()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool(
        storedValue: Json((1, "Task", "Pending")));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Complete","id":1}"""));

    Assert.False(result.IsError);
    Assert.Equal("[todo] completed #1", result.Content);

    (string _, string Value, int? _) = Assert.Single(state.SetCalls);
    Assert.Equal(/*lang=json,strict*/ """[{"id":1,"description":"Task","status":"Completed"}]""", Value);
  }

  [Fact]
  public async Task Complete_AlreadyCompleted_IdempotentSuccess()
  {
    (TodoTool? tool, FakeTodoListStore _) = MakeTool(
        storedValue: Json((1, "Task", "Completed")));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Complete","id":1}"""));

    Assert.False(result.IsError);
    Assert.Equal("[todo] completed #1", result.Content);
  }

  [Fact]
  public async Task Complete_UnknownId_TodoNotFound()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool(
        storedValue: Json((1, "Task", "Pending")));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Complete","id":42}"""));

    Assert.True(result.IsError);
    Assert.Contains("TodoNotFound", result.Content, StringComparison.Ordinal);
    Assert.Contains("42", result.Content, StringComparison.Ordinal);
    Assert.Empty(state.SetCalls);
  }

  // ---- Group 5: remove ----

  [Fact]
  public async Task Remove_Existing_PersistsWithoutItem_ExactOutput()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool(storedValue: Json(
        (1, "Doomed", "Pending"),
        (2, "Survivor", "InProgress")));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Remove","id":1}"""));

    Assert.False(result.IsError);
    Assert.Equal("[todo] removed #1", result.Content);

    (string _, string Value, int? _) = Assert.Single(state.SetCalls);
    Assert.Equal(/*lang=json,strict*/ """[{"id":2,"description":"Survivor","status":"InProgress"}]""", Value);
  }

  [Fact]
  public async Task Remove_LastItem_PersistsEmptyArray()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool(
        storedValue: Json((1, "Only", "Pending")));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Remove","id":1}"""));

    Assert.False(result.IsError);
    Assert.Equal("[todo] removed #1", result.Content);
    Assert.Equal("[]", Assert.Single(state.SetCalls).Value);
  }

  [Fact]
  public async Task Remove_UnknownId_TodoNotFound_NoWrite()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool(
        storedValue: Json((1, "Task", "Pending")));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Remove","id":7}"""));

    Assert.True(result.IsError);
    Assert.Contains("TodoNotFound", result.Content, StringComparison.Ordinal);
    Assert.Contains("7", result.Content, StringComparison.Ordinal);
    Assert.Empty(state.SetCalls);
  }

  // ---- Group 6: clear gate ----

  [Fact]
  public async Task Clear_WithoutConfirm_GateRejected_NoWrite()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool(
        storedValue: Json((1, "Task", "Pending")));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Clear"}"""));

    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("'confirm'", result.Content, StringComparison.Ordinal);
    Assert.Empty(state.SetCalls);
  }

  [Fact]
  public async Task Clear_ConfirmFalse_RejectedByGate()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool(
        storedValue: Json((1, "Task", "Pending")));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Clear","confirm":false}"""));

    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("'confirm'", result.Content, StringComparison.Ordinal);
    Assert.Empty(state.SetCalls);
  }

  [Fact]
  public async Task Clear_ConfirmStringTrue_RejectedByGate()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool(
        storedValue: Json((1, "Task", "Pending")));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Clear","confirm":"true"}"""));

    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Empty(state.SetCalls);
  }

  [Fact]
  public async Task Clear_WithConfirmTrue_PersistsEmptyArray_ExactOutput()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool(storedValue: Json(
        (1, "A", "Completed"),
        (2, "B", "Pending")));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Clear","confirm":true}"""));

    Assert.False(result.IsError);
    Assert.Equal("[todo] cleared", result.Content);
    Assert.Equal("[]", Assert.Single(state.SetCalls).Value);
  }

  // ---- Group 7: list formatting ----

  [Fact]
  public async Task List_TwoItemsMixedStatuses_ExactFormatting()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool(storedValue: Json(
        (1, "Write failing test", "InProgress"),
        (2, "Ship the release", "Completed")));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"List"}"""));

    Assert.False(result.IsError);
    Assert.Equal(
        "[todo: 1 open / 2 total]\n" +
        "#1 [InProgress] Write failing test\n" +
        "#2 [Completed] Ship the release",
        result.Content);
    Assert.Empty(state.SetCalls);
  }

  [Fact]
  public async Task List_EmptyDocument_ExactOutput()
  {
    (TodoTool? tool, FakeTodoListStore _) = MakeTool(storedValue: "[]");

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"List"}"""));

    Assert.False(result.IsError);
    Assert.Equal("[todo: empty]", result.Content);
  }

  [Fact]
  public async Task List_MissingKey_TreatedAsEmptyDocument()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool();

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"List"}"""));

    Assert.False(result.IsError);
    Assert.Equal("[todo: empty]", result.Content);
    Assert.Equal([StoreKey], state.GetCalls);
    Assert.Empty(state.SetCalls);
  }

  // ---- Group 8: version conflict is retryable ----

  [Fact]
  public async Task VersionConflict_FromStore_RetryableErrorResult()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool(
        storedValue: Json((1, "Task", "Pending")));
    state.WriteResultOverride = Result.Failure<int>(new DomainError("VersionConflict",
        $"Version conflict for '{StoreKey}': current version is 7."));

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Add","description":"Raced"}"""));

    Assert.True(result.IsError);
    Assert.Contains("VersionConflict", result.Content, StringComparison.Ordinal);
    Assert.Contains("Re-issue the same call to retry", result.Content, StringComparison.Ordinal);
  }

  // ---- Group 9: corrupt storage fails closed ----

  [Fact]
  public async Task MalformedStoredJson_Add_StorageCorrupt_NeverResets()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool(storedValue: "not json at all");

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Add","description":"X"}"""));

    Assert.True(result.IsError);
    Assert.Contains("StorageCorrupt", result.Content, StringComparison.Ordinal);
    Assert.Contains(StoreKey, result.Content, StringComparison.Ordinal);
    Assert.Empty(state.SetCalls);
  }

  [Fact]
  public async Task CorruptStoredJson_List_StorageCorrupt()
  {
    (TodoTool? tool, FakeTodoListStore _) = MakeTool(storedValue: "{not an array}");

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"List"}"""));

    Assert.True(result.IsError);
    Assert.Contains("StorageCorrupt", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Corrupt_ItemUnknownField_StorageCorrupt()
  {
    (TodoTool? tool, FakeTodoListStore _) = MakeTool(
        storedValue: /*lang=json,strict*/ """[{"id":1,"description":"a","status":"Pending","extra":true}]""");

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo", /*lang=json,strict*/ """{"timeoutSeconds":120,"action":"List"}"""));

    Assert.True(result.IsError);
    Assert.Contains("StorageCorrupt", result.Content, StringComparison.Ordinal);
    Assert.Contains("extra", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Corrupt_ItemBadStatus_StorageCorrupt()
  {
    (TodoTool? tool, FakeTodoListStore _) = MakeTool(
        storedValue: /*lang=json,strict*/ """[{"id":1,"description":"a","status":"Done"}]""");

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo", /*lang=json,strict*/ """{"timeoutSeconds":120,"action":"List"}"""));

    Assert.True(result.IsError);
    Assert.Contains("StorageCorrupt", result.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """[{"id":0,"description":"a","status":"Pending"}]""")]
  [InlineData(/*lang=json,strict*/ """[{"id":-2,"description":"a","status":"Pending"}]""")]
  [InlineData(/*lang=json,strict*/ """[{"id":1,"description":"","status":"Pending"}]""")]
  [InlineData(/*lang=json,strict*/ """[{"id":"1","description":"a","status":"Pending"}]""")]
  [InlineData(/*lang=json,strict*/ """[{"id":1}]""")]
  [InlineData(/*lang=json,strict*/ """[{"id":1,"description":"a","status":"Pending"},{"id":1,"description":"dup","status":"Pending"}]""")]
  public async Task Corrupt_Documents_StorageCorrupt(string stored)
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool(storedValue: stored);

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo", /*lang=json,strict*/ """{"timeoutSeconds":120,"action":"List"}"""));

    Assert.True(result.IsError);
    Assert.Contains("StorageCorrupt", result.Content, StringComparison.Ordinal);
    Assert.Empty(state.SetCalls);
  }

  // ---- Group 10: input rules ----

  [Fact]
  public async Task UnknownActionString_InvalidParameterValue_ListingActions()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool();

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"add","description":"x"}"""));

    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("'add'", result.Content, StringComparison.Ordinal);
    Assert.Contains("case-sensitive", result.Content, StringComparison.Ordinal);
    foreach (string a in new[] { "Add", "Update", "Complete", "Remove", "List", "Clear" })
    {
      Assert.Contains(a, result.Content, StringComparison.Ordinal);
    }

    Assert.Empty(state.SetCalls);
  }

  [Fact]
  public async Task Add_MissingDescription_MissingParameter()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool();

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Add"}"""));

    Assert.True(result.IsError);
    Assert.Contains("MissingParameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("'description'", result.Content, StringComparison.Ordinal);
    Assert.Empty(state.GetCalls);
    Assert.Empty(state.SetCalls);
  }

  [Fact]
  public async Task Add_EmptyDescription_InvalidParameterValue()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool();

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Add","description":""}"""));

    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("'description'", result.Content, StringComparison.Ordinal);
    Assert.Empty(state.SetCalls);
  }

  [Fact]
  public async Task Add_ExplicitStatus_Rejected_ItemsStartPending()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool();

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Add","description":"x","status":"Completed"}"""));

    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("'status'", result.Content, StringComparison.Ordinal);
    Assert.Contains("Pending", result.Content, StringComparison.Ordinal);
    Assert.Empty(state.SetCalls);
  }

  [Fact]
  public async Task Update_MissingId_MissingParameter()
  {
    (TodoTool? tool, FakeTodoListStore _) = MakeTool();

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Update","description":"x"}"""));

    Assert.True(result.IsError);
    Assert.Contains("MissingParameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("'id'", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task Update_ChangesNothing_InvalidParameterValue()
  {
    (TodoTool? tool, FakeTodoListStore? state) = MakeTool();

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Update","id":1}"""));

    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("description", result.Content, StringComparison.Ordinal);
    Assert.Contains("status", result.Content, StringComparison.Ordinal);
    Assert.Empty(state.SetCalls);
  }

  [Fact]
  public async Task Update_StatusOutsideEnum_InvalidParameterValue_ListingStatuses()
  {
    (TodoTool? tool, FakeTodoListStore _) = MakeTool();

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"Update","id":1,"status":"Done"}"""));

    Assert.True(result.IsError);
    Assert.Contains("InvalidParameterValue", result.Content, StringComparison.Ordinal);
    Assert.Contains("'Done'", result.Content, StringComparison.Ordinal);
    foreach (string s in new[] { "Pending", "InProgress", "Completed" })
    {
      Assert.Contains(s, result.Content, StringComparison.Ordinal);
    }
  }

  [Fact]
  public async Task UnknownParameter_Rejected()
  {
    (TodoTool? tool, FakeTodoListStore _) = MakeTool();

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"action":"List","filter":"open"}"""));

    Assert.True(result.IsError);
    Assert.Contains("Unknown parameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("filter", result.Content, StringComparison.Ordinal);
  }

  [Theory]
  [InlineData(/*lang=json,strict*/ """{"action":"Complete","id":"3"}""", "InvalidParameterType")]
  [InlineData(/*lang=json,strict*/ """{"action":"Complete","id":1.5}""", "InvalidParameterType")]
  [InlineData(/*lang=json,strict*/ """{"action":"Complete","id":0}""", "InvalidParameterValue")]
  [InlineData(/*lang=json,strict*/ """{"action":"Complete","id":-1}""", "InvalidParameterValue")]
  public async Task Id_Rules_Enforced(string args, string expectedCode)
  {
    (TodoTool? tool, FakeTodoListStore _) = MakeTool();

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo", args));

    Assert.True(result.IsError);
    Assert.Contains(expectedCode, result.Content, StringComparison.Ordinal);
    Assert.Contains("'id'", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task InvalidJsonArguments_Rejected()
  {
    (TodoTool? tool, FakeTodoListStore _) = MakeTool();

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo", "{\"timeoutSeconds\":120,bad"));

    Assert.True(result.IsError);
    Assert.Contains("not valid JSON", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task NonObjectArguments_Rejected()
  {
    (TodoTool? tool, FakeTodoListStore _) = MakeTool();

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo", "[]"));

    Assert.True(result.IsError);
    Assert.Contains("JSON object", result.Content, StringComparison.Ordinal);
  }

  [Fact]
  public async Task MissingAction_MissingParameter()
  {
    (TodoTool? tool, FakeTodoListStore _) = MakeTool();

    ToolResult result = await tool.ExecuteAsync(new RawToolInput("todo",
                                 /*lang=json,strict*/
                                 """{"timeoutSeconds":120,"description":"x"}"""));

    Assert.True(result.IsError);
    Assert.Contains("MissingParameter", result.Content, StringComparison.Ordinal);
    Assert.Contains("'action'", result.Content, StringComparison.Ordinal);
  }

  /// <summary>In-memory CAS store mirroring the real composition-root adapter's
  ///     semantics: expectedVersion must match the current version when supplied;
  ///     null creates or overwrites. Scriptable overrides force failure results.</summary>
  private sealed class FakeTodoListStore : ITodoListStore
  {
    private readonly Dictionary<string, (string Value, int Version)> _keys = [];

    public FakeTodoListStore() { }
    public FakeTodoListStore(string key, string value, int version) => _keys[key] = (value, version);

    public Result<string>? GetResultOverride { get; set; }
    public Result<int>? WriteResultOverride { get; set; }

    public List<string> GetCalls { get; } = [];
    public List<(string Key, string Value, int? ExpectedVersion)> SetCalls { get; } = [];

    public Task<Result<string>> GetValueAsync(string key, CancellationToken ct = default)
    {
      GetCalls.Add(key);
      Result<string>? getOverride = GetResultOverride;
      return getOverride is not null
        ? Task.FromResult(getOverride)
        : Task.FromResult(_keys.TryGetValue(key, out (string Value, int Version) kv)
                ? Result.Success(kv.Value)
                : Result.Failure<string>(new DomainError("KeyNotFound", $"'{key}' does not exist.")));
    }

    public Task<Result<int>> WriteValueAsync(string key, string value,
        int? expectedVersion, CancellationToken ct = default)
    {
      SetCalls.Add((key, value, expectedVersion));
      Result<int>? writeOverride = WriteResultOverride;
      if (writeOverride is not null)
      {
        return Task.FromResult(writeOverride);
      }

      if (_keys.TryGetValue(key, out (string Value, int Version) current))
      {
        if (expectedVersion.HasValue && expectedVersion.Value != current.Version)
        {
          return Task.FromResult(Result.Failure<int>(new DomainError("VersionConflict",
                        $"Version conflict for '{key}': current version is {current.Version}.")));
        }

        _keys[key] = (value, current.Version + 1);
        return Task.FromResult(Result.Success(current.Version + 1));
      }
      if (expectedVersion.HasValue)
      {
        return Task.FromResult(Result.Failure<int>(new DomainError("VersionConflict",
                    $"Version conflict for '{key}': current version is 0.")));
      }

      _keys[key] = (value, 1);
      return Task.FromResult(Result.Success(1));
    }
  }
}
