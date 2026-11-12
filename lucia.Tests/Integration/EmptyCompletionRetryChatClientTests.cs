using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging.Abstractions;
using lucia.Agents.Integration;
using lucia.Tests.TestDoubles;
using Xunit;

namespace lucia.Tests.Integration;

public sealed class EmptyCompletionRetryChatClientTests
{
    private static ChatMessage UserMessage(string text = "What is the price of bitcoin?") =>
        new(ChatRole.User, text);

    private static ChatMessage ToolResultMessage(string callId = "call_1", string result = "73500") =>
        new(ChatRole.Tool, [new FunctionResultContent(callId, result)]);

    private static ChatResponse EmptyResponse() =>
        new([new ChatMessage(ChatRole.Assistant, "")]);

    private static ChatResponse TextResponse(string text) =>
        new([new ChatMessage(ChatRole.Assistant, text)]);

    private static ChatResponse FunctionCallResponse() =>
        new([new ChatMessage(ChatRole.Assistant, [new FunctionCallContent("call_2", "GetMore", new Dictionary<string, object?> { ["a"] = 1 })])]);

    [Fact]
    public async Task RetriesOnce_AndReturnsRecovery_WhenEmptyAfterToolResult()
    {
        var inner = new StubChatClient([
            _ => EmptyResponse(),
            _ => TextResponse("Bitcoin is $73,500.")
        ]);
        var client = new EmptyCompletionRetryChatClient(inner, NullLogger<EmptyCompletionRetryChatClient>.Instance);

        var response = await client.GetResponseAsync([UserMessage(), ToolResultMessage()]);

        Assert.Equal(2, inner.InvocationCount);
        Assert.Equal("Bitcoin is $73,500.", response.Text);
    }

    [Fact]
    public async Task ReturnsEmptyWithoutThrowing_WhenRetryIsAlsoEmpty()
    {
        var inner = new StubChatClient([
            _ => EmptyResponse(),
            _ => EmptyResponse()
        ]);
        var client = new EmptyCompletionRetryChatClient(inner, NullLogger<EmptyCompletionRetryChatClient>.Instance);

        var response = await client.GetResponseAsync([UserMessage(), ToolResultMessage()]);

        Assert.Equal(2, inner.InvocationCount);
        Assert.True(string.IsNullOrWhiteSpace(response.Text));
    }

    [Fact]
    public async Task DoesNotRetry_WhenResponseHasTextAfterToolResult()
    {
        var inner = new StubChatClient([_ => TextResponse("ok")]);
        var client = new EmptyCompletionRetryChatClient(inner, NullLogger<EmptyCompletionRetryChatClient>.Instance);

        var response = await client.GetResponseAsync([UserMessage(), ToolResultMessage()]);

        Assert.Equal(1, inner.InvocationCount);
        Assert.Equal("ok", response.Text);
    }

    [Fact]
    public async Task DoesNotRetry_WhenEmptyButHistoryHasNoToolResult()
    {
        var inner = new StubChatClient([_ => EmptyResponse()]);
        var client = new EmptyCompletionRetryChatClient(inner, NullLogger<EmptyCompletionRetryChatClient>.Instance);

        var response = await client.GetResponseAsync([UserMessage()]);

        Assert.Equal(1, inner.InvocationCount);
        Assert.True(string.IsNullOrWhiteSpace(response.Text));
    }

    [Fact]
    public async Task DoesNotRetry_WhenResponseIsAnotherFunctionCall()
    {
        var inner = new StubChatClient([_ => FunctionCallResponse()]);
        var client = new EmptyCompletionRetryChatClient(inner, NullLogger<EmptyCompletionRetryChatClient>.Instance);

        await client.GetResponseAsync([UserMessage(), ToolResultMessage()]);

        Assert.Equal(1, inner.InvocationCount);
    }

    [Fact]
    public async Task Retries_WhenResponseHasNoMessagesAtAll()
    {
        var inner = new StubChatClient([
            _ => new ChatResponse([]),
            _ => TextResponse("recovered")
        ]);
        var client = new EmptyCompletionRetryChatClient(inner, NullLogger<EmptyCompletionRetryChatClient>.Instance);

        var response = await client.GetResponseAsync([UserMessage(), ToolResultMessage()]);

        Assert.Equal(2, inner.InvocationCount);
        Assert.Equal("recovered", response.Text);
    }

    [Fact]
    public async Task SecondInvocationReceivesSameMessagesAndOptions()
    {
        var inner = new StubChatClient([
            _ => EmptyResponse(),
            _ => TextResponse("done")
        ]);
        var client = new EmptyCompletionRetryChatClient(inner, NullLogger<EmptyCompletionRetryChatClient>.Instance);
        var options = new ChatOptions { Instructions = "be brief" };

        await client.GetResponseAsync([UserMessage(), ToolResultMessage()], options);

        Assert.Equal(2, inner.InvocationCount);
        Assert.Same(options, inner.CapturedOptions[0]);
        Assert.Same(options, inner.CapturedOptions[1]);
        Assert.Equal(2, inner.CapturedMessages[1].Count);
        Assert.Contains(inner.CapturedMessages[1][^1].Contents!, c => c is FunctionResultContent);
    }
}
