using System.Text.Json;
using AzureBuddy.Core.Agent;
using AzureBuddy.Core.AzureDevOps;
using AzureBuddy.Core.WorkItemStates;
using AzureBuddy.Data;
using AzureBuddy.Tests.Integration;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Caching.Memory;
using Xunit;

namespace AzureBuddy.Tests.Agent;

public class ToolCatalogTests
{
    private static ToolCatalog NewToolCatalog()
    {
        var options = new DbContextOptionsBuilder<AppDbContext>().UseInMemoryDatabase(Guid.NewGuid().ToString()).Options;
        var stateConfigService = new WorkItemStateConfigService(new AppDbContext(options), new MemoryCache(new MemoryCacheOptions()));
        var connectionAccessor = new AdoConnectionContextAccessor { Current = new AdoConnectionContext("https://dev.azure.com/org", "Proj", "pat") };
        return new ToolCatalog(new AdoWorkItemToolset(new FakeAdoClient(), connectionAccessor, stateConfigService));
    }

    private static readonly string[] ExpectedToolNames =
    {
        "search_work_items", "create_linked_bug", "get_linked_items", "get_work_item_details",
        "update_work_item", "get_my_work_items", "get_prioritized_work_items", "attach_evidence_link"
    };

    [Fact]
    public void GetTools_RegistersExactlyTheEightExpectedTools()
    {
        var tools = NewToolCatalog().GetTools();

        Assert.Equal(ExpectedToolNames.Length, tools.Count);
        Assert.Equal(ExpectedToolNames, tools.Select(t => t.Definition.Name));
    }

    [Fact]
    public void GetTools_NoDuplicateToolNames()
    {
        var tools = NewToolCatalog().GetTools();

        Assert.Equal(tools.Count, tools.Select(t => t.Definition.Name).Distinct().Count());
    }

    [Fact]
    public void GetTools_EveryToolHasNonEmptyNameAndDescription()
    {
        foreach (var tool in NewToolCatalog().GetTools())
        {
            Assert.False(string.IsNullOrWhiteSpace(tool.Definition.Name));
            Assert.False(string.IsNullOrWhiteSpace(tool.Definition.Description));
            Assert.NotNull(tool.InvokeAsync);
        }
    }

    [Fact]
    public void GetTools_EveryToolHasWellFormedJsonObjectSchema()
    {
        foreach (var tool in NewToolCatalog().GetTools())
        {
            var schema = tool.Definition.ParametersSchema;
            Assert.Equal(JsonValueKind.Object, schema.ValueKind);
            Assert.Equal("object", schema.GetProperty("type").GetString());
            Assert.True(schema.TryGetProperty("properties", out var properties));
            Assert.Equal(JsonValueKind.Object, properties.ValueKind);
            Assert.True(schema.TryGetProperty("required", out var required));
            Assert.Equal(JsonValueKind.Array, required.ValueKind);
        }
    }

    [Theory]
    [InlineData("create_linked_bug", new[] { "title", "description", "parent_id" })]
    [InlineData("update_work_item", new[] { "id" })]
    [InlineData("attach_evidence_link", new[] { "id", "evidence_url" })]
    public void GetTools_ToolsWithRequiredArguments_DeclareThemInSchema(string toolName, string[] expectedRequired)
    {
        var tool = NewToolCatalog().GetTools().Single(t => t.Definition.Name == toolName);
        var required = tool.Definition.ParametersSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString()).ToArray();

        Assert.Equal(expectedRequired, required);
    }

    [Theory]
    [InlineData("search_work_items")]
    [InlineData("get_linked_items")]
    [InlineData("get_work_item_details")]
    [InlineData("get_my_work_items")]
    [InlineData("get_prioritized_work_items")]
    public void GetTools_ToolsWithNoRequiredArguments_DeclareAnEmptyRequiredArray(string toolName)
    {
        var tool = NewToolCatalog().GetTools().Single(t => t.Definition.Name == toolName);
        var required = tool.Definition.ParametersSchema.GetProperty("required");

        Assert.Empty(required.EnumerateArray());
    }

    [Fact]
    public void GetTools_EveryDeclaredPropertyHasATypeAndDescription()
    {
        foreach (var tool in NewToolCatalog().GetTools())
        {
            var properties = tool.Definition.ParametersSchema.GetProperty("properties");
            foreach (var property in properties.EnumerateObject())
            {
                Assert.True(property.Value.TryGetProperty("type", out _), $"{tool.Definition.Name}.{property.Name} missing 'type'");
                Assert.True(property.Value.TryGetProperty("description", out var desc), $"{tool.Definition.Name}.{property.Name} missing 'description'");
                Assert.False(string.IsNullOrWhiteSpace(desc.GetString()));
            }
        }
    }

    [Fact]
    public void GetTools_EveryRequiredNameIsActuallyDeclaredAsAProperty()
    {
        foreach (var tool in NewToolCatalog().GetTools())
        {
            var propertyNames = tool.Definition.ParametersSchema.GetProperty("properties").EnumerateObject().Select(p => p.Name).ToHashSet();
            var requiredNames = tool.Definition.ParametersSchema.GetProperty("required").EnumerateArray().Select(e => e.GetString());

            foreach (var name in requiredNames)
            {
                Assert.Contains(name!, propertyNames);
            }
        }
    }
}
