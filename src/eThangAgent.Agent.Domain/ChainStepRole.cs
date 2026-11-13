namespace eThangAgent.AgentDomain;

/// <summary>What a chain step's session is FOR (issue #313 D7). The vocabulary is exactly two
///     members: <see cref="Implement"/> and <see cref="Refine"/>.
///     <para>REFINE REPLACED REVIEW, and the reason is what a reviewer could not do. A reviewer
///     judged work that had already been done and returned a verdict; a denial then had to be
///     enacted, which meant reopening a settled step, spending a round-trip budget, and - when
///     the budget ran out - parking the chain for a human. Every one of those was machinery for
///     a decision the stakeholder had already made before the chain started.</para>
///     <para>THE STAKEHOLDER'S REVIEW IS THE GATE BEFORE A CHAIN STARTS, not a step inside it.
///     A plan is reviewed while it is still a plan, by the person who decided it should be done.
///     What a chain needs instead is a step that makes the plan EXECUTABLE: it reads the plan,
///     resolves what is ambiguous, sharpens the steps that are not yet checkable, and then
///     advances into implementation. That is technical work on the plan, and it is the model's.
///     </para>
///     <para>ROLES ARE ASSIGNED, NOT EMERGENT (parent #299 D15). An assigned role is checkable
///     and an emergent one is not: there is a role on the record, a brief that differs per role,
///     and a completion contract that differs per role.</para>
///     <para>A ROLE CARRIES NO MODEL. A step's session runs on whatever model the workspace has
///     selected, exactly as every other session does; the role decides what the session is
///     asked to do, never which model is asked to do it.</para></summary>
public enum ChainStepRoleKind
{
  /// <summary>This step's session changes the workspace: it is the work.</summary>
  Implement,

  /// <summary>This step's session makes the plan executable and then advances: it refines the
  ///     plan's remaining steps rather than implementing them.</summary>
  Refine,
}

/// <summary>A chain step's assigned role (issue #313 D7): the kind, and nothing else.
///     <para>IMMUTABLE AND TOTAL. Every chain step has a role - there is no 'unassigned'
///     state, because a step whose role nobody declared is a step nobody can start
///     correctly. <see cref="Implement"/> is the default the creation call applies when a
///     step names none, and that default is stated once, here, rather than at each call
///     site.</para>
///     <para>PERSISTED AS ONE COLUMN (<c>role</c>): the kind is a closed set a CHECK constraint
///     can hold.</para></summary>
/// <param name="Kind">Which role this step was assigned.</param>
public sealed record ChainStepRole(ChainStepRoleKind Kind)
{
  /// <summary>The implementer role: the default for a step that names none.</summary>
  public static ChainStepRole Implement { get; } = new(ChainStepRoleKind.Implement);

  /// <summary>The refine role.</summary>
  public static ChainStepRole Refine { get; } = new(ChainStepRoleKind.Refine);

  /// <summary>True for a refine step: the role whose brief asks for the plan to be made
  ///     executable rather than for the work.</summary>
  public bool IsRefine => Kind == ChainStepRoleKind.Refine;

  /// <summary>The stored role name. The enum member name, so a CHECK constraint and a
  ///     reader agree without a second spelling to keep in sync.</summary>
  public string Encoded => Kind.ToString();

  /// <summary>Reads a persisted role. STRICT: only a DEFINED enum member parses, so a
  ///     corrupt column is reported rather than silently read as an implementer - a step
  ///     whose role nobody can name is worse than a named failure.
  ///     <para><c>Enum.TryParse</c> alone is not enough: it accepts a numeric string ('5')
  ///     and yields an UNDEFINED member, which would smuggle a value outside the enum into
  ///     the domain (the <c>SqliteChainStore</c> status rule, applied here for the same
  ///     reason).</para>
  ///     <para>THE RETIRED SPELLING IS NOT ACCEPTED. A row that still says 'Review' was written
  ///     before the role changed, and the migration that renamed the vocabulary rewrites it; a
  ///     decoder that also accepted 'Review' would let the two spellings coexist forever and
  ///     make 'which role is this' a question with two answers.</para></summary>
  /// <param name="encoded">The stored role name.</param>
  /// <param name="role">The decoded role, or null.</param>
  public static bool TryDecode(string? encoded,
      [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out ChainStepRole? role)
  {
    role = null;
    if (string.IsNullOrWhiteSpace(encoded)
        || !Enum.TryParse(encoded, ignoreCase: false, out ChainStepRoleKind kind)
        || !Enum.IsDefined(kind))
    {
      return false;
    }

    role = new ChainStepRole(kind);
    return true;
  }

  public override string ToString() => Encoded;
}

/// <summary>One step a chain is created from (issue #313 D2): the plan position it executes
///     and the role assigned to the session that will run it.
///     <para>A SEED, NOT A <see cref="ChainStep"/>: a chain step carries execution state
///     (status, attempts, claim clock) that does not exist until the record does. The
///     creation call states the two facts it actually knows.</para></summary>
/// <param name="Position">The 1-based plan step position this chain step executes.</param>
/// <param name="Role">The role assigned to the session that runs it.</param>
/// <param name="Group">The group the step runs in, or null when it runs alone. A group id is
///     an integer of at least 1, assigned by the caller; it is not a position and carries no
///     ordering meaning.</param>
public sealed record ChainStepSeed(int Position, ChainStepRole Role, int? Group = null)
{
  /// <summary>An implementer seed for <paramref name="position"/>: the common case, stated
  ///     once so a caller that assigns no role does not re-derive the default.</summary>
  /// <param name="position">The 1-based plan step position.</param>
  public static ChainStepSeed Implementing(int position) =>
      new(position, ChainStepRole.Implement);
}
