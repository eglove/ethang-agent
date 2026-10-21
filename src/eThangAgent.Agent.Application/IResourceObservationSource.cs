namespace eThangAgent.Agent.Application;

/// <summary>One conversation's current memory footprint and the spawned programs it
///     owns (spec D5). Program ids are opaque handles the enactment side understands -
///     the governor never inspects processes itself.</summary>
public sealed record ResourceObservation(string ConversationId, long UsageBytes, IReadOnlyList<string> ProgramIds);

/// <summary>Where the governor reads the world from. The host supplies the real
///     implementation; tests supply fixed observations.</summary>
public interface IResourceObservationSource
{
  IReadOnlyList<ResourceObservation> Observe();
}
