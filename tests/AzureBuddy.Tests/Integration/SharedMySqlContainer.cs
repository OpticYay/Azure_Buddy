using Testcontainers.MySql;

namespace AzureBuddy.Tests.Integration;

/// <summary>
/// One real MySQL container for the entire test run, not one per test (too slow to start per test -
/// each start takes a few seconds) and not even one per test class - a single container is started
/// lazily on first use and reused by every CustomWebApplicationFactory instance for the rest of the
/// process's lifetime. Test isolation between classes/instances doesn't come from separate containers;
/// each CustomWebApplicationFactory instead gets its own uniquely-named database on this shared server
/// (see CreateIsolatedDatabaseAsync), the same isolation shape the EF InMemory provider gave each
/// factory before this change (a fresh, uniquely-named database per instance).
///
/// <see cref="Lazy{T}"/> around a Task (rather than a plain static field assigned in a static
/// constructor) is what makes this safe under concurrent test classes: the factory delegate only ever
/// runs once even if multiple xUnit test classes construct their CustomWebApplicationFactory at the
/// same time, and every caller awaits the same in-flight start rather than racing to create two
/// containers.
///
/// Deliberately never disposed here: Testcontainers' own "Ryuk" resource-reaper container watches the
/// test process and removes every container it started as soon as that process exits, so there is
/// nothing left running after `dotnet test` finishes - explicitly stopping the container ourselves
/// would only need to duplicate that, with no isolation or speed benefit.
/// </summary>
internal static class SharedMySqlContainer
{
    private static readonly Lazy<Task<MySqlContainer>> LazyContainer = new(StartAsync);

    public static Task<MySqlContainer> GetAsync() => LazyContainer.Value;

    private static async Task<MySqlContainer> StartAsync()
    {
        var container = new MySqlBuilder("mysql:8.0")
            .WithDatabase("azurebuddy_test")
            .WithUsername("test_user")
            .WithPassword("test_password")
            .Build();

        await container.StartAsync();
        return container;
    }
}
