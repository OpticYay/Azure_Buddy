using System.Security.Cryptography;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Data;
using AzureBuddy.Data.Entities;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace AzureBuddy.Core.Settings;

/// <summary>
/// Reads/writes each user's own ADO connection config. Every method here takes an explicit userId and
/// every EF query below filters "WHERE UserId = @userId" - that's the enforcement point for
/// "a user must never fetch another user's ADO config" (IDOR prevention). There's deliberately no
/// method that takes just a settings row id without also checking ownership.
/// </summary>
public sealed class UserAdoConfigService
{
    private readonly AppDbContext _dbContext;
    private readonly PatProtector _patProtector;
    private readonly IAdoClient _adoClient;
    private readonly ILogger<UserAdoConfigService> _logger;

    public UserAdoConfigService(AppDbContext dbContext, PatProtector patProtector, IAdoClient adoClient, ILogger<UserAdoConfigService> logger)
    {
        _dbContext = dbContext;
        _patProtector = patProtector;
        _adoClient = adoClient;
        _logger = logger;
    }

    public async Task<AdoSettingsView> GetAsync(string userId, CancellationToken cancellationToken = default)
    {
        var settings = await FindByUserAsync(userId, cancellationToken);
        return ToView(settings, _patProtector);
    }

    public async Task<AdoSettingsView> UpsertAsync(string userId, SaveAdoSettingsRequest request, CancellationToken cancellationToken = default)
    {
        var settings = await FindByUserAsync(userId, cancellationToken);
        var encryptedPat = _patProtector.Encrypt(request.PersonalAccessToken);

        if (settings is null)
        {
            settings = new UserAdoSettings
            {
                UserId = userId,
                OrganizationUrl = request.OrganizationUrl,
                DefaultProject = request.DefaultProject,
                EncryptedPat = encryptedPat,
                UpdatedAt = DateTime.UtcNow
            };
            _dbContext.UserAdoSettings.Add(settings);
        }
        else
        {
            settings.OrganizationUrl = request.OrganizationUrl;
            settings.DefaultProject = request.DefaultProject;
            settings.EncryptedPat = encryptedPat;
            settings.UpdatedAt = DateTime.UtcNow;
        }

        await _dbContext.SaveChangesAsync(cancellationToken);
        return ToView(settings, _patProtector);
    }

    public async Task DeleteAsync(string userId, CancellationToken cancellationToken = default)
    {
        var settings = await FindByUserAsync(userId, cancellationToken);
        if (settings is not null)
        {
            _dbContext.UserAdoSettings.Remove(settings);
            await _dbContext.SaveChangesAsync(cancellationToken);
        }
    }

    /// <summary>Decrypts the stored PAT and builds the AdoConnectionContext every ADO call needs.
    /// Returns null if the user hasn't configured ADO yet - callers (e.g. ChatController) turn that
    /// into a clear "configure your ADO settings first" response rather than a confusing 500.</summary>
    public async Task<AdoConnectionContext?> GetConnectionContextAsync(string userId, CancellationToken cancellationToken = default)
    {
        var settings = await FindByUserAsync(userId, cancellationToken);
        if (settings is null)
        {
            return null;
        }

        return new AdoConnectionContext(settings.OrganizationUrl, settings.DefaultProject, _patProtector.Decrypt(settings.EncryptedPat));
    }

    public async Task<TestConnectionResult> TestConnectionAsync(string userId, CancellationToken cancellationToken = default)
    {
        var context = await GetConnectionContextAsync(userId, cancellationToken);
        if (context is null)
        {
            return new TestConnectionResult(false, "No Azure DevOps settings saved yet.");
        }

        try
        {
            var success = await _adoClient.TestConnectionAsync(context, cancellationToken);
            return success
                ? new TestConnectionResult(true, null)
                : new TestConnectionResult(false, "Azure DevOps rejected the request - check the organization URL, project name, and PAT.");
        }
        catch (AdoApiException ex)
        {
            _logger.LogWarning(ex, "ADO test-connection failed for user {UserId}.", userId);
            return new TestConnectionResult(false, ex.Message);
        }
    }

    private Task<UserAdoSettings?> FindByUserAsync(string userId, CancellationToken cancellationToken) =>
        _dbContext.UserAdoSettings.SingleOrDefaultAsync(s => s.UserId == userId, cancellationToken);

    private static AdoSettingsView ToView(UserAdoSettings? settings, PatProtector patProtector)
    {
        if (settings is null)
        {
            return new AdoSettingsView(false, null, null, null, null);
        }

        return new AdoSettingsView(true, settings.OrganizationUrl, settings.DefaultProject, MaskPat(settings, patProtector), settings.UpdatedAt);
    }

    /// <summary>
    /// Decrypts only to build a "••••••••1234"-style mask (last 4 chars visible, same pattern as a
    /// masked credit card) - this is safe specifically because it's the PAT owner reading their own
    /// data back; the full plaintext is still never included in the response. If decryption fails
    /// (e.g. the Data Protection key ring rotated/was lost), we fall back to a generic mask rather
    /// than raising an error on a simple GET.
    /// </summary>
    private static string MaskPat(UserAdoSettings settings, PatProtector patProtector)
    {
        try
        {
            var pat = patProtector.Decrypt(settings.EncryptedPat);
            var lastFour = pat.Length >= 4 ? pat[^4..] : pat;
            return new string('•', 8) + lastFour;
        }
        catch (CryptographicException)
        {
            return new string('•', 12);
        }
    }
}
