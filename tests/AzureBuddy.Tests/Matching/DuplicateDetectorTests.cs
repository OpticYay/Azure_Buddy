using AzureBuddy.Core.Matching;
using Xunit;

namespace AzureBuddy.Tests.Matching;

public class DuplicateDetectorTests
{
    [Fact]
    public void FindDuplicate_HighWordOverlap_FlagsDuplicate()
    {
        var existing = new[]
        {
            new ExistingBug(201, "Login button unresponsive on Safari"),
            new ExistingBug(202, "SOA report export fails for large datasets")
        };

        var result = DuplicateDetector.FindDuplicate("Login button unresponsive on Chrome", existing);

        Assert.True(result.DuplicateFound);
        Assert.Equal(201, result.BestMatch!.Id);
    }

    [Fact]
    public void FindDuplicate_LowOverlap_DoesNotFlag()
    {
        var existing = new[]
        {
            new ExistingBug(201, "Login button unresponsive on Safari")
        };

        var result = DuplicateDetector.FindDuplicate("Report export crashes on empty dataset", existing);

        Assert.False(result.DuplicateFound);
    }

    [Fact]
    public void FindDuplicate_NoExistingBugs_DoesNotFlag()
    {
        var result = DuplicateDetector.FindDuplicate("Anything", Array.Empty<ExistingBug>());

        Assert.False(result.DuplicateFound);
        Assert.Null(result.BestMatch);
    }

    [Fact]
    public void FindDuplicate_PicksHighestScoringMatch()
    {
        var existing = new[]
        {
            new ExistingBug(201, "Report export fails"),
            new ExistingBug(202, "Report export fails for large datasets on Chrome")
        };

        var result = DuplicateDetector.FindDuplicate("Report export fails for large datasets", existing);

        Assert.True(result.DuplicateFound);
        Assert.Equal(202, result.BestMatch!.Id);
    }
}
