using System.ComponentModel.DataAnnotations;

namespace WhatsAppSaaS.Application.Common;

public sealed class AiParserOptions
{
    public const string SectionName = "AiParser";

    /// <summary>
    /// OpenAI API key. Prefer setting via OPENAI_API_KEY environment variable.
    /// Resolved order: env var → this config value.
    /// </summary>
    public string? ApiKey { get; set; }

    /// <summary>
    /// Chat-completion model to use.
    /// </summary>
    public string Model { get; set; } = "gpt-4o-mini";

    /// <summary>
    /// Maximum tokens for the LLM response.
    /// </summary>
    public int MaxTokens { get; set; } = 512;

    /// <summary>
    /// Temperature for the LLM (lower = more deterministic).
    /// </summary>
    public double Temperature { get; set; } = 0.1;

    /// <summary>
    /// HTTP timeout in seconds for the OpenAI call.
    /// </summary>
    public int TimeoutSeconds { get; set; } = 30;

    /// <summary>
    /// Resolves the API key: environment variable takes precedence.
    /// </summary>
    public string ResolveApiKey()
    {
        var envKey = Environment.GetEnvironmentVariable("OPENAI_API_KEY");
        return !string.IsNullOrWhiteSpace(envKey) ? envKey : ApiKey ?? string.Empty;
    }
}
