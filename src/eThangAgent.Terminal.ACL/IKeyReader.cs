namespace eThangAgent.Terminal.ACL;

/// <summary>Blocking key source. Returns <c>null</c> when the source is exhausted (EOF).</summary>
public interface IKeyReader
{
    ConsoleKeyInfo? ReadKey();
}
