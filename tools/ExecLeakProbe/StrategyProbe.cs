// StrategyProbe: the decisive micro-experiment for the exec-engine leak.
// It builds a type INSIDE a collectible AssemblyLoadContext (the shape of a
// script's anonymous type), serializes an instance of it with a candidate
// serializer configuration, drops every reference, forces an aggressive gen2
// collect, and reports whether the context was actually reclaimed.
//
// A strategy that leaves the context ALIVE is a leak: the serializer's cache
// holds the Type strongly, the Type's m_keepalive holds the LoaderAllocator,
// and the LoaderAllocator holds the collectible assembly - forever.
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Emit;

namespace ExecLeakProbe;

internal static class StrategyProbe
{
  private static readonly JsonSerializerOptions SharedStatic = new(JsonSerializerDefaults.General);

  /// <summary>Runs one strategy and reports whether the collectible context was
  ///  reclaimed after a forced aggressive collect.</summary>
  public static string Run(string strategy)
  {
    WeakReference contextRef = RunScoped(strategy, out string json);

    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);
    GC.WaitForPendingFinalizers();
    GC.Collect(2, GCCollectionMode.Aggressive, blocking: true, compacting: true);

    bool reclaimed = !contextRef.IsAlive;
    return $"strategy={strategy} reclaimed={(reclaimed ? "YES" : "NO-LEAKED")} json={json}";
  }

  /// <summary>Does the work inside a scope that ends before the collect, so no
  ///  local reference can keep the context alive.</summary>
  private static WeakReference RunScoped(string strategy, out string json)
  {
    AssemblyLoadContext context = new("probe-" + Guid.NewGuid().ToString("N"), isCollectible: true);
    WeakReference contextRef = new(context);
    Assembly assembly = LoadProbeAssembly(context);
    Type payloadType = assembly.GetType("Probe.Payload", throwOnError: true)!;
    object instance = Activator.CreateInstance(payloadType)!;
    json = Serialize(strategy, instance, payloadType);
    context.Unload();
    return contextRef;
  }

  private static string Serialize(string strategy, object instance, Type type)
  {
    switch (strategy)
    {
      case "none":
        // BASELINE: no serialization at all. If the context leaks here too, the
        // leak is the ALC mechanics, not the serializer.
        return "(no serialization)";
      case "tostring":
        return instance.ToString() ?? "";
      case "touch-type":
        // BASELINE: reflect over the type WITHOUT serializing it.
        return string.Join(",", type.GetProperties().Select(p => p.Name));
      case "default":
        return JsonSerializer.Serialize(instance);
      case "shared":
        return JsonSerializer.Serialize(instance, type, SharedStatic);
      case "fresh-options":
        return JsonSerializer.Serialize(instance, type, new JsonSerializerOptions());
      case "fresh-options-general":
        return JsonSerializer.Serialize(instance, type, new JsonSerializerOptions(JsonSerializerDefaults.General));
      case "fresh-options-resolver":
        return JsonSerializer.Serialize(instance, type, new JsonSerializerOptions
        {
          TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        });
      case "toelement":
        return JsonSerializer.SerializeToElement(instance, type, new JsonSerializerOptions()).ToString();
      case "utf8bytes":
        return System.Text.Encoding.UTF8.GetString(JsonSerializer.SerializeToUtf8Bytes(instance, type, new JsonSerializerOptions()));
      case "manual":
        // Hand-rolled reflection JSON writer - no System.Text.Json at all.
        return ManualWrite(instance, type);
      default:
        throw new ArgumentException("unknown strategy " + strategy, nameof(strategy));
    }
  }

  /// <summary>A hand-rolled JSON writer over public properties - the shape of a
  ///  fix that never hands the type to System.Text.Json.</summary>
  private static string ManualWrite(object instance, Type type)
  {
    List<string> parts = [];
    foreach (PropertyInfo property in type.GetProperties())
    {
      object? value = property.GetValue(instance);
      string rendered = value switch
      {
        null => "null",
        string s => "\"" + s + "\"",
        _ => value.ToString() ?? "null",
      };
      parts.Add("\"" + property.Name + "\":" + rendered);
    }

    return "{" + string.Join(",", parts) + "}";
  }

  /// <summary>Compiles and loads a tiny assembly into the collectible context -
  ///  the shape of a Roslyn script submission.</summary>
  private static Assembly LoadProbeAssembly(AssemblyLoadContext context)
  {
    SyntaxTree tree = CSharpSyntaxTree.ParseText(
        "namespace Probe { public class Payload { public int Count { get; set; } public string Name { get; set; } = \"alpha\"; } }");
    CSharpCompilation compilation = CSharpCompilation.Create(
        "ProbeAsm",
        new SyntaxTree[] { tree },
        new MetadataReference[] { MetadataReference.CreateFromFile(typeof(object).Assembly.Location) },
        new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary));
    using MemoryStream pe = new();
    EmitResult emit = compilation.Emit(pe);
    if (!emit.Success)
    {
      throw new InvalidOperationException("probe assembly emit failed: " + emit.Diagnostics);
    }

    pe.Position = 0;
    return context.LoadFromStream(pe);
  }
}
