using System.Globalization;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ConversationDomain;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using Microsoft.Extensions.DependencyInjection;
using Xunit;

namespace eThangAgent.LiveModel.Tests;

/// <summary>One live turn's artifacts: everything the Tier-1 assertions read, captured AFTER
///     the session is gone so the assertions are about what was PERSISTED, not about what one
///     in-memory object happened to hold.
///     <para>The persisted transcript and the usage ledger are the two artifacts that matter:
///     a lossy transcript and a dropped tool result are invisible while a turn runs (the
///     conversation object is correct either way) and only the row-level record reveals them.</para></summary>
internal sealed record LiveRunArtifacts(
    LiveModelChoice Model,
    string SessionId,
    AgentId RootId,
    string WorkspaceRoot,
    string AssistantText,
    int ConversationLength,
    IReadOnlyList<Message> PersistedTranscript,
    UsageSummary Usage,
    IReadOnlyList<string> PersistenceErrors)
{
  /// <summary>A failure message that NAMES the model: a red run must say which model
  ///     produced it, because the suite deliberately does not pin one.</summary>
  public string Fail(string what) => $"{what} [model: {Model.Describe()}] [session: {SessionId}]";

  /// <summary>The persisted tool calls whose id is not answered by a persisted tool result:
  ///     a dangling id is a lost tool result - the wire round trip regressed.</summary>
  public IReadOnlyList<string> DanglingToolCallIds()
  {
    HashSet<string> answered = [.. PersistedTranscript
        .Where(m => m.Role == Role.Tool && m.ToolCallId is not null)
        .Select(m => m.ToolCallId!)];
    return [.. PersistedTranscript
        .SelectMany(m => m.ToolCalls ?? [])
        .Select(call => call.Id)
        .Where(id => !answered.Contains(id))];
  }

  /// <summary>The persisted tool-result texts, in order.</summary>
  public IReadOnlyList<string> ToolResultTexts() => [.. PersistedTranscript
      .Where(m => m.Role == Role.Tool)
      .Select(m => m.Content)];
}

/// <summary>Drives ONE real live-model turn end to end: resolve a free model from the live
///     catalog, open a real session on it, run a turn under a wall-clock budget, persist the
///     transcript through the production lifecycle contract, dispose the session, and read the
///     artifacts back out of the database.
///     <para>The workspace is a FRESH TEMP directory per run and the database is the suite's
///     own temp file - never the interactive app's database, never a real user directory.</para>
///     <para>MODEL PIN: the turn must serve the model the selector chose. The session's live
///     model preference is set to that id before the turn, because the resolver serves the
///     user's live choice first and REFUSES to substitute - which is the harness contract this
///     suite is checking, not an implementation detail.</para></summary>
internal sealed class LiveRun : IAsyncDisposable
{
  private static readonly TimeSpan StartupBudget = TimeSpan.FromMinutes(6);

  private LiveRun(AgentSession session, AppDatabase database, LiveModelChoice model, string workspaceRoot)
  {
    Session = session;
    Database = database;
    Model = model;
    WorkspaceRoot = workspaceRoot;
  }

  public AgentSession Session { get; }

  public AppDatabase Database { get; }

  public LiveModelChoice Model { get; }

  public string WorkspaceRoot { get; }

