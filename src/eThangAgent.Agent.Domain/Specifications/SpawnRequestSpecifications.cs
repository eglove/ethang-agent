using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain.Specifications;

public sealed class NonEmptyTaskPromptSpecification : Specification<SpawnRequest>
{
    public override bool IsSatisfiedBy(SpawnRequest candidate)
        => !string.IsNullOrWhiteSpace(candidate.TaskPrompt);

    protected override string FailureMessageFor(SpawnRequest candidate)
        => "TaskPrompt must be a non-empty string.";
}

public sealed class ValidModelReferenceSpecification : Specification<SpawnRequest>
{
    public override bool IsSatisfiedBy(SpawnRequest candidate)
        => candidate.Model is not string model || model.Trim().Length > 0;

    protected override string FailureMessageFor(SpawnRequest candidate)
        => "Model must be a non-empty provider model reference when supplied.";
}