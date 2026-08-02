namespace AzureBuddy.Data.Entities;

/// <summary>One conversation thread, owned by exactly one user. Title is a short human-readable
/// summary (e.g. first few words of the first message) shown in the session list UI.</summary>
public sealed class ChatSession
{
    public Guid Id { get; set; } = Guid.NewGuid();

    public required string UserId { get; set; }
    public ApplicationUser? User { get; set; }

    public string Title { get; set; } = "New conversation";
    public DateTime CreatedAt { get; set; } = DateTime.UtcNow;
    public DateTime UpdatedAt { get; set; } = DateTime.UtcNow;

    public List<ChatMessage> Messages { get; set; } = new();
}