  /// <summary>Opens the session ON a specific catalog candidate. The candidate rides
  ///     AgentSettings.SubAgents.DefaultModel as the session's model pin: the root resolver
  ///     serves it, and a model that cannot be served fails the turn loudly instead of quietly
  ///     running something else - which is what makes the walk in <see cref="LiveModelSession"/>
  ///     able to notice a refusal instead of silently testing a different model.</summary>
  public static async Task<LiveRun> StartAsync(LiveModelChoice model, CancellationToken ct)
  {
    ArgumentNullException.ThrowIfNull(model);
    string apiKey = LiveModelEnvironment.RequireApiKey();

    string databasePath = Path.Combine(Path.GetTempPath(),
        "ethang-live-" + Guid.NewGuid().ToString("N") + ".db");
    string workspaceRoot = Directory.CreateTempSubdirectory("ethang-live-ws").FullName;
    AppDatabase database = new(databasePath);

    AgentSettings settings = new(
        new OpenRouterSettings(apiKey, LiveModelEnvironment.BaseUrl),
        new SubAgentOptions(model.ModelId, 2));
    AgentSessionFactory factory = new(settings, database);
    using CancellationTokenSource startup = CancellationTokenSource.CreateLinkedTokenSource(ct);
    startup.CancelAfter(StartupBudget);
    Result<AgentSession> created = await factory.CreateAsync(workspaceRoot, Providers.OpenRouter,
        ct: startup.Token).ConfigureAwait(false);
    Assert.True(created.IsSuccess, created.IsSuccess ? string.Empty
        : $"the live session could not be opened on {model.Describe()}: {created.Error.Message}");

    // The turn must serve the SELECTED model: the resolver takes the live preference first.
    created.Value.Preferences!.ModelId = model.ModelId;
    return new LiveRun(created.Value, database, model, workspaceRoot);
  }

  /// <summary>Runs one turn under the budget and returns the assistant's final text.
  ///     A provider fault that is TRANSPORT-SHAPED (a timeout, a rate limit, a 5xx) is
  ///     retried ONCE, because a free model queues and a 429 is not a harness regression;
  ///     anything else - and a second transport fault - propagates so the test reports it.
  ///     An ASSERTION failure is never retried: it does not come from here.</summary>
  public async Task<Result<string>> RunTurnAsync(string input, CancellationToken ct)
  {
    Result<string> first = await SendAsync(input, ct).ConfigureAwait(false);
    return first.IsSuccess || !IsTransportFault(first.Error.Code)
        ? first
        : await SendAsync(input, ct).ConfigureAwait(false);
  }

  private async Task<Result<string>> SendAsync(string input, CancellationToken ct)
  {
    using CancellationTokenSource budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
    budget.CancelAfter(LiveModelEnvironment.Budget);
    Result<string> result = await Session.Handler
        .Handle(new SendMessageCommand(input), ct: budget.Token).ConfigureAwait(false);
    return result;
  }

  /// <summary>True for the failures a live provider produces that say nothing about this
  ///     repository: transport, rate limiting, and the provider's own 5xx.
  ///     <para><c>RateLimited</c> and <c>ProviderTimeout</c> are named by the provider ACL;
  ///     anything else is a harness answer and must not be retried past.</para></summary>
  public static bool IsTransportFault(string code) =>
      code is "RateLimited" or "ProviderTimeout" or "ProviderError";

  /// <summary>True when the provider REFUSED THIS MODEL rather than failing the call:
  ///     measured 2026-10-26, <c>HTTP 403 ... is only available on agentic harnesses</c>.
  ///     <para>The distinction decides what a caller may do about it. A transport fault is
  ///     weather and is retried on the SAME model; a model-level refusal will refuse every
  ///     retry, so the only useful response is the next candidate. Detected on the provider's
  ///     own words because the ACL maps every non-success status to one code (the body is
  ///     preserved in the message, which is exactly why it is preserved).</para></summary>
  public static bool IsModelRefusal(DomainError error) =>
      error.Code == "ProviderError"
      && (error.Message.Contains("HTTP 403", StringComparison.Ordinal)
          || error.Message.Contains("is only available on", StringComparison.Ordinal)
          || error.Message.Contains("No endpoints found", StringComparison.Ordinal));

