using FakeItEasy;
using lucia.Agents.Abstractions;
using lucia.Agents.Agents;
using lucia.Agents.Configuration.UserConfiguration;
using lucia.Agents.Integration;
using lucia.Agents.Services;
using lucia.Agents.Training;
using lucia.Tests.Data;
using Microsoft.Agents.AI;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;

namespace lucia.Tests;

public sealed class GeneralAgentTests
{
    [Fact]
    public async Task RefreshConfigAsync_WhenTwoCallsOverlapOnFirstApply_BuildsAgentOnlyOnce()
    {
        var buildStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allowBuildToComplete = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var buildCount = 0;

        var resolver = A.Fake<IChatClientResolver>();
        A.CallTo(() => resolver.ResolveAIAgentAsync(A<string?>._, A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                Interlocked.Increment(ref buildCount);
                buildStarted.TrySetResult();
                await allowBuildToComplete.Task;
                return (AIAgent?)A.Fake<AIAgent>();
            });

        var definition = CreateDefinition("model-x", "instructions", DateTime.UnixEpoch.AddDays(1));
        var repository = A.Fake<IAgentDefinitionRepository>();
        A.CallTo(() => repository.GetAgentDefinitionAsync("general-assistant", A<CancellationToken>._))
            .Returns(Task.FromResult<AgentDefinition?>(definition));

        var agent = CreateAgent(resolver, repository);

        var first = agent.RefreshConfigAsync();
        await buildStarted.Task;
        // Second call arrives while first is in-flight inside the semaphore.
        var second = agent.RefreshConfigAsync();
        allowBuildToComplete.SetResult();
        await Task.WhenAll(first, second);

        // The reload gate serializes both calls; the second sees _aiAgent already set by the
        // first and skips the expensive build entirely.
        Assert.Equal(1, buildCount);
    }

    [Fact]
    public async Task RefreshConfigAsync_WhenDefinitionChangesDuringApply_AppliesNewDefinitionNextCycle()
    {
        using var database = new AgentDefinitionSqliteTestDatabase();
        var repository = database.Repository;
        var firstDefinition = CreateDefinition("model-x", "instructions-x", DateTime.UnixEpoch);
        await repository.UpsertAgentDefinitionAsync(firstDefinition);
        var firstApplyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirstApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appliedDefinitions = new List<(string? ModelConnectionName, string Instructions)>();

        GeneralAgent? agent = null;
        var resolver = A.Fake<IChatClientResolver>();
        A.CallTo(() => resolver.ResolveAIAgentAsync(A<string?>._, A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                appliedDefinitions.Add((call.GetArgument<string?>(0), agent!.Instructions));
                if (appliedDefinitions.Count == 1)
                {
                    firstApplyStarted.SetResult();
                    await finishFirstApply.Task;
                }

                return (AIAgent?)A.Fake<AIAgent>();
            });

        agent = new GeneralAgent(
            resolver,
            repository,
            A.Fake<IMcpToolRegistry>(),
            new TracingChatClientFactory(A.Fake<ITraceRepository>(), NullLoggerFactory.Instance),
            NullLoggerFactory.Instance);

        var initialization = agent.InitializeAsync();
        await firstApplyStarted.Task;
        var nextDefinition = CreateDefinition("model-y", "instructions-y", DateTime.UnixEpoch);
        await repository.UpsertAgentDefinitionAsync(nextDefinition);
        finishFirstApply.SetResult();
        await initialization;

        var persistedWinner = await repository.GetAgentDefinitionAsync("general-assistant");
        await agent.RefreshConfigAsync();

        Assert.NotNull(persistedWinner);
        Assert.Equal("model-y", persistedWinner.ModelConnectionName);
        Assert.True(persistedWinner.UpdatedAt > firstDefinition.UpdatedAt);
        Assert.Equal(nextDefinition.UpdatedAt, persistedWinner.UpdatedAt);
        Assert.Equal(
            [("model-x", "instructions-x"), ("model-y", "instructions-y")],
            appliedDefinitions);
    }

