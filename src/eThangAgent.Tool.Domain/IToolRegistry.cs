namespace eThangAgent.ToolDomain;

public interface IToolRegistry
{
  ITool? Find(string name);
  IReadOnlyList<ToolDefinition> Definitions { get; }
}
