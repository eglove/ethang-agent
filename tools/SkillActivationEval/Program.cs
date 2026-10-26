// SkillActivationEval: the headless skill-activation eval runner (skill-routing Phase 1,
// Task 9). Drives ONE real agent session per prompts.json entry against a real provider,
// then extracts the skill_view tool calls the model made from the persisted transcript.
// The phase-4 after-baseline (plan Task 15) runs this OUTSIDE the test suite: a mock
// provider cannot measure real activation. Not part of eThangAgent.slnx by design.
//
// Usage: dotnet run --project tools/SkillActivationEval -- <workspaceRoot> <providerName> <outPath>
//   workspaceRoot  an EXISTING empty directory; one fresh subdirectory is created per prompt
//                  and each session is anchored there.
//   providerName   exactly 'OpenRouter' (passed through to the session factory's provider
//                  selection; z.ai and Local were retired with their providers).
//   outPath        where the JSON result array is written.
// The provider API key comes from the ETHANG_EVAL_KEY environment variable. Eval sessions
// persist in the runner's OWN app database, never in the interactive app's database.
//
// The runner SCORES its own rows (see EvalRow.Correct): an unscored row is not a result,
// and comparing output against prompts.json by hand is how a scoring bug survived a run.
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using eThangAgent.Agent.Application;
using eThangAgent.AgentDomain;
using eThangAgent.Composition;
using eThangAgent.SharedKernel;
using eThangAgent.Storage.ACL;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;

namespace SkillActivationEval;

internal static class Program
{
  private const int UsageExitCode = 2;
  private const int PromptHeadChars = 80;
  private const string EvalKeyVariable = "ETHANG_EVAL_KEY";
  private const string ModelVariable = "ETHANG_EVAL_MODEL";
  private const string EvalDatabaseFileName = "skill-activation-eval.db";
  private const string BudgetVariable = "ETHANG_EVAL_PROMPT_BUDGET_SECONDS";
  private const int DefaultPromptBudgetSeconds = 600;
  private const int MinPromptBudgetSeconds = 30;

  /// <summary>One self-contained extraction over persisted tool-call payloads. Stored
  ///     meta_json is System.Text.Json of MessageMeta; each ToolCall.Arguments value is
  ///     ITSELF a JSON string, so quotes inside it ride as multi-layer escapes
  ///     (backslash-u0022 stacked two or more deep in real rows - a historical session
  ///     invoked skills THROUGH exec, which nests one layer deeper). Unescape LOOPS
  ///     until the text stops changing, then matches the invocation in the forms it
  ///     actually takes: a JSON tool call ="name": "systematic-debugging"= (the key
  ///     quoted, after the layers are stripped) and a C# exec call
  ///     Tools.Invoke(="skill_view"=, new { name = ="brainstorming"= }). Sample of a
  ///     real stored fragment, one layer stripped (backslashes elided, the = marks the
  ///     outer string boundary): =Tools.Invoke("skill_view", new { name =
  ///     "brainstorming" })=. Bounded so a pathological row can never hang the run.</summary>
  private static readonly Regex JsonToolCallPattern = new(
      "skill_view.{0,200}?[\"']?name[\"']?\\s*[=:]\\s*\"(?<skill>[A-Za-z0-9._-]+)\"",
      RegexOptions.Compiled,
      TimeSpan.FromSeconds(1));

  private static readonly Regex ExecInvokePattern = new(
      "Tools\\.Invoke\\(\\s*[\"']skill_view[\"']\\s*,\\s*new\\s*\\{[^{}]*?name\\s*=\\s*\"(?<skill>[A-Za-z0-9._-]+)\"[^{}]*\\}\\s*\\)",
      RegexOptions.Compiled,
      TimeSpan.FromSeconds(1));

