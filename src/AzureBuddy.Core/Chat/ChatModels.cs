using System.ComponentModel.DataAnnotations;
using AzureBuddy.Data.Entities;

namespace AzureBuddy.Core.Chat;

public sealed record ChatSessionSummary(Guid Id, string Title, DateTime CreatedAt, DateTime UpdatedAt);

/// <summary>Structured payload for a Table-typed message - the exact rows/columns a deterministic flow
/// (ViewBugsFlow, MyItemsFlow) built, instead of the frontend re-parsing a "| ID | Title |" markdown
/// string back into a table. Null for every other message type.</summary>
public sealed record ChatMessageTableData(IReadOnlyList<string> Headers, IReadOnlyList<IReadOnlyList<string>> Rows);

public sealed record ChatMessageView(
    Guid Id,
    ChatMessageRole Role,
    string Content,
    string? AdoAttachmentUrl,
    int? WorkItemId,
    ChatMessageType Type,
    ChatMessageTableData? Table,
    DateTime CreatedAt);

public sealed record ChatSessionDetail(Guid Id, string Title, DateTime CreatedAt, DateTime UpdatedAt, IReadOnlyList<ChatMessageView> Messages);

/// <summary>Generic paged wrapper. Every consumer of a paged endpoint used to have to recompute "is
/// there another page?" itself from Page/PageSize/TotalCount (the frontend's SessionList did exactly
/// this) - HasMore computes that once, here, so every client gets the same answer for free.</summary>
public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount)
{
    public bool HasMore => (long)Page * PageSize < TotalCount;
}

public sealed record CreateSessionRequest(string? Title);

public sealed record RenameSessionRequest([Required] string Title);

/// <summary>Result of appending a message that may have included a screenshot. Success is false only
/// for the screenshot-upload-failed case; Message is still populated either way so the caller has
/// something to show in the chat (an error message on failure, per the "don't silently drop it" rule).</summary>
public sealed record AppendMessageResult(bool Success, ChatMessageView Message, string? Error);
