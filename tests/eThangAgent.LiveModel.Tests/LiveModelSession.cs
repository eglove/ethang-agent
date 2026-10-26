using System.Globalization;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;
using Xunit;

namespace eThangAgent.LiveModel.Tests;

/// <summary>What ONE candidate attempt produced: the artifacts when the model served, or the
///     failure, plus whether that failure was the PROVIDER REFUSING THE MODEL (walk on) or the
///     harness/provider failing (stop and report).</summary>
internal sealed record LiveModelAttempt(LiveRunArtifacts? Artifacts, string? Failure, bool IsModelRefusal)
{
  public bool Served => Artifacts is not null;
}

/// <summary>The ONE live session the Tier-1 tests share.
///     <para>WHY ONE SESSION, SHARED: a real free model queues, so every turn costs wall-clock
///     time and the whole suite runs under a bounded CI job. The contract assertions are
///     cumulative facts about one run, so they are collected once and read by five tests.
///     The <see cref="Lazy{T}"/> also keeps an OFFLINE or UNCONFIGURED run cheap: the first
///     awaiter skips before any session is built.</para>
///     <para>THE MODEL IS WALKED, NOT PINNED. The selector ranks the catalog's free models; the
///     suite then asks them IN ORDER until one actually serves. That is not defensive padding -
///     it is the measured reality: on 2026-10-26 the catalog's best-scoring free model refused
///     every request with HTTP 403 ("only available on agentic harnesses"), a fact about the
///     provider's gating policy that no catalog row can express. Each refusal is RECORDED and
///     named in later failures, so a run stays attributable to the model that answered.</para>
///     <para>THE SEQUENCE asks for exactly the behaviors Tier 1 asserts: a file change through
///     exec (the change report), a deliberately invalid tool call (the typed error), and a
///     plain answer (a settled turn). It asks in plain language because the model is not this
///     repository's code - what the harness GUARANTEES is the shape of the results.</para></summary>
internal static class LiveModelSession
{
  private const string ChangeAndRefusePrompt =
      "Use exec to write a file named hello.txt in the workspace containing the single word hello. "
      + "Then call a tool that does not exist anywhere in this harness, exactly once, with a nonsense "
      + "argument, and read the error it returns. Do not try to work around the error afterwards.";

  private const string AnswerPrompt =
      "In one short sentence, say what you just did.";

  /// <summary>How many ranked free models the suite will try before giving up. Small on
  ///     purpose: every attempt costs a session and two turns, and a run that has to walk past
  ///     three refusals is reporting a provider-policy change worth reading about.</summary>
  private const int MaxCandidateAttempts = 4;

  private static readonly Lazy<Task<LiveRunArtifacts>> Shared =
      new(RunOnceAsync, LazyThreadSafetyMode.ExecutionAndPublication);

  /// <summary>The factory of the session that served, exposed so the resume test reopens the
  ///     SAME session from its persisted record. Null until the sequence has run.</summary>
  public static AgentSessionFactory? Factory { get; private set; }

  /// <summary>The run whose temp database and workspace must outlive the assertions that read its
  ///     persisted record. Deleted by <see cref="DeleteRecordAsync"/> at the very end.</summary>
  public static LiveRun? RecordToDelete { get; private set; }

  /// <summary>Deletes the temp record of the run that served. Idempotent: every test that has
  ///     finished with the record calls it, and the first call wins.</summary>
  public static async Task DeleteRecordAsync()
  {
    if (RecordToDelete is not null)
    {
      await RecordToDelete.DeleteRecordAsync().ConfigureAwait(false);
      RecordToDelete = null;
    }
  }

  /// <summary>Runs (once) and returns the shared artifacts.</summary>
  public static Task<LiveRunArtifacts> RunSequenceAsync() => Shared.Value;

