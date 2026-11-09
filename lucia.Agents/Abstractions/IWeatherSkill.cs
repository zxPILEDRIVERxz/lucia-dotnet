using Microsoft.Extensions.AI;

namespace lucia.Agents.Abstractions;

/// <summary>
/// Abstraction for a weather skill that can be optionally provided by a plugin.
/// Agents that support weather accept an optional <see cref="IWeatherSkill"/>
/// via DI and expose weather tools only when a provider is registered.
/// </summary>
public interface IWeatherSkill
{
    /// <summary>Returns the AI tools exposed by this weather provider.</summary>
    IList<AITool> GetTools();

    /// <summary>Performs one-time initialization (e.g. connectivity check, logging).</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
