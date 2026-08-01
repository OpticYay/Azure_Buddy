using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace AzureBuddy.Data;

/// <summary>
/// Lets `dotnet ef migrations add` build an AppDbContext without running the actual web app (which is
/// where the real connection string/DI setup lives). We pass ServerVersion.Create(...) with an
/// explicit MySQL version instead of ServerVersion.AutoDetect(connectionString) specifically so that
/// generating a migration doesn't require a live MySQL server to be reachable at design time - only
/// running the app for real does.
/// </summary>
public sealed class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<AppDbContext>
{
    public AppDbContext CreateDbContext(string[] args)
    {
        var optionsBuilder = new DbContextOptionsBuilder<AppDbContext>();

        optionsBuilder.UseMySql(
            "server=localhost;database=azurebuddy;user=azurebuddy;password=placeholder",
            new MySqlServerVersion(new Version(8, 0, 34)));

        return new AppDbContext(optionsBuilder.Options);
    }
}
