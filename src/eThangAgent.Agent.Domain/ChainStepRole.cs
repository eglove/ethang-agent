namespace eThangAgent.AgentDomain;

/// <summary>What a chain step's session is FOR (issue #313 D7). The vocabulary is exactly two
///     members in v1: <see cref="Implement"/> and
///     <see cref="Review"/>.
///     <para>ROLES ARE ASSIGNED, NOT EMERGENT (parent #299 D15). An assigned role is checkable
///     and an emergent one is not: there is a role on the record, a verdict the cursor reads,
///     and a separation rule that can be enforced. That is a deliberate departure from Agensh,
///     recorded on the parent, and reopening it is a decision rather than an improvement.</para>
///     <para>TWO MEMBERS IS ENOUGH to express the loop the parent describes; more would be
///     speculation about workflows that do not exist. A chain with no review step is legal -
///     independent implementation work needs none (issue #306 D4).</para>
///     <para>THE ROLE CARRIES A MODEL, and the ENFORCEMENT IS NOT HERE. Issue #218 owns two
///     rules this record only makes room for: a review step must name its reviewer model
///     explicitly, and that model must differ from the implementer's. #313 records the
///     declaration; refusing a review step with no model, and refusing a reviewer that
///     matches the implementer, belong to #218 at session start. Adding the field without the
///     rule is the deliberate split: a shape decided here, a gate decided there.</para>
///     <para>NO GRANT SET YET (D7). A role that carried capabilities would be a second
///     permission surface beside the spawn contract's, and nothing asks for one.</para></summary>
public enum ChainStepRoleKind
{
  /// <summary>This step's session changes the workspace: it is the work.</summary>
  Implement,

  /// <summary>This step's session judges another step's work and returns a verdict
  ///     (issue #306). The verdict type and its record are #306's and #216's; this member
  ///     exists so a chain can DECLARE a review step before those land.</summary>
  Review,
}

/// <summary>A chain step's assigned role (issue #313 D7): the kind plus, for a review step,
///     the reviewer's model.
///     <para>IMMUTABLE AND TOTAL. Every chain step has a role - there is no 'unassigned'
///     state, because a step whose role nobody declared is a step nobody can start
///     correctly. <see cref="Implement"/> is the default the creation call applies when a
///     step names none, and that default is stated once, here, rather than at each call
///     site.</para>
///     <para>PERSISTED AS TWO COLUMNS (<c>role</c>, <c>role_model</c>) rather than one
///     encoded string: the kind is a closed set a CHECK constraint can hold, and a model id
///     is free text that must not be parsed back out of a composite value.</para></summary>
/// <param name="Kind">Which role this step was assigned.</param>
/// <param name="Model">The reviewer's model for a review step; null otherwise. #218 owns
///     requiring it and refusing a match with the implementer.</param>
public sealed record ChainStepRole(ChainStepRoleKind Kind, string? Model = null)
{
  /// <summary>The implementer role: the default for a step that names none.</summary>
  public static ChainStepRole Implement { get; } = new(ChainStepRoleKind.Implement);

  /// <summary>The reviewer role, optionally naming the reviewer's model.
  ///     <para>The model is NOT required here. #218 refuses a review step that omits it, at
  ///     session start, where the implementer's model is also known and the separation rule
  ///     can be evaluated - a creation-time refusal could not check separation at all.</para></summary>
  /// <param name="model">The reviewer's model id, or null when none was named.</param>
  public static ChainStepRole Review(string? model = null) =>
      new(ChainStepRoleKind.Review, string.IsNullOrWhiteSpace(model) ? null : model.Trim());

  /// <summary>True for a review step: the one role #218's separation rule applies to.</summary>
  public bool IsReview => Kind == ChainStepRoleKind.Review;

  /// <summary>The stored role name. The enum member name, so a CHECK constraint and a
  ///     reader agree without a second spelling to keep in sync.</summary>
  public string Encoded => Kind.ToString();

  /// <summary>Reads a persisted role. STRICT: only a DEFINED enum member parses, so a
  ///     corrupt column is reported rather than silently read as an implementer - a step
  ///     whose role nobody can name is worse than a named failure.
  ///     <para><c>Enum.TryParse</c> alone is not enough: it accepts a numeric string ('5')
  ///     and yields an UNDEFINED member, which would smuggle a value outside the enum into
  ///     the domain (the <c>SqliteChainStore</c> status rule, applied here for the same
  ///     reason).</para></summary>
  /// <param name="encoded">The stored role name.</param>
  /// <param name="model">The stored reviewer model, or null.</param>
  /// <param name="role">The decoded role, or null.</param>
  public static bool TryDecode(string? encoded, string? model,
      [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ChainStepRole? role)
  {
    role = null;
    if (string.IsNullOrWhiteSpace(encoded)
        || !Enum.TryParse(encoded, ignoreCase: false, out ChainStepRoleKind kind)
        || !Enum.IsDefined(kind))
    {
      return false;
    }

    role = new ChainStepRole(kind, string.IsNullOrWhiteSpace(model) ? null : model.Trim());
    return true;
  }

  public override string ToString() =>
      Model is { } model ? $"{Encoded}:{model}" : Encoded;
}

/// <summary>One step a chain is created from (issue #313 D2): the plan position it executes
///     and the role assigned to the session that will run it.
///     <para>A SEED, NOT A <see cref="ChainStep"/>: a chain step carries execution state
///     (status, attempts, claim clock) that does not exist until the record does. The
///     creation call states the two facts it actually knows.</para></summary>
/// <param name="Position">The 1-based plan step position this chain step executes.</param>
/// <param name="Role">The role assigned to the session that runs it.</param>
public sealed record ChainStepSeed(int Position, ChainStepRole Role)
{
  /// <summary>An implementer seed for <paramref name="position"/>: the common case, stated
  ///     once so a caller that assigns no role does not re-derive the default.</summary>
  /// <param name="position">The 1-based plan step position.</param>
  public static ChainStepSeed Implementing(int position) =>
      new(position, ChainStepRole.Implement);
}
