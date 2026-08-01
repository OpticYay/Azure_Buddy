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

        builder.Entity<ChatSession>()
            .HasOne(s => s.User)
            .WithMany()
            .HasForeignKey(s => s.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        builder.Entity<ChatMessage>()
            .HasOne(m => m.Session)
            .WithMany(s => s.Messages)
            // Deleting a session deletes all of its messages in the same transaction - this is the
            // "cascading delete of a user's sessions" behavior called out in the task's schema notes.
            .OnDelete(DeleteBehavior.Cascade);

        // Store the Role enum as its string name ("User"/"Assistant") instead of an integer - makes
        // the raw table readable when debugging, at a trivial storage cost.
        builder.Entity<ChatMessage>()
            .Property(m => m.Role)
            .HasConversion<string>();

        builder.Entity<RefreshToken>()
            .HasOne(t => t.User)
            .WithMany()
            .HasForeignKey(t => t.UserId)
            .OnDelete(DeleteBehavior.Cascade);

        // Looking up "is this refresh token still valid" by its hash needs to be fast and is the
        // dominant query pattern against this table.
        builder.Entity<RefreshToken>()
            .HasIndex(t => t.TokenHash);
    }
}
