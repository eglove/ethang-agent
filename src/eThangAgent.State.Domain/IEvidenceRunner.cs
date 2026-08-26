namespace eThangAgent.StateDomain;

public interface IEvidenceRunner
{
  Task<EvidenceResult> RunAsync(string command, CancellationToken ct = default);
}
