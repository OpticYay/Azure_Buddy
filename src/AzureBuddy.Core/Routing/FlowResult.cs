namespace AzureBuddy.Core.Routing;

/// <summary>
/// Result of a deterministic flow attempt. Handled=false means the flow couldn't resolve the request
/// on its own (no parent match, no target id given, ambiguous duplicate, etc.) and the caller should
/// fall through to the full conversational AI Agent - mirroring every "AI Agent" fallback branch in the
/// n8n workflow's IF/Switch nodes (Has Candidates?, Parent Resolved?, Duplicate Found?, Has Target Id?, ...).
/// </summary>
public sealed record FlowResult(bool Handled, string? Output)
{
    public static FlowResult Done(string output) => new(true, output);
    public static FlowResult FallThroughToAgent() => new(false, null);
}
