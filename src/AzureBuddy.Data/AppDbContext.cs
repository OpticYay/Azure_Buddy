using AzureBuddy.Data.Entities;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace AzureBuddy.Data;

/// <summary>
/// IdentityDbContext already wires up the standard Identity tables (AspNetUsers, AspNetRoles, etc.) -
/// we just add our own DbSets on top and configure the relationships/constraints EF can't infer
/// automatically (cascade delete, unique indexes, enum-as-string storage).
/// </summary>
public sealed class AppDbContext : IdentityDbContext<ApplicationUser>
{
    public AppDbContext(DbContextOptions<AppDbContext> options) : base(options)
    {
    }

    public DbSet<UserAdoSettings> UserAdoSettings => Set<UserAdoSettings>();
    public DbSet<ChatSession> ChatSessions => Set<ChatSession>();
    public DbSet<ChatMessage> ChatMessages => Set<ChatMessage>();
    public DbSet<RefreshToken> RefreshTokens => Set<RefreshToken>();
    public DbSet<LlmSettings> LlmSettings => Set<LlmSettings>();
    public DbSet<WorkItemStateConfiguration> WorkItemStateConfigurations => Set<WorkItemStateConfiguration>();

    protected override void OnModelCreating(ModelBuilder builder)
    {
        base.OnModelCreating(builder);

        // One ADO config per user. This unique index is what makes "PUT /api/settings/ado" behave as
        // an upsert (create-or-update) rather than accumulating duplicate rows per user.
        builder.Entity<UserAdoSettings>()
            .HasIndex(s => s.UserId)
            .IsUnique();

        builder.Entity<UserAdoSettings>()
            .HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            // Deleting a user deletes their stored ADO settings too - no orphaned PAT ciphertext left behind.
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<UserAdoSettings>()
            .Property(s => s.OrganizationUrl)
            .HasMaxLength(512);

        builder.Entity<UserAdoSettings>()
            .Property(s => s.DefaultProject)
            .HasMaxLength(512);

        builder.Entity<UserAdoSettings>()
            .Property(s => s.EncryptedPat)
            .HasMaxLength(1024);

        builder.Entity<ChatSession>()
            .HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ChatSession>()
            .Property(s => s.Title)
            .HasMaxLength(128);

        // ListSessionsAsync filters by UserId and orders by UpdatedAt - without this composite index
        // MySQL can use the existing UserId index to narrow rows but still has to filesort every page
        // by UpdatedAt itself.
        builder.Entity<ChatSession>()
            .HasIndex(s => new { s.UserId, s.UpdatedAt });

        builder.Entity<ChatMessage>()
            .HasOne(m => m.Session)
            .WithMany(s => s.Messages)
            // Deleting a session deletes all of its messages in the same transaction - this is the
            // "cascading delete of a user's sessions" behavior called out in the task's schema notes.
            .OnDelete(DeleteBehavior.Cascade);

        // Store the Role enum as its string name ("User"/"Assistant") instead of an integer - makes
        // the raw table readable when debugging, at a trivial storage cost. Bounded to the longest
        // enum member name rather than left as longtext, since it can only ever be "User" or "Assistant".
        builder.Entity<ChatMessage>()
            .Property(m => m.Role)
            .HasConversion<string>()
            .HasMaxLength(16);

        builder.Entity<ChatMessage>()
            .Property(m => m.AdoAttachmentUrl)
            .HasMaxLength(512);

        // GetSessionAsync orders included messages by CreatedAt - this composite index covers both
        // the SessionId filter and that ordering.
        builder.Entity<ChatMessage>()
            .HasIndex(m => new { m.SessionId, m.CreatedAt });

        // LlmSettings is a singleton row - its Id is always exactly LlmSettings.SingletonId (1), by
        // application convention, never database-assigned. Without ValueGeneratedNever(), EF Core
        // configures Id as an auto-increment identity column by default for an int primary key - a
        // freshly created table happens to hand out 1 as ITS first auto-generated value too, so this
        // bug wouldn't show up in casual testing, but it breaks the moment the row is ever deleted and
        // re-inserted (the next auto-increment value would be 2, not 1), silently orphaning the code
        // everywhere that assumes "the row's Id" and "SingletonId" are the same thing.
        builder.Entity<LlmSettings>()
            .Property(s => s.Id)
            .ValueGeneratedNever();

        builder.Entity<LlmSettings>()
            .Property(s => s.ProvidersCsv)
            .HasMaxLength(64);

        builder.Entity<LlmSettings>()
            .Property(s => s.GeminiModel)
            .HasMaxLength(128);

        builder.Entity<LlmSettings>()
            .Property(s => s.GeminiBaseUrl)
            .HasMaxLength(512);

        builder.Entity<LlmSettings>()
            .Property(s => s.GeminiEncryptedApiKey)
            .HasMaxLength(1024);

        builder.Entity<LlmSettings>()
            .Property(s => s.OllamaModel)
            .HasMaxLength(128);

        builder.Entity<LlmSettings>()
            .Property(s => s.OllamaBaseUrl)
            .HasMaxLength(512);

        builder.Entity<RefreshToken>()
            .HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Looking up "is this refresh token still valid" by its hash needs to be fast and is the
        // dominant query pattern against this table.
        builder.Entity<RefreshToken>()
            .HasIndex(t => t.TokenHash);

        // AccountService's RevokeOtherSessionsAsync (and any bulk-revoke path built on the same shape)
        // filters active refresh tokens per user.
        builder.Entity<RefreshToken>()
            .HasIndex(t => new { t.UserId, t.RevokedAt });

        // A state name can only appear once per work item type - this is what makes the admin
        // "add a state" form fail cleanly instead of silently creating a confusing duplicate row.
        builder.Entity<WorkItemStateConfiguration>()
            .HasIndex(s => new { s.WorkItemType, s.StateName })
            .IsUnique();

        // The read path every update-state validation takes (UpdateItemFlow, AdoWorkItemToolset) -
        // "give me the enabled states for this one work item type, in display order" - so an index
        // on WorkItemType alone (the DisplayOrder sort is cheap once the type is narrowed) keeps that
        // lookup fast even as the table grows across many types.
        builder.Entity<WorkItemStateConfiguration>()
            .HasIndex(s => s.WorkItemType);
    }
}
