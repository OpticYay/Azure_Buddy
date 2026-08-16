namespace AzureBuddy.Core.Intent;

/// <summary>Mirrors the "intent" enum in the n8n "Extract Intent" node's output schema.</summary>
public enum ChatIntent
{
    CreateBug,
    ViewBugs,
    UpdateItem,
    MyItems,
    PrioritizeWorkItems,
    Other
}
