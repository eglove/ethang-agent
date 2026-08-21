using eThangAgent.StateDomain;

namespace eThangAgent.PowerShell.ACL;

/// <summary>Runs evidence commands in a fresh default runspace. Confirmed = no errors
///     written AND $LASTEXITCODE is 0 or unset. Fails closed on timeout, cancellation,
///     errors, syntax failures, and engine exceptions.</summary>
public sealed class PsEvidenceRunner : IEvidenceRunner
{
    private readonly EvidenceOptions _options;

    public PsEvidenceRunner(EvidenceOptions? options = null)
        => _options = options ?? EvidenceOptions.Default;

    public async Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default)
    {
        using var ps = System.Management.Automation.PowerShell.Create();
        ps.AddScript(command);
        try
        {
            var invokeTask = Task.Run(() => ps.Invoke());
            var completed = await Task.WhenAny(invokeTask, Task.Delay(_options.Timeout, ct));
            if (completed != invokeTask)
            {
                try { ps.Stop(); } catch { /* pipeline already stopping */ }
                return new EvidenceResult(command, false,
                    $"Timed out after {_options.Timeout.TotalSeconds:0}s.");
            }

            await invokeTask;
            var exitCode = ReadExitCode(ps);
            if (ps.HadErrors)
            {
                var detail = ps.Streams.Error.FirstOrDefault()?.Exception.Message;
                if (string.IsNullOrWhiteSpace(detail))
                    detail = exitCode is { } code ? $"$LASTEXITCODE = {code}." : "unknown error";
                return new EvidenceResult(command, false, detail);
            }

            if (exitCode is not (null or 0))
                return new EvidenceResult(command, false, $"$LASTEXITCODE = {exitCode}.");

            return new EvidenceResult(command, true, "");
        }
        catch (Exception ex)
        {
            return new EvidenceResult(command, false, ex.Message);
        }
    }

    private static int? ReadExitCode(System.Management.Automation.PowerShell ps)
    {
        try
        {
            var value = ps.Runspace.SessionStateProxy.GetVariable("LASTEXITCODE");
            return value is null ? null : Convert.ToInt32(value);
        }
        catch
        {
            return null; // LASTEXITCODE unset — no native executable ran
        }
    }
}
