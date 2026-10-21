using System.Reflection;
using System.Runtime.Loader;

namespace ExecLeakProbe;

internal static class AlcProbe
{
  /// <summary>A census of the process's load contexts: how many are still
  /// ALIVE (referenced), how many hold a submission assembly, and how many
  /// are non-collectible (the shape that can never be reclaimed).</summary>
  public static string Census()
  {
    int subsInCollectible = 0;
    var ctxs = new HashSet<nint>();
    foreach (Assembly assembly in AppDomain.CurrentDomain.GetAssemblies())
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
        ctxs.Add(context.GetHashCode());
        if (isSubmission)
        {
          subsInCollectible++;
        }
      }
    }

    List<AssemblyLoadContext> all = [.. AssemblyLoadContext.All];
    int collectible = all.Count(c => c.IsCollectible);
    int nonCollectible = all.Count - collectible;
    int emptyNonCollectible = all.Count(c => !c.IsCollectible && !c.Assemblies.Any());
    return $"subsInCollectible={subsInCollectible} distinctCollectibleCtx={ctxs.Count} ALC.All.total={all.Count} collectible={collectible} nonCollectible={nonCollectible} emptyNonCollectible={emptyNonCollectible}";
  }
}
