using System.Collections.Concurrent;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Application.Services;

public sealed class WebhookProcessor : IWebhookProcessor
{
    private readonly IAiParser _aiParser;
    private readonly IWhatsAppClient _whatsAppClient;
    private readonly IOrderRepository _orderRepository;

    // Multi-tenant ready: phoneNumberId = tenant key (each WABA number = tenant)
    private static readonly ConcurrentDictionary<string, ConversationState> _stateByConversation = new();

    // Dedupe: conversationId -> processed message ids (trimmed by TTL)
    private static readonly ConcurrentDictionary<string, ConcurrentQueue<(string MessageId, DateTime Utc)>> _processedByConversation = new();

    private static readonly TimeSpan ConversationTtl = TimeSpan.FromHours(6);
    private static readonly TimeSpan DedupeTtl = TimeSpan.FromHours(2);

    private enum FlowPhase
    {
        Idle = 0,
        CollectingOrder = 1,
        AwaitingDeliveryType = 2,
        AwaitingDetails = 3,
        ReadyToConfirm = 4
    }

    private sealed class ConversationState
    {
        public bool Welcomed { get; set; }

        public FlowPhase Phase { get; set; } = FlowPhase.Idle;

        public List<(string Name, int Quantity)> Items { get; } = new();

        // "pickup" | "delivery"
        public string? DeliveryType { get; set; }

        // Planilla ya enviada 1 vez
        public bool RequestedDetailsForm { get; set; }

        // El usuario ya mandó la planilla llena (heurístico) o envió GPS
        public bool DetailsReceived { get; set; }

        // After finalizing, ignore late/out-of-order form-like payloads for a short window
        public DateTime? LastFinalizedAtUtc { get; set; }

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

        CleanupOldConversations();

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
                    var from = message.From;
                    if (string.IsNullOrWhiteSpace(from)) continue;

                    // Multi-tenant conversation id
                    var conversationId = $"{phoneNumberId}:{from}";

                    // Dedupe (if your DTO doesn't have Id, set this to null and it will just skip dedupe)
                    var messageId = message.Id;

                    if (!string.IsNullOrWhiteSpace(messageId) && AlreadyProcessed(conversationId, messageId))
                        continue;

                    var state = _stateByConversation.GetOrAdd(conversationId, _ => new ConversationState());
                    state.UpdatedAtUtc = DateTime.UtcNow;

                    // Read input
                    string rawText;

                    if (message.Type == "location")
                    {
                        // Mark as details received and let AI see it as "ubicacion gps"
                        state.DetailsReceived = true;
                        rawText = "ubicacion gps";
                    }
                    else
                    {
                        if (message.Type != "text") continue;
                        if (string.IsNullOrWhiteSpace(message.Text?.Body)) continue;
                        rawText = message.Text!.Body!;
                    }

                    rawText = rawText.Trim();
                    if (rawText.Length == 0) continue;

                    // If a late filled form arrives AFTER finalize, do NOT restart flow
                    if (LooksLikeFilledForm(rawText) && state.Items.Count == 0 && state.LastFinalizedAtUtc is not null)
                    {
                        var replyLate = "Recibido ✅ Si quieres hacer un nuevo pedido, dime *qué deseas ordenar*.";
                        await SendAsync(phoneNumberId, from, replyLate, cancellationToken);
                        continue;
                    }

                    // Always parse via your IAiParser (no manual AiArgs construction)
                    var parsed = await _aiParser.ParseAsync(rawText, from, conversationId, cancellationToken);

