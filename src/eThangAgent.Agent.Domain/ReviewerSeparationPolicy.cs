using eThangAgent.ModelDomain;
using eThangAgent.SharedKernel;

namespace eThangAgent.AgentDomain;

/// <summary>The named refusals the reviewer-separation rule produces (issue #218). Each is a
///     distinct answer a caller acts on differently: a missing model is a DECLARATION defect
///     the caller fixes in the chain, while a match is a CONFIGURATION defect that no edit to
///     the step can fix without changing which model the reviewer runs on.</summary>
public static class ReviewerSeparationCode
{
  /// <summary>The review step names no reviewer model. D1 makes the field required: model
  ///     resolution otherwise falls back through the session's live model choice, which is
  ///     exactly the implementer's model in a single-model run.</summary>
  public const string ModelMissing = "ReviewerModelMissing";

  /// <summary>The reviewer's model IS the implementer's. D4: a typed refusal with its own
  ///     code, never a silent substitution - swapping models quietly would make the review's
  ///     provenance a lie and hide a misconfiguration the user must fix.</summary>
  public const string ModelMatchesImplementer = "ReviewerMatchesImplementer";
}

/// <summary>The separation rule for a review step (issue #218): a reviewer must NAME its model,
///     and that model must differ from the implementer's.
///     <para>THE ENFORCEMENT POINT MOVED HERE FROM THE SPAWN HANDLER. The #215 version compared a
///     child's resolved model to its parent's live config; the comparison is now against the
///     CHAIN RECORD, which is the better source (D3): the implementer's model is a recorded fact
///     rather than a parent's live configuration, so the check survives a restart and works when
///     the implementer session has already closed.</para>
///     <para>PURE: facts in, refusal out, no I/O and no clock - the <see cref="ChainAdvancePolicy"/>
///     and <see cref="ChainCreationPolicy"/> shape, so the rule is unit-testable and cannot
///     acquire a side effect by accident. The ENACTMENT (parking the chain) lives in the
///     application layer's gate.</para>
///     <para>EACH CHECK IS ITS OWN HELPER RETURNING A REFUSAL OR NULL, folded with <c>??</c>: every
///     refusal stays a separate named rule with its own code instead of one guard chain whose
///     branches drift apart.</para></summary>
public static class ReviewerSeparationPolicy
{
  /// <summary>The first refusal this step earns, or null when it may start.
  ///     <para>ORDER IS THE ORDER THEY MATTER IN: a step that names no reviewer model cannot be
  ///     compared at all, so the missing model is reported before the match.</para></summary>
  /// <param name="chain">The chain as it stands.</param>
  /// <param name="position">The 1-based step position about to start.</param>
  public static DomainError? RefusalFor(Chain chain, int position)
  {
    ArgumentNullException.ThrowIfNull(chain);
    return chain.StepAt(position) is { } step
        ? MissingModel(step.Role, step.Position) ?? NotSeparated(chain, step)
        : null;
  }

  /// <summary>D1: a review step must name its reviewer model. Expressed over the ROLE rather
  ///     than a whole step, so the creation path can apply the same rule to a DECLARATION before
  ///     any record exists - one rule, two boundaries, rather than two spellings that can drift.</summary>
  /// <param name="role">The role assigned to the step.</param>
  /// <param name="position">The 1-based step position, for the message.</param>
  public static DomainError? MissingModel(ChainStepRole role, int position)
  {
    ArgumentNullException.ThrowIfNull(role);
    return role.IsReview && role.Model is null
        ? new DomainError(ReviewerSeparationCode.ModelMissing,
            $"step {position} was assigned the review role but names no reviewer model. "
            + "A reviewer must name the model it runs on ('model' on the step declaration): "
            + "without one the session inherits this workspace's model choice, which is the "
            + "implementer's model in a single-model run, and a reviewer on the implementer's "
            + "model is not an independent verdict.")
        : null;
  }

