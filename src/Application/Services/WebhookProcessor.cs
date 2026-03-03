using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Application.Services;

public sealed class WebhookProcessor : IWebhookProcessor
{
    private readonly IAiParser _aiParser;
    private readonly IWhatsAppClient _whatsAppClient;
    private readonly IOrderRepository _orderRepository;

    private static readonly Dictionary<string, ConversationState> _stateByConversation = new();
    private static readonly Dictionary<string, HashSet<string>> _processedMessageIdsByConversation = new();

    private static readonly TimeSpan StateTtl = TimeSpan.FromHours(6);

    private sealed class ConversationState
    {
        public bool Welcomed { get; set; }

        public List<(string Name, int Quantity)> Items { get; } = new();
        public string? DeliveryType { get; set; }

        // Flags del flujo
        public bool RequestedDetailsForm { get; set; } // planilla enviada
        public bool DetailsReceived { get; set; }       // tenemos info suficiente para confirmar

        // ==============================
        // PLANILLA / CHECKOUT (in-memory)
        // ==============================
        public string? CustomerName { get; set; }
        public string? CustomerIdNumber { get; set; }
        public string? CustomerPhone { get; set; }
        public string? Address { get; set; }
        public string? PaymentMethod { get; set; }
        public string? ReceiverName { get; set; }
        public string? AdditionalNotes { get; set; }

        // GPS (por ahora guardamos texto; luego si tu DTO trae lat/lng lo conectamos)
        public decimal? LocationLat { get; set; }
        public decimal? LocationLng { get; set; }
        public string? LocationText { get; set; }

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;
    }

    public WebhookProcessor(
        IAiParser aiParser,
        IWhatsAppClient whatsAppClient,
        IOrderRepository orderRepository)
    {
        _aiParser = aiParser;
        _whatsAppClient = whatsAppClient;
        _orderRepository = orderRepository;
    }

    public async Task ProcessAsync(WebhookPayload payload, CancellationToken cancellationToken = default)
    {
        if (payload?.Entry is null) return;

        PurgeOldState();

        foreach (var entry in payload.Entry)
        {
            foreach (var change in entry.Changes ?? [])
            {
                var value = change.Value;
                if (value?.Messages is null) continue;

                var phoneNumberId = value.Metadata?.PhoneNumberId;
                if (string.IsNullOrWhiteSpace(phoneNumberId)) continue;

                foreach (var message in value.Messages)
                {
                    if (string.IsNullOrWhiteSpace(message.From)) continue;

                    var conversationId = $"{message.From}:{phoneNumberId}";

                    if (!_stateByConversation.TryGetValue(conversationId, out var state))
                    {
                        state = new ConversationState();
                        _stateByConversation[conversationId] = state;
                    }

                    state.UpdatedAtUtc = DateTime.UtcNow;

                    var msgId = message.Id;
                    if (!string.IsNullOrWhiteSpace(msgId))
                    {
                        if (IsDuplicateMessage(conversationId, msgId))
                            continue;
                    }

                    // =========================
                    // LOCATION PIN
                    // =========================
                    if (message.Type == "location")
                    {
                        // Tu DTO no trae message.Location -> guardamos un marcador de texto.
                        if (state.Items.Count > 0 && state.RequestedDetailsForm)
                        {
                            state.LocationText = "GPS_PIN";
                            state.DetailsReceived = HasMinimumCheckout(state);

                            await _whatsAppClient.SendTextMessageAsync(
                                new OutgoingMessage
                                {
                                    To = message.From,
                                    Body = "Recibido ✅ Escribe *CONFIRMAR* para finalizar.",
                                    PhoneNumberId = phoneNumberId
                                },
                                cancellationToken);
                        }
                        continue;
                    }

                    if (message.Type != "text") continue;
                    if (string.IsNullOrWhiteSpace(message.Text?.Body)) continue;

                    var rawText = message.Text.Body;
                    var normalized = Normalize(rawText);

                    // Si el usuario está llenando planilla, capturamos datos ANTES de responder
                    if (state.Items.Count > 0 && state.RequestedDetailsForm && !IsConfirmCommand(normalized))
                    {
                        TryCaptureCheckoutFromText(state, rawText, message.From);
                        state.DetailsReceived = HasMinimumCheckout(state);
                    }

                    var parsed = await _aiParser.ParseAsync(
                        rawText,
                        message.From,
                        conversationId,
                        cancellationToken);

                    var reply = await BuildReplyAsync(
                        parsed,
                        rawText,
                        conversationId,
                        message.From,
                        phoneNumberId,
                        cancellationToken);

                    await _whatsAppClient.SendTextMessageAsync(
                        new OutgoingMessage
                        {
                            To = message.From,
                            Body = reply,
                            PhoneNumberId = phoneNumberId
                        },
                        cancellationToken);
                }
            }
        }
    }

