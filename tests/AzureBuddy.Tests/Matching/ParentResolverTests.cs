using AzureBuddy.Core.Matching;
using Xunit;

namespace AzureBuddy.Tests.Matching;

public class ParentResolverTests
{
    [Fact]
    public void Resolve_ExactTitleMatch_ResolvesToThatCandidate()
    {
        var candidates = new[]
        {
            new ParentCandidate(101, "SOA report", "User Story"),
            new ParentCandidate(102, "Login page redesign", "User Story")
        };

        var result = ParentResolver.Resolve(candidates, "SOA report");

        Assert.True(result.Resolved);
        Assert.Equal(101, result.ParentId);
    }

    [Fact]
    public void Resolve_SubstringMatch_Resolves()
    {
        var candidates = new[]
        {
            new ParentCandidate(101, "Testing of SOA report", "Task"),
            new ParentCandidate(102, "Login page redesign", "User Story")
        };

        var result = ParentResolver.Resolve(candidates, "SOA report");

        Assert.True(result.Resolved);
        Assert.Equal(101, result.ParentId);
    }

    [Fact]
    public void Resolve_AmbiguousCandidatesWithinMargin_DoesNotResolve()
    {
        var candidates = new[]
        {
            new ParentCandidate(101, "SOA report task", "Task"),
            new ParentCandidate(102, "SOA report testing", "Task")
        };

        var result = ParentResolver.Resolve(candidates, "SOA report");

        Assert.False(result.Resolved);
        Assert.Null(result.ParentId);
    }

    [Fact]
    public void Resolve_NoCandidates_DoesNotResolve()
    {
        var result = ParentResolver.Resolve(Array.Empty<ParentCandidate>(), "anything");

        Assert.False(result.Resolved);
        Assert.Null(result.ParentId);
    }

    [Fact]
    public void Resolve_LowOverlapBelowThreshold_DoesNotResolve()
    {
        var candidates = new[]
        {
            new ParentCandidate(101, "Completely unrelated title", "Task")
        };

        var result = ParentResolver.Resolve(candidates, "SOA report");

        Assert.False(result.Resolved);
    }

    [Fact]
    public void Resolve_SingleCandidateClearsThreshold_ResolvesWithoutRunnerUp()
    {
        var candidates = new[]
        {
            new ParentCandidate(101, "soa report", "Task")
        };

        var result = ParentResolver.Resolve(candidates, "soa report extended");

        Assert.True(result.Resolved);
        Assert.Equal(101, result.ParentId);
    }
}
