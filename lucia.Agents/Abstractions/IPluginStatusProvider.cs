namespace lucia.Agents.Abstractions;

/// <summary>
/// Optional capability for plugins to surface live, read-only status on the
/// dashboard's Plugins page (rendered under the plugin's config section).
/// Status reflects what the plugin actually resolved at runtime — never user-edited.
/// </summary>
public interface IPluginStatusProvider
{
    /// <summary>Short human-readable status line, or <c>null</c> when there is nothing to report.</summary>
    string? StatusLine { get; }
}