  /// <summary>Persists the turn through the SAME lifecycle contract the Desktop uses, then
  ///     reads every artifact back out of the database. Called once per session; the session
  ///     is disposed afterwards so the assertions observe a settled process, not a live one.</summary>
  public async Task<LiveRunArtifacts> CollectAsync(string assistantText, List<string> persistenceErrors)
  {
    ArgumentNullException.ThrowIfNull(assistantText);
    ArgumentNullException.ThrowIfNull(persistenceErrors);
    AgentId rootId = Session.RootId;

    await Session.Lifecycle.AppendExchangeAsync(rootId, Session.Conversation,
        alreadyPersisted: 0, assistantText, persistenceErrors.Add).ConfigureAwait(false);
    await Session.Lifecycle.CompleteAsync(rootId, persistenceErrors.Add).ConfigureAwait(false);

    IAgentStore store = Session.Services.GetRequiredService<IAgentStore>();
    Result<IReadOnlyList<Message>> transcript = await store.GetTranscriptAsync(rootId)
        .ConfigureAwait(false);
    Assert.True(transcript.IsSuccess, transcript.IsSuccess ? string.Empty
        : $"the live session's transcript could not be read back: {transcript.Error.Message}");

    IUsageQueries usage = Session.Services.GetRequiredService<IUsageQueries>();
    Result<UsageSummary> summary = await usage.SessionSummaryAsync(rootId.ToString())
        .ConfigureAwait(false);
    Assert.True(summary.IsSuccess, summary.IsSuccess ? string.Empty
        : $"the live session's usage ledger could not be read back: {summary.Error.Message}");

    return new LiveRunArtifacts(Model, rootId.ToString(), rootId, WorkspaceRoot, assistantText,
        Session.Conversation.Messages.Count, transcript.Value, summary.Value, persistenceErrors);
  }

  /// <summary>Disposes the session, which kills its ChildHost - the process an abandoned run
  ///     would otherwise leave holding the database (issue #284 item 3).</summary>
  /// <para>DISPOSAL NEVER DELETES THE RECORD. The resume assertion reopens this session from
  /// its persisted rows and validates that the workspace directory still exists, so deleting
  /// them here would make a resume fail for a reason that has nothing to do with the harness.
  /// The temp database and workspace are removed by <see cref="DeleteRecordAsync"/>, called once
  /// at the very end of the run.</para>
  public async ValueTask DisposeAsync()
  {
    await Session.Services.DisposeAsync().ConfigureAwait(false);
    GC.SuppressFinalize(this);
  }

  /// <summary>Deletes the temp database and workspace - the LAST thing the run does, after every
  ///     assertion that needs the persisted record has run. Best effort: a dying host may still
  ///     hold them, and a cleanup failure must never mask a real one.
  ///     <para>DELIBERATELY NOT a <c>ProcessExit</c> handler: registering one makes runtime exit
  ///     block waiting for foreground threads, which turns a fast test run into a two-minute
  ///     one and reports "foreground threads were left running" on a run where every test
  ///     passed (measured 2026-10-26).</para></summary>
  public Task DeleteRecordAsync()
  {
    DeleteQuietly(Path.GetDirectoryName(Database.DatabasePath));
    DeleteQuietly(WorkspaceRoot);
    return Task.CompletedTask;
  }

  /// <summary>Named decision (CA1031): temp cleanup is not part of any test's contract, and a
  ///     failure on the way out must never mask the assertion that already ran.</summary>
  private static void DeleteQuietly(string? path)
  {
    if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
    {
      return;
    }

#pragma warning disable CA1031 // Do not catch general exception types
    try
    {
      Directory.Delete(path, recursive: true);
    }
    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
    {
      // Deliberately ignored: see the named decision above.
    }
#pragma warning restore CA1031
  }

  /// <summary>The ledger's own record of the turn, as one line for a failure message.</summary>
  public static string Describe(UsageSummary usage) => string.Create(CultureInfo.InvariantCulture,
      $"{usage.Totals.Calls} call(s), {usage.Totals.InputTokens} input / {usage.Totals.OutputTokens} output tokens, "
      + $"{usage.Totals.PricedCalls} priced / {usage.Totals.UnpricedCalls} unpriced");
}
