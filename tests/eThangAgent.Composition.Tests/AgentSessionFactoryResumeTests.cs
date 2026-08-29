using eThangAgent.AgentDomain;
using eThangAgent.ConversationDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using eThangAgent.ToolDomain;
using Microsoft.Extensions.DependencyInjection;

// Best-effort temp-file cleanup in catch blocks is deliberate (CA1031).
#pragma warning disable CA1031 // Do not catch general exception types

namespace eThangAgent.Composition.Tests;

/// <summary>Resume over the real factory: a persisted root rehydrates its transcript
///     into a fresh container on its ORIGINAL provider and workspace; every bad id
///     shape fails with a structured error. A workspace holds many sessions — resume
///     targets exactly one id and never touches another session's history.</summary>
public class AgentSessionFactoryResumeTests
{
  private sealed class StubChannel : IClarifyChannel
  {
    public Task<Result<string>> AskAsync(ClarifyQuestion question, CancellationToken ct = default)
        => Task.FromResult(Result.Success("1"));
  }

  private static readonly Uri BaseUrl = new("https://openrouter.test");

  private static AgentSettings Settings(string? openRouterKey = "sk-or-test", string? zaiKey = null) => new(
      new OpenRouterSettings(openRouterKey, BaseUrl),
      new ZaiSettings(zaiKey, new Uri("https://zai.test")),
      new SubAgentOptions(null, TimeSpan.FromSeconds(300), 2));

  private static (AgentSessionFactory Factory, string DbPath) CreateFactory(AgentSettings? settings = null)
  {
    string dbPath = Path.Combine(Path.GetTempPath(), $"ethang-resume-{Guid.NewGuid():N}.db");
    return (new AgentSessionFactory(settings ?? Settings(), new AppDatabase(dbPath)), dbPath);
  }

  private static void DeleteDb(string dbPath)
  {
    try
    {
      File.Delete(dbPath);
    }
    catch
    {
      // best effort
    }
  }

