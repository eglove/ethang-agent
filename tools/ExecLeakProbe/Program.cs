// ExecLeakProbe: the measurement instrument for the exec-engine memory leak
// (2026-10-19 incident: 15 GB working set, 9.3M handles, ~200 exec calls).
// Runs the REAL CSharpScriptExecEngine N times and reports, after each run:
// live heap, working set, OS handle count, the collectible ALCs still holding a
// submission assembly (the leak signature), and the load-context census.
//
// Usage: dotnet run --project tools/ExecLeakProbe -- [--runs N] [--tool]
//   --tool  drive the REAL exec TOOL path (validate + run) instead of calling
//           the engine directly - the tool path compiles the script TWICE.
using System.Diagnostics;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using eThangAgent.CapabilityDomain;
using eThangAgent.Roslyn.ACL;
using eThangAgent.ToolDomain;

namespace ExecLeakProbe;

internal static class Program
{
  private const string RealisticScript =
      "using System.Diagnostics; var p = Process.GetCurrentProcess(); return p.Id;";

  /// <summary>The DECISIVE variant: the script returns an ANONYMOUS TYPE, which
  /// is declared IN the submission assembly (the collectible ALC). A serializer
  /// that caches that Type in a static, non-collectible cache pins the whole
  /// context forever - the leak the 2026-10-19 incident measured.</summary>
  private const string AnonymousTypeScript =
      "return new { count = 5, name = \"alpha\" };";

  private sealed record Snapshot(
      int Run, long LiveHeapBytes, long WorkingSetBytes, int Handles, int Threads,
      int CollectibleSubmissionAlcs, int LiveSubmissionAssemblies, int TotalAlcs,
      int AllAssemblies, int Gen2Collections);

  private static int Main(string[] args)
  {
    int runs = 30;
    bool toolMode = false;
    bool anonMode = false;
    for (int i = 0; i < args.Length; i++)
    {
      if (args[i] == "--runs" && i + 1 < args.Length && int.TryParse(args[i + 1], out int parsed))
      {
        runs = parsed;
      }

      if (args[i] == "--tool")
      {
        toolMode = true;
      }

      if (args[i] == "--anon")
      {
        anonMode = true;
      }

      if (args[i] == "--strategy")
      {
        Console.WriteLine(StrategyProbe.Run(args[i + 1]));
        return 0;
      }

      if (args[i] == "--toolargs")
      {
        Console.WriteLine(ToolArgsProbe.Run());
        return 0;
      }

      if (args[i] == "--strategy-hold")
      {
        Console.WriteLine(StrategyProbe.Run(args[i + 1]));
        Console.Out.Flush();
        Console.WriteLine("pid=" + Environment.ProcessId);
        Console.Out.Flush();
        Thread.Sleep(120_000);
        return 0;
      }
    }

    Process self = Process.GetCurrentProcess();
    CSharpScriptExecEngine engine = new(CapabilityRegistry.Create([]),
        workspaceRoot: () => AppContext.BaseDirectory);
    ExecTool tool = new(engine, ExecOptions.Default, new NullExecActivitySink());

    Console.WriteLine("pid=" + self.Id + " runs=" + runs + " mode=" + (toolMode ? "tool" : "engine"));
    Console.WriteLine(AlcProbe.Census());
    Console.WriteLine("run,liveHeapMB,workingSetMB,handles,threads,collectibleSubmissionAlcs,liveSubmissionAssemblies,totalAlcs,allAssemblies,gen2GCs");

    Snapshot first = Capture(0);
    Console.WriteLine(Format(first));

    for (int i = 1; i <= runs; i++)
    {
      if (toolMode)
      {
        string json = JsonSerializer.Serialize(new
        {
          program = anonMode ? AnonymousTypeScript : RealisticScript,
          title = "probe",
          timeoutSeconds = 60,
        });
        ToolResult toolResult = tool.ExecuteAsync(new RawToolInput("exec", json)).GetAwaiter().GetResult();
        if (toolResult.IsError)
        {
          Console.Error.WriteLine("run " + i + " tool error: " + toolResult.Content);
          return 1;
        }
      }
      else
      {
        ExecRunResult result = engine.ExecuteAsync(new ExecProgram(anonMode ? AnonymousTypeScript : RealisticScript)).GetAwaiter().GetResult();
        if (result.Status != ExecRunStatus.Completed)
        {
          Console.Error.WriteLine("run " + i + " did not complete: " + string.Join(" | ", result.ErrorLines));
          return 1;
        }
      }

      Snapshot snap = Capture(i);
      Console.WriteLine(Format(snap));
    }

    // A forced, aggressive, compacting gen2 collect plus a finalizer wait plus a
    // second collect: if the leak were GARBAGE this reclaims it; if it is LIVE
    // (rooted), the numbers barely move. The incident's own diagnostic.
    Console.WriteLine("--- forced aggressive gen2 collect ---");
    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    Snapshot after = Capture(runs + 1);
    Console.WriteLine(Format(after));
    Console.WriteLine(AlcProbe.Census());

    Snapshot last = Capture(runs + 2);
    Console.WriteLine("--- per-call deltas (last run vs first run) ---");
    Console.WriteLine("liveHeapMB/call      = " + ((last.LiveHeapBytes - first.LiveHeapBytes) / 1048576.0 / runs).ToString("F3"));
    Console.WriteLine("workingSetMB/call    = " + ((last.WorkingSetBytes - first.WorkingSetBytes) / 1048576.0 / runs).ToString("F3"));
    Console.WriteLine("handles/call         = " + ((last.Handles - first.Handles) / (double)runs).ToString("F3"));
    Console.WriteLine("threads/call         = " + ((last.Threads - first.Threads) / (double)runs).ToString("F3"));
    Console.WriteLine("collectibleAlcs/call = " + ((last.CollectibleSubmissionAlcs - first.CollectibleSubmissionAlcs) / (double)runs).ToString("F3"));
    Console.WriteLine("liveSubmissions/call = " + ((last.LiveSubmissionAssemblies - first.LiveSubmissionAssemblies) / (double)runs).ToString("F3"));
    Console.WriteLine("--- after forced collect (live = rooted, garbage = collectable) ---");
    Console.WriteLine("liveHeapMB before/after collect = " + (last.LiveHeapBytes / 1048576.0).ToString("F1") + " -> " + (after.LiveHeapBytes / 1048576.0).ToString("F1"));
    Console.WriteLine("collectibleSubmissionAlcs before/after = " + last.CollectibleSubmissionAlcs + " -> " + after.CollectibleSubmissionAlcs);
    return 0;
  }

