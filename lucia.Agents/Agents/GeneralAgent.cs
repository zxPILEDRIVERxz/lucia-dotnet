using System.Diagnostics;
using A2A;
using AgentSkill = A2A.AgentSkill;
using lucia.Agents.Abstractions;
using lucia.Agents.Integration;
using lucia.Agents.Services;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace lucia.Agents.Agents;

public sealed class GeneralAgent : ILuciaAgent
{
    private const string AgentId = "general-assistant";
    
    private readonly AgentCard _agent;
    private readonly IChatClientResolver _clientResolver;
    private readonly IAgentDefinitionRepository _definitionRepository;
    private readonly IMcpToolRegistry _toolRegistry;
    private readonly IWebSearchSkill? _webSearchSkill;
    private readonly IWeatherSkill? _weatherSkill;
    private readonly IReadOnlyList<IAgentToolProvider> _toolProviders;
    private readonly TracingChatClientFactory _tracingFactory;
    private readonly ILoggerFactory _loggerFactory;
    private readonly ILogger<GeneralAgent> _logger;
    private volatile AIAgent _aiAgent;
    private DateTime? _lastConfigUpdate;
    private readonly SemaphoreSlim _reloadGate = new(1, 1);

    /// <summary>
    /// The system instructions used by this agent.
    /// </summary>
    public string Instructions { get; set; }

    /// <summary>
    /// The AI tools available to this agent (plugin-provided tools like web search).
    /// MCP tools from the agent definition are resolved at initialization time and merged in.
    /// </summary>
    public IList<AITool> Tools { get; private set; }

    public GeneralAgent(
        IChatClientResolver clientResolver,
        IAgentDefinitionRepository definitionRepository,
        IMcpToolRegistry toolRegistry,
        TracingChatClientFactory tracingFactory,
        ILoggerFactory loggerFactory,
        IWebSearchSkill? webSearchSkill = null,
        IWeatherSkill? weatherSkill = null,
        IEnumerable<IAgentToolProvider>? toolProviders = null)
    {
        _clientResolver = clientResolver;
        _definitionRepository = definitionRepository;
        _toolRegistry = toolRegistry;
        _webSearchSkill = webSearchSkill;
        _weatherSkill = weatherSkill;
        _toolProviders = toolProviders?.ToList() ?? [];
        _tracingFactory = tracingFactory;
        _loggerFactory = loggerFactory;
        _logger = loggerFactory.CreateLogger<GeneralAgent>();

        var pluginTools = new List<AITool>();
        if (_webSearchSkill is not null)
            pluginTools.AddRange(_webSearchSkill.GetTools());
        if (_weatherSkill is not null)
            pluginTools.AddRange(_weatherSkill.GetTools());
        foreach (var provider in _toolProviders)
            pluginTools.AddRange(provider.GetTools());
        Tools = pluginTools;

        // Create the agent card for registration
        var skills = new List<AgentSkill>();
        skills.Add(new AgentSkill
        {
            Id = "id_personal_memory",
            Name = "Personal memory",
            Description = "Handle personal memory requests such as 'remember I prefer tea', 'what do you know about me', and 'forget my preference' for an enrolled speaker."
        });
        if (_webSearchSkill is not null)
        {
            skills.Add(new AgentSkill
            {
                Id = "id_web_search",
                Name = "Web Search",
                Description = "Search the web for current events, news, and facts",
            });
        }
        if (_weatherSkill is not null)
        {
            skills.Add(new AgentSkill
            {
                Id = "id_weather",
                Name = "Weather",
                Description = "Current weather conditions and multi-day forecasts",
            });
        }

        foreach (var provider in _toolProviders)
        {
            var providerSkillId = ProviderSkillId(provider.Name);
            if (!skills.Any(s => s.Id == providerSkillId))
            {
                skills.Add(new AgentSkill
                {
                    Id = providerSkillId,
                    Name = provider.Name,
                    Description = provider.Description,
                });
            }
        }

        var descriptionParts = new List<string>();
        if (_webSearchSkill is not null)
            descriptionParts.Add("web search");
        if (_weatherSkill is not null)
            descriptionParts.Add("weather forecasts");
        foreach (var provider in _toolProviders)
            descriptionParts.Add(provider.Name.ToLowerInvariant());
        var agentDescription = descriptionParts.Count > 0
            ? $"Agent for general knowledge, {string.Join(", ", descriptionParts)}, and personal memory requests in Home Assistant"
            : "Agent for handling #general-knowledge questions and personal memory requests in Home Assistant";

        _agent = new AgentCard
        {
            SupportedInterfaces = [new AgentInterface { Url = "/a2a/general-assistant" }],
            Name = AgentId,
            Description = agentDescription,
            Capabilities = new AgentCapabilities
            {
                PushNotifications = false,
                Streaming = true,
            },
            DefaultInputModes = ["text"],
            DefaultOutputModes = ["text"],
            Skills = [.. skills],
            Version = "1.0.0",
        };

        var webSearchHint = _webSearchSkill is not null
            ? "\n- When asked about recent events, news, or facts that may change, use the web_search tool to find current information before answering."
            : "";
        var weatherHint = _weatherSkill is not null
            ? "\n- For current weather conditions or forecasts, use the get_weather tool instead of guessing; base your answer on its data."
            : "";
        var providerHints = string.Concat(
            _toolProviders
                .Where(p => !string.IsNullOrWhiteSpace(p.InstructionHint))
                .Select(p => $"\n- {p.InstructionHint!.Trim()}"));

        var instructions = """
                You are a specialized general knowledge agent for a Home Assistant platform.

                Your responsibilities:
                - Be informative and friendly.
                - Answer questions to the best of your ability, but don't invent facts or make up knowledge
                - If you do not know the answer, simply state in your response "I do not know."
                - Try to answer the user's request to the best of your ability. Keep your response short
                    enough to be about 6-10 seconds of audio. Roughly about 2 sentences at most.
                """ + webSearchHint + weatherHint + providerHints + """

                ## IMPORTANT
                * Keep your responses short. Aim for about 2 sentences max.
                * Do not offer to provide other assistance.
                """;

        Instructions = instructions;

        // _aiAgent is built during InitializeAsync via ApplyDefinitionAsync
        _aiAgent = null!;
    }

