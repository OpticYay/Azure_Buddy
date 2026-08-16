using System.Xml;
using System.Xml.Linq;
using StackExchange.Redis;

namespace AzureBuddy.Core.Caching;

/// <summary>
/// One-time migration helper: copies Data Protection keys already on a filesystem key ring (each one a
/// "key-{guid}.xml" file under the directory PersistKeysToFileSystem was pointed at) into the Redis list
/// PersistKeysToStackExchangeRedis reads from - so an existing single-instance deployment can cut over to
/// Redis-backed keys without every currently-encrypted PAT/LLM API key becoming unreadable the moment the
/// switch flips (see Program.cs's Data Protection bootstrap, which picks between the two based on
/// whether Redis is configured).
///
/// Not wired into the app's normal startup path - intended to be run once, manually, during the cutover
/// (see the README's migration runbook), after which every replica reads/writes the same Redis-backed
/// key ring going forward and this tool never needs to run again for that deployment.
/// </summary>
public static class DataProtectionKeyImporter
{
    /// <summary>Idempotent: keys already present in the Redis list (matched by their "id" attribute) are
    /// skipped, so accidentally running this twice - or running it again after a partial failure -
    /// doesn't duplicate entries. Returns the number of keys actually imported.</summary>
    public static async Task<int> ImportAsync(string keyDirectoryPath, IConnectionMultiplexer multiplexer, string redisKey)
    {
        var directory = new DirectoryInfo(keyDirectoryPath);
        if (!directory.Exists)
        {
            return 0;
        }

        var db = multiplexer.GetDatabase();
        var alreadyImported = (await db.ListRangeAsync(redisKey))
            .Select(value => TryGetKeyId((string)value!))
            .Where(id => id is not null)
            .ToHashSet();

        var imported = 0;
        foreach (var file in directory.GetFiles("key-*.xml"))
        {
            var xml = await File.ReadAllTextAsync(file.FullName);
            var id = TryGetKeyId(xml);
            if (id is null || alreadyImported.Contains(id))
            {
                continue;
            }

            await db.ListRightPushAsync(redisKey, xml);
            imported++;
        }

        return imported;
    }

    private static string? TryGetKeyId(string xml)
    {
        try
        {
            return XElement.Parse(xml).Attribute("id")?.Value;
        }
        catch (XmlException)
        {
            return null;
        }
    }
}