  /// <summary>D2/D3/D7: the reviewer's resolved model id must differ from the implementer's
  ///     RECORDED model id.
  ///     <para>THE COMPARISON IS ON THE RESOLVED ID (D2). Two request strings can name the same
  ///     model through aliasing - this harness's aliasing is the routing-variant suffix, where
  ///     <c>alpha/model:nitro</c> and <c>alpha/model</c> are one model served two ways - so
  ///     comparing the raw request strings would pass a review that runs on the implementer's
  ///     model. <see cref="ModelVariants.BaseOf"/> is that resolution, and it is the SAME one
  ///     every catalog lookup already applies, so there is no second spelling of 'the same
  ///     model' to keep in sync.</para>
  ///     <para>D7: MODEL ID EQUALITY IS THE WHOLE RULE. Comparing providers as well would be a
  ///     stronger requirement with no evidence behind it; model id equality is what the
  ///     self-preference-bias literature supports.</para>
  ///     <para>AN UNRECORDED IMPLEMENTER MODEL IS NOT A MATCH. A step whose session settled
  ///     before this rule existed carries no served model, and fabricating a comparison from a
  ///     model nobody recorded would refuse work on a guess - the same direction the 'null is
  ///     not zero' rule takes everywhere else in this harness.</para></summary>
  /// <param name="chain">The chain the review step belongs to.</param>
  /// <param name="step">The review step about to start.</param>
  public static DomainError? NotSeparated(Chain chain, ChainStep step)
  {
    ArgumentNullException.ThrowIfNull(chain);
    ArgumentNullException.ThrowIfNull(step);
    return ImplementerFor(chain, step.Position) is { } implementer
        && step.Role.Model is { } reviewer
        && SameModel(reviewer, implementer.ServedModel!)
        ? new DomainError(ReviewerSeparationCode.ModelMatchesImplementer,
            $"chain #{chain.Id} step {step.Position} is a review step whose reviewer model "
            + $"'{reviewer}' is the same model as step {implementer.Position}'s implementer "
            + $"('{implementer.ServedModel}'). A reviewer must run on a different model: a model "
            + "judging its own work is the self-preference bias the separation rule exists to "
            + "remove. The chain is parked - give the review step a different model and resume it.")
        : null;
  }

  /// <summary>The implementer this review step judges: the NEAREST PRECEDING step that is not
  ///     a review and that recorded the model it served on, or null when there is none.
  ///     <para>WHY THE PRECEDING IMPLEMENTER AND NOT A DECLARED PAIRING: chain steps are
  ///     INDEPENDENT (#299 D8, settled again when #321 was closed as not-a-defect), so the record
  ///     carries no review-to-implementer link. A review step judges the work that came before
  ///     it, and the nearest preceding implementer is that work. The deny back-edge (#320) makes
  ///     the pairing explicit by moving the cursor; until then this is the honest reading, and
  ///     stating it here is what keeps it from being re-derived differently at the next call
  ///     site.</para>
  ///     <para>A review step at the chain's first position has nothing to separate from: a chain
  ///     of reviews has no implementer, and a reviewer cannot be biased toward its own work when
  ///     it did none.</para></summary>
  /// <param name="chain">The chain the review step belongs to.</param>
  /// <param name="reviewPosition">The review step's 1-based position.</param>
  public static ChainStep? ImplementerFor(Chain chain, int reviewPosition)
  {
    ArgumentNullException.ThrowIfNull(chain);
    return chain.Steps
        .Where(candidate => candidate.Position < reviewPosition
            && !candidate.Role.IsReview
            && !string.IsNullOrWhiteSpace(candidate.ServedModel))
        .OrderByDescending(candidate => candidate.Position)
        .FirstOrDefault();
  }

  /// <summary>True when two model ids name the same model once the routing variant is resolved.
  ///     <para>ORDINAL-IGNORE-CASE, deliberately: a model id that differs only in case is the
  ///     same model, and a check that could be defeated by capitalisation would be a check
  ///     nobody could rely on.</para></summary>
  private static bool SameModel(string left, string right) =>
      string.Equals(ModelVariants.BaseOf(left), ModelVariants.BaseOf(right),
          StringComparison.OrdinalIgnoreCase);
}
