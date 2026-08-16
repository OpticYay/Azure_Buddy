using AzureBuddy.Core.Caching;
using AzureBuddy.Core.Llm;
using AzureBuddy.Data;
using AzureBuddy.Data.Entities;
using AzureBuddy.Tests.Integration;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using StackExchange.Redis;
using Xunit;

namespace AzureBuddy.Tests.Llm;

/// <summary>Exercises RedisLlmSettingsChangePublisher and LlmSettingsChangeNotifier together against a
/// real Redis container (see SharedRedisContainer) - the two only mean something in combination: one
/// publishes, the other subscribes and reacts by re-reading LlmSettings from the database. Deliberately
/// doesn't go through LlmSettingsService.SaveAsync's own publish call - the row here is written directly,
/// standing in for "another replica already committed this change," which is exactly the scenario this
/// notifier exists for.</summary>
public class LlmSettingsChangeNotifierTests : IAsyncLifetime
{
    private IConnectionMultiplexer _multiplexer = null!;
    private string _instanceName = null!;
    private ServiceProvider _serviceProvider = null!;
    private LlmSettingsProvider _settingsProvider = null!;

    public async Task InitializeAsync()
    {
        var container = await SharedRedisContainer.GetAsync();
        _multiplexer = await ConnectionMultiplexer.ConnectAsync(container.GetConnectionString());
        _instanceName = $"test:{Guid.NewGuid():N}:";

        var dbName = Guid.NewGuid().ToString();
        var keyPath = Path.Combine(Path.GetTempPath(), $"azurebuddy-test-keys-{Guid.NewGuid():N}");

        var services = new ServiceCollection();
        services.AddLogging();
        services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(dbName));
        services.AddSingleton<IDataProtectionProvider>(_ => DataProtectionProvider.Create(new DirectoryInfo(keyPath)));
        services.AddScoped<LlmApiKeyProtector>();
        services.AddSingleton<ILlmSettingsChangePublisher, NoOpLlmSettingsChangePublisher>();
        services.AddScoped<LlmSettingsService>();

        _settingsProvider = new LlmSettingsProvider(Options.Create(new LlmOptions()));
        services.AddSingleton<ILlmSettingsProvider>(_settingsProvider);

        _serviceProvider = services.BuildServiceProvider();
    }

    public Task DisposeAsync()
    {
        _serviceProvider.Dispose();
        _multiplexer.Dispose();
        return Task.CompletedTask;
    }

    [Fact]
    public async Task WhenPublisherPublishes_NotifierRefreshesThisReplicasProviderFromTheDatabase()
    {
        using (var scope = _serviceProvider.CreateScope())
        {
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();
            db.LlmSettings.Add(new LlmSettings { ProvidersCsv = "Ollama", OllamaModel = "test-model" });
            await db.SaveChangesAsync();
        }

        Assert.Empty(_settingsProvider.Current.Providers);

        var notifier = new LlmSettingsChangeNotifier(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new RedisOptions { InstanceName = _instanceName }),
            NullLogger<LlmSettingsChangeNotifier>.Instance,
            _multiplexer);
        await notifier.StartAsync(CancellationToken.None);

        try
        {
            var publisher = new RedisLlmSettingsChangePublisher(_multiplexer, Options.Create(new RedisOptions { InstanceName = _instanceName }));
            await publisher.PublishAsync();

            // Pub/sub delivery is asynchronous even on a local container - poll briefly rather than
            // assuming the subscription callback has already run by the time PublishAsync returns.
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (DateTime.UtcNow < deadline && _settingsProvider.Current.Providers.Count == 0)
            {
                await Task.Delay(50);
            }

            Assert.Contains("Ollama", _settingsProvider.Current.Providers);
            Assert.Equal("test-model", _settingsProvider.Current.Ollama.Model);
        }
        finally
        {
            await notifier.StopAsync(CancellationToken.None);
        }
    }

    [Fact]
    public async Task DifferentInstanceNamePrefix_DoesNotReceiveTheNotification()
    {
        // Cross-check that the instance-name key prefix genuinely isolates unrelated deployments on the
        // same Redis server, not just unrelated sessions on the same test run.
        var notifier = new LlmSettingsChangeNotifier(
            _serviceProvider.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new RedisOptions { InstanceName = _instanceName }),
            NullLogger<LlmSettingsChangeNotifier>.Instance,
            _multiplexer);
        await notifier.StartAsync(CancellationToken.None);

        try
        {
            var differentPrefixPublisher = new RedisLlmSettingsChangePublisher(
                _multiplexer, Options.Create(new RedisOptions { InstanceName = $"other:{Guid.NewGuid():N}:" }));
            await differentPrefixPublisher.PublishAsync();

            await Task.Delay(300);

            Assert.Empty(_settingsProvider.Current.Providers);
        }
        finally
        {
            await notifier.StopAsync(CancellationToken.None);
        }
    }
}
