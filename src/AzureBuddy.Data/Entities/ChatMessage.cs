namespace AzureBuddy.Data.Entities;

public enum ChatMessageRole
{
    User,
    Assistant
}

/// <summary>
/// What KIND of content a message's Content string represents - previously every message was an
/// untagged plain string regardless of whether it was really conversational text, a "| ID | Title |"
/// markdown table, a "Bug #123 created" confirmation, or a failure message, forcing the frontend to
/// regex-guess the type back out of the text. Set once, at the point each message is created, by
/// whichever flow/service actually knows what it produced (see FlowResult in AzureBuddy.Core.Routing
/// and ChatSessionService) - never inferred later.
/// </summary>
public enum ChatMessageType
{
    Text,
    Table,
    Confirmation,
    Error
}

/// <summary>
/// One message in a ChatSession. Screenshots are never stored here (or anywhere on disk/blob storage) -
/// when a user attaches one, it's forwarded straight to Azure DevOps as a work-item attachment, and only
/// the URL Azure DevOps hands back is saved in AdoAttachmentUrl. The raw image bytes are discarded as
/// soon as that call completes. If the ADO call fails, no message row with a "successful" attachment is
/// ever written - see ChatSessionService.AppendMessageWithScreenshotAsync for where that's enforced.
/// </summary>
public sealed class ChatMessage
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required Guid SessionId { get; set; }
    public ChatSession? Session { get; set; }

    public required ChatMessageRole Role { get; set; }
    public required string Content { get; set; }

    /// <summary>Set only when a screenshot in this message was successfully attached to an ADO work item.</summary>
    public string? AdoAttachmentUrl { get; set; }

    /// <summary>The ADO work item this message (and its attachment, if any) relates to. Also populated
    /// for a Confirmation-typed message now (e.g. "Bug #123 created") - previously ChatController never
    /// set this for chat replies at all, even though the text already named a specific work item.</summary>
    public int? WorkItemId { get; set; }

    public ChatMessageType Type { get; set; } = ChatMessageType.Text;

    /// <summary>JSON-serialized AzureBuddy.Core.Chat.ChatMessageTableData ({headers, rows}) - set only
    /// when Type is Table, null otherwise. Kept as a single JSON column rather than separate
    /// headers/rows columns since a table's column count varies by flow and EF Core has no clean way
    /// to map a jagged string[][] to relational columns directly.</summary>
    public string? TableDataJson { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
