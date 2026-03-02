using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Application.Services;

public sealed class WebhookProcessor : IWebhookProcessor
{
    private readonly IAiParser _aiParser;
    private readonly IWhatsAppClient _whatsAppClient;
    private readonly IOrderRepository _orderRepository;

    // MVP in-memory conversation state (per instance)
    // TODO: replace with Redis/DB for SaaS multi-tenant + multi-instance
    private static readonly Dictionary<string, ConversationState> _stateByConversation = new();

    private sealed class ConversationState
    {
        public bool Welcomed { get; set; }

        public List<(string Name, int Quantity)> Items { get; } = new();

        // "pickup" | "delivery"
        public string? DeliveryType { get; set; }

        // Planilla ya enviada 1 vez
        public bool RequestedDetailsForm { get; set; }

        // El usuario ya mandó la planilla llena (heurístico)
        public bool DetailsReceived { get; set; }

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

    public async Task ProcessAsync(
        WebhookPayload payload,
        CancellationToken cancellationToken = default)
    {
        if (payload?.Entry is null) return;

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
                    if (message.Type != "text") continue;
                    if (string.IsNullOrWhiteSpace(message.Text?.Body)) continue;

                    var conversationId = $"{message.From}:{phoneNumberId}";

                    if (!_stateByConversation.TryGetValue(conversationId, out var state))
                    {
                        state = new ConversationState();
                        _stateByConversation[conversationId] = state;
                    }
                    state.UpdatedAtUtc = DateTime.UtcNow;

                    // ya validamos IsNullOrWhiteSpace arriba
                    var rawText = message.Text!.Body!;

                    var parsed = await _aiParser.ParseAsync(
                        rawText,
                        message.From,
                        conversationId,
                        cancellationToken);

                    var replyText = await BuildReplyAsync(
                        parsed,
                        rawText,
                        conversationId,
                        message.From,
                        phoneNumberId,
                        cancellationToken);

                    var outgoing = new OutgoingMessage
                    {
                        To = message.From,
                        Body = replyText,
                        PhoneNumberId = phoneNumberId
                    };

                    await _whatsAppClient.SendTextMessageAsync(outgoing, cancellationToken);
                }
            }
        }
    }

    // -------------------------
    // Intent -> WhatsApp reply
    // -------------------------
    private async Task<string> BuildReplyAsync(
        AiParseResult parsed,
        string? rawText,
        string conversationId,
        string from,
        string phoneNumberId,
        CancellationToken ct)
    {
        var state = _stateByConversation[conversationId];

        var t = (rawText ?? "").Trim().ToLowerInvariant();

        // ✅ Comando de cierre (siempre disponible)
        if (t == "confirmar" || t == "confirmado" || t == "listo")
        {
            return await FinalizeOrderIfPossibleAsync(state, from, phoneNumberId, ct);
        }

        // ✅ Anti-loop: si intent=General pero el texto contiene "pedido"/"reserv"
        if (parsed.Intent == RestaurantIntent.General && state.Items.Count == 0)
        {
            var looksLikeOrder =
                t.Contains("pedido") ||
                t.Contains("orden") ||
                t.Contains("comprar") ||
                t.Contains("quiero pedir") ||
                t.Contains("quiero hacer un pedido");

            var looksLikeReservation =
                t.Contains("reserv") ||
                t.Contains("mesa") ||
                t.Contains("reservación") ||
                t.Contains("reservacion");

            if (looksLikeOrder)
            {
                state.Welcomed = true;
                return await BuildOrderReplyAsync(parsed, rawText, conversationId, from, phoneNumberId, ct);
            }

            if (looksLikeReservation)
            {
                state.Welcomed = true;
                return BuildReservationReply(parsed);
            }
        }

        // ✅ Saludo inicial (solo 1 vez por conversación)
        if (!state.Welcomed && parsed.Intent == RestaurantIntent.General)
        {
            state.Welcomed = true;
            return "¡Hola! 👋 Para ayudarte rápido: ¿deseas hacer un *pedido* o una *reservación*?";
        }

        return parsed.Intent switch
        {
            RestaurantIntent.OrderCreate => await BuildOrderReplyAsync(parsed, rawText, conversationId, from, phoneNumberId, ct),
            RestaurantIntent.ReservationCreate => BuildReservationReply(parsed),
            RestaurantIntent.HumanHandoff => "Te paso con un humano en un momento 🙌",
            RestaurantIntent.General => "¿Deseas hacer un *pedido* o una *reservación*?",
            _ => "Gracias por tu mensaje."
        };
    }

    // ⚠️ NO async: no necesita await
    private Task<string> BuildOrderReplyAsync(
        AiParseResult parsed,
        string? rawText,
        string conversationId,
        string from,
        string phoneNumberId,
        CancellationToken ct)
    {
        var state = _stateByConversation[conversationId];
        var t = (rawText ?? "").Trim().ToLowerInvariant();

        // 1) Merge info de AI -> state
        if (parsed.Args.Order is not null)
        {
            foreach (var item in parsed.Args.Order.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Name) || item.Quantity <= 0) continue;
                state.Items.Add((item.Name.Trim(), item.Quantity));
            }

            if (!string.IsNullOrWhiteSpace(parsed.Args.Order.DeliveryType))
            {
                state.DeliveryType = NormalizeDeliveryType(parsed.Args.Order.DeliveryType);
            }
        }

        // 2) Capturar delivery/pickup suelto
        if (state.DeliveryType is null)
        {
            var maybe = NormalizeDeliveryType(t);
            if (maybe is not null) state.DeliveryType = maybe;
        }

        // 3) Heurística de planilla llena
        if (state.RequestedDetailsForm && !state.DetailsReceived)
        {
            var looksLikeFilledForm =
                t.Contains("nombre") &&
                (t.Contains("ced") || t.Contains("céd")) &&
                t.Contains("direc");

            if (looksLikeFilledForm)
            {
                state.DetailsReceived = true;
                return Task.FromResult("Recibido ✅ Cuando estés listo, escribe *CONFIRMAR* para finalizar el pedido.");
            }
        }

        // 4) Si no hay items todavía
        if (state.Items.Count == 0)
            return Task.FromResult("Perfecto 😊 ¿Qué deseas ordenar?");

        var itemsText = BuildItemsText(state);

        // 5) Si falta deliveryType
        if (string.IsNullOrWhiteSpace(state.DeliveryType))
        {
            return Task.FromResult($"Perfecto 👍 Tengo: {itemsText}\n\n¿Es *pick up* o *delivery*?");
        }

        // 6) Modo edición (si ya mandamos planilla)
        if (state.RequestedDetailsForm)
        {
            if (parsed.Intent == RestaurantIntent.OrderCreate || LooksLikeNewItemText(t))
            {
                itemsText = BuildItemsText(state);
                var prettyDelivery = state.DeliveryType == "pickup" ? "pick up" : "delivery";

                return Task.FromResult(
$@"Agregado ✅

Pedido actual: {itemsText}
Tipo: {prettyDelivery}

Cuando termines, escribe *CONFIRMAR* para finalizar (o agrega más productos).");
            }

            return Task.FromResult("Cuando tengas todo listo, escribe *CONFIRMAR* para finalizar tu pedido (o agrega más productos).");
        }

        // 7) Primera vez: mandamos planilla UNA vez
        state.RequestedDetailsForm = true;

        var deliveryPretty = state.DeliveryType == "pickup" ? "pick up" : "delivery";

        return Task.FromResult(
$@"Perfecto 👍 Pedido recibido: {itemsText}.
Tipo: {deliveryPretty}

Para agilizar el proceso, por favor envíanos lo siguiente (puedes responder copiando y llenando):

👤 *Nombre y apellido*: 
🪪 *Cédula de identidad*:
☎️ *Número de teléfono local*: 
📱 *Número de teléfono celular*: 
🏡 *Dirección escrita (Residencia, calle y N° apto/casa)*: 
📝 *Pedido*: {itemsText}
📦 *¿Recibe usted mismo/a?*: 
💵 *Forma de Pago*: 
📍 *Ubicación GPS*: 

Cuando termines, escribe *CONFIRMAR* para finalizar el pedido.");
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

        // Reset state
        state.Items.Clear();
        state.DeliveryType = null;
        state.RequestedDetailsForm = false;
        state.DetailsReceived = false;
        state.UpdatedAtUtc = DateTime.UtcNow;

        var prettyDelivery = deliveryType == "pickup" ? "pick up" : "delivery";
        return $"Pedido confirmado ✅\n\nItems: {itemsText}\nTipo: {prettyDelivery}\n\n¿Quieres hacer otro pedido o una reservación?";
    }

    private static string BuildReservationReply(AiParseResult parsed)
    {
        if (parsed.Args.Reservation is null)
            return "Perfecto 😊 ¿Para qué fecha deseas reservar?";

        return $"Reserva recibida para {parsed.Args.Reservation.Date} a las {parsed.Args.Reservation.Time}. ¿Para cuántas personas?";
    }

    private static string BuildItemsText(ConversationState state)
    {
        return string.Join(", ",
            state.Items.Select(i => i.Quantity > 1 ? $"{i.Quantity} {i.Name}s" : $"{i.Quantity} {i.Name}"));
    }

    private static string? NormalizeDeliveryType(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var t = input.Trim().ToLowerInvariant();

        if (t == "delivery" || t.Contains("domicilio") || t.Contains("enviar") || t.Contains("envio"))
            return "delivery";

        if (t == "pickup" || t == "pick up" || t.Contains("recoger") || t.Contains("retiro") || t.Contains("retirar") || t.Contains("buscar"))
            return "pickup";

        return null;
    }

    private static bool LooksLikeNewItemText(string t)
    {
        if (string.IsNullOrWhiteSpace(t)) return false;

        if (t == "delivery" || t == "pickup" || t == "pick up" || t == "confirmar" || t == "confirmado" || t == "listo")
            return false;

        return t.Any(char.IsDigit);
    }
}
