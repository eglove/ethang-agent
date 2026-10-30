namespace eThangAgent.ToolDomain;

/// <summary>The workspace-visible REFUTATION record (issue #315): what was disproved, by what
///     evidence, and against what workspace state.
///     <para>THE DISTINCTION THIS EXISTS FOR (D1): a falsification is a fact about the world, a
///     conclusion is a fact about one chain's work. "Approach X does not work because Y" costs
///     the next chain real budget to rediscover, so it belongs where every chain in the
///     workspace can find it; "I implemented step 3 this way" describes work no other chain is
///     doing, so it stays in that chain's ledger.</para>
///     <para>ONE KEY PER REFUTATION, NOT ONE GROWING BOARD. A single appended document is the
///     shape that made the retired todo list an 80 KB value rewritten whole on every change
///     (#310). A key per refutation keeps each write small and lets the workspace's full-text
///     search index them individually, which is what D1 means by 'the board is a QUERY, not a
///     feed': nobody reads the namespace, they search it.</para>
///     <para>PURE, AND THEREFORE IDEMPOTENT: the key and the value are functions of the facts
///     alone, so recording the same refutation twice lands the same bytes on the same key. That
///     is what lets the writer be a plain write rather than an append - an append would
///     duplicate a record whose whole purpose is to be found once and read once.</para>
///     <para>WHY THE ORIGIN IS IN THE VALUE: a state key's name is searchable, but a hit whose
///     value said only 'prediction 1' would not say which chain established it. The reader
///     needs to know where to go for the context, and a fact with no provenance is one nobody
///     can check.</para></summary>
public static class RefutationRecord
{
  /// <summary>The state namespace every refutation lives under.
  ///     <para>WORKSPACE-VISIBLE BY CONSTRUCTION: state keys are scoped to the workspace, so a
  ///     record written by one chain is readable by every other chain in it. That is the whole
  ///     point, and it is why the privacy rule had to be restated rather than left claiming the
  ///     opposite.</para>
  ///     <para>ONE DEFINITION: the experiment tool writes here and the chain brief names it, so
  ///     the place a session is told to search and the place a refutation lands cannot drift
  ///     apart.</para></summary>
  public const string Namespace = "refutations";

  /// <summary>The clause carrying the observed output that did the refuting.</summary>
  public const string RefutedMarker = " \u2014 refuted: ";

  /// <summary>The clause carrying the falsifiable claim itself, so a reader sees what was
  ///     predicted AND the command that would have disproved it.</summary>
  public const string PredictedMarker = " \u2014 predicted: ";

  /// <summary>The clause naming what the refutation is against (D7). Named so the stamp is
  ///     greppable and a reader can find it without parsing the prose around it.</summary>
  public const string AgainstMarker = " \u2014 against: ";

  /// <summary>The stamp used when the workspace has no commit to name - it is not a git
  ///     repository, HEAD is unborn, or git could not be run.
  ///     <para>AN HONEST ABSENCE, NEVER A FABRICATED HASH AND NEVER A REFUSAL. A refutation
  ///     found outside git is still a refutation, and refusing to record it would lose the
  ///     finding to keep a field tidy - the opposite of what D8 asks for. What D6's guard
  ///     demands is that the record NAMES what it was refuted against; 'no commit, and here is
  ///     why' names it, where a blank field would be the silence the guard exists to prevent.
  ///     </para></summary>
  public const string NoCommit = "no commit (this workspace has no HEAD commit to name)";

  /// <summary>What the record says when the hypothesis a refuted prediction tested is no longer
  ///     in the ledger. A refutation whose hypothesis cannot be quoted is still a refutation,
  ///     and stating the absence keeps the record's shape intact where omitting the clause
  ///     would silently change it.</summary>
  public const string HypothesisUnavailable = "(hypothesis no longer in this ledger)";

  /// <summary>The state key one refutation lives at: <c>refutations/&lt;slug&gt;-p&lt;id&gt;</c>.
  ///     <para>A PREDICTION TAKES EXACTLY ONE VERDICT, so (slug, prediction id) identifies one
  ///     refutation for the life of the workspace - which is what lets the key be DERIVED
  ///     rather than minted, and lets a re-record land on the same key instead of beside it.
  ///     </para></summary>
  /// <param name="slug">The ledger slug the refutation was established under.</param>
  /// <param name="predictionId">The refuted prediction's id within that ledger.</param>
  public static string KeyFor(string slug, int predictionId)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(slug);
    return $"{Namespace}/{slug}-p{predictionId}";
  }

  /// <summary>The stored value: one line carrying the origin, the hypothesis, the falsifiable
  ///     claim, the evidence that refuted it, and what it was refuted against.
  ///     <para>SINGLE LINE BY CONTRACT: the state store rejects text carrying a newline or
  ///     padding, so the shape is a line rather than a document - the same constraint every
  ///     ledger record lives under, and the reason this is a renderer rather than a serializer.
  ///     </para></summary>
  /// <param name="slug">The ledger slug the refutation was established under.</param>
  /// <param name="predictionId">The refuted prediction's id.</param>
  /// <param name="hypothesis">The hypothesis the prediction tested, or null when the ledger no
  ///     longer holds it.</param>
  /// <param name="prediction">The prediction's own text, which carries its falsifying command.</param>
  /// <param name="evidence">The observed output that refuted it.</param>
  /// <param name="refutedAgainst">What the refutation is against - the commit it was found on,
  ///     or <see cref="NoCommit"/>.</param>
  public static string Render(string slug, int predictionId, string? hypothesis, string prediction,
      string evidence, string refutedAgainst)
  {
    ArgumentException.ThrowIfNullOrWhiteSpace(slug);
    ArgumentException.ThrowIfNullOrWhiteSpace(prediction);
    ArgumentException.ThrowIfNullOrWhiteSpace(evidence);
    ArgumentException.ThrowIfNullOrWhiteSpace(refutedAgainst);
    string claim = string.IsNullOrWhiteSpace(hypothesis) ? HypothesisUnavailable : hypothesis;
    return $"refutation {slug}#{predictionId}: {claim}"
        + $"{PredictedMarker}{prediction}"
        + $"{RefutedMarker}{evidence}"
        + $"{AgainstMarker}{refutedAgainst}";
  }
}
