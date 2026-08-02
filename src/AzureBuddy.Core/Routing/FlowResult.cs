using AzureBuddy.Data.Entities;

namespace AzureBuddy.Core.Routing;

/// <summary>
/// Result of a deterministic flow attempt. Handled=false means the flow couldn't resolve the request
/// on its own (no parent match, no target id given, ambiguous duplicate, etc.) and the caller should
/// fall through to the full conversational AI Agent - mirroring every "AI Agent" fallback branch in the
/// n8n workflow's IF/Switch nodes (Has Candidates?, Parent Resolved?, Duplicate Found?, Has Target Id?, ...).
///
/// Type/WorkItemId/TableHeaders/TableRows carry exactly what each flow already knows about the SHAPE
/// of the text it built in Output - e.g. CreateBugFlow knows the moment it builds "Bug #123 created..."
/// that this is a Confirmation about work item 123, not just a string. Previously that knowledge was
/// thrown away as soon as the flow returned a bare string; IntentRouter and ChatController now carry
/// it all the way out to the stored ChatMessage instead of the frontend having to regex it back out.
/// </summary>
public sealed record FlowResult(
    bool Handled,
    string? Output,
    ChatMessageType Type = ChatMessageType.Text,
    int? WorkItemId = null,
    IReadOnlyList<string>? TableHeaders = null,
    IReadOnlyList<IReadOnlyList<string>>? TableRows = null)
{
    public static FlowResult Done(string output) => new(true, output);

    public static FlowResult DoneWithConfirmation(string output, int workItemId) =>
        new(true, output, ChatMessageType.Confirmation, WorkItemId: workItemId);

    public static FlowResult DoneWithTable(string output, IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows) =>
        new(true, output, ChatMessageType.Table, TableHeaders: headers, TableRows: rows);

    public static FlowResult DoneWithError(string output) => new(true, output, ChatMessageType.Error);

    public static FlowResult FallThroughToAgent() => new(false, null);
}
