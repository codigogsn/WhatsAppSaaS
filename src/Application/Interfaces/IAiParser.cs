using WhatsAppSaaS.Application.DTOs;

namespace WhatsAppSaaS.Application.Interfaces;

public interface IAiParser
{
    /// <summary>
    /// Sends the customer message to the LLM and returns a strongly-typed
    /// intent + args + missing_fields + confidence result.
    /// </summary>
    Task<AiParseResult> ParseAsync(
        string message,
        string from,
        string conversationId,
        CancellationToken cancellationToken = default);
}
