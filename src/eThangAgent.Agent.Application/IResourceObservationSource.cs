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

  /// <summary>The APP PROCESS's own memory, in bytes, as of the last observation.
  ///     <para>The app process is a consumer of the app-wide budget, but it is NOT a
  ///     conversation: the governor picks the largest OBSERVATION as its target and
  ///     enacts a rung against it, so an 'app' row presented as a conversation is a
  ///     decision about a conversation that does not exist - it produced an audit row
  ///     naming 'conversation app' and a refusal, every tick, for the app's own
  ///     memory. The app's usage therefore arrives here as a NUMBER on its own
  ///     channel, where the governor counts it in the app-wide picture without ever
  ///     being able to select it as a target.</para>
  ///     <para>A default member: an observation source with no app process to report
  ///     simply reports zero.</para></summary>
  long AppUsageBytes => 0;
}
