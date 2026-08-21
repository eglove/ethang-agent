namespace eThangAgent.StateDomain;

public sealed record TransitionRecord(
    string Id,
    string From,
    string To,
    string Summary,
    IReadOnlyList<string> Evidence,
    string Status,
    DateTimeOffset CreatedAt);
