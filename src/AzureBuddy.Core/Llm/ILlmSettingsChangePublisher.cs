namespace AzureBuddy.Core.Llm;

/// <summary>
/// Tells every OTHER replica that the LLM settings row changed, so LlmSettingsChangeNotifier can refresh
/// their own in-memory ILlmSettingsProvider from the database. LlmSettingsService.SaveAsync already
/// updates THIS replica's provider directly via Refresh() - this interface exists purely for the
/// multi-replica case that Refresh() alone can't reach.
/// </summary>
public interface ILlmSettingsChangePublisher
{
    Task PublishAsync(CancellationToken cancellationToken = default);
}

/// <summary>Used whenever Redis isn't configured - a no-op, not an error, since a single-instance
/// deployment has no other replicas to notify in the first place.</summary>
public sealed class NoOpLlmSettingsChangePublisher : ILlmSettingsChangePublisher
{
    public Task PublishAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;
}

/// <summary>The Redis channel name both RedisLlmSettingsChangePublisher and LlmSettingsChangeNotifier
/// derive their subscription/publish target from - kept in one place so the two can never drift apart.</summary>
internal static class LlmSettingsChangeChannel
{
    public const string Suffix = "llm-settings-changed";
}
