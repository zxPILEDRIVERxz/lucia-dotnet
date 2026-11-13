using System.Text.Json;

using A2A;

using FakeItEasy;

using lucia.Agents;
using lucia.Agents.Orchestration;
using lucia.Agents.Registry;
using lucia.Tests.TestDoubles;

using Microsoft.Agents.AI.Workflows;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Options;

namespace lucia.Tests.Orchestration;

/// <summary>
/// Tests for preserving original user text in router results.
/// </summary>
public sealed class RouterExecutorTests
{
    [Fact]
    public async Task HandleAsync_PopulatesOriginalUserTextFromMessageMetadata()
    {
        // Arrange
        var responsePayload = JsonSerializer.Serialize(new
        {
            agentId = "light-agent",
            reasoning = "Light request",
            confidence = 0.9
        });

        var chatClient = new StubChatClient(
        [
            _ => new ChatResponse([new ChatMessage(ChatRole.Assistant, responsePayload)])
        ]);

        var registry = A.Fake<IAgentRegistry>();
        var agent = new AgentCard
        {
            Name = "light-agent",
            Description = "Controls lights",
            Version = "1.0.0",
            Capabilities = new AgentCapabilities(),
            DefaultInputModes = ["text"],
            DefaultOutputModes = ["text"],
            Skills = []
        };

        A.CallTo(() => registry.GetEnumerableAgentsAsync(A<CancellationToken>._))
            .Returns(GetAgentsAsync(agent));

        var router = new RouterExecutor(
            chatClient,
            registry,
            A.Fake<Microsoft.Extensions.Logging.ILogger<RouterExecutor>>(),
            new AgentsTelemetrySource(),
            Options.Create(new RouterExecutorOptions()));

        var message = new ChatMessage(ChatRole.User, "HOME ASSISTANT CONTEXT\n\nUser: turn on the basement")
        {
            AdditionalProperties = new AdditionalPropertiesDictionary
            {
                ["lucia.originalUserText"] = "schalte den keller ein"
            }
        };

        // Act
        var result = await router.HandleAsync(message, A.Fake<IWorkflowContext>(), CancellationToken.None);

        // Assert
        Assert.Equal("schalte den keller ein", result.OriginalUserText);
    }

    /// <summary>
    /// Pipeline guard: plugin capabilities are only visible to the router through skill
    /// examples on the agent card (the catalog never prints skill names or descriptions).
    /// Cards that advertise examples must have them rendered into the catalog prompt so
    /// e.g. "add an event at 6:30" can be matched against a calendar-capable agent.
    /// </summary>
    [Fact]
    public async Task HandleAsync_AgentCatalogIncludesSkillExamples()
    {
        var responsePayload = JsonSerializer.Serialize(new
        {
            agentId = "general-assistant",
            reasoning = "Calendar request",
            confidence = 0.9
        });

        var chatClient = new StubChatClient(
        [
            _ => new ChatResponse([new ChatMessage(ChatRole.Assistant, responsePayload)])
        ]);

        var registry = A.Fake<IAgentRegistry>();
        var agent = new AgentCard
        {
            Name = "general-assistant",
            Description = "Agent for handling general knowledge questions in Home Assistant",
            Version = "1.0.0",
            Capabilities = new AgentCapabilities(),
            DefaultInputModes = ["text"],
            DefaultOutputModes = ["text"],
            Skills =
            [
                new AgentSkill
                {
                    Id = "id_home_assistant_calendar",
                    Name = "Home Assistant Calendar",
                    Description = "List, create, update, and delete events on your Home Assistant calendars.",
                    Examples = ["What's on my calendar this week?", "Add Carnizza to the calendar at 6:30 today"],
                },
                new AgentSkill
                {
                    Id = "id_personal_memory",
                    Name = "Personal memory",
                    Description = "No examples on this skill.",
                }
            ]
        };

        A.CallTo(() => registry.GetEnumerableAgentsAsync(A<CancellationToken>._))
            .Returns(GetAgentsAsync(agent));

        var router = new RouterExecutor(
            chatClient,
            registry,
            A.Fake<Microsoft.Extensions.Logging.ILogger<RouterExecutor>>(),
            new AgentsTelemetrySource(),
            Options.Create(new RouterExecutorOptions()));

        // Act
        await router.HandleAsync(
            new ChatMessage(ChatRole.User, "add an event at 6:30 today"),
            A.Fake<IWorkflowContext>(),
            CancellationToken.None);

        // Assert — examples are rendered; the example-less skill contributes nothing
        var systemPrompt = chatClient.CapturedMessages[0][0].Text!;
        Assert.Contains("example: What's on my calendar this week?", systemPrompt);
        Assert.Contains("example: Add Carnizza to the calendar at 6:30 today", systemPrompt);
        Assert.DoesNotContain("No examples on this skill.", systemPrompt);
    }