  /// <summary>Decodes one escape layer of a JSON payload: \uXXXX, the short forms
  ///     (\\, \" and friends), and nothing else. An unrecognized sequence passes
  ///     through verbatim - Regex.Unescape would THROW on the unknown escapes real
  ///     rows carry (file paths such as ..\name inside exec programs), so the
  ///     decoder here is total by construction.</summary>
  internal static string UnescapeOneLayer(string text)
  {
    StringBuilder sb = new(text.Length);
    int i = 0;
    while (i < text.Length)
    {
      int consumed = TryDecodeEscape(text, i, sb);
      if (consumed == 0)
      {
        _ = sb.Append(text[i]);
        i++;
        continue;
      }

      i += consumed;
    }

    return sb.ToString();
  }

  /// <summary>Decodes the escape sequence starting at <paramref name="index" />, appending
  ///     its decoded text to <paramref name="sb" /> and returning the characters consumed —
  ///     or 0 when the position is not an escape this decoder understands. An UNKNOWN
  ///     escape consumes both characters and passes them through verbatim: real rows carry
  ///     file paths such as ..\\name, and Regex.Unescape would throw on them, so the
  ///     decoder is total by construction.</summary>
  private static int TryDecodeEscape(string text, int index, StringBuilder sb)
  {
    if (text[index] != '\\' || index + 1 >= text.Length)
    {
      return 0;
    }

    char next = text[index + 1];
    if (TryDecodeUnicode(text, index, sb) is { } unicodeLength)
    {
      return unicodeLength;
    }

    _ = sb.Append(ShortForm(next));
    return 2;
  }

  /// <summary>The \\uXXXX form: four hex digits after 'u'. Returns the characters
  ///     consumed (6) when the sequence decodes, or null when it is not one.</summary>
  private static int? TryDecodeUnicode(string text, int index, StringBuilder sb)
  {
    if (text[index + 1] != 'u' || index + 5 >= text.Length
        || !int.TryParse(text.AsSpan(index + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
    {
      return null;
    }

    _ = sb.Append((char)code);
    return 6;
  }

  /// <summary>The single-character short forms. An UNKNOWN escape passes its character
  ///     through verbatim (real rows carry file paths such as ..\\name).</summary>
  private static char ShortForm(char escape) => escape switch
  {
    'n' => '\n',
    'r' => '\r',
    't' => '\t',
    _ => escape,
  };

  /// <summary>Strips escape layers until the text stops changing: real rows stack
  ///     backslash-u0022 two or more deep, and a single pass leaves inner layers
  ///     encoded. Bounded at 8 passes - far past any real depth.</summary>
  internal static string UnescapeLayers(string text)
  {
    string current = text;
    for (int i = 0; i < 8; i++)
    {
      string next = UnescapeOneLayer(current);
      if (next == current)
      {
        return current;
      }

      current = next;
    }

    return current;
  }


  /// <summary>Entry point: validates the exact three-argument interface, runs every
  ///     prompt sequentially, writes the JSON array to <paramref name="args"/>[2].</summary>
  public static async Task<int> Main(string[] args)
  {
    ArgumentNullException.ThrowIfNull(args);
    Console.OutputEncoding = Encoding.UTF8;
    if (args.Length != 3)
    {
      await PrintUsage().ConfigureAwait(false);
      return UsageExitCode;
    }

    string workspaceRoot = Path.GetFullPath(args[0]);
    string outPath = Path.GetFullPath(args[2]);
    string? resolved = ResolveProviderId(args[1]);
    if (await ValidateInvocationAsync(workspaceRoot, args[1], resolved).ConfigureAwait(false) is { } refusal)
    {
      await Console.Error.WriteLineAsync(refusal).ConfigureAwait(false);
      return UsageExitCode;
    }

    // ValidateInvocationAsync returned null, so the id resolved to a known provider.
    string providerId = resolved!;

    // The eval's OWN app database beside the executable - never the interactive app's
    // database, never in-memory (extraction reads the file after sessions are disposed).
    //
    // DELETED FIRST, and that is load-bearing: the file outlives a run, so a learned skill
    // or a workspace-scoped row from an EARLIER run silently joins the next one's catalog.
    // Measured: a stale learned skill ('debugging-without-artifacts') activated alongside
    // real ones and inflated a run's activation count with a skill that does not exist in
    // the repo. An eval whose inputs drift between runs measures nothing comparable.
    string databasePath = Path.Combine(AppContext.BaseDirectory, EvalDatabaseFileName);
    DeleteStaleDatabase(databasePath);
    AppDatabase database = new(databasePath);
    if (!IsConfigured(providerId))
    {
      await Console.Error.WriteLineAsync($"eval needs the {EvalKeyVariable} environment variable for provider '{args[1]}'.").ConfigureAwait(false);
      return UsageExitCode;
    }

    // A run with no model measures NOTHING (every prompt fails NoModelSelected), so this
    // is a refusal at the door rather than a table of identical failures that reads like a
    // finding. The harness never substitutes a model, and neither does the eval.
    if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ModelVariable)))
    {
      await Console.Error.WriteLineAsync($"eval needs the {ModelVariable} environment variable naming the model under test.").ConfigureAwait(false);
      return UsageExitCode;
    }

    AgentSettings settings = (await BuildSettingsAsync(providerId, database).ConfigureAwait(false))!;

    await RunAllAsync(workspaceRoot, providerId, settings, database, outPath).ConfigureAwait(false);
    return 0;
  }

