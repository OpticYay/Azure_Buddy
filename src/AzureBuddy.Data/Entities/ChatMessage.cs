namespace AzureBuddy.Data.Entities;

public enum ChatMessageRole
{
    User,
    Assistant
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

    /// <summary>The ADO work item this message (and its attachment, if any) relates to.</summary>
    public int? WorkItemId { get; set; }

    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
}