    private async Task<string> BuildReplyAsync(
        AiParseResult parsed,
        string rawText,
        string conversationId,
        string from,
        string phoneNumberId,
        CancellationToken ct)
    {
        var state = _stateByConversation[conversationId];
        var t = Normalize(rawText);

        if (IsConfirmCommand(t))
            return await FinalizeOrderIfPossibleAsync(state, from, phoneNumberId, ct);

        // Guard PRO anti-menú
        if (state.Items.Count > 0 && state.RequestedDetailsForm && parsed.Intent == RestaurantIntent.General)
        {
            if (state.DetailsReceived)
                return "Recibido ✅ Escribe *CONFIRMAR* para finalizar.";

            return "Sigue llenando la planilla y luego escribe *CONFIRMAR*.";
        }

        if (!state.Welcomed && parsed.Intent == RestaurantIntent.General)
        {
            state.Welcomed = true;
            return "¡Hola! 👋 ¿Deseas hacer un *pedido* o una *reservación*?";
        }

        return parsed.Intent switch
        {
            RestaurantIntent.OrderCreate => await BuildOrderReplyAsync(parsed, conversationId),
            RestaurantIntent.ReservationCreate => "Perfecto 😊 ¿Para qué fecha deseas reservar?",
            RestaurantIntent.HumanHandoff => "Te paso con un humano 🙌",
            _ => "¿Deseas hacer un *pedido* o una *reservación*?"
        };
    }

    private Task<string> BuildOrderReplyAsync(AiParseResult parsed, string conversationId)
    {
        var state = _stateByConversation[conversationId];

        if (parsed.Args?.Order != null)
        {
            foreach (var item in parsed.Args.Order.Items ?? [])
            {
                if (!string.IsNullOrWhiteSpace(item.Name) && item.Quantity > 0)
                {
                    if (!state.Items.Any(x => x.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)))
                        state.Items.Add((item.Name.Trim(), item.Quantity));
                }
            }

            if (!string.IsNullOrWhiteSpace(parsed.Args.Order.DeliveryType))
                state.DeliveryType = NormalizeDeliveryType(parsed.Args.Order.DeliveryType);
        }

        if (state.Items.Count == 0)
            return Task.FromResult("¿Qué deseas ordenar?");

        if (string.IsNullOrWhiteSpace(state.DeliveryType))
            return Task.FromResult("¿Es *pick up* o *delivery*?");

        if (!state.RequestedDetailsForm)
        {
            state.RequestedDetailsForm = true;

            return Task.FromResult(
@"Para finalizar envíanos:

👤 Nombre:
🪪 Cédula:
📱 Teléfono:
🏡 Dirección:
💳 Forma de pago: (opcional)
👥 Recibe: (opcional)
📝 Notas: (opcional)
📍 Ubicación GPS: (manda el pin)

Luego escribe *CONFIRMAR*.");
        }

        if (state.DetailsReceived)
            return Task.FromResult("Recibido ✅ Escribe *CONFIRMAR* para finalizar.");

        return Task.FromResult("Sigue llenando la planilla y luego escribe *CONFIRMAR*.");
    }