  /// <summary>Runs every prompt sequentially and writes the scored JSON array. Sequential
  ///     on purpose: the eval measures activation, and parallel sessions would contend for
  ///     the provider's rate limit (measured: free tiers answer 429 under any concurrency).</summary>
  private static async Task RunAllAsync(string workspaceRoot, string providerId,
      AgentSettings settings, AppDatabase database, string outPath)
  {
    List<EvalPrompt> prompts = ReadPrompts();
    int budgetSeconds = ResolveBudgetSeconds();
    await Console.Out.WriteLineAsync($"per-prompt budget: {budgetSeconds}s (override with {BudgetVariable})").ConfigureAwait(false);
    List<EvalRow> rows = [];
    foreach (EvalPrompt prompt in prompts)
    {
      await Console.Error.WriteLineAsync($"prompt {prompt.Id} started (budget {budgetSeconds}s)").ConfigureAwait(false);
      EvalRow row = await RunPromptAsync(prompt, workspaceRoot, providerId, settings, database, budgetSeconds).ConfigureAwait(false);
      rows.Add(row);
      await Console.Out.WriteLineAsync(row.SummaryLine).ConfigureAwait(false);
    }

    string json = JsonSerializer.Serialize(rows, EvalJsonOptions);
    await File.WriteAllTextAsync(outPath, json, Encoding.UTF8).ConfigureAwait(false);
    await Console.Out.WriteLineAsync($"wrote {rows.Count} row(s) to {outPath}").ConfigureAwait(false);
    await PrintScoreAsync(rows).ConfigureAwait(false);
  }

