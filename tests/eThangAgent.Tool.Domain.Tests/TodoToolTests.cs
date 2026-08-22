using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.ToolDomain.Tests;

public class TodoToolTests
{
    private const string StoreKey = "todo/list";

    private static (TodoTool Tool, FakeTodoListStore State) MakeTool(
        string? storedValue = null, int storedVersion = 1)
    {
        var state = storedValue is null
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
        var (tool, state) = MakeTool();

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Add","description":"First task."}"""));

        Assert.False(result.IsError);
        Assert.Equal("[todo] added #1", result.Content);

        var call = Assert.Single(state.SetCalls);
        Assert.Equal(StoreKey, call.Key);
        Assert.Equal("""[{"id":1,"description":"First task.","status":"Pending"}]""", call.Value);
        Assert.Null(call.ExpectedVersion);
        Assert.Equal([StoreKey], state.GetCalls);
    }

    // ---- Group 2: id sequencing across gaps ----

    [Fact]
    public async Task Add_AfterGap_UsesMaxPlusOne()
    {
        var (tool, state) = MakeTool(
            storedValue: Json((2, "Survivor", "Pending")));

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Add","description":"Next task"}"""));

        Assert.False(result.IsError);
        Assert.Equal("[todo] added #3", result.Content);

        var call = Assert.Single(state.SetCalls);
        Assert.Equal(
            """[{"id":2,"description":"Survivor","status":"Pending"},{"id":3,"description":"Next task","status":"Pending"}]""",
            call.Value);
    }

    [Fact]
    public async Task Add_SequenceContinues_AfterRemovals_AndChainsCasVersions()
    {
        var (tool, state) = MakeTool();

        await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Add","description":"One"}"""));
        await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Add","description":"Two"}"""));
        await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Add","description":"Three"}"""));
        await tool.ExecuteAsync(new RawToolInput("todo", """{"action":"Remove","id":1}"""));
        await tool.ExecuteAsync(new RawToolInput("todo", """{"action":"Remove","id":2}"""));
        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Add","description":"Four"}"""));

        Assert.False(result.IsError);
        Assert.Equal("[todo] added #4", result.Content);

        Assert.Equal(6, state.SetCalls.Count);
        Assert.Null(state.SetCalls[0].ExpectedVersion);
        Assert.Equal(1, state.SetCalls[1].ExpectedVersion);
        Assert.Equal(2, state.SetCalls[2].ExpectedVersion);
        Assert.Equal(5, state.SetCalls[5].ExpectedVersion);
        Assert.Equal(
            """[{"id":3,"description":"Three","status":"Pending"},{"id":4,"description":"Four","status":"Pending"}]""",
            state.SetCalls[5].Value);
    }

    // ---- Group 3: update ----

    [Fact]
    public async Task Update_Description_PersistsChange_ExactOutput()
    {
        var (tool, state) = MakeTool(
            storedValue: Json((1, "Old text", "Pending")));

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Update","id":1,"description":"New text"}"""));

        Assert.False(result.IsError);
        Assert.Equal("[todo] updated #1", result.Content);

        var call = Assert.Single(state.SetCalls);
        Assert.Equal("""[{"id":1,"description":"New text","status":"Pending"}]""", call.Value);
    }

    [Fact]
    public async Task Update_Status_PersistsInProgress()
    {
        var (tool, state) = MakeTool(
            storedValue: Json((1, "Task", "Pending")));

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Update","id":1,"status":"InProgress"}"""));

        Assert.False(result.IsError);
        Assert.Equal("[todo] updated #1", result.Content);

        var call = Assert.Single(state.SetCalls);
        Assert.Equal("""[{"id":1,"description":"Task","status":"InProgress"}]""", call.Value);
    }

    [Fact]
    public async Task Update_UnknownId_TodoNotFound_NoWrite()
    {
        var (tool, state) = MakeTool(
            storedValue: Json((1, "Task", "Pending")));

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Update","id":9,"description":"Nope"}"""));

        Assert.True(result.IsError);
        Assert.Contains("TodoNotFound", result.Content);
        Assert.Contains("9", result.Content);
        Assert.Empty(state.SetCalls);
    }

    // ---- Group 4: complete ----

    [Fact]
    public async Task Complete_Existing_MarksCompleted()
    {
        var (tool, state) = MakeTool(
            storedValue: Json((1, "Task", "Pending")));

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Complete","id":1}"""));

        Assert.False(result.IsError);
        Assert.Equal("[todo] completed #1", result.Content);

        var call = Assert.Single(state.SetCalls);
        Assert.Equal("""[{"id":1,"description":"Task","status":"Completed"}]""", call.Value);
    }

    [Fact]
    public async Task Complete_AlreadyCompleted_IdempotentSuccess()
    {
        var (tool, _) = MakeTool(
            storedValue: Json((1, "Task", "Completed")));

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Complete","id":1}"""));

        Assert.False(result.IsError);
        Assert.Equal("[todo] completed #1", result.Content);
    }

    [Fact]
    public async Task Complete_UnknownId_TodoNotFound()
    {
        var (tool, state) = MakeTool(
            storedValue: Json((1, "Task", "Pending")));

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Complete","id":42}"""));

        Assert.True(result.IsError);
        Assert.Contains("TodoNotFound", result.Content);
        Assert.Contains("42", result.Content);
        Assert.Empty(state.SetCalls);
    }

    // ---- Group 5: remove ----

    [Fact]
    public async Task Remove_Existing_PersistsWithoutItem_ExactOutput()
    {
        var (tool, state) = MakeTool(storedValue: Json(
            (1, "Doomed", "Pending"),
            (2, "Survivor", "InProgress")));

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Remove","id":1}"""));

        Assert.False(result.IsError);
        Assert.Equal("[todo] removed #1", result.Content);

        var call = Assert.Single(state.SetCalls);
        Assert.Equal("""[{"id":2,"description":"Survivor","status":"InProgress"}]""", call.Value);
    }

    [Fact]
    public async Task Remove_LastItem_PersistsEmptyArray()
    {
        var (tool, state) = MakeTool(
            storedValue: Json((1, "Only", "Pending")));

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Remove","id":1}"""));

        Assert.False(result.IsError);
        Assert.Equal("[todo] removed #1", result.Content);
        Assert.Equal("[]", Assert.Single(state.SetCalls).Value);
    }

    [Fact]
    public async Task Remove_UnknownId_TodoNotFound_NoWrite()
    {
        var (tool, state) = MakeTool(
            storedValue: Json((1, "Task", "Pending")));

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Remove","id":7}"""));

        Assert.True(result.IsError);
        Assert.Contains("TodoNotFound", result.Content);
        Assert.Contains("7", result.Content);
        Assert.Empty(state.SetCalls);
    }

    // ---- Group 6: clear gate ----

    [Fact]
    public async Task Clear_WithoutConfirm_GateRejected_NoWrite()
    {
        var (tool, state) = MakeTool(
            storedValue: Json((1, "Task", "Pending")));

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Clear"}"""));

        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("'confirm'", result.Content);
        Assert.Empty(state.SetCalls);
    }

    [Fact]
    public async Task Clear_ConfirmFalse_RejectedByGate()
    {
        var (tool, state) = MakeTool(
            storedValue: Json((1, "Task", "Pending")));

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Clear","confirm":false}"""));

        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("'confirm'", result.Content);
        Assert.Empty(state.SetCalls);
    }

    [Fact]
    public async Task Clear_ConfirmStringTrue_RejectedByGate()
    {
        var (tool, state) = MakeTool(
            storedValue: Json((1, "Task", "Pending")));

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Clear","confirm":"true"}"""));

        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Empty(state.SetCalls);
    }

    [Fact]
    public async Task Clear_WithConfirmTrue_PersistsEmptyArray_ExactOutput()
    {
        var (tool, state) = MakeTool(storedValue: Json(
            (1, "A", "Completed"),
            (2, "B", "Pending")));

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Clear","confirm":true}"""));

        Assert.False(result.IsError);
        Assert.Equal("[todo] cleared", result.Content);
        Assert.Equal("[]", Assert.Single(state.SetCalls).Value);
    }

    // ---- Group 7: list formatting ----

    [Fact]
    public async Task List_TwoItemsMixedStatuses_ExactFormatting()
    {
        var (tool, state) = MakeTool(storedValue: Json(
            (1, "Write failing test", "InProgress"),
            (2, "Ship the release", "Completed")));

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"List"}"""));

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
        var (tool, _) = MakeTool(storedValue: "[]");

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"List"}"""));

        Assert.False(result.IsError);
        Assert.Equal("[todo: empty]", result.Content);
    }

    [Fact]
    public async Task List_MissingKey_TreatedAsEmptyDocument()
    {
        var (tool, state) = MakeTool();

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"List"}"""));

        Assert.False(result.IsError);
        Assert.Equal("[todo: empty]", result.Content);
        Assert.Equal([StoreKey], state.GetCalls);
        Assert.Empty(state.SetCalls);
    }

    // ---- Group 8: version conflict is retryable ----

    [Fact]
    public async Task VersionConflict_FromStore_RetryableErrorResult()
    {
        var (tool, state) = MakeTool(
            storedValue: Json((1, "Task", "Pending")));
        state.WriteResultOverride = Result<int>.Failure(new Error("VersionConflict",
            $"Version conflict for '{StoreKey}': current version is 7."));

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Add","description":"Raced"}"""));

        Assert.True(result.IsError);
        Assert.Contains("VersionConflict", result.Content);
        Assert.Contains("Re-issue the same call to retry", result.Content);
    }

    // ---- Group 9: corrupt storage fails closed ----

    [Fact]
    public async Task MalformedStoredJson_Add_StorageCorrupt_NeverResets()
    {
        var (tool, state) = MakeTool(storedValue: "not json at all");

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Add","description":"X"}"""));

        Assert.True(result.IsError);
        Assert.Contains("StorageCorrupt", result.Content);
        Assert.Contains(StoreKey, result.Content);
        Assert.Empty(state.SetCalls);
    }

    [Fact]
    public async Task CorruptStoredJson_List_StorageCorrupt()
    {
        var (tool, _) = MakeTool(storedValue: "{not an array}");

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"List"}"""));

        Assert.True(result.IsError);
        Assert.Contains("StorageCorrupt", result.Content);
    }

    [Fact]
    public async Task Corrupt_ItemUnknownField_StorageCorrupt()
    {
        var (tool, _) = MakeTool(
            storedValue: """[{"id":1,"description":"a","status":"Pending","extra":true}]""");

        var result = await tool.ExecuteAsync(new RawToolInput("todo", """{"action":"List"}"""));

        Assert.True(result.IsError);
        Assert.Contains("StorageCorrupt", result.Content);
        Assert.Contains("extra", result.Content);
    }

    [Fact]
    public async Task Corrupt_ItemBadStatus_StorageCorrupt()
    {
        var (tool, _) = MakeTool(
            storedValue: """[{"id":1,"description":"a","status":"Done"}]""");

        var result = await tool.ExecuteAsync(new RawToolInput("todo", """{"action":"List"}"""));

        Assert.True(result.IsError);
        Assert.Contains("StorageCorrupt", result.Content);
    }

    [Theory]
    [InlineData("""[{"id":0,"description":"a","status":"Pending"}]""")]
    [InlineData("""[{"id":-2,"description":"a","status":"Pending"}]""")]
    [InlineData("""[{"id":1,"description":"","status":"Pending"}]""")]
    [InlineData("""[{"id":"1","description":"a","status":"Pending"}]""")]
    [InlineData("""[{"id":1}]""")]
    [InlineData("""[{"id":1,"description":"a","status":"Pending"},{"id":1,"description":"dup","status":"Pending"}]""")]
    public async Task Corrupt_Documents_StorageCorrupt(string stored)
    {
        var (tool, state) = MakeTool(storedValue: stored);

        var result = await tool.ExecuteAsync(new RawToolInput("todo", """{"action":"List"}"""));

        Assert.True(result.IsError);
        Assert.Contains("StorageCorrupt", result.Content);
        Assert.Empty(state.SetCalls);
    }

    // ---- Group 10: input rules ----

    [Fact]
    public async Task UnknownActionString_InvalidParameterValue_ListingActions()
    {
        var (tool, state) = MakeTool();

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"add","description":"x"}"""));

        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("'add'", result.Content);
        Assert.Contains("case-sensitive", result.Content);
        foreach (var a in new[] { "Add", "Update", "Complete", "Remove", "List", "Clear" })
            Assert.Contains(a, result.Content);
        Assert.Empty(state.SetCalls);
    }

    [Fact]
    public async Task Add_MissingDescription_MissingParameter()
    {
        var (tool, state) = MakeTool();

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Add"}"""));

        Assert.True(result.IsError);
        Assert.Contains("MissingParameter", result.Content);
        Assert.Contains("'description'", result.Content);
        Assert.Empty(state.GetCalls);
        Assert.Empty(state.SetCalls);
    }

    [Fact]
    public async Task Add_EmptyDescription_InvalidParameterValue()
    {
        var (tool, state) = MakeTool();

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Add","description":""}"""));

        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("'description'", result.Content);
        Assert.Empty(state.SetCalls);
    }

    [Fact]
    public async Task Add_ExplicitStatus_Rejected_ItemsStartPending()
    {
        var (tool, state) = MakeTool();

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Add","description":"x","status":"Completed"}"""));

        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("'status'", result.Content);
        Assert.Contains("Pending", result.Content);
        Assert.Empty(state.SetCalls);
    }

    [Fact]
    public async Task Update_MissingId_MissingParameter()
    {
        var (tool, _) = MakeTool();

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Update","description":"x"}"""));

        Assert.True(result.IsError);
        Assert.Contains("MissingParameter", result.Content);
        Assert.Contains("'id'", result.Content);
    }

    [Fact]
    public async Task Update_ChangesNothing_InvalidParameterValue()
    {
        var (tool, state) = MakeTool();

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Update","id":1}"""));

        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("description", result.Content);
        Assert.Contains("status", result.Content);
        Assert.Empty(state.SetCalls);
    }

    [Fact]
    public async Task Update_StatusOutsideEnum_InvalidParameterValue_ListingStatuses()
    {
        var (tool, _) = MakeTool();

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"Update","id":1,"status":"Done"}"""));

        Assert.True(result.IsError);
        Assert.Contains("InvalidParameterValue", result.Content);
        Assert.Contains("'Done'", result.Content);
        foreach (var s in new[] { "Pending", "InProgress", "Completed" })
            Assert.Contains(s, result.Content);
    }

    [Fact]
    public async Task UnknownParameter_Rejected()
    {
        var (tool, _) = MakeTool();

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"action":"List","filter":"open"}"""));

        Assert.True(result.IsError);
        Assert.Contains("Unknown parameter", result.Content);
        Assert.Contains("filter", result.Content);
    }

    [Theory]
    [InlineData("""{"action":"Complete","id":"3"}""", "InvalidParameterType")]
    [InlineData("""{"action":"Complete","id":1.5}""", "InvalidParameterType")]
    [InlineData("""{"action":"Complete","id":0}""", "InvalidParameterValue")]
    [InlineData("""{"action":"Complete","id":-1}""", "InvalidParameterValue")]
    public async Task Id_Rules_Enforced(string args, string expectedCode)
    {
        var (tool, _) = MakeTool();

        var result = await tool.ExecuteAsync(new RawToolInput("todo", args));

        Assert.True(result.IsError);
        Assert.Contains(expectedCode, result.Content);
        Assert.Contains("'id'", result.Content);
    }

    [Fact]
    public async Task InvalidJsonArguments_Rejected()
    {
        var (tool, _) = MakeTool();

        var result = await tool.ExecuteAsync(new RawToolInput("todo", "{bad"));

        Assert.True(result.IsError);
        Assert.Contains("not valid JSON", result.Content);
    }

    [Fact]
    public async Task NonObjectArguments_Rejected()
    {
        var (tool, _) = MakeTool();

        var result = await tool.ExecuteAsync(new RawToolInput("todo", "[]"));

        Assert.True(result.IsError);
        Assert.Contains("JSON object", result.Content);
    }

    [Fact]
    public async Task MissingAction_MissingParameter()
    {
        var (tool, _) = MakeTool();

        var result = await tool.ExecuteAsync(new RawToolInput("todo",
            """{"description":"x"}"""));

        Assert.True(result.IsError);
        Assert.Contains("MissingParameter", result.Content);
        Assert.Contains("'action'", result.Content);
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
            var getOverride = GetResultOverride;
            if (getOverride is not null)
                return Task.FromResult(getOverride);
            return Task.FromResult(_keys.TryGetValue(key, out var kv)
                ? Result<string>.Success(kv.Value)
                : Result<string>.Failure(new Error("KeyNotFound", $"'{key}' does not exist.")));
        }

        public Task<Result<int>> WriteValueAsync(string key, string value,
            int? expectedVersion, CancellationToken ct = default)
        {
            SetCalls.Add((key, value, expectedVersion));
            var writeOverride = WriteResultOverride;
            if (writeOverride is not null)
                return Task.FromResult(writeOverride);

            if (_keys.TryGetValue(key, out var current))
            {
                if (expectedVersion.HasValue && expectedVersion.Value != current.Version)
                    return Task.FromResult(Result<int>.Failure(new Error("VersionConflict",
                        $"Version conflict for '{key}': current version is {current.Version}.")));
                _keys[key] = (value, current.Version + 1);
                return Task.FromResult(Result<int>.Success(current.Version + 1));
            }
            if (expectedVersion.HasValue)
                return Task.FromResult(Result<int>.Failure(new Error("VersionConflict",
                    $"Version conflict for '{key}': current version is 0.")));
            _keys[key] = (value, 1);
            return Task.FromResult(Result<int>.Success(1));
        }
    }
}
