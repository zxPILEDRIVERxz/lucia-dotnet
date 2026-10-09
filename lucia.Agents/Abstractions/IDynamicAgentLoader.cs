namespace lucia.Agents.Abstractions;

/// <summary>
/// Abstraction over the dynamic agent background service so API handlers can trigger
/// catalog rebuilds without depending on the sealed hosted-service implementation.
/// Callers use this after the set of available tools changes (e.g. an MCP server
/// reconnect) so rebuilt agents resolve tool wrappers against fresh clients.
/// </summary>
public interface IDynamicAgentLoader
{
    /// <summary>Rebuilds all dynamic agents from their stored definitions.</summary>
    Task ReloadAsync(CancellationToken ct = default);
}