  /// <summary>Runs ONE prompt in a fresh anchored session, disposes the session, and then
  ///     extracts the skill_view calls from that session's persisted transcript rows.
  ///     A failure on this prompt is reported per-row (empty skills plus an error field)
  ///     and never aborts the run.</summary>
  private static async Task<EvalRow> RunPromptAsync(EvalPrompt prompt, string evalRoot,
      string providerName, AgentSettings settings, AppDatabase database, int budgetSeconds)
  {
    string sessionWorkspace = Path.Combine(evalRoot, $"prompt-{prompt.Id}");
    _ = Directory.CreateDirectory(sessionWorkspace);
    AgentSessionFactory factory = new(settings, database);
    using CancellationTokenSource budget = new(TimeSpan.FromSeconds(budgetSeconds));
    List<AgentSession> createdSessions = [];
    AppDatabase? sessionDatabase = null;
    string? sessionId = null;
    string? error = null;
    try
    {
      Result<AgentSession> created = await factory.CreateAsync(sessionWorkspace, providerName).ConfigureAwait(false);
      if (!created.IsSuccess)
      {
        return new EvalRow(prompt.Id, Head(prompt.Prompt), [], prompt.Expected, prompt.ExpectNone, null, null, created.Error.Message);
      }

      // The container registers THE database the factory passed in; resolve it here so
      // extraction keeps working after disposal.
      AgentSession session = created.Value;
      sessionDatabase = session.Services.GetRequiredService<AppDatabase>();
      createdSessions.Add(session);
      sessionId = session.RootId.ToString();

      // The eval must NAME its model: the harness deliberately never substitutes one
      // (a session with no selection fails NoModelSelected), so an eval that does not set
      // the preference measures nothing at all. EVAL_MODEL is the runner's own knob -
      // the eval is an instrument, and the model under test is its input.
      string? evalModel = Environment.GetEnvironmentVariable(ModelVariable);
      if (!string.IsNullOrWhiteSpace(evalModel) && session.Preferences is not null)
      {
        session.Preferences.ModelId = evalModel;
      }
      Result<string> result;
      try
      {
        result = await session.Handler.Handle(new SendMessageCommand(prompt.Prompt), ct: budget.Token).ConfigureAwait(false);
      }
      catch (OperationCanceledException)
      {
        return new EvalRow(prompt.Id, Head(prompt.Prompt), [], prompt.Expected, prompt.ExpectNone, null, sessionId,
            $"prompt budget exhausted after {budgetSeconds}s - the turn was cancelled before completing");
      }
      if (!result.IsSuccess)
      {
        return new EvalRow(prompt.Id, Head(prompt.Prompt), [], prompt.Expected, prompt.ExpectNone, null, sessionId,
            $"[{result.Error.Code}] {result.Error.Message}");
      }

      // The handler does not persist the transcript (the Desktop view-model layer owns
      // RootSessionLifecycle.AppendExchangeAsync); a headless runner replicates that
      // contract here so the transcript rows exist for extraction and later resume.
      await session.Lifecycle.AppendExchangeAsync(
          session.RootId, session.Conversation, alreadyPersisted: 0, assistantText: AssistantText(result),
          reportError => Console.Error.WriteLine(reportError)).ConfigureAwait(false);
      await session.Lifecycle.CompleteAsync(session.RootId,
          reportError => Console.Error.WriteLine(reportError)).ConfigureAwait(false);
    }
    // Named decision (CA1031): one prompt's fault is a per-row report, never a run abort.
#pragma warning disable CA1031 // Do not catch general exception types
    catch (Exception ex)
    {
      error = ex.Message;
    }
#pragma warning restore CA1031 // Do not catch general exception types
    finally
    {
      foreach (AgentSession s in createdSessions)
      {
        await s.Services.DisposeAsync().ConfigureAwait(false);
      }
    }

    // Post-disposal extraction over the container's database: assistant rows whose
    // meta_json contains 'skill_view', plus the root row's model_used stamp.
    if (error is not null || sessionDatabase is null)
    {
      return new EvalRow(prompt.Id, Head(prompt.Prompt), [], prompt.Expected, prompt.ExpectNone, null, sessionId, error ?? "session was not created");
    }

    string modelUsed = await ReadModelUsedAsync(sessionDatabase, new AgentId(Guid.Parse(sessionId!, CultureInfo.InvariantCulture))).ConfigureAwait(false);
    List<string> activated = ExtractSkillViews(sessionDatabase, new AgentId(Guid.Parse(sessionId!, CultureInfo.InvariantCulture)));
    return new EvalRow(prompt.Id, Head(prompt.Prompt), activated, prompt.Expected, prompt.ExpectNone, modelUsed, sessionId, null);
  }

