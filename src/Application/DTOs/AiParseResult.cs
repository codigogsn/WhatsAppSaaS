using System.Text.Json.Serialization;

namespace WhatsAppSaaS.Application.DTOs;

// ─────────────────────────────────────────────────────
// JSON contract returned by the LLM  (strict schema)
// ─────────────────────────────────────────────────────

[JsonConverter(typeof(JsonStringEnumConverter))]
public enum RestaurantIntent
{
    [JsonPropertyName("order_create")]
    OrderCreate,

    [JsonPropertyName("reservation_create")]
    ReservationCreate,

    [JsonPropertyName("human_handoff")]
    HumanHandoff,

    [JsonPropertyName("general")]
    General
}

/// <summary>
/// Top-level result the AI parser returns.
/// The LLM is forced to produce exactly this shape.
/// </summary>
public sealed class AiParseResult
{
    [JsonPropertyName("intent")]
    public RestaurantIntent Intent { get; init; } = RestaurantIntent.General;

    [JsonPropertyName("args")]
    public ParsedArgs Args { get; init; } = new();

    [JsonPropertyName("missing_fields")]
    public string[] MissingFields { get; init; } = [];

    [JsonPropertyName("confidence")]
    public double Confidence { get; init; }
}

/// <summary>
/// Union-style args: only the branch matching the intent is populated.
/// </summary>
public sealed class ParsedArgs
{
    [JsonPropertyName("order")]
    public OrderArgs? Order { get; init; }

    [JsonPropertyName("reservation")]
    public ReservationArgs? Reservation { get; init; }

    [JsonPropertyName("handoff")]
    public HandoffArgs? Handoff { get; init; }

    [JsonPropertyName("general")]
    public GeneralArgs? General { get; init; }
}

// ── Per-intent argument types ────────────────────────

public sealed class OrderArgs
{
    [JsonPropertyName("items")]
    public List<OrderItem> Items { get; init; } = [];

    [JsonPropertyName("delivery_type")]
    public string? DeliveryType { get; init; }

    [JsonPropertyName("notes")]
    public string? Notes { get; init; }
}

public sealed class OrderItem
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("quantity")]
    public int Quantity { get; init; } = 1;
}

public sealed class ReservationArgs
{
    [JsonPropertyName("date")]
    public string? Date { get; init; }

    [JsonPropertyName("time")]
    public string? Time { get; init; }

    [JsonPropertyName("party_size")]
    public int? PartySize { get; init; }

    [JsonPropertyName("name")]
    public string? Name { get; init; }
}

public sealed class HandoffArgs
{
    [JsonPropertyName("reason")]
    public string? Reason { get; init; }
}

public sealed class GeneralArgs
{
    [JsonPropertyName("topic")]
    public string? Topic { get; init; }

    [JsonPropertyName("suggested_reply")]
    public string? SuggestedReply { get; init; }
}