  private static string Format(Snapshot s) => string.Join(",",
      s.Run,
      (s.LiveHeapBytes / 1048576.0).ToString("F2"),
      (s.WorkingSetBytes / 1048576.0).ToString("F2"),
      s.Handles,
      s.Threads,
      s.CollectibleSubmissionAlcs,
      s.LiveSubmissionAssemblies,
      s.TotalAlcs,
      s.AllAssemblies,
      s.Gen2Collections);

  private static Snapshot Capture(int run)
  {
    Process self = Process.GetCurrentProcess();
    self.Refresh();
    var collectibleAlcs = new HashSet<nint>();
    int liveSubmissions = 0;
    int collectibleSubmissionAlcs = 0;
    Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
    foreach (Assembly assembly in assemblies)
    {
      bool isSubmission = assembly.GetName().Name?.Contains('#') == true;
      AssemblyLoadContext? context = null;
      try
      {
        context = AssemblyLoadContext.GetLoadContext(assembly);
      }
      catch (InvalidOperationException)
      {
        // A dynamic assembly can throw while being named; not a retention signal.
      }

      if (context is { IsCollectible: true })
      {
        collectibleAlcs.Add(context.GetHashCode());
        if (isSubmission)
        {
          collectibleSubmissionAlcs++;
          liveSubmissions++;
        }
      }
    }

    return new Snapshot(run,
        GC.GetTotalMemory(false),
        self.WorkingSet64,
        self.HandleCount,
        self.Threads.Count,
        collectibleSubmissionAlcs,
        liveSubmissions,
        collectibleAlcs.Count,
        assemblies.Length,
        GC.CollectionCount(2));
  }

  /// <summary>A sink that drops the activity records - the probe measures the
  ///  engine, not the activity ledger.</summary>
  private sealed class NullExecActivitySink : IExecActivitySink
  {
    public Task RecordAsync(ExecActivity activity, CancellationToken ct = default) => Task.CompletedTask;
  }
}
