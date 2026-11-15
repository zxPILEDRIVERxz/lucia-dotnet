using FakeItEasy;
using lucia.HomeAssistant.Models;
using lucia.HomeAssistant.Services;
using lucia.TimerAgent.ScheduledTasks;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace lucia.Tests.ScheduledTasks;

public sealed class TimerScheduledTaskTests
{
    [Fact]
    public void IsExpired_ReturnsTrue_WhenFireAtInPast()
    {
        var task = new TimerScheduledTask
        {
            Id = "t1",
            TaskId = "task-t1",
            Label = "Test timer",
            FireAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Message = "Done!",
            EntityId = "assist_satellite.bedroom",
            DurationSeconds = 60
        };

        Assert.True(task.IsExpired(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsExpired_ReturnsFalse_WhenFireAtInFuture()
    {
        var task = new TimerScheduledTask
        {
            Id = "t1",
            TaskId = "task-t1",
            Label = "Test timer",
            FireAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Message = "Done!",
            EntityId = "assist_satellite.bedroom",
            DurationSeconds = 300
        };

        Assert.False(task.IsExpired(DateTimeOffset.UtcNow));
    }

    [Fact]
    public void IsExpired_ReturnsTrue_WhenFireAtEqualsNow()
    {
        var now = DateTimeOffset.UtcNow;
        var task = new TimerScheduledTask
        {
            Id = "t1",
            TaskId = "task-t1",
            Label = "Test timer",
            FireAt = now,
            Message = "Done!",
            EntityId = "assist_satellite.bedroom",
            DurationSeconds = 0
        };

        Assert.True(task.IsExpired(now));
    }

    [Fact]
    public void TaskType_IsTimer()
    {
        var task = new TimerScheduledTask
        {
            Id = "t1",
            TaskId = "task-t1",
            Label = "Test timer",
            FireAt = DateTimeOffset.UtcNow,
            Message = "Done!",
            EntityId = "assist_satellite.bedroom",
            DurationSeconds = 60
        };

        Assert.Equal(ScheduledTaskType.Timer, task.TaskType);
    }

    [Fact]
    public void ToDocument_RoundTripsCorrectly()
    {
        var task = new TimerScheduledTask
        {
            Id = "t1",
            TaskId = "task-t1",
            Label = "5 minute timer",
            FireAt = DateTimeOffset.UtcNow.AddMinutes(5),
            Message = "Timer is up!",
            EntityId = "assist_satellite.bedroom",
            DurationSeconds = 300
        };

        var doc = task.ToDocument();

        Assert.Equal("t1", doc.Id);
        Assert.Equal("task-t1", doc.TaskId);
        Assert.Equal("5 minute timer", doc.Label);
        Assert.Equal(task.FireAt, doc.FireAt);
        Assert.Equal(ScheduledTaskType.Timer, doc.TaskType);
        Assert.Equal(ScheduledTaskStatus.Pending, doc.Status);
        Assert.Equal("Timer is up!", doc.Message);
        Assert.Equal("assist_satellite.bedroom", doc.EntityId);
        Assert.Equal(300, doc.DurationSeconds);

        // Verify factory can reconstitute from document
        var reconstituted = ScheduledTaskFactory.FromDocument(doc);
        Assert.NotNull(reconstituted);
        var timer = Assert.IsType<TimerScheduledTask>(reconstituted);
        Assert.Equal(task.Id, timer.Id);
        Assert.Equal(task.Message, timer.Message);
        Assert.Equal(task.EntityId, timer.EntityId);
    }

    [Fact]
    public async Task ExecuteAsync_TargetEntityExists_AnnouncesOnSatellite()
    {
        var (task, provider, haClient) = CreateExpiredTaskWithServices();

        A.CallTo(() => haClient.GetStateAsync("assist_satellite.bedroom", A<CancellationToken>._))
            .Returns(Task.FromResult<HomeAssistantState?>(new() { EntityId = "assist_satellite.bedroom" }));

        await task.ExecuteAsync(provider, CancellationToken.None);

        A.CallTo(() => haClient.CallServiceAsync(
                "assist_satellite", "announce", null,
                A<ServiceCallRequest>._, A<CancellationToken>._))
            .MustHaveHappenedOnceExactly();
    }

    [Fact]
    public async Task ExecuteAsync_TargetEntityGone_FailsInsteadOfSilentNoOp()
    {
        // A target that no longer exists in HA (renamed/removed since the timer was set) must not
        // produce a service call Home Assistant would accept and silently ignore. The task must
        // fail loudly so ScheduledTaskService records it as Failed rather than Completed.
        var (task, provider, haClient) = CreateExpiredTaskWithServices();

        A.CallTo(() => haClient.GetStateAsync("assist_satellite.bedroom", A<CancellationToken>._))
            .Returns(Task.FromResult<HomeAssistantState?>(null));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => task.ExecuteAsync(provider, CancellationToken.None));

        A.CallTo(() => haClient.CallServiceAsync(
                A<string>._, A<string>._, A<string?>._, A<ServiceCallRequest>._, A<CancellationToken>._))
            .MustNotHaveHappened();
    }

    /// <summary>
    /// Builds an expired timer task plus a service provider resolving the logger and a scoped
    /// IHomeAssistantClient, mirroring how ScheduledTaskService invokes tasks.
    /// </summary>
    private static (TimerScheduledTask Task, IServiceProvider Provider, IHomeAssistantClient HaClient) CreateExpiredTaskWithServices()
    {
        var task = new TimerScheduledTask
        {
            Id = "t1",
            TaskId = "task-t1",
            Label = "Test timer",
            FireAt = DateTimeOffset.UtcNow.AddMinutes(-1),
            Message = "Done!",
            EntityId = "assist_satellite.bedroom",
            DurationSeconds = 60
        };

        var haClient = A.Fake<IHomeAssistantClient>();
        var logger = A.Fake<ILogger<TimerScheduledTask>>();

        // A real provider so the task's CreateAsyncScope() and scoped resolution work exactly as
        // in production; singleton registrations are resolvable from scopes.
        var services = new ServiceCollection();
        services.AddSingleton(haClient);
        services.AddSingleton(logger);

        return (task, services.BuildServiceProvider(), haClient);
    }
}