  [Fact]
  public async Task ResumeAsync_Hydrates_Transcript_Rebinds_Identity_Reactives_Running()
  {
    (AgentSessionFactory factory, string db) = CreateFactory();
    try
    {
      DirectoryInfo dir = Directory.CreateTempSubdirectory("ethang-resume-ws");
      try
      {
        Result<AgentSession> created = await factory.CreateAsync(dir.FullName, Providers.OpenRouter, new StubChannel(), ct: TestContext.Current.CancellationToken);
        Assert.True(created.IsSuccess);
        AgentId rootId = created.Value.RootId;

        // Simulate a turn's slice through the store directly — the lifecycle's
        // contract is covered in RootSessionLifecycleTests.
        SqliteAgentStore store = new(new AppDatabase(db));
        DateTimeOffset at = DateTimeOffset.UtcNow;
        Assert.True((await store.AppendMessageAsync(rootId,
            new Message(Role.User, "first question", at), ct: TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AppendMessageAsync(rootId,
            new Message(Role.Assistant, "", at, [new ToolCall("call-1", "read", "{}")]), ct: TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AppendMessageAsync(rootId,
            new Message(Role.Tool, "file content", at, ToolCallId: "call-1"), ct: TestContext.Current.CancellationToken)).IsSuccess);
        Assert.True((await store.AppendMessageAsync(rootId,
            new Message(Role.Assistant, "final answer", at), ct: TestContext.Current.CancellationToken)).IsSuccess);

        // Close the tab: the container is torn down and the row marked Completed.
        await created.Value.Lifecycle.CompleteAsync(rootId, _ => Assert.Fail("no complete errors expected"));
        await created.Value.Services.DisposeAsync();

        Result<AgentSession> resumed = await factory.ResumeAsync(rootId, new StubChannel(), ct: TestContext.Current.CancellationToken);

        Assert.True(resumed.IsSuccess);
        AgentSession session = resumed.Value;
        Assert.Equal(rootId, session.RootId);
        Assert.Equal(dir.FullName, session.WorkspaceRoot);
        // The session resumes on its ORIGINAL provider.
        Assert.Equal(Providers.OpenRouter, session.ProviderName);

        // The transcript hydrates the new container's conversation, losslessly —
        // including the tool call and its result.
        IReadOnlyList<Message> messages = session.Conversation.Messages;
        Assert.Equal(4, messages.Count);
        Assert.Equal(Role.User, messages[0].Role);
        Assert.Equal("first question", messages[0].Content);
        Assert.NotNull(messages[1].ToolCalls);
        Assert.Equal("call-1", messages[1].ToolCalls![0].Id);
        Assert.Equal(Role.Tool, messages[2].Role);
        Assert.Equal("call-1", messages[2].ToolCallId);
        Assert.Equal("final answer", messages[^1].Content);

        // The root identity is published BEFORE the session is handed out.
        Assert.Equal(rootId, session.Services.GetRequiredService<RootSessionIdentity>().Id);

        // A Completed row returns to Running.
        AgentRecord record = (await store.GetAsync(rootId, ct: TestContext.Current.CancellationToken)).Value!;
        Assert.Equal(AgentStatus.Running, record.Status);
        Assert.Null(record.CompletedAt);
      }
      finally
      {
        dir.Delete(true);
      }
    }
    finally
    {
      DeleteDb(db);
    }
  }

  [Fact]
  public async Task ResumeAsync_Unknown_Id_Fails_Structured()
  {
    (AgentSessionFactory factory, string db) = CreateFactory();
    try
    {
      Result<AgentSession> resumed = await factory.ResumeAsync(AgentId.NewId(), new StubChannel(), ct: TestContext.Current.CancellationToken);
      Assert.False(resumed.IsSuccess);
      Assert.Equal("NotFound", resumed.Error.Code);
    }
    finally
    {
      DeleteDb(db);
    }
  }

  [Fact]
  public async Task ResumeAsync_SpawnedChild_Fails_NotResumable()
  {
    (AgentSessionFactory factory, string db) = CreateFactory();
    try
    {
      SqliteAgentStore store = new(new AppDatabase(db));
      AgentId childId = AgentId.NewId();
      _ = await store.SaveAsync(AgentRecord.Spawned(childId, AgentId.NewId(), depth: 1,
          modelUsed: "mock/model", label: "child", taskPrompt: "task", createdAt: DateTimeOffset.UtcNow), ct: TestContext.Current.CancellationToken);

      Result<AgentSession> resumed = await factory.ResumeAsync(childId, new StubChannel(), ct: TestContext.Current.CancellationToken);
      Assert.False(resumed.IsSuccess);
      Assert.Equal("NotResumable", resumed.Error.Code);
    }
    finally
    {
      DeleteDb(db);
    }
  }

  [Fact]
  public async Task ResumeAsync_RowWithoutBinding_Fails_NotResumable()
  {
    (AgentSessionFactory factory, string db) = CreateFactory();
    try
    {
      // A root persisted before the binding migration: depth 0, no workspace/provider.
      SqliteAgentStore store = new(new AppDatabase(db));
      AgentId legacyId = AgentId.NewId();
      _ = await store.SaveAsync(new AgentRecord(legacyId, null, 0, AgentStatus.Completed,
          null, "unassigned", "root", "conversation root", DateTimeOffset.UtcNow, DateTimeOffset.UtcNow, null), ct: TestContext.Current.CancellationToken);

      Result<AgentSession> resumed = await factory.ResumeAsync(legacyId, new StubChannel(), ct: TestContext.Current.CancellationToken);
      Assert.False(resumed.IsSuccess);
      Assert.Equal("NotResumable", resumed.Error.Code);
      Assert.Contains("workspace", resumed.Error.Message, StringComparison.OrdinalIgnoreCase);
    }
    finally
    {
      DeleteDb(db);
    }
  }

  [Fact]
  public async Task ResumeAsync_UnconfiguredProvider_Fails_Structured()
  {
    // OpenRouter key only; the persisted session ran on z.ai.
    (AgentSessionFactory factory, string db) = CreateFactory(Settings(zaiKey: null));
    try
    {
      SqliteAgentStore store = new(new AppDatabase(db));
      AgentId rootId = AgentId.NewId();
      string workspace = Directory.CreateDirectory(
          Path.Combine(Path.GetTempPath(), $"ethang-resume-z-{Guid.NewGuid():N}")).FullName;
      _ = await store.SaveAsync(AgentRecord.Root(rootId, DateTimeOffset.UtcNow, workspace, Providers.Zai), ct: TestContext.Current.CancellationToken);

      Result<AgentSession> resumed = await factory.ResumeAsync(rootId, new StubChannel(), ct: TestContext.Current.CancellationToken);
      Assert.False(resumed.IsSuccess);
      Assert.Equal("ProviderNotConfigured", resumed.Error.Code);
    }
    finally
    {
      DeleteDb(db);
    }
  }

  [Fact]
  public async Task ResumeAsync_MissingWorkspaceDirectory_Fails_Structured()
  {
    (AgentSessionFactory factory, string db) = CreateFactory();
    try
    {
      SqliteAgentStore store = new(new AppDatabase(db));
      AgentId rootId = AgentId.NewId();
      string gone = Path.Combine(Path.GetTempPath(), $"ethang-gone-{Guid.NewGuid():N}");
      _ = await store.SaveAsync(AgentRecord.Root(rootId, DateTimeOffset.UtcNow, gone, Providers.OpenRouter), ct: TestContext.Current.CancellationToken);

      Result<AgentSession> resumed = await factory.ResumeAsync(rootId, new StubChannel(), ct: TestContext.Current.CancellationToken);
      Assert.False(resumed.IsSuccess);
      Assert.Equal("WorkspaceNotFound", resumed.Error.Code);
    }
    finally
    {
      DeleteDb(db);
    }
  }

  [Fact]
  public async Task ResumeAsync_NullChannel_Fails_Structured()
  {
    (AgentSessionFactory factory, string db) = CreateFactory();
    try
    {
      Result<AgentSession> resumed = await factory.ResumeAsync(AgentId.NewId(), null!, ct: TestContext.Current.CancellationToken);
      Assert.False(resumed.IsSuccess);
      Assert.Equal("InvalidChannel", resumed.Error.Code);
    }
    finally
    {
      DeleteDb(db);
    }
  }
}
