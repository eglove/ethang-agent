using System.Reflection;
using eThangAgent.CapabilityDomain;
using eThangAgent.ToolDomain;

namespace eThangAgent.Roslyn.ACL.Tests;

/// <summary>The Tools.* convenience surface must cover every ITool-backed action whose
///     name is a valid C# identifier — a model calling Tools.command_output(...) must
///     not hit a compile error for a tool that exists (a real session lost turns to
///     exactly that). Dot/hyphen actions (state.*, agent.*, plan.*, memory.*) stay
///     Invoke-only: they are not identifiers.</summary>
public class ScriptToolsSurfaceTests
{
  [Fact]
  public void EveryIdentifierSafeToolAction_HasAConvenienceMethod()
  {
    IEnumerable<string> actionNames = typeof(ExecTool).Assembly.GetTypes()
        .Where(t => typeof(ITool).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
        .Select(t => t.GetField("ToolName", BindingFlags.Public | BindingFlags.Static))
        .OfType<FieldInfo>()
        .Select(f => (string)f.GetValue(null)!)
        .Where(n => n is not null)
        .Distinct();

    HashSet<string> scriptMethodNames = [.. typeof(ScriptTools)
        .GetMethods(BindingFlags.Public | BindingFlags.Instance)
        .Where(m => m.DeclaringType == typeof(ScriptTools))
        .Select(m => m.Name)];

    List<string> missing = [.. actionNames
        .Where(IsIdentifierSafe)
        .Where(n => !scriptMethodNames.Contains(n))];

    Assert.True(missing.Count == 0,
        $"Tool actions without a ScriptTools convenience method: {string.Join(", ", missing)}");
  }

  private static bool IsIdentifierSafe(string name) =>
      name.Length > 0 && char.IsAsciiLetterLower(name[0]) &&
      name.All(c => char.IsAsciiLetterOrDigit(c) || c == '_');

  [Fact]
  public void ConvenienceMethod_DispatchesThroughTheRegistry()
  {
    EchoProvider provider = new();
    ScriptGlobals globals = new(CapabilityRegistry.Create([provider]), ".", Path.GetTempPath(),
        execBudget: TimeSpan.FromSeconds(30));

    string result = globals.Tools.command_output(/*lang=json,strict*/ """{"id":7}""");

    Assert.Equal("ok", result);
    Assert.Contains("\"id\":7", provider.LastJson!, StringComparison.Ordinal);
    // The action's contract does not declare timeoutSeconds: the inherited budget is
    // stripped before dispatch, never leaked to the provider.
    Assert.DoesNotContain("timeoutSeconds", provider.LastJson!, StringComparison.Ordinal);
  }

  private sealed class EchoProvider : ICapabilityProvider
  {
    public string Id => "echo";
    public string? LastJson { get; private set; }
    public IReadOnlyList<ActionDescriptor> Actions { get; } =
    [
        new ActionDescriptor("command_output", "Reads back a command run.", "Contract text.",
                [new ActionParameter("id", "WholeNumber", "Run id.")]),
        ];
    public Task<CapabilityInvocationResult> InvokeAsync(string actionName,
        string jsonArguments, CancellationToken ct = default)
    {
      LastJson = jsonArguments;
      return Task.FromResult(CapabilityInvocationResult.Ok("ok"));
    }
  }
}
