namespace eThangAgent.StateDomain;

public sealed record CertificationReport(
    bool Certified,
    bool Violated,
    IReadOnlyList<EvidenceResult> Results,
    IReadOnlyList<string> BlockingReasons);
