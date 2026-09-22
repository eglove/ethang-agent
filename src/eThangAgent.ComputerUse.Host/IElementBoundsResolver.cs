namespace eThangAgent.ComputerUse.Host;

/// <summary>The drag path's element-resolution seam: resolves an observation index to
///     the element's bounds center. Production wires UiaElementOps (real UIA); tests
///     wire fakes. False when the element is absent from the cache or its rectangle is
///     empty - the honest failure the dispatcher surfaces.</summary>
public interface IElementBoundsResolver
{
  bool TryResolveBoundsCenter(int element, out int centerX, out int centerY);
}