    [Fact]
    public async Task RefreshConfigAsync_WhenDefinitionIsAbsent_DoesNotRebuildContinuously()
    {
        var repository = A.Fake<IAgentDefinitionRepository>();
        A.CallTo(() => repository.GetAgentDefinitionAsync("general-assistant", A<CancellationToken>._))
            .Returns(Task.FromResult<AgentDefinition?>(null));

        var appliedConnections = new List<string?>();
        var resolver = A.Fake<IChatClientResolver>();
        A.CallTo(() => resolver.ResolveAIAgentAsync(A<string?>._, A<CancellationToken>._))
            .ReturnsLazily(call =>
            {
                appliedConnections.Add(call.GetArgument<string?>(0));
                return Task.FromResult<AIAgent?>(A.Fake<AIAgent>());
            });

        var agent = CreateAgent(resolver, repository);

        await agent.InitializeAsync();
        await agent.RefreshConfigAsync();

        Assert.Equal([null], appliedConnections);
    }

    [Fact]
    public async Task RefreshConfigAsync_WhenDefinitionIsCreatedDuringAbsentApply_AppliesCreatedDefinitionNextCycle()
    {
        using var database = new AgentDefinitionSqliteTestDatabase();
        var repository = database.Repository;
        var firstApplyStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishFirstApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var appliedConnections = new List<string?>();

        var resolver = A.Fake<IChatClientResolver>();
        A.CallTo(() => resolver.ResolveAIAgentAsync(A<string?>._, A<CancellationToken>._))
            .ReturnsLazily(async call =>
            {
                appliedConnections.Add(call.GetArgument<string?>(0));
                if (appliedConnections.Count == 1)
                {
                    firstApplyStarted.SetResult();
                    await finishFirstApply.Task;
                }

                return (AIAgent?)A.Fake<AIAgent>();
            });

        var agent = CreateAgent(resolver, repository);

        var initialization = agent.InitializeAsync();
        await firstApplyStarted.Task;
        var nextDefinition = CreateDefinition("model-y", "instructions-y", DateTime.UnixEpoch);
        await repository.UpsertAgentDefinitionAsync(nextDefinition);
        finishFirstApply.SetResult();
        await initialization;

        var absenceWatermark = (DateTime?)typeof(GeneralAgent)
            .GetField("_lastConfigUpdate", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(agent) ?? DateTime.UtcNow;
        using (var connection = database.ConnectionFactory.CreateConnection())
        using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                UPDATE agent_definitions
                SET data = json_set(data, '$.updatedAt', @updatedAt)
                WHERE id = @id;
                """;
            command.Parameters.AddWithValue("@updatedAt", absenceWatermark.ToString("O"));
            command.Parameters.AddWithValue("@id", nextDefinition.Id);
            await command.ExecuteNonQueryAsync();
        }

        var persistedWinner = await repository.GetAgentDefinitionAsync("general-assistant");
        await agent.RefreshConfigAsync();

        Assert.NotNull(persistedWinner);
        Assert.Equal(absenceWatermark, persistedWinner.UpdatedAt);
        Assert.Equal(DateTimeKind.Utc, persistedWinner.UpdatedAt.Kind);
        Assert.Equal([null, "model-y"], appliedConnections);
    }

    [Fact]
    public void Ctor_WhenWeatherSkillRegistered_ExposesWeatherToolAndInstructionHint()
    {
        // Arrange — a real tool built the same way an IWeatherSkill plugin exposes get_weather
        var weatherSkill = A.Fake<IWeatherSkill>();
        var tool = AIFunctionFactory.Create(() => "sunny", new AIFunctionFactoryOptions { Name = "get_weather" });
        A.CallTo(() => weatherSkill.GetTools()).Returns(new List<AITool> { tool });

        // Act
        var agent = CreateAgent(A.Fake<IChatClientResolver>(), A.Fake<IAgentDefinitionRepository>(), weatherSkill);

        // Assert
        var exposed = Assert.Single(agent.Tools);
        Assert.Equal("get_weather", exposed.Name);
        Assert.Contains("get_weather", agent.Instructions);
    }

    [Fact]
    public void Ctor_WhenToolProvidersRegistered_MergesTools_SkillCardsAndHints()
    {
        // Arrange — two providers built the way a plugin script would register them:
        // one with an instruction hint, one without.
        var flight = CreateToolProvider(
            name: "Flight weather",
            description: "Flyability check for paramotor and ultralight flights",
            hint: "use the check_flight_conditions tool when asked whether it is safe to fly",
            toolName: "check_flight_conditions");
        var homelab = CreateToolProvider(
            name: "Homelab status",
            description: "Reports on homelab services and nodes",
            hint: null,
            toolName: "get_homelab_status");

        // Act
        var agent = CreateAgent(
            A.Fake<IChatClientResolver>(),
            A.Fake<IAgentDefinitionRepository>(),
            toolProviders: [flight, homelab]);

        // Assert — tools from every provider are merged in
        Assert.Equal(
            ["check_flight_conditions", "get_homelab_status"],
            agent.Tools.Select(t => t.Name).ToArray());

        // Assert — each provider contributes an agent-card skill entry
        var skills = agent.GetAgentCard().Skills;
        Assert.Contains(skills, s => s.Name == "Flight weather" && s.Description == "Flyability check for paramotor and ultralight flights");
        Assert.Contains(skills, s => s.Name == "Homelab status" && s.Description == "Reports on homelab services and nodes");

        // Assert — the hinted provider's voice hint reaches the instructions;
        // the unhinted one adds nothing stray.
        Assert.Contains("use the check_flight_conditions tool when asked whether it is safe to fly", agent.Instructions);
    }

    [Fact]
    public async Task InitializeAsync_WhenToolProvidersRegistered_InitializesEachProviderOnce()
    {
        // Arrange
        var provider = CreateToolProvider("Flight weather", "Flyability check", null, "check_flight_conditions");
        var resolver = A.Fake<IChatClientResolver>();
        A.CallTo(() => resolver.ResolveAIAgentAsync(A<string?>._, A<CancellationToken>._))
            .Returns((AIAgent?)A.Fake<AIAgent>());

        var agent = CreateAgent(resolver, A.Fake<IAgentDefinitionRepository>(), toolProviders: [provider]);

        // Act
        await agent.InitializeAsync();

        // Assert
        A.CallTo(() => provider.InitializeAsync(A<CancellationToken>._)).MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task RefreshConfigAsync_WhenToolProvidersRegistered_ProviderToolsSurviveReload()
    {
        // Arrange
        var tool = AIFunctionFactory.Create(() => "ok", new AIFunctionFactoryOptions { Name = "check_flight_conditions" });
        var provider = A.Fake<IAgentToolProvider>();
        A.CallTo(() => provider.GetTools()).Returns(new List<AITool> { tool });

        var resolver = A.Fake<IChatClientResolver>();
        A.CallTo(() => resolver.ResolveAIAgentAsync(A<string?>._, A<CancellationToken>._))
            .Returns((AIAgent?)A.Fake<AIAgent>());

        var agent = CreateAgent(resolver, A.Fake<IAgentDefinitionRepository>(), toolProviders: [provider]);
        await agent.InitializeAsync();

        // Act — the reload path rebuilds the tool list from scratch
        await agent.RefreshConfigAsync();

        // Assert
        Assert.Contains(agent.Tools, t => t.Name == "check_flight_conditions");
    }

    private static GeneralAgent CreateAgent(
        IChatClientResolver resolver,
        IAgentDefinitionRepository repository,
        IWeatherSkill? weatherSkill = null,
        IEnumerable<IAgentToolProvider>? toolProviders = null)
    {
        return new GeneralAgent(
            resolver,
            repository,
            A.Fake<IMcpToolRegistry>(),
            new TracingChatClientFactory(A.Fake<ITraceRepository>(), NullLoggerFactory.Instance),
            NullLoggerFactory.Instance,
            weatherSkill: weatherSkill,
            toolProviders: toolProviders);
    }

    private static IAgentToolProvider CreateToolProvider(
        string name,
        string description,
        string? hint,
        string toolName)
    {
        var provider = A.Fake<IAgentToolProvider>();
        A.CallTo(() => provider.Name).Returns(name);
        A.CallTo(() => provider.Description).Returns(description);
        A.CallTo(() => provider.InstructionHint).Returns(hint);
        A.CallTo(() => provider.GetTools())
            .Returns(new List<AITool> { AIFunctionFactory.Create(() => "ok", new AIFunctionFactoryOptions { Name = toolName }) });
        return provider;
    }

    private static AgentDefinition CreateDefinition(
        string modelConnectionName,
        string instructions,
        DateTime updatedAt)
    {
        return new AgentDefinition
        {
            Id = "general-assistant",
            Name = "general-assistant",
            DisplayName = "General Assistant",
            Description = "General assistant",
            Instructions = instructions,
            ModelConnectionName = modelConnectionName,
            UpdatedAt = updatedAt,
        };
    }
}
