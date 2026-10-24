namespace eThangAgent.ToolDomain;

/// <summary>One hunk of a multi-hunk edit: a literal anchor, its replacement, and the
///     selector that says how many matches were expected. The selector is the SAME
///     either-or rule anchor mode uses — exactly one of <c>All</c> or <c>Occurrences</c>
///     — never a silent default: a hunk that does not say how many matches it expects
///     is genuinely ambiguous (issue #252 keeps the strict rule; #259 records why
///     defaulting it was rejected).</summary>
public sealed record EditHunk(string Old, string Replacement, bool All, int Occurrences);
