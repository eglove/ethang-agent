using System;
using System.Threading.Tasks;

namespace eThangAgent.ToolDomain;

/// <summary>Base contract for a model-callable tool. <see cref="ExecuteAsync"/> receives
///     the caller's cancellation token; the per-call execution budget arrives inside the
///     JSON arguments (required key <c>timeoutSeconds</c>, parsed by
///     <see cref="ToolTimeout"/>) and is enforced by <see cref="ToolExecution.RunAsync"/>.</summary>
public interface ITool
{
    ToolDefinition Definition { get; }
    Task<ToolResult> ExecuteAsync(RawToolInput input, CancellationToken ct = default);
}