  /// <summary>Extracts the skill_view name arguments from the session's persisted
  ///     assistant messages. Assistant rows whose meta_json contains 'skill_view' carry
  ///     ToolCalls; each call's Arguments is itself a JSON string, so its quotes appear
  ///     once more escaped (backslash-u0022) inside meta_json. Unescape the layers first,
  ///     then collect the first name argument after each skill_view occurrence.
  ///     Self-contained on purpose: no shared code with the Tool Domain.</summary>
  private static List<string> ExtractSkillViews(AppDatabase database, AgentId sessionId)
  {
    using SqliteConnection connection = database.OpenReadOnly();
    using SqliteCommand command = connection.CreateCommand();
    command.CommandText = "SELECT meta_json FROM agent_messages WHERE agent_id = $id AND role = 'Assistant' AND meta_json LIKE '%skill_view%' ORDER BY seq;";
    _ = command.Parameters.AddWithValue("$id", sessionId.ToString());
    List<string> names = [];
    using SqliteDataReader reader = command.ExecuteReader();
    while (reader.Read())
    {
      string unescaped = UnescapeLayers(reader.GetString(0));
      foreach (Match match in JsonToolCallPattern.Matches(unescaped).Cast<Match>()
          .Concat(ExecInvokePattern.Matches(unescaped).Cast<Match>()))
      {
        string name = match.Groups["skill"].Value;
        if (!names.Contains(name))
        {
          names.Add(name);
        }
      }
    }
    return names;
  }

  /// <summary>Reads the root row's model_used stamp. Session creation stamps the resolved
  ///     bootstrap model; the root resolver updates it at selection and with a user model
  ///     choice. The sentinel 'unassigned' appears only on rows persisted before the
  ///     creation-time stamp or when no model was known at creation.</summary>
  private static async Task<string> ReadModelUsedAsync(AppDatabase database, AgentId sessionId)
  {
    SqliteAgentStore store = new(database);
    Result<AgentRecord> record = await store.GetAsync(sessionId).ConfigureAwait(false);
    return record.IsSuccess ? record.Value.ModelUsed : "unknown";
  }

