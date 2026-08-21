namespace eThangAgent.StateDomain;

public sealed record StateEvent(long Id, string Kind, string PayloadJson, DateTimeOffset OccurredAt);
