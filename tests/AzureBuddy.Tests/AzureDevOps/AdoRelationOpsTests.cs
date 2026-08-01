using System.Text.Json;
using AzureBuddy.Core.AzureDevOps;
using Xunit;

namespace AzureBuddy.Tests.AzureDevOps;

public class AdoRelationOpsTests
{
    [Fact]
    public void ParentLink_ProducesHierarchyReverseRelation()
    {
        var op = AdoRelationOps.ParentLink("https://dev.azure.com/org/proj/_apis/wit/workItems/42");

        Assert.Equal("add", op.Op);
        Assert.Equal("/relations/-", op.Path);

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(op.Value));
        Assert.Equal("System.LinkTypes.Hierarchy-Reverse", doc.RootElement.GetProperty("rel").GetString());
        Assert.Equal("https://dev.azure.com/org/proj/_apis/wit/workItems/42", doc.RootElement.GetProperty("url").GetString());
    }

    [Fact]
    public void EvidenceLink_ProducesHyperlinkRelationWithComment()
    {
        var op = AdoRelationOps.EvidenceLink("https://example.com/screenshot.png", "some comment");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(op.Value));
        Assert.Equal("Hyperlink", doc.RootElement.GetProperty("rel").GetString());
        Assert.Equal("https://example.com/screenshot.png", doc.RootElement.GetProperty("url").GetString());
        Assert.Equal("some comment", doc.RootElement.GetProperty("attributes").GetProperty("comment").GetString());
    }

    [Fact]
    public void AttachedFileLink_ProducesAttachedFileRelationWithComment()
    {
        var op = AdoRelationOps.AttachedFileLink("https://dev.azure.com/org/proj/_apis/wit/attachments/abc", "screenshot comment");

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(op.Value));
        Assert.Equal("AttachedFile", doc.RootElement.GetProperty("rel").GetString());
        Assert.Equal("https://dev.azure.com/org/proj/_apis/wit/attachments/abc", doc.RootElement.GetProperty("url").GetString());
        Assert.Equal("screenshot comment", doc.RootElement.GetProperty("attributes").GetProperty("comment").GetString());
    }
}
