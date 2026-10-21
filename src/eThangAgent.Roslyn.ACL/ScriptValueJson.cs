using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;

namespace eThangAgent.Roslyn.ACL;

/// <summary>Renders a script's return value as one-line JSON WITHOUT letting
/// System.Text.Json ever see a type declared in the collectible submission
/// assembly (the 2026-10-19 leak: 15 GB working set, 9.3M handles).
/// <para><b>Why this exists.</b> A script that returns an anonymous type (or
/// any type it declares) returns an instance whose RUNTIME TYPE lives in the
/// per-run collectible AssemblyLoadContext. Handing that instance to
/// <c>JsonSerializer.Serialize</c> makes System.Text.Json cache the type in
/// its static <c>JsonSerializerOptions.Default</c> singleton - the
/// <c>_lastTypeInfo</c> field and the <c>CachingContext</c> dictionary both
/// hold the <c>Type</c> STRONGLY. A cached <c>Type</c> holds its
/// <c>m_keepalive</c>, which holds the <c>LoaderAllocator</c>, which holds
/// the collectible submission assembly. The context can therefore NEVER be
/// reclaimed: the submission's IL, metadata and assembly leak for the
/// process lifetime, one per exec call. Measured: 1 leaked context per
/// call, uncollectable even under an aggressive compacting gen2 collect, with
/// the runtime's per-context handle and thread bookkeeping growing to
/// match (the 9.3 MILLION retained handles).</para>
/// <para><b>The fix.</b> PROJECT the value into a tree of DEFAULT-CONTEXT
/// types first (a <see cref="Dictionary{TKey, TValue}"/> of
/// <see cref="string"/> to <see cref="object"/>, a
/// <see cref="List{T}"/> of <see cref="object"/>, and boxed
/// primitives), and hand THAT to System.Text.Json. The serializer then
/// caches only types that live in the default context forever - pinning
/// them is harmless - and the collectible context becomes reclaimable.
/// The projection is recursive, so a nested script-declared object becomes
/// a nested dictionary.</para>
/// <para><b>Fidelity.</b> The rendered JSON keeps System.Text.Json's
/// shape for the shapes scripts actually return: objects become JSON
/// objects with their property names, arrays become JSON arrays, numbers
/// and booleans and null keep their JSON forms, and strings are escaped
/// as JSON strings. A value whose type is NOT declared in a collectible
/// context is passed to the serializer untouched, so ordinary
/// domain types serialize exactly as they always did.</para>
/// </summary>
internal static class ScriptValueJson
{
  /// <summary>The types whose JSON shape System.Text.Json already produces
  /// from the value alone - the primitive-ish leaves. They live in the
  /// default context, so handing them to the serializer is safe.</summary>
  private static readonly HashSet<Type> PassthroughTypes =
  [
    typeof(string), typeof(bool), typeof(char),
    typeof(byte), typeof(sbyte), typeof(short), typeof(ushort),
    typeof(int), typeof(uint), typeof(long), typeof(ulong),
    typeof(float), typeof(double), typeof(decimal), typeof(byte[]),
    typeof(DateTime), typeof(DateTimeOffset), typeof(TimeSpan),
    typeof(Guid), typeof(Uri),
  ];

  /// <summary>Renders <paramref name="value"/> as one-line JSON: a string
  /// verbatim, any other value as JSON.</summary>
  public static string Serialize(object? value) =>
      value is string s ? s : JsonSerializer.Serialize(Project(value));

  /// <summary>Projects a value into default-context types so the serializer
  /// never caches a collectible type. A value whose runtime type is
  /// declared in a collectible context becomes a dictionary of its public
  /// properties, a list, or its underlying number (for an enum); every
  /// other value is passed through unchanged - its runtime type lives in
  /// the default context, where caching it is harmless.</summary>
  private static object? Project(object? value)
  {
    if (value is null)
    {
      return null;
    }

    Type type = value.GetType();
    if (PassthroughTypes.Contains(type) || !IsCollectibleDeclared(type))
    {
      // A default-context type: the serializer may cache it without harm.
      return value;
    }

    if (type.IsEnum)
    {
      // An enum declared by the script: project it onto its underlying
      // number so the JSON is the same shape System.Text.Json would emit.
      return Convert.ChangeType(value, Enum.GetUnderlyingType(type), CultureInfo.InvariantCulture);
    }

    return value is IEnumerable sequence
        ? ProjectSequence(sequence)
        : ProjectProperties(type, value);
  }

  /// <summary>Projects a sequence into a list of projected items.</summary>
  private static List<object?> ProjectSequence(IEnumerable sequence)
  {
    List<object?> items = [];
    foreach (object? item in sequence)
    {
      items.Add(Project(item));
    }

    return items;
  }

  /// <summary>Projects an object into a dictionary of its public readable
  /// properties.</summary>
  private static Dictionary<string, object?> ProjectProperties(Type type, object value)
  {
    Dictionary<string, object?> projection = [];
    foreach (PropertyInfo property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
    {
      if (property.GetIndexParameters().Length == 0 && property.CanRead)
      {
        projection[property.Name] = Project(property.GetValue(value));
      }
    }

    return projection;
  }

  /// <summary>True when the type is declared in a COLLECTIBLE load context -
  /// the shape of a type a script declares in its submission assembly.</summary>
  private static bool IsCollectibleDeclared(Type type) =>
      AssemblyLoadContext.GetLoadContext(type.Assembly) is { IsCollectible: true };
}
