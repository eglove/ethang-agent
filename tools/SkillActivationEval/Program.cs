// SkillActivationEval: the headless skill-activation eval runner (skill-routing Phase 1,
// Task 9). Drives ONE real agent session per prompts.json entry against a real provider,
// then extracts the skill_view tool calls the model made from the persisted transcript.
// The phase-4 after-baseline (plan Task 15) runs this OUTSIDE the test suite: a mock
// provider cannot measure real activation. Not part of eThangAgent.slnx by design.
//
// Usage: dotnet run --project tools/SkillActivationEval -- <workspaceRoot> <providerName> <outPath>
//   workspaceRoot  an EXISTING empty directory; one fresh subdirectory is created per prompt
//                  and each session is anchored there.
//   providerName   exactly 'OpenRouter' | 'zai' | 'Local' (passed through to the session
//                  factory's provider selection).
//   outPath        where the JSON result array is written.
// The provider API key comes from the ETHANG_EVAL_KEY environment variable (OpenRouter/zai;
// Local needs none). Eval sessions persist in the runner's OWN app database, never in the
// interactive app's database.
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
  private const string EvalDatabaseFileName = "skill-activation-eval.db";
  private const string LocalBaseUrlVariable = "ETHANG_EVAL_LOCAL_BASE_URL";

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
      if (text[i] != '\\' || i + 1 >= text.Length)
      {
        _ = sb.Append(text[i]);
        i++;
        continue;
      }

      char next = text[i + 1];
      if (next == 'u' && i + 5 < text.Length
          && int.TryParse(text.AsSpan(i + 2, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out int code))
      {
        _ = sb.Append((char)code);
        i += 6;
        continue;
      }

      if (next is '\\' or '\"' or '\'' or 'n' or 'r' or 't')
      {
        _ = sb.Append(next switch
        {
          'n' => '\n',
          'r' => '\r',
          't' => '\t',
          _ => next,
        });
        i += 2;
        continue;
      }

      // Unknown escape (real rows carry file paths such as ..\\name): verbatim pass.
      _ = sb.Append(text[i]);
      _ = sb.Append(next);
      i += 2;
    }

    return sb.ToString();
  }

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
    string? providerId = ResolveProviderId(args[1]);
    string outPath = Path.GetFullPath(args[2]);
    if (providerId is null)
    {
      await Console.Error.WriteLineAsync($"unknown provider '{args[1]}'. Known providers: OpenRouter, zai, Local.");
      return UsageExitCode;
    }

    if (!Directory.Exists(workspaceRoot))
    {
      await Console.Error.WriteLineAsync($"workspace root not found: '{workspaceRoot}'");
      return UsageExitCode;
    }

    // The anchor must be an EXISTING EMPTY directory: one fresh subdirectory per prompt,
    // and a reused root would silently carry a previous run's workspace-scoped state.
    if (Directory.EnumerateFileSystemEntries(workspaceRoot).Any())
    {
      await Console.Error.WriteLineAsync($"workspace root must be an empty directory: '{workspaceRoot}'");
      return UsageExitCode;
    }

    // The eval's OWN app database beside the executable - never the interactive app's
    // database, never in-memory (extraction reads the file after sessions are disposed).
    AppDatabase database = new(Path.Combine(AppContext.BaseDirectory, EvalDatabaseFileName));
    if (!IsConfigured(providerId))
    {
      await Console.Error.WriteLineAsync(providerId == Providers.Local
          ? $"Local eval needs the {LocalBaseUrlVariable} environment variable (an OpenAI-compatible base URL)."
          : $"eval needs the {EvalKeyVariable} environment variable for provider '{args[1]}'.");
      return UsageExitCode;
    }

    AgentSettings settings = (await BuildSettingsAsync(providerId, database).ConfigureAwait(false))!;

    List<EvalPrompt> prompts = ReadPrompts();
    List<EvalRow> rows = [];
    foreach (EvalPrompt prompt in prompts)
    {
      EvalRow row = await RunPromptAsync(prompt, workspaceRoot, providerId, settings, database).ConfigureAwait(false);
      rows.Add(row);
      await Console.Out.WriteLineAsync(row.SummaryLine);
    }

    string json = JsonSerializer.Serialize(rows, EvalJsonOptions);
    await File.WriteAllTextAsync(outPath, json, Encoding.UTF8).ConfigureAwait(false);
    await Console.Out.WriteLineAsync($"wrote {rows.Count} row(s) to {outPath}");
    return 0;
  }

  /// <summary>Runs ONE prompt in a fresh anchored session, disposes the session, and then
  ///     extracts the skill_view calls from that session's persisted transcript rows.
  ///     A failure on this prompt is reported per-row (empty skills plus an error field)
  ///     and never aborts the run.</summary>
  private static async Task<EvalRow> RunPromptAsync(EvalPrompt prompt, string evalRoot,
      string providerName, AgentSettings settings, AppDatabase database)
  {
    string sessionWorkspace = Path.Combine(evalRoot, $"prompt-{prompt.Id}");
    _ = Directory.CreateDirectory(sessionWorkspace);
    AgentSessionFactory factory = new(settings, database);
    List<AgentSession> createdSessions = [];
    AppDatabase? sessionDatabase = null;
    string? sessionId = null;
    string? error = null;
    try
    {
      Result<AgentSession> created = await factory.CreateAsync(sessionWorkspace, providerName).ConfigureAwait(false);
      if (!created.IsSuccess)
      {
        return new EvalRow(prompt.Id, Head(prompt.Prompt), [], null, null, created.Error.Message);
      }

      // The container registers THE database the factory passed in; resolve it here so
      // extraction keeps working after disposal.
      AgentSession session = created.Value;
      sessionDatabase = session.Services.GetRequiredService<AppDatabase>();
      createdSessions.Add(session);
      sessionId = session.RootId.ToString();
      Result<string> result = await session.Handler.Handle(new SendMessageCommand(prompt.Prompt)).ConfigureAwait(false);
      if (!result.IsSuccess)
      {
        return new EvalRow(prompt.Id, Head(prompt.Prompt), [], null, sessionId,
            $"[{result.Error.Code}] {result.Error.Message}");
      }

      // The handler does not persist the transcript (the Desktop view-model layer owns
      // RootSessionLifecycle.AppendExchangeAsync); a headless runner replicates that
      // contract here so the transcript rows exist for extraction and later resume.
      await session.Lifecycle.AppendExchangeAsync(
          session.RootId, session.Conversation, messageCountBefore: 0, result,
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
      return new EvalRow(prompt.Id, Head(prompt.Prompt), [], null, sessionId, error ?? "session was not created");
    }

    string modelUsed = await ReadModelUsedAsync(sessionDatabase, new AgentId(Guid.Parse(sessionId!, CultureInfo.InvariantCulture))).ConfigureAwait(false);
    List<string> activated = ExtractSkillViews(sessionDatabase, new AgentId(Guid.Parse(sessionId!, CultureInfo.InvariantCulture)));
    return new EvalRow(prompt.Id, Head(prompt.Prompt), activated, modelUsed, sessionId, null);
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

  /// <summary>Reads the root row's model_used stamp (the resolver writes it on selection;
  ///     'unassigned' means no model was ever persisted for the session).</summary>
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
    Providers.OpenRouter or Providers.Zai =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(EvalKeyVariable)),
    Providers.Local =>
        !string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LocalBaseUrlVariable)),
    _ => false,
  };
  /// <summary>Maps the CLI's display spellings (OpenRouter | zai | Local - the brief's
  ///     exact interface) onto the provider ids the composition knows. Anything else is
  ///     null: the caller reports usage. Case-sensitive by contract.</summary>
  private static string? ResolveProviderId(string providerName) => providerName switch
  {
    "OpenRouter" => Providers.OpenRouter,
    "zai" => Providers.Zai,
    "Local" => Providers.Local,
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
      Providers.OpenRouter => loaded.WithApiKeys(key, null),
      Providers.Zai => loaded.WithApiKeys(null, key),
      Providers.Local => loaded.WithLocalSettings(Environment.GetEnvironmentVariable(LocalBaseUrlVariable), key),
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

  private static string Head(string text) =>
      text.Length <= PromptHeadChars ? text : text[..PromptHeadChars];

  private static async Task PrintUsage()
  {
    await Console.Out.WriteLineAsync("SkillActivationEval - headless skill-activation eval runner");
    await Console.Out.WriteLineAsync();
    await Console.Out.WriteLineAsync("usage: dotnet run --project tools/SkillActivationEval -- <workspaceRoot> <providerName> <outPath>");
    await Console.Out.WriteLineAsync("  workspaceRoot  existing EMPTY directory; one fresh subdirectory is created per prompt");
    await Console.Out.WriteLineAsync("  providerName   'OpenRouter' | 'zai' | 'Local'");
    await Console.Out.WriteLineAsync("  outPath        path of the JSON result file to write");
    await Console.Out.WriteLineAsync($"  API key        {EvalKeyVariable} environment variable (OpenRouter/zai; Local needs none)");
    await Console.Out.WriteLineAsync($"  local server   {LocalBaseUrlVariable} environment variable (Local provider's OpenAI-compatible base URL)");
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

/// <summary>One output row: the JSON entry shape the brief pins.</summary>
internal sealed record EvalRow(
    string Id,
    string Prompt,
    IReadOnlyList<string> ActivatedSkills,
    string? ModelUsed,
    string? SessionId,
    string? Error)
{
  /// <summary>The one-line console table row: id | activated | model.</summary>
  [JsonIgnore]
  public string SummaryLine => string.Create(CultureInfo.InvariantCulture,
      $"{Id} | {(ActivatedSkills.Count == 0 ? "-" : string.Join(",", ActivatedSkills))} | {ModelUsed ?? "-"}{(Error is null ? string.Empty : " | error: " + Error)}");
}
