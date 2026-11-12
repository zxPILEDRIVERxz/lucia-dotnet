using Microsoft.Extensions.AI;
using Microsoft.Extensions.Logging;

namespace lucia.Agents.Integration;

/// <summary>
/// IChatClient decorator that recovers from empty completions on tool-result turns. Some models
/// (observed on OpenAI-compatible Gemini endpoints) answer the turn immediately after a tool result
/// with an empty "stop" completion — no text and no tool calls — which would otherwise end the agent
/// loop without producing an answer. When the request ends with a tool result and the response is
/// empty, the request is retried exactly once; if the retry is also empty it is returned as-is so
/// downstream orchestration surfaces it as a failure instead of a fake success. Streaming calls and
/// non-tool-result turns pass through untouched.
/// </summary>
public sealed class EmptyCompletionRetryChatClient : DelegatingChatClient
{
    private readonly ILogger<EmptyCompletionRetryChatClient> _logger;

    public EmptyCompletionRetryChatClient(
        IChatClient innerClient,
        ILogger<EmptyCompletionRetryChatClient> logger)
        : base(innerClient)
    {
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public override async Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> messages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        var materialized = messages.ToArray();

        var response = await base.GetResponseAsync(materialized, options, cancellationToken).ConfigureAwait(false);

        if (HasUsableContent(response) || !EndsWithToolResult(materialized))
        {
            return response;
        }

        _logger.LogWarning(
            "Model returned an empty completion after a tool result; retrying the request once.");

        return await base.GetResponseAsync(materialized, options, cancellationToken).ConfigureAwait(false);
    }

    private static bool EndsWithToolResult(IReadOnlyList<ChatMessage> messages)
    {
        if (messages.Count == 0)
        {
            return false;
        }

        var last = messages[^1];
        return last.Contents is not null && last.Contents.OfType<FunctionResultContent>().Any();
    }

    private static bool HasUsableContent(ChatResponse response)
    {
        foreach (var message in response.Messages ?? [])
        {
            foreach (var content in message.Contents ?? [])
            {
                if (content is FunctionCallContent)
                {
                    return true;
                }

                if (content is TextContent { Text: { } text } && !string.IsNullOrWhiteSpace(text))
                {
                    return true;
                }
            }
        }

        return false;
    }
}
