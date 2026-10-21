using System.Reflection;
using System.Runtime.Loader;
using eThangAgent.CapabilityDomain;
using eThangAgent.Roslyn.ACL;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace ExecLeakProbe;

/// <summary>The THIRD leak vector: a script that passes an ANONYMOUS OBJECT to a
/// tool (<c>Tools.Invoke("read", new { path = "x" })</c>) - the most common
/// script call shape there is. The arguments are serialized by
/// ScriptTools.SerializeArguments, so the same serializer pins the
/// script-declared anonymous type.</summary>
internal static class ToolArgsProbe
{
  public static string Run()
  {
    CSharpScriptExecEngine engine = new(CapabilityRegistry.Create([new EchoProvider()]),
        workspaceRoot: () => AppContext.BaseDirectory);

    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    int before = Live();

    const int runs = 10;
    for (int i = 0; i < runs; i++)
    {
      ExecRunResult result = engine.ExecuteAsync(new ExecProgram(
          "return Tools.Invoke(\"echo\", new { timeoutSeconds = 30, key = \"k\" });")).GetAwaiter().GetResult();
      if (result.ErrorLines.Count > 0)
      {
        return "run failed: " + string.Join(" | ", result.ErrorLines);
      }
    }

    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    int after = Live();

    return $"toolArgsPath: liveCollectibleContexts before={before} after={after} leakedPerCall={((after - before) / (double)runs):F3}";
  }

  private static int Live()
  {
    HashSet<nint> contexts = [];
    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
    {
      if (assembly.GetName().Name?.Contains('#', StringComparison.Ordinal) != true)
      {
        continue;
      }

      AssemblyLoadContext? context = null;
      try
      {
        context = AssemblyLoadContext.GetLoadContext(assembly);
      }
      catch (InvalidOperationException)
      {
      }

      if (context is { IsCollectible: true })
      {
        _ = contexts.Add(context.GetHashCode());
      }
    }

    return contexts.Count;
  }

  /// <summary>A provider exposing one action the script can call.</summary>
  private sealed class EchoProvider : ICapabilityProvider
  {
    public string Id => "echo";

    public IReadOnlyList<ActionDescriptor> Actions =>
    [
      new ActionDescriptor(
        "echo",
        "Echo",
        "Echoes the arguments.",
        [new ActionParameter("timeoutSeconds", "WholeNumber", "The budget."),
         new ActionParameter("key", "Text", "A key.")]),
    ];

    public Task<CapabilityInvocationResult> InvokeAsync(
        string actionName, string jsonArguments, CancellationToken ct = default)
        => Task.FromResult(CapabilityInvocationResult.Ok("echoed"));
  }
}