  /// <summary>The eval's own configuration gate: an OpenRouter/zai run demands the
  ///     ETHANG_EVAL_KEY variable; a Local run demands its base URL variable. The
  ///     factory's own HasXxx gates cannot see a MISSING EVAL KEY (a null key keeps
  ///     the provider configured with credentials absent), so the runner checks its
  ///     own variables before any session is built.</summary>
  private static bool IsConfigured(string providerId) => providerId switch
  {
    Providers.OpenRouter =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EvalKeyVariable)),
    _ => false,
  };
  /// <summary>Maps the CLI's display spellings (OpenRouter | zai | Local - the brief's
  ///     exact interface) onto the provider ids the composition knows. Anything else is
  ///     null: the caller reports usage. Case-sensitive by contract.</summary>
  private static string? ResolveProviderId(string providerName) => providerName switch
  {
    "OpenRouter" => Providers.OpenRouter,
    _ => null,
  };
  /// <summary>Builds the settings the way the Desktop does (AgentSettingsLoader over app
  ///     preferences for every non-secret knob), then overlays the eval key from
  ///     ETHANG_EVAL_KEY through the same WithApiKeys seam the Desktop uses. Unknown
  ///     providers return null - the caller reports usage.</summary>
  private static async Task<AgentSettings?> BuildSettingsAsync(string providerName, AppDatabase database)
  {
    string? key = Environment.GetEnvironmentVariable(EvalKeyVariable);
    AgentSettings loaded = await AgentSettingsLoader.LoadAsync(new SqliteAppPreferenceStore(database)).ConfigureAwait(false);
    return providerName switch
    {
      Providers.OpenRouter => loaded.WithApiKeys(key),
      _ => null,
    };
  }

  private static List<EvalPrompt> ReadPrompts()
  {
    string promptsPath = Path.Combine(AppContext.BaseDirectory, "prompts.json");
    string json = File.ReadAllText(promptsPath);
    List<EvalPrompt>? parsed = JsonSerializer.Deserialize<List<EvalPrompt>>(json, EvalJsonOptions);
    return parsed ?? [];
  }

  /// <summary>The per-prompt hard deadline: a turn that wedges anywhere (provider,
  ///     network, console) becomes a per-row failure instead of a silent infinite
  ///     hang. Configurable for slow models; never unbounded.</summary>
  private static int ResolveBudgetSeconds()
  {
    string? raw = Environment.GetEnvironmentVariable(BudgetVariable);
    return int.TryParse(raw, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed) && parsed >= MinPromptBudgetSeconds
        ? parsed
        : DefaultPromptBudgetSeconds;
  }

  private static string Head(string text) =>
      text.Length <= PromptHeadChars ? text : text[..PromptHeadChars];

  /// <summary>Prints the accuracy summary the run exists to produce: overall, split
  ///     into task rows (a skill should activate) and abstention rows (none should).
  ///     The split is the point - a matcher that never abstains and a matcher that never
  ///     activates can post the same overall number and mean opposite things.</summary>
  private static async Task PrintScoreAsync(List<EvalRow> rows)
  {
    if (rows.Count == 0)
    {
      await Console.Out.WriteLineAsync("score: no rows").ConfigureAwait(false);
      return;
    }

    int taskTotal = rows.Count(r => !r.ExpectNone);
    int taskHit = rows.Count(r => !r.ExpectNone && r.Correct);
    int noneTotal = rows.Count(r => r.ExpectNone);
    int noneHit = rows.Count(r => r.ExpectNone && r.Correct);
    int hit = rows.Count(r => r.Correct);
    await Console.Out.WriteLineAsync().ConfigureAwait(false);
    await Console.Out.WriteLineAsync(string.Create(CultureInfo.InvariantCulture,
        $"score: {hit}/{rows.Count} correct ({100.0 * hit / rows.Count:F0}%)  tasks {taskHit}/{taskTotal}  abstain {noneHit}/{noneTotal}")).ConfigureAwait(false);
    foreach (EvalRow row in rows.Where(r => !r.Correct))
    {
      await Console.Out.WriteLineAsync("    FAIL " + row.SummaryLine).ConfigureAwait(false);
    }
  }

  /// <summary>The turn's final assistant text for the persistence contract. The runner has
  ///     no streaming surface, so the response's own text is the answer; an empty response
  ///     still satisfies the non-null contract (the slice, not this string, is what persists).</summary>
  private static string AssistantText(Result<string> result) =>
      result.IsSuccess ? result.Value ?? string.Empty : string.Empty;

  /// <summary>Deletes a previous run's eval database, retrying briefly. A file that stays
  ///     locked is REPORTED and the run continues on the existing database: a stale catalog
  ///     is a caveat on the results, never a reason to lose them.</summary>
  private static void DeleteStaleDatabase(string databasePath)
  {
    for (int attempt = 0; attempt < 5; attempt++)
    {
      try
      {
        if (!File.Exists(databasePath))
        {
          return;
        }

        File.Delete(databasePath);
        return;
      }
      catch (IOException) when (attempt < 4)
      {
        Thread.Sleep(400);
      }
      catch (IOException ex)
      {
        Console.Error.WriteLine($"warning: could not delete the previous eval database ({ex.Message}); results may include skills learned in an earlier run.");
        return;
      }
    }
  }

  /// <summary>The invocation checks Main used to inline: an unknown provider, a missing
  ///     workspace root, and a NON-EMPTY root (which would silently carry a previous run's
  ///     workspace-scoped state into the next). Returns the refusal text, or null when the
  ///     invocation is valid.</summary>
  private static Task<string?> ValidateInvocationAsync(string workspaceRoot, string providerName, string? providerId)
  {
    if (providerId is null)
    {
      return Task.FromResult<string?>($"unknown provider '{providerName}'. Known providers: OpenRouter.");
    }

    if (!Directory.Exists(workspaceRoot))
    {
      return Task.FromResult<string?>($"workspace root not found: '{workspaceRoot}'");
    }

    // The anchor must be an EXISTING EMPTY directory: one fresh subdirectory per prompt,
    // and a reused root would silently carry a previous run's workspace-scoped state.
    bool empty = !Directory.EnumerateFileSystemEntries(workspaceRoot).Any();
    return Task.FromResult(empty ? null : $"workspace root must be an empty directory: '{workspaceRoot}'");
  }

  private static async Task PrintUsage()
  {
    await Console.Out.WriteLineAsync("SkillActivationEval - headless skill-activation eval runner");
    await Console.Out.WriteLineAsync();
    await Console.Out.WriteLineAsync("usage: dotnet run --project tools/SkillActivationEval -- <workspaceRoot> <providerName> <outPath>");
    await Console.Out.WriteLineAsync("  workspaceRoot  existing EMPTY directory; one fresh subdirectory is created per prompt");
    await Console.Out.WriteLineAsync("  providerName   'OpenRouter' (the only wired provider)");
    await Console.Out.WriteLineAsync("  outPath        path of the JSON result file to write");
    await Console.Out.WriteLineAsync($"  API key        {EvalKeyVariable} environment variable");
    await Console.Out.WriteLineAsync($"  model          {ModelVariable} environment variable (REQUIRED: the harness never substitutes a model, so an unset model measures nothing)");
    await Console.Out.WriteLineAsync($"  per-prompt cap {BudgetVariable} seconds (default {DefaultPromptBudgetSeconds}; minimum {MinPromptBudgetSeconds})");
  }

  private static JsonSerializerOptions EvalJsonOptions { get; } = new()
  {
    PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
    DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    WriteIndented = true,
  };
}
/// <summary>One prompts.json entry (Task 1's eval prompt set). Expected skills and the
///     expectNone flag ride along for the after-baseline's comparison; the runner itself
///     only needs the id and the prompt text, and reports what actually activated.</summary>
// Named decision (CA1812): JsonSerializer materializes EvalPrompt instances during
// deserialization - the analyzer cannot see that construction site.
#pragma warning disable CA1812 // Avoid uninstantiated internal classes
internal sealed record EvalPrompt(string Id, string Prompt, string[] Expected, bool ExpectNone);
#pragma warning restore CA1812 // Avoid uninstantiated internal classes