    private async Task<string> FinalizeOrderIfPossibleAsync(
        ConversationState state,
        string from,
        string phoneNumberId,
        CancellationToken ct)
    {
        if (state.Items.Count == 0)
            return "No hay productos en el pedido.";

        if (string.IsNullOrWhiteSpace(state.DeliveryType))
            return "Indica si es *pick up* o *delivery*.";

        if (state.RequestedDetailsForm && !HasMinimumCheckout(state))
        {
            return
@"Aún falta información para confirmar ✅

Envíanos al menos:
👤 Nombre
🪪 Cédula
📱 Teléfono
🏡 Dirección
📍 Ubicación GPS (pin) o escribe la ubicación en texto

Luego escribe *CONFIRMAR*.";
        }

        var order = new Order
        {
            From = from,
            PhoneNumberId = phoneNumberId,
            DeliveryType = state.DeliveryType!,
            CreatedAtUtc = DateTime.UtcNow,

            // ✅ planilla a DB
            CustomerName = state.CustomerName,
            CustomerIdNumber = state.CustomerIdNumber,
            CustomerPhone = string.IsNullOrWhiteSpace(state.CustomerPhone) ? from : state.CustomerPhone,
            Address = state.Address,
            PaymentMethod = state.PaymentMethod,
            ReceiverName = state.ReceiverName,
            AdditionalNotes = state.AdditionalNotes,

            LocationLat = state.LocationLat,
            LocationLng = state.LocationLng,
            LocationText = state.LocationText,

            CheckoutFormSent = state.RequestedDetailsForm,
            CheckoutCompleted = true,
            CheckoutCompletedAtUtc = DateTime.UtcNow,

            // ✅ FIX: desambiguar OrderItem (Domain)
            Items = state.Items.Select(i => new WhatsAppSaaS.Domain.Entities.OrderItem
            {
                Name = i.Name,
                Quantity = i.Quantity
            }).ToList()
        };

        await _orderRepository.AddOrderAsync(order, ct);

        var itemsText = string.Join(", ", state.Items.Select(i => $"{i.Quantity} {i.Name}"));
        var gpsText =
            (state.LocationLat.HasValue && state.LocationLng.HasValue)
                ? $"{state.LocationLat.Value}, {state.LocationLng.Value}"
                : (state.LocationText ?? "—");

        var receipt =
$@"🧾 *PEDIDO CONFIRMADO* ✅

Items: {itemsText}
Tipo: {state.DeliveryType}
Nombre: {state.CustomerName ?? "—"}
Tel: {(string.IsNullOrWhiteSpace(state.CustomerPhone) ? from : state.CustomerPhone)}
Dir: {state.Address ?? "—"}
GPS: {gpsText}

Gracias 🙌";

        // reset del estado
        state.Items.Clear();
        state.DeliveryType = null;

        state.RequestedDetailsForm = false;
        state.DetailsReceived = false;

        state.CustomerName = null;
        state.CustomerIdNumber = null;
        state.CustomerPhone = null;
        state.Address = null;
        state.PaymentMethod = null;
        state.ReceiverName = null;
        state.AdditionalNotes = null;
        state.LocationLat = null;
        state.LocationLng = null;
        state.LocationText = null;

        return receipt;
    }

