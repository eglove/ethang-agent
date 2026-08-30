namespace eThangAgent.ModelDomain;

/// <summary>Estimated token counts per cost bucket of the last request, for the statusline
///     hover. An estimate (character-share scaling), never provider truth.</summary>
public sealed record ContextBreakdown(int? SystemPromptTokens, int? MessageTokens, int? ToolTokens);