                    var replyText = await BuildReplyAsync(parsed, rawText, conversationId, from, phoneNumberId, cancellationToken);
                    await SendAsync(phoneNumberId, from, replyText, cancellationToken);
                }
            }
        }
    }

    private async Task SendAsync(string phoneNumberId, string to, string body, CancellationToken ct)
    {
        var outgoing = new OutgoingMessage
        {
            To = to,
            Body = body,
            PhoneNumberId = phoneNumberId
        };

        await _whatsAppClient.SendTextMessageAsync(outgoing, ct);
    }

    // -------------------------
    // Intent -> WhatsApp reply
    // -------------------------
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

        // Confirm always available
        if (IsConfirmCommand(t))
        {
            return await FinalizeOrderIfPossibleAsync(state, from, phoneNumberId, ct);
        }

        // Anti-loop: General but user clearly wants order/reservation
        if (parsed.Intent == RestaurantIntent.General && state.Items.Count == 0)
        {
            if (LooksLikeOrderIntent(t))
            {
                state.Welcomed = true;
                state.Phase = FlowPhase.CollectingOrder;
                return await BuildOrderReplyAsync(parsed, rawText, conversationId);
            }

            if (LooksLikeReservationIntent(t))
            {
                state.Welcomed = true;
                return BuildReservationReply(parsed);
            }
        }

        // Welcome once
        if (!state.Welcomed && parsed.Intent == RestaurantIntent.General)
        {
            state.Welcomed = true;
            return "¡Hola! 👋 Para ayudarte rápido: ¿deseas hacer un *pedido* o una *reservación*?";
        }

        return parsed.Intent switch
        {
            RestaurantIntent.OrderCreate => await BuildOrderReplyAsync(parsed, rawText, conversationId),
            RestaurantIntent.ReservationCreate => BuildReservationReply(parsed),
            RestaurantIntent.HumanHandoff => "Te paso con un humano en un momento 🙌",
            RestaurantIntent.General => "¿Deseas hacer un *pedido* o una *reservación*?",
            _ => "Gracias por tu mensaje."
        };
    }

    // Order flow (NO async method here)
    private Task<string> BuildOrderReplyAsync(AiParseResult parsed, string rawText, string conversationId)
    {
        var state = _stateByConversation[conversationId];
        var t = Normalize(rawText);

        // If user pasted full form, mark details received (do not reprint)
        if (LooksLikeFilledForm(rawText))
        {
            state.DetailsReceived = true;

            if (state.Items.Count == 0)
                return Task.FromResult("Recibido ✅ Si quieres hacer un pedido, dime *qué deseas ordenar*.");

            if (!string.IsNullOrWhiteSpace(state.DeliveryType))
                state.Phase = FlowPhase.ReadyToConfirm;

            return Task.FromResult("Recibido ✅ Escribe *CONFIRMAR* para finalizar (o agrega más productos).");
        }

        // Merge AI -> state
        if (parsed.Args?.Order is not null)
        {
            foreach (var item in parsed.Args.Order.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Name) || item.Quantity <= 0) continue;
                state.Items.Add((item.Name.Trim(), item.Quantity));
            }

            if (!string.IsNullOrWhiteSpace(parsed.Args.Order.DeliveryType))
                state.DeliveryType = NormalizeDeliveryType(parsed.Args.Order.DeliveryType);
        }

        // Capture delivery/pickup standalone
        if (state.DeliveryType is null)
        {
            var maybe = NormalizeDeliveryType(t);
            if (maybe is not null) state.DeliveryType = maybe;
        }

        // No items
        if (state.Items.Count == 0)
        {
            state.Phase = FlowPhase.CollectingOrder;
            return Task.FromResult("Perfecto 😊 ¿Qué deseas ordenar?");
        }

        var itemsText = BuildItemsText(state);

        // Missing delivery type
        if (string.IsNullOrWhiteSpace(state.DeliveryType))
        {
            state.Phase = FlowPhase.AwaitingDeliveryType;
            return Task.FromResult($"Perfecto 👍 Tengo: {itemsText}\n\n¿Es *pick up* o *delivery*?");
        }

        // Edit mode: no reprint
        if (state.RequestedDetailsForm)
        {
            state.Phase = state.DetailsReceived ? FlowPhase.ReadyToConfirm : FlowPhase.AwaitingDetails;

            if (parsed.Intent == RestaurantIntent.OrderCreate || LooksLikeNewItemText(t))
            {
                itemsText = BuildItemsText(state);
                var prettyDelivery = PrettyDelivery(state.DeliveryType);

                if (state.DetailsReceived)
                {
                    return Task.FromResult(
$@"Agregado ✅

🧾 *Pedido actual*
Items: {itemsText}
Tipo: {prettyDelivery}

Escribe *CONFIRMAR* para finalizar (o agrega más productos).");
                }

                return Task.FromResult(
$@"Agregado ✅

Pedido actual: {itemsText}
Tipo: {prettyDelivery}

Pega la planilla completa y luego escribe *CONFIRMAR* para finalizar (o agrega más productos).");
            }

            return Task.FromResult(state.DetailsReceived
                ? "Perfecto ✅ Escribe *CONFIRMAR* para finalizar (o agrega más productos)."
                : "Pega la planilla completa y luego escribe *CONFIRMAR* para finalizar (o agrega más productos).");
        }

        // First time: send the form ONCE
        state.RequestedDetailsForm = true;
        state.Phase = FlowPhase.AwaitingDetails;

        var deliveryPretty = PrettyDelivery(state.DeliveryType);
        return Task.FromResult(BuildDetailsFormMessage(itemsText, deliveryPretty));
    }

    private async Task<string> FinalizeOrderIfPossibleAsync(
        ConversationState state,
        string from,
        string phoneNumberId,
        CancellationToken ct)
    {
        if (state.Items.Count == 0)
            return "Aún no tengo productos en tu pedido. ¿Qué deseas ordenar?";

        if (string.IsNullOrWhiteSpace(state.DeliveryType))
            return $"Listo ✅ Tengo: {BuildItemsText(state)}\n\n¿Es *pick up* o *delivery*?";

        var itemsText = BuildItemsText(state);
        var deliveryType = state.DeliveryType!;

        var order = new Order
        {
            From = from,
            PhoneNumberId = phoneNumberId,
            DeliveryType = deliveryType,
            CreatedAtUtc = DateTime.UtcNow,
            Items = state.Items.Select(i => new WhatsAppSaaS.Domain.Entities.OrderItem
            {
                Name = i.Name,
                Quantity = i.Quantity
            }).ToList()
        };

        await _orderRepository.AddOrderAsync(order, ct);

        var receipt = BuildReceipt(itemsText, PrettyDelivery(deliveryType));

        // Reset (keep LastFinalizedAtUtc to protect against late form paste)
        state.Items.Clear();
        state.DeliveryType = null;
        state.RequestedDetailsForm = false;
        state.DetailsReceived = false;
        state.Phase = FlowPhase.Idle;
        state.LastFinalizedAtUtc = DateTime.UtcNow;
        state.UpdatedAtUtc = DateTime.UtcNow;

        return receipt;
    }

    // -------------------------
    // Messages / Formatting
    // -------------------------
    private static string BuildDetailsFormMessage(string itemsText, string deliveryPretty)
    {
        return
$@"Perfecto 👍 *Pedido registrado*
Items: {itemsText}
Tipo: {deliveryPretty}

Para agilizar el proceso, envíanos lo siguiente (puedes responder copiando y llenando):

👤 *Nombre y apellido*: 
🪪 *Cédula de identidad*: 
☎️ *Teléfono local*: 
📱 *Teléfono celular*: 
🏡 *Dirección (Residencia, calle y N° apto/casa)*: 
📦 *¿Recibe usted mismo/a?*: 
💵 *Forma de pago*: 
📍 *Ubicación GPS*: 

Cuando termines, escribe *CONFIRMAR*.";
    }

    private static string BuildReceipt(string itemsText, string deliveryPretty)
    {
        return
$@"🧾 *PEDIDO CONFIRMADO* ✅

Items: {itemsText}
Tipo: {deliveryPretty}

Gracias 🙌 Si quieres hacer otro pedido, dime *qué deseas ordenar*.";
    }

    private static string BuildReservationReply(AiParseResult parsed)
    {
        if (parsed.Args?.Reservation is null)
            return "Perfecto 😊 ¿Para qué fecha deseas reservar?";

        return $"Reserva recibida para {parsed.Args.Reservation.Date} a las {parsed.Args.Reservation.Time}. ¿Para cuántas personas?";
    }

    private static string BuildItemsText(ConversationState state)
    {
        return string.Join(", ", state.Items.Select(i => $"{i.Quantity} {i.Name}"));
    }

    // -------------------------
    // Helpers
    // -------------------------
    private static string Normalize(string input)
        => (input ?? "").Trim().ToLowerInvariant();

    private static bool IsConfirmCommand(string t)
        => t is "confirmar" or "confirmado" or "listo" or "ok" or "okey";

    private static bool LooksLikeOrderIntent(string t)
    {
        return t.Contains("pedido") ||
               t.Contains("orden") ||
               t.Contains("comprar") ||
               t.Contains("quiero pedir") ||
               t.Contains("quiero un pedido") ||
               t.Contains("hacer un pedido");
    }

    private static bool LooksLikeReservationIntent(string t)
    {
        return t.Contains("reserv") ||
               t.Contains("mesa") ||
               t.Contains("reservación") ||
               t.Contains("reservacion");
    }

    private static bool LooksLikeFilledForm(string rawText)
    {
        var t = Normalize(rawText);

        var hasName = t.Contains("nombre") && (t.Contains("apellido") || t.Contains("y apellido"));
        var hasId = t.Contains("céd") || t.Contains("cedula") || t.Contains("ced");
        var hasAddress = t.Contains("direc") || t.Contains("residencia");
        var hasPayment = t.Contains("pago") || t.Contains("efectivo") || t.Contains("pago móvil") || t.Contains("pago movil");
        var hasOrderLine = t.Contains("pedido:");

        var score = 0;
        if (hasName) score++;
        if (hasId) score++;
        if (hasAddress) score++;
        if (hasPayment) score++;
        if (hasOrderLine) score++;

        return score >= 3;
    }

    private static string PrettyDelivery(string? normalized)
        => normalized == "pickup" ? "pick up" : "delivery";

    private static string? NormalizeDeliveryType(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var t = Normalize(input);

        if (t == "delivery" || t.Contains("domicilio") || t.Contains("enviar") || t.Contains("envio") || t.Contains("envío"))
            return "delivery";

        if (t == "pickup" || t == "pick up" || t.Contains("recoger") || t.Contains("retiro") || t.Contains("retirar") || t.Contains("buscar"))
            return "pickup";

        return null;
    }

    private static bool LooksLikeNewItemText(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return false;

        if (t is "delivery" or "pickup" or "pick up" or "confirmar" or "confirmado" or "listo")
            return false;

        return t.Any(char.IsDigit);
    }

    // -------------------------
    // Dedupe / Cleanup
    // -------------------------
    private static bool AlreadyProcessed(string conversationId, string messageId)
    {
        var q = _processedByConversation.GetOrAdd(conversationId, _ => new ConcurrentQueue<(string, DateTime)>());

        TrimQueue(q, DedupeTtl);

        foreach (var (mid, _) in q)
            if (mid == messageId) return true;

        q.Enqueue((messageId, DateTime.UtcNow));
        return false;
    }

    private static void TrimQueue(ConcurrentQueue<(string MessageId, DateTime Utc)> q, TimeSpan ttl)
    {
        while (q.TryPeek(out var head))
        {
            if (DateTime.UtcNow - head.Utc <= ttl) break;
            q.TryDequeue(out _);
        }
    }

    private static void CleanupOldConversations()
    {
        var now = DateTime.UtcNow;

        foreach (var kv in _stateByConversation)
        {
            var state = kv.Value;
            if (now - state.UpdatedAtUtc <= ConversationTtl) continue;

            _stateByConversation.TryRemove(kv.Key, out _);
            _processedByConversation.TryRemove(kv.Key, out _);
        }
    }
}