    private static void TryCaptureCheckoutFromText(ConversationState state, string rawText, string from)
    {
        var lines = rawText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        if (lines.Count == 0) return;

        foreach (var line in lines)
        {
            var lower = line.ToLowerInvariant();

            if (lower.StartsWith("ubicacion") || lower.StartsWith("ubicación") || lower.StartsWith("gps"))
            {
                var v = ExtractValue(line);
                if (!string.IsNullOrWhiteSpace(v))
                    state.LocationText = v;
                continue;
            }

            if (lower.StartsWith("nombre"))
            {
                var v = ExtractValue(line);
                if (!string.IsNullOrWhiteSpace(v))
                    state.CustomerName = v;
                continue;
            }

            if (lower.StartsWith("cedula") || lower.StartsWith("cédula") || lower.StartsWith("id"))
            {
                var v = ExtractValue(line);
                if (!string.IsNullOrWhiteSpace(v))
                    state.CustomerIdNumber = v;
                continue;
            }

            if (lower.StartsWith("telefono") || lower.StartsWith("teléfono") || lower.StartsWith("tlf") || lower.StartsWith("tel"))
            {
                var v = ExtractValue(line);
                if (!string.IsNullOrWhiteSpace(v))
                    state.CustomerPhone = v;
                continue;
            }

            if (lower.StartsWith("direccion") || lower.StartsWith("dirección") || lower.StartsWith("dir"))
            {
                var v = ExtractValue(line);
                if (!string.IsNullOrWhiteSpace(v))
                    state.Address = v;
                continue;
            }

            if (lower.StartsWith("pago") || lower.StartsWith("forma de pago") || lower.StartsWith("metodo") || lower.StartsWith("método"))
            {
                var v = ExtractValue(line);
                if (!string.IsNullOrWhiteSpace(v))
                    state.PaymentMethod = v;
                continue;
            }

            if (lower.StartsWith("recibe"))
            {
                var v = ExtractValue(line);
                if (!string.IsNullOrWhiteSpace(v))
                    state.ReceiverName = v;
                continue;
            }

            if (lower.StartsWith("nota") || lower.StartsWith("notas") || lower.StartsWith("observ"))
            {
                var v = ExtractValue(line);
                if (!string.IsNullOrWhiteSpace(v))
                    state.AdditionalNotes = v;
                continue;
            }
        }

        if (string.IsNullOrWhiteSpace(state.CustomerPhone))
            state.CustomerPhone = from;
    }

    private static string ExtractValue(string line)
    {
        var idx = line.IndexOf(':');
        if (idx >= 0 && idx < line.Length - 1)
            return line[(idx + 1)..].Trim();

        idx = line.IndexOf('-');
        if (idx >= 0 && idx < line.Length - 1)
            return line[(idx + 1)..].Trim();

        return string.Empty;
    }

    private static bool HasMinimumCheckout(ConversationState state)
    {
        if (string.IsNullOrWhiteSpace(state.CustomerName)) return false;
        if (string.IsNullOrWhiteSpace(state.CustomerIdNumber)) return false;
        if (string.IsNullOrWhiteSpace(state.CustomerPhone)) return false;
        if (string.IsNullOrWhiteSpace(state.Address)) return false;

        var hasGps = state.LocationLat.HasValue && state.LocationLng.HasValue;
        var hasLocationText = !string.IsNullOrWhiteSpace(state.LocationText);

        return hasGps || hasLocationText;
    }

    private static string Normalize(string input)
        => input.Trim().ToLowerInvariant();

    private static bool IsConfirmCommand(string t)
        => t == "confirmar" || t == "confirmado" || t == "listo";

    private static string? NormalizeDeliveryType(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var t = input.ToLowerInvariant();

        if (t.Contains("delivery") || t.Contains("domicilio"))
            return "delivery";

        if (t.Contains("pick") || t.Contains("recoger"))
            return "pickup";

        return null;
    }

    private static bool IsDuplicateMessage(string conversationId, string messageId)
    {
        if (!_processedMessageIdsByConversation.TryGetValue(conversationId, out var set))
        {
            set = new HashSet<string>();
            _processedMessageIdsByConversation[conversationId] = set;
        }

        if (set.Contains(messageId))
            return true;

        set.Add(messageId);
        return false;
    }

    private static void PurgeOldState()
    {
        var now = DateTime.UtcNow;

        var stale = _stateByConversation
            .Where(kvp => now - kvp.Value.UpdatedAtUtc > StateTtl)
            .Select(kvp => kvp.Key)
            .ToList();

        foreach (var key in stale)
        {
            _stateByConversation.Remove(key);
            _processedMessageIdsByConversation.Remove(key);
        }
    }
}