    [Fact]
    public async Task HandleAsync_SlugifiedAgentId_ResolvesToCanonicalCardName()
    {
        // Legacy/built-in cards may carry display-text names. The router LLM routinely
        // emits the kebab-case slug instead — it must resolve to the exact card name,
        // because that is what agent dispatch keys on.
        var responsePayload = JsonSerializer.Serialize(new
        {
            agentId = "open-wearables-agent",
            reasoning = "Wearable activity request",
            confidence = 0.95
        });

        var chatClient = new StubChatClient(
        [
            _ => new ChatResponse([new ChatMessage(ChatRole.Assistant, responsePayload)])
        ]);

        var registry = A.Fake<IAgentRegistry>();
        var agent = new AgentCard
        {
            Name = "Open Wearables Agent",
            Description = "Tracks wearable activity",
            Version = "1.0.0",
            Capabilities = new AgentCapabilities(),
            DefaultInputModes = ["text"],
            DefaultOutputModes = ["text"],
            Skills = []
        };

        A.CallTo(() => registry.GetEnumerableAgentsAsync(A<CancellationToken>._))
            .Returns(GetAgentsAsync(agent));

        var router = new RouterExecutor(
            chatClient,
            registry,
            A.Fake<Microsoft.Extensions.Logging.ILogger<RouterExecutor>>(),
            new AgentsTelemetrySource(),
            Options.Create(new RouterExecutorOptions()));

        var result = await router.HandleAsync(
            new ChatMessage(ChatRole.User, "How many steps did I take yesterday?"),
            A.Fake<IWorkflowContext>(),
            CancellationToken.None);

        Assert.Equal("Open Wearables Agent", result.AgentId);

        // Dispatch seam: WorkflowFactory keys local agent invokers by the card name, so
        // the canonicalized id must hit an invoker dictionary built that way.
        var invokers = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase)
        {
            [agent.Name] = new object()
        };
        Assert.True(invokers.ContainsKey(result.AgentId));
    }

    [Fact]
    public async Task HandleAsync_CaseInsensitiveExactMatch_CanonicalizesCardName()
    {
        var responsePayload = JsonSerializer.Serialize(new
        {
            agentId = "LIGHT-AGENT",
            reasoning = "Light request",
            confidence = 0.9
        });

        var chatClient = new StubChatClient(
        [
            _ => new ChatResponse([new ChatMessage(ChatRole.Assistant, responsePayload)])
        ]);

        var registry = A.Fake<IAgentRegistry>();
        var agent = new AgentCard
        {
            Name = "light-agent",
            Description = "Controls lights",
            Version = "1.0.0",
            Capabilities = new AgentCapabilities(),
            DefaultInputModes = ["text"],
            DefaultOutputModes = ["text"],
            Skills = []
        };

        A.CallTo(() => registry.GetEnumerableAgentsAsync(A<CancellationToken>._))
            .Returns(GetAgentsAsync(agent));

        var router = new RouterExecutor(
            chatClient,
            registry,
            A.Fake<Microsoft.Extensions.Logging.ILogger<RouterExecutor>>(),
            new AgentsTelemetrySource(),
            Options.Create(new RouterExecutorOptions()));

        var result = await router.HandleAsync(
            new ChatMessage(ChatRole.User, "Turn on the lights"),
            A.Fake<IWorkflowContext>(),
            CancellationToken.None);

        Assert.Equal("light-agent", result.AgentId);
    }

    [Fact]
    public async Task HandleAsync_UnknownAgentId_FallsBack()
    {
        var responsePayload = JsonSerializer.Serialize(new
        {
            agentId = "nonexistent-agent",
            reasoning = "Unknown request",
            confidence = 0.9
        });

        var chatClient = new StubChatClient(
        [
            _ => new ChatResponse([new ChatMessage(ChatRole.Assistant, responsePayload)])
        ]);

        var registry = A.Fake<IAgentRegistry>();
        var agent = new AgentCard
        {
            Name = "light-agent",
            Description = "Controls lights",
            Version = "1.0.0",
            Capabilities = new AgentCapabilities(),
            DefaultInputModes = ["text"],
            DefaultOutputModes = ["text"],
            Skills = []
        };

        A.CallTo(() => registry.GetEnumerableAgentsAsync(A<CancellationToken>._))
            .Returns(GetAgentsAsync(agent));

        var router = new RouterExecutor(
            chatClient,
            registry,
            A.Fake<Microsoft.Extensions.Logging.ILogger<RouterExecutor>>(),
            new AgentsTelemetrySource(),
            Options.Create(new RouterExecutorOptions()));

        var result = await router.HandleAsync(
            new ChatMessage(ChatRole.User, "Do something unknown"),
            A.Fake<IWorkflowContext>(),
            CancellationToken.None);

        Assert.Equal(RouterExecutorOptions.DefaultFallbackAgentId, result.AgentId);
        Assert.Equal(0, result.Confidence);
    }

    [Fact]
    public async Task HandleAsync_AmbiguousSlug_FallsBack()
    {
        // Two cards whose names slugify to the same value make a slug-level match
        // ambiguous — routing by slug alone could hit the wrong agent.
        var responsePayload = JsonSerializer.Serialize(new
        {
            agentId = "open-wearables-agent",
            reasoning = "Wearable activity request",
            confidence = 0.95
        });

        var chatClient = new StubChatClient(
        [
            _ => new ChatResponse([new ChatMessage(ChatRole.Assistant, responsePayload)])
        ]);

        var registry = A.Fake<IAgentRegistry>();
        var first = CreateCard("Open Wearables Agent", "Tracks wearable activity");
        var second = CreateCard("Open.Wearables.Agent", "Tracks wearable activity (second card)");

        A.CallTo(() => registry.GetEnumerableAgentsAsync(A<CancellationToken>._))
            .Returns(GetAgentsAsync(first, second));

        var router = new RouterExecutor(
            chatClient,
            registry,
            A.Fake<Microsoft.Extensions.Logging.ILogger<RouterExecutor>>(),
            new AgentsTelemetrySource(),
            Options.Create(new RouterExecutorOptions()));

        var result = await router.HandleAsync(
            new ChatMessage(ChatRole.User, "How many steps did I take yesterday?"),
            A.Fake<IWorkflowContext>(),
            CancellationToken.None);

        Assert.Equal(RouterExecutorOptions.DefaultFallbackAgentId, result.AgentId);
    }

    [Theory]
    // Parity vectors: every case below must produce the same output from
    // lucia-dashboard/src/pages/AgentDefinitionsPage.tsx (toAgentSlug). Keep the two
    // lists in lockstep when changing either algorithm.
    [InlineData("Open Wearables Agent", "open-wearables-agent")]
    [InlineData("research-agent", "research-agent")]
    [InlineData("My_Garage  Agent!!", "my-garage-agent")]
    [InlineData("Café Agent", "caf-agent")]
    [InlineData("Über Agent", "ber-agent")]
    [InlineData("--Weird-- 123--", "weird-123")]
    [InlineData("   Spaced   ", "spaced")]
    [InlineData("Ünïcødé", "n-c-d")]
    [InlineData("123", "123")]
    [InlineData("a-b-c", "a-b-c")]
    public void ToAgentSlug_MatchesDashboardJavaScript(string input, string expected)
    {
        Assert.Equal(expected, RouterExecutor.ToAgentSlug(input));
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("---")]
    [InlineData("///")]
    public void ToAgentSlug_ReturnsNull_WhenNoAsciiCharactersRemain(string input)
    {
        Assert.Null(RouterExecutor.ToAgentSlug(input));
    }

    private static AgentCard CreateCard(string name, string description) => new()
    {
        Name = name,
        Description = description,
        Version = "1.0.0",
        Capabilities = new AgentCapabilities(),
        DefaultInputModes = ["text"],
        DefaultOutputModes = ["text"],
        Skills = []
    };

    private static async IAsyncEnumerable<AgentCard> GetAgentsAsync(params AgentCard[] agents)
    {
        foreach (var agent in agents)
        {
            yield return agent;
            await Task.Yield();
        }
    }
}
