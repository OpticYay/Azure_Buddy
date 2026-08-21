namespace AzureBuddy.Core.AzureDevOps;

/// <summary>
/// Single source of truth for the bug-description HTML shape shared by three places that must stay
/// byte-compatible: CreateBugFlow (produces it), AzureBuddyAgent's system prompt (instructs the LLM to
/// produce the same shape when it builds a bug description itself), and AdoAttachmentService (parses
/// the Evidence line back out to replace it once real evidence arrives). See
/// docs/improvements/04-refactor-and-dedup.md §4.5 for why this is the riskiest duplication in the
/// codebase - a producer/producer/consumer triangle that silently breaks if any one copy drifts.
/// </summary>
public static class BugDescriptionTemplate
{
    public const string NotProvided = "Not provided";

    /// <summary>The exact "Evidence: Not provided" line CreateBugFlow emits when no evidence was given,
    /// and the only string AdoAttachmentService looks for to replace with real evidence once it
    /// arrives.</summary>
    public const string EvidencePlaceholder = $"<b>Evidence:</b> {NotProvided}";

    /// <summary>The exact placeholder-style rule shown to the LLM in AzureBuddyAgent's system prompt -
    /// documents the same tag order/shape <see cref="Build"/> produces below, using {text}/{step}
    /// tokens since the LLM fills those in itself rather than calling this method. Kept alongside
    /// <see cref="Build"/> so both are edited together.</summary>
    public const string PromptRule =
        "<b>Bug description:</b> {text}<br><br><b>Steps to reproduce:</b><br>1. {step}<br>2. {step}<br><br>" +
        "<b>Expected result:</b> {text}<br><br><b>Actual result:</b> {text}<br><br>" +
        $"<b>Evidence:</b> {{text or '{NotProvided}'}}<br><br><b>Environment:</b> {{text or '{NotProvided}'}}";

    /// <summary>Builds the full description HTML CreateBugFlow writes to ReproSteps: no literal
    /// newlines, &lt;br&gt;/&lt;b&gt; tags only. <paramref name="stepsHtml"/> is expected pre-joined
    /// (e.g. "1. step one&lt;br&gt;2. step two") or <see cref="NotProvided"/> when there are none.</summary>
    public static string Build(string title, string stepsHtml, string expectedResult, string actualResult, string evidence, string environment) =>
        $"<b>Bug description:</b> {title}<br><br>" +
        $"<b>Steps to reproduce:</b><br>{stepsHtml}<br><br>" +
        $"<b>Expected result:</b> {(string.IsNullOrEmpty(expectedResult) ? NotProvided : expectedResult)}<br><br>" +
        $"<b>Actual result:</b> {(string.IsNullOrEmpty(actualResult) ? NotProvided : actualResult)}<br><br>" +
        $"{(string.IsNullOrEmpty(evidence) ? EvidencePlaceholder : $"<b>Evidence:</b> {evidence}")}<br><br>" +
        $"<b>Environment:</b> {(string.IsNullOrEmpty(environment) ? NotProvided : environment)}";
}
