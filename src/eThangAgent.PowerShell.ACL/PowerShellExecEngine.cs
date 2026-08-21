using System.Management.Automation;
using System.Management.Automation.Language;
using System.Management.Automation.Runspaces;
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

    public Task<ExecRunResult> ExecuteAsync(ExecProgram program, CancellationToken ct = default)
        => throw new NotImplementedException("Task 9");
}
