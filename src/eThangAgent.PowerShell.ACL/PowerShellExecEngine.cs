using System.Collections.ObjectModel;
using System.Management.Automation;
using System.Management.Automation.Language;
using eThangAgent.SharedKernel;
using eThangAgent.ToolDomain;

namespace eThangAgent.PowerShell.ACL;

public sealed class PowerShellExecEngine : IExecEngine
{
    private readonly Lazy<IToolRegistry> _registry;
    private readonly ExecOptions _options;

    /// <summary>Primary ctor. The registry is lazy: the composition root's IToolRegistry
    ///     contains ExecTool, whose engine would otherwise force the registry to exist
    ///     before it is complete (DI cycle).</summary>
    public PowerShellExecEngine(Lazy<IToolRegistry> registry, ExecOptions options)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    /// <summary>Convenience ctor for tests and direct use.</summary>
    public PowerShellExecEngine(IToolRegistry registry, ExecOptions options)
        : this(new Lazy<IToolRegistry>(() => registry), options)
    {
    }

    public Task<Result<IReadOnlyList<ExecParseError>>> ValidateAsync(
        ExecProgram program, CancellationToken ct = default)
    {
        _ = Parser.ParseInput(program.Text, out _, out var parseErrors);
        var errors = parseErrors
            .Select(e => new ExecParseError(
                e.Extent.StartLineNumber, e.Extent.StartColumnNumber, e.Message))
            .ToList();
        return Task.FromResult(Result<IReadOnlyList<ExecParseError>>.Success(errors));
    }

    public async Task<ExecRunResult> ExecuteAsync(ExecProgram program, CancellationToken ct = default)
    {
        var broker = new ToolBroker(_registry.Value);
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.Runspace.SessionStateProxy.PSVariable.Set("broker", broker);
        ps.AddScript(CreateSetupScript(broker));
        ps.AddScript(program.Text);

        Collection<PSObject> collected;
        try
        {
            var invokeTask = Task.Run(() => ps.Invoke());
            var completed = await Task.WhenAny(invokeTask, Task.Delay(_options.Timeout, ct));
            if (completed != invokeTask)
            {
                try { ps.Stop(); } catch { /* pipeline already stopping */ }
                try
                {
                    collected = await invokeTask; // Invoke returns what was collected before the stop
                }
                catch (Exception ex)
                {
                    return new ExecRunResult(ExecRunStatus.EngineFailure, "", [], ex.Message);
                }

                var status = ct.IsCancellationRequested
                    ? ExecRunStatus.Cancelled
                    : ExecRunStatus.Timeout;
                return new ExecRunResult(status, RenderOutput(collected), ErrorLines(ps),
                    status == ExecRunStatus.Cancelled
                        ? "Execution was cancelled."
                        : $"Execution timed out after {_options.Timeout.TotalSeconds:0} seconds; pipeline stopped.");
            }

            collected = await invokeTask;
        }
        catch (Exception ex)
        {
            return new ExecRunResult(ExecRunStatus.EngineFailure, "", [], ex.Message);
        }

        return new ExecRunResult(ExecRunStatus.Completed, RenderOutput(collected), ErrorLines(ps));
    }

    /// <summary>Functions are injected as setup-script text into a default PowerShell.Create()
    ///     runspace. CreateDefault2-based runspaces fail to load the built-in modules in
    ///     hosted (non-pwsh) processes; the default Create() runspace does not.</summary>
    private static string CreateSetupScript(ToolBroker broker)
        => string.Join("\n",
            broker.WrappableDefinitions.Select(d =>
                $"function {d.Name} {{ param([object]$ToolInput) $broker.InvokeTool('{d.Name}', $ToolInput) }}")
                .Append("function Invoke-AgentTool { param([string]$Name, [object]$ToolInput) $broker.InvokeTool($Name, $ToolInput) }")
                .Append("function Get-AgentTool { $broker.DescribeTools() }"));

    private static IReadOnlyList<string> ErrorLines(System.Management.Automation.PowerShell ps)
        => ps.Streams.Error.Select(e => e.Exception.Message).ToList();

    private static string RenderOutput(IEnumerable<PSObject> output)
        => string.Join("\n", output.Select(o =>
        {
            var b = o.BaseObject;
            return b is string s ? s : PowerShellValueConverter.ToJson(b);
        }));
}
