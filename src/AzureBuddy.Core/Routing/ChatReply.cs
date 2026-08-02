using AzureBuddy.Data.Entities;

namespace AzureBuddy.Core.Routing;

/// <summary>What IntentRouter.RouteAsync now returns instead of a bare string - carries the same
/// Type/WorkItemId/Table shape a FlowResult does (see FlowResult.cs), so ChatController can persist
/// and return a properly-tagged message instead of a plain string with the shape thrown away.</summary>
public sealed record ChatReply(
    string Text,
    ChatMessageType Type = ChatMessageType.Text,
    int? WorkItemId = null,
    IReadOnlyList<string>? TableHeaders = null,
    IReadOnlyList<IReadOnlyList<string>>? TableRows = null);