/// <summary>One output row: the JSON entry shape the brief pins, plus the score.
///     <para>The runner SCORES because an unscored row is not a result: a reader had to
///     compare every line against prompts.json by hand, which is how a scoring bug (an
///     empty-string 'no skill' compared against null) survived a whole run and reported
///     0/5 abstentions while the transcript showed NONE on all five.</para></summary>
internal sealed record EvalRow(
    string Id,
    string Prompt,
    IReadOnlyList<string> ActivatedSkills,
    IReadOnlyList<string> ExpectedSkills,
    bool ExpectNone,
    string? ModelUsed,
    string? SessionId,
    string? Error)
{
  /// <summary>Whether this row matched its expectation. A task row is correct when ANY
  ///     expected skill activated (the prompt set lists alternatives where more than one
  ///     answer is defensible); an abstention row is correct when NOTHING activated. An
  ///     errored row is never correct.</summary>
  [JsonIgnore]
  public bool Correct => Error is null && (ExpectNone
      ? ActivatedSkills.Count == 0
      : ActivatedSkills.Any(a => ExpectedSkills.Contains(a, StringComparer.OrdinalIgnoreCase)));

  /// <summary>The one-line console table row: id | PASS/fail | got | want | model.</summary>
  [JsonIgnore]
  public string SummaryLine => string.Create(CultureInfo.InvariantCulture,
      $"{Id} | {(Correct ? "PASS" : "fail")} | got {(ActivatedSkills.Count == 0 ? "-" : string.Join(",", ActivatedSkills))} | want {(ExpectNone ? "NONE" : string.Join(",", ExpectedSkills))} | {ModelUsed ?? "-"}{(Error is null ? string.Empty : " | error: " + Error)}");
}
