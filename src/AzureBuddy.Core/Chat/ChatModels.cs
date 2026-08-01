using AzureBuddy.Data.Entities;

namespace AzureBuddy.Core.Chat;

public sealed record ChatSessionSummary(Guid Id, string Title, DateTime CreatedAt, DateTime UpdatedAt);

public sealed record ChatMessageView(Guid Id, ChatMessageRole Role, string Content, string? AdoAttachmentUrl, int? WorkItemId, DateTime CreatedAt);

public sealed record ChatSessionDetail(Guid Id, string Title, DateTime CreatedAt, DateTime UpdatedAt, IReadOnlyList<ChatMessageView> Messages);

public sealed record PagedResult<T>(IReadOnlyList<T> Items, int Page, int PageSize, int TotalCount);

public sealed record CreateSessionRequest(string? Title);

public sealed record AppendMessageRequest(ChatMessageRole Role, string Content, int? WorkItemId);

/// <summary>Result of appending a message that may have included a screenshot. Success is false only
/// for the screenshot-upload-failed case; Message is still populated either way so the caller has
/// something to show in the chat (an error message on failure, per the "don't silently drop it" rule).</summary>
public sealed record AppendMessageResult(bool Success, ChatMessageView Message, string? Error);
