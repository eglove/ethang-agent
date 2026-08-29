namespace eThangAgent.Zai.ACL;

/// <summary>Which z.ai endpoint family a configuration targets. GLM Coding Plan
///     subscriptions are entitled only on the coding endpoint — a coding key against the
///     general endpoint is rejected with HTTP 429 — while pay-as-you-go API keys work
///     only on the general platform endpoint.</summary>
public enum ZaiEndpointMode
{
  /// <summary>The GLM Coding Plan path (<c>/coding/paas/v4/…</c>): chat completions only —
  ///     the capability APIs do not exist there.</summary>
  CodingPlan,

  /// <summary>The general platform path (<c>/paas/v4/…</c>): chat completions plus the
  ///     capability APIs (web search, reader, tokenizer, image, OCR, transcription).</summary>
  GeneralApi,
}
