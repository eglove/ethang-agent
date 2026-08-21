namespace eThangAgent.ModelDomain;

/// <summary>Creates a provider bound to a specific per-spawn model configuration. Implemented by provider ACLs; one credential set can serve every model.</summary>
public interface IModelProviderFactory
{
    IModelProvider Create(ModelConfig config);
}
