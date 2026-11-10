namespace lucia.AgentHost.PluginFramework;

/// <summary>
/// DTO returned by the plugin config schemas endpoint.
/// </summary>
public sealed class PluginConfigSchemaDto
{
    public string PluginId { get; set; } = default!;
    public string Section { get; set; } = default!;
    public string Description { get; set; } = default!;
    /// <summary>Optional live status reported by the plugin (read-only, rendered by the dashboard).</summary>
    public string Status { get; set; } = "";
    public List<PluginConfigPropertyDto> Properties { get; set; } = [];
}
