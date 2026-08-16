namespace AzureBuddy.Core.Caching;

/// <summary>Binds to the "Redis" config section. Leave Configuration blank to run without Redis - the
/// app falls back to today's in-memory/per-instance behaviour (see RedisConnectionFactory.TryCreate),
/// which is fine for `dotnet run` and for a single-instance deployment but means the app cannot be
/// scaled beyond one replica.</summary>
public sealed class RedisOptions
{
    public const string SectionName = "Redis";

    public string Configuration { get; init; } = string.Empty;

    /// <summary>Prefixed onto every key this app writes (chat windows, the Data Protection key ring,
    /// the LLM settings pub/sub channel) so one Redis instance can safely host other apps, and so each
    /// integration test factory can claim its own isolated namespace.</summary>
    public string InstanceName { get; init; } = "azurebuddy:";

    public int ChatWindowTtlHours { get; init; } = 24;
}