  private static async Task<LiveRunArtifacts> RunOnceAsync()
  {
    // Skip FIRST, before any session or network work: an unconfigured machine must not pay
    // for a catalog crawl it will abandon.
    _ = LiveModelEnvironment.RequireApiKey();
    CancellationToken ct = TestContext.Current.CancellationToken;

    Result<IReadOnlyList<ModelProviderEntry>> candidates =
        await LiveModelCatalog.CandidatesAsync().ConfigureAwait(false);
    Assert.True(candidates.IsSuccess, candidates.IsSuccess ? string.Empty
        : $"the live catalog produced no usable free model: {candidates.Error.Message}");

    List<LiveModelRejection> rejections = [];
    string? lastFailure = null;
    foreach (ModelProviderEntry candidate in candidates.Value.Take(MaxCandidateAttempts))
    {
      LiveModelChoice choice = new(candidate.ModelId, candidate.ContextLength, candidate.CodingScore,
          candidate.IntelligenceScore, candidate.AgenticScore, [.. rejections]);
      LiveModelAttempt attempt = await TryCandidateAsync(choice, ct).ConfigureAwait(false);
      if (attempt.Served)
      {
        return attempt.Artifacts!;
      }

      lastFailure = attempt.Failure;
      if (!attempt.IsModelRefusal)
      {
        // The turn failed for a NON-model reason (a harness fault, or the transport after its
        // one retry). Walking to another model would hide it, so stop and report.
        Assert.Fail(string.Create(CultureInfo.InvariantCulture,
            $"the live turn failed on {choice.Describe()}: {attempt.Failure}"));
      }

      rejections.Add(new LiveModelRejection(candidate.ModelId, attempt.Failure ?? "refused"));
    }

    string summary = rejections.Count == 0
        ? "none"
        : string.Join("; ", rejections.Select(r => r.Describe()));
    Assert.Fail(string.Create(CultureInfo.InvariantCulture,
        $"no free model served within {MaxCandidateAttempts} attempts. Refusals: {summary}. "
        + $"Last failure: {lastFailure ?? "none"}"));
    throw new InvalidOperationException("unreachable: Assert.Fail always throws");
  }

  /// <summary>Opens a session on ONE candidate, drives the two turns, collects the artifacts,
  ///     and tears the session (and its ChildHost) down on every path - a refusal, a failure,
  ///     and a success alike.</summary>
  private static async Task<LiveModelAttempt> TryCandidateAsync(LiveModelChoice choice, CancellationToken ct)
  {
    LiveRun run = await LiveRun.StartAsync(choice, ct).ConfigureAwait(false);
    try
    {
      Result<string> assistant = await RunTurnsAsync(run, ct).ConfigureAwait(false);
      if (!assistant.IsSuccess)
      {
        return new LiveModelAttempt(null,
            $"[{assistant.Error.Code}] {assistant.Error.Message}", LiveRun.IsModelRefusal(assistant.Error));
      }

      Factory = new AgentSessionFactory(
          new AgentSettings(
              new OpenRouterSettings(LiveModelEnvironment.ApiKey, LiveModelEnvironment.BaseUrl),
              new SubAgentOptions(run.Model.ModelId, 2)),
          run.Database);
      List<string> persistenceErrors = [];
      LiveRunArtifacts artifacts = await run.CollectAsync(assistant.Value, persistenceErrors)
          .ConfigureAwait(false);
      // The record (database + workspace) is deleted AFTER the resume assertion, not here:
      // the suite's last test reopens this session from it.
      RecordToDelete = run;
      return new LiveModelAttempt(artifacts, null, IsModelRefusal: false);
    }
    finally
    {
      await run.DisposeAsync().ConfigureAwait(false);
    }
  }

  /// <summary>Two turns: the behavior-collecting one, then a plain answer. A transport fault is
  ///     retried once inside <see cref="LiveRun.RunTurnAsync(string, CancellationToken)"/>;
  ///     anything else is returned so the caller can decide whether to walk on.</summary>
  private static async Task<Result<string>> RunTurnsAsync(LiveRun run, CancellationToken ct)
  {
    Result<string> first = await run.RunTurnAsync(ChangeAndRefusePrompt, ct).ConfigureAwait(false);
    return first.IsSuccess
        ? await run.RunTurnAsync(AnswerPrompt, ct).ConfigureAwait(false)
        : first;
  }
}
