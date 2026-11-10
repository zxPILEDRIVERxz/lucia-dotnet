using Microsoft.Extensions.AI;

namespace lucia.Agents.Abstractions;

/// <summary>
/// A generic capability a plugin can provide to agents: a set of AI tools plus the
/// metadata an agent needs to describe and use them. Agents that accept tool providers
/// merge in every registered implementation, so plugins can expose voice-tuned tools
/// without any core change per capability domain. Register implementations in a plugin's
/// <c>ConfigureServices</c>, e.g. <c>AddSingleton&lt;IAgentToolProvider&gt;(sp =&gt; new MySkill(...))</c>.
/// </summary>
public interface IAgentToolProvider
{
    /// <summary>Short display name for the capability, shown on the agent card (e.g. "Flight weather").</summary>
    string Name { get; }

    /// <summary>What this capability does; used for the agent-card skill entry and agent description.</summary>
    string Description { get; }

    /// <summary>
    /// Optional plain-text voice hint appended to the agent's instructions (e.g.
    /// "use the check_flight_conditions tool when asked whether it is safe to fly").
    /// The applying agent adds its own list formatting; return null for none.
    /// </summary>
    string? InstructionHint { get; }

    /// <summary>Returns the AI tools exposed by this capability.</summary>
    IList<AITool> GetTools();

    /// <summary>Performs one-time initialization (e.g. connectivity check, logging).</summary>
    Task InitializeAsync(CancellationToken cancellationToken = default);
}