    /// <summary>
    /// Get the agent card for registration with the registry and A2A endpoints.
    /// </summary>
    public AgentCard GetAgentCard() => _agent;
    
    /// <summary>
    /// Get the underlying AI agent for processing requests.
    /// </summary>
    public AIAgent GetAIAgent() => _aiAgent;

    /// <summary>
    /// Initialize the agent
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Initializing General Knowledge Agent...");

        if (_webSearchSkill is not null)
            await _webSearchSkill.InitializeAsync(cancellationToken).ConfigureAwait(false);

        if (_weatherSkill is not null)
            await _weatherSkill.InitializeAsync(cancellationToken).ConfigureAwait(false);

        foreach (var provider in _toolProviders)
            await provider.InitializeAsync(cancellationToken).ConfigureAwait(false);

        await ApplyDefinitionAsync(cancellationToken).ConfigureAwait(false);

        _logger.LogInformation("General Knowledge initialized successfully");
    }

    /// <inheritdoc />
    public async Task RefreshConfigAsync(CancellationToken cancellationToken = default)
    {
        await ApplyDefinitionAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task ApplyDefinitionAsync(CancellationToken cancellationToken)
    {
        await _reloadGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            var definition = await _definitionRepository.GetAgentDefinitionAsync(AgentId, cancellationToken).ConfigureAwait(false);
            var newConnectionName = definition?.ModelConnectionName;

            if (!string.IsNullOrEmpty(definition?.Instructions))
                Instructions = definition.Instructions;

            // Resolve MCP tools from the agent definition and merge with plugin-provided tools
            var pluginTools = new List<AITool>();
            if (_webSearchSkill is not null)
                pluginTools.AddRange(_webSearchSkill.GetTools());
            if (_weatherSkill is not null)
                pluginTools.AddRange(_weatherSkill.GetTools());
            foreach (var provider in _toolProviders)
                pluginTools.AddRange(provider.GetTools());
            var mcpTools = definition?.Tools.Count > 0
                ? await _toolRegistry.ResolveToolsAsync(definition.Tools, cancellationToken).ConfigureAwait(false)
                : [];

            var allTools = new List<AITool>(pluginTools);
            allTools.AddRange(mcpTools);
            Tools = allTools;

            if (allTools.Count > 0)
                _logger.LogInformation("GeneralAgent: resolved {Count} tools ({Plugin} plugin, {Mcp} MCP)",
                    allTools.Count, pluginTools.Count, mcpTools.Count);

            if (_aiAgent is null
                || definition is not null && (_lastConfigUpdate is null || _lastConfigUpdate < definition.UpdatedAt))
            {
                var copilotAgent = await _clientResolver.ResolveAIAgentAsync(newConnectionName, cancellationToken)
                    .ConfigureAwait(false);
                _aiAgent = copilotAgent ?? BuildAgent(
                        await _clientResolver.ResolveAsync(newConnectionName, cancellationToken).ConfigureAwait(false))
                    .AsBuilder()
                    .UseOpenTelemetry()
                    .Build();
                _logger.LogInformation("GeneralAgent: using model provider '{Provider}'",
                    newConnectionName ?? "default-chat");
                _lastConfigUpdate = definition?.UpdatedAt;
            }
        }
        finally
        {
            _reloadGate.Release();
        }
    }

    private AIAgent BuildAgent(IChatClient chatClient)
    {
        var traced = _tracingFactory.Wrap(chatClient, AgentId);
        var chatOptions = new ChatOptions { Instructions = Instructions };
        if (Tools.Count > 0)
            chatOptions.Tools = [..Tools];

        var agentOptions = new ChatClientAgentOptions
        {
            AIContextProviders = _tracingFactory.AIContextProviders,
            ChatHistoryProvider = _tracingFactory.ChatHistoryProvider,
            Id = AgentId,
            Name = AgentId,
            Description = "Agent for answering general knowledge questions in Home Assistant",
            ChatOptions = chatOptions
        };

        return new ChatClientAgent(traced, agentOptions, _loggerFactory)
            .AsBuilder()
            .UseOpenTelemetry()
            .Build();
    }

    /// <summary>Builds a stable agent-card skill id from a provider name ("Flight weather" → "id_flight_weather").</summary>
    private static string ProviderSkillId(string name)
    {
        var slug = string.Concat(name.Select(c => char.IsLetterOrDigit(c) ? char.ToLowerInvariant(c) : '_'));
        while (slug.Contains("__"))
            slug = slug.Replace("__", "_");
        return "id_" + slug.Trim('_');
    }
}