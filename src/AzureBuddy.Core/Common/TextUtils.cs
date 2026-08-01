using System.Text.RegularExpressions;

namespace AzureBuddy.Core.Common;

/// <summary>Small text helpers shared across modules that otherwise had no natural common dependency
/// (intent extraction, the agent's tools) - kept here instead of duplicated per-file.</summary>
public static class TextUtils
{
    /// <summary>Strips everything except digits - used to sanitize a work item id the model or a WIQL
    /// result handed back as a loosely-formatted string (e.g. "12172" from "#12172" or "12172.0").</summary>
    public static string DigitsOnly(string? value) =>
        value is null ? string.Empty : Regex.Replace(value, "[^0-9]", "");
}
