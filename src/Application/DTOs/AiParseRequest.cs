using System.Text.Json.Serialization;

namespace WhatsAppSaaS.Application.DTOs;

/// <summary>
/// Input DTO for the AI parser.
/// Also exposed via POST /ai/parse for quick curl testing.
/// </summary>
public sealed class AiParseRequest
{
    [JsonPropertyName("message")]
    public string Message { get; init; } = string.Empty;

    [JsonPropertyName("from")]
    public string From { get; init; } = string.Empty;

    /// <summary>
    /// Conversation id used to scope multi-turn context in the future.
    /// For now the parser is stateless; this field is forwarded for logging.
    /// </summary>
    [JsonPropertyName("conversation_id")]
    public string ConversationId { get; init; } = string.Empty;
}
