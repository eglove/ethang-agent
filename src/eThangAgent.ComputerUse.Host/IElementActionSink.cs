namespace eThangAgent.ComputerUse.Host;

/// <summary>The element-op surface the input dispatcher routes to (fix round 5):
///     click invokes, scroll uses the UIA scroll pattern, type focuses. UiaElementOps
///     implements it over the real walk cache; tests wire journaling doubles through
///     the same seam. Every method answers an honest BrokerResponse - accepted after a
///     real pattern call, a typed wire error otherwise.</summary>
public interface IElementActionSink
{
  BrokerResponse Invoke(int element);

  BrokerResponse Focus(int element);

  BrokerResponse Scroll(int element, string direction, int pages);
}
