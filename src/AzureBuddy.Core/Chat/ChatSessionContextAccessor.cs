namespace AzureBuddy.Core.Chat;

/// <summary>
/// Holds "which session is this request for", set once per HTTP request by ChatController right after
/// session resolution - mirrors AdoConnectionContextAccessor's pattern. The agent's attach_file_to_work_item
/// tool needs the session id to look up IPendingAttachmentStore, but tool methods only ever receive their
/// own JSON arguments (the model never knows or supplies a session id), so this is how that value reaches
/// AdoWorkItemToolset without threading it through every method signature. Deliberately redundant with
/// ChatSessionWindow.SessionId (which IntentRouter already has) - kept in sync manually by ChatController.
/// </summary>
public sealed class ChatSessionContextAccessor
{
    public string? SessionId { get; set; }
}
