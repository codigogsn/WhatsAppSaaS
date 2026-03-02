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
        public string? DeliveryType { get; set; } // "pickup" | "delivery"
        public bool RequestedDetailsForm { get; set; }

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

                    // Ensure conversation state exists
                    if (!_stateByConversation.TryGetValue(conversationId, out var state))
                    {
                        state = new ConversationState();
                        _stateByConversation[conversationId] = state;
                    }
                    state.UpdatedAtUtc = DateTime.UtcNow;

                    var rawText = message.Text.Body;

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
        string rawText,
        string conversationId,
        string from,
        string phoneNumberId,
        CancellationToken ct)
    {
        var state = _stateByConversation[conversationId];

        // ✅ Anti-loop: si el intent viene General pero el texto contiene intención clara,
        // lo tratamos como confirmación (pedido/reservación) en vez de volver a preguntar.
        if (parsed.Intent == RestaurantIntent.General)
        {
            var t = (rawText ?? "").Trim().ToLowerInvariant();

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
                state.Welcomed = true; // por si venía de un saludo
                return await BuildOrderReplyAsync(parsed, conversationId, from, phoneNumberId, ct);
            }

            if (looksLikeReservation)
            {
                state.Welcomed = true;
                return BuildReservationReply(parsed);
            }
        }

        // ✅ 1) Saludo inicial (solo 1 vez por conversación)
        // Si el AI clasifica "General" y aún no hemos dado bienvenida, damos un arranque humano.
        if (!state.Welcomed && parsed.Intent == RestaurantIntent.General)
        {
            state.Welcomed = true;
            return "¡Hola! 👋 ¿Cómo estás? Para ayudarte rápido: ¿deseas hacer un *pedido* o una *reservación*?";
        }

        return parsed.Intent switch
        {
            RestaurantIntent.OrderCreate => await BuildOrderReplyAsync(parsed, conversationId, from, phoneNumberId, ct),
            RestaurantIntent.ReservationCreate => BuildReservationReply(parsed),
            RestaurantIntent.HumanHandoff => "Te paso con un humano en un momento 🙌",
            RestaurantIntent.General => "¿Deseas hacer un pedido o una reservación?",
            _ => "Gracias por tu mensaje."
        };
    }

    private async Task<string> BuildOrderReplyAsync(
        AiParseResult parsed,
        string conversationId,
        string from,
        string phoneNumberId,
        CancellationToken ct)
    {
        var state = _stateByConversation[conversationId];

        // ✅ Si no hay nada de orden, preguntamos qué desea ordenar
        if (parsed.Args.Order is null && state.Items.Count == 0)
            return "Perfecto 😊 ¿Qué deseas ordenar?";

        // Merge new info from AI -> state
        if (parsed.Args.Order is not null)
        {
            foreach (var item in parsed.Args.Order.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Name) || item.Quantity <= 0) continue;
                state.Items.Add((item.Name.Trim(), item.Quantity));
            }

            if (!string.IsNullOrWhiteSpace(parsed.Args.Order.DeliveryType))
            {
                state.DeliveryType = parsed.Args.Order.DeliveryType.Trim().ToLowerInvariant();
            }
        }

        state.UpdatedAtUtc = DateTime.UtcNow;

        if (state.Items.Count == 0)
            return "¿Qué deseas ordenar?";

        // (MVP) pluralización naive con "s"
        var itemsText = string.Join(", ",
            state.Items.Select(i => i.Quantity > 1 ? $"{i.Quantity} {i.Name}s" : $"{i.Quantity} {i.Name}"));

        // ✅ 2) Mensaje de recolección de data DESPUÉS de que el usuario dijo el pedido
        // Lo mandamos 1 vez cuando ya tenemos items y todavía no lo hemos pedido
        if (!state.RequestedDetailsForm)
        {
            state.RequestedDetailsForm = true;

            // OJO: tu mensaje + el pedido precargado
            return
$@"Perfecto 👍 Pedido recibido: {itemsText}.

Para agilizar el proceso, y poder enviar a nuestro repartidor correctamente por favor envíenos lo siguiente: 

👤 *Nombre y apellido*: 
🪪 *Cédula de identidad*:
☎️ *Número de teléfono local*: 
📱 *Número de teléfono celular* 
🏡 *Dirección  escrita  (Nombre de residencia, calle y N° apto/casa):* 
📝 *Pedido*: {itemsText}
📦 *¿Recibe usted mismo/a?:* 
💵 *Forma de Pago:* 
📍 *Ubicación GPS*.

Y porfa indícanos si es *pick up* o *delivery*.";
        }

        // Si ya pidió la planilla una vez, aquí seguimos intentando completar delivery_type (si llegó suelto)
        if (string.IsNullOrWhiteSpace(state.DeliveryType))
        {
            return "Listo ✅ ¿Es *pick up* o *delivery*?";
        }

        var deliveryType = state.DeliveryType;
        if (deliveryType != "pickup" && deliveryType != "delivery")
        {
            state.DeliveryType = null;
            return "Disculpa 🙏 ¿Es *pick up* o *delivery*?";
        }

        // Persist order (DB) vía repositorio
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

        // Reset state for next order (but keep Welcomed)
        state.Items.Clear();
        state.DeliveryType = null;
        state.RequestedDetailsForm = false;
        state.UpdatedAtUtc = DateTime.UtcNow;

        var prettyDelivery = deliveryType == "pickup" ? "pick up" : "delivery";
        return $"Pedido confirmado ✅\n\nItems: {itemsText}\nTipo: {prettyDelivery}\n\n¿Algo más que quieras agregar?";
    }

    private static string BuildReservationReply(AiParseResult parsed)
    {
        if (parsed.Args.Reservation is null)
            return "Perfecto 😊 ¿Para qué fecha deseas reservar?";

        return $"Reserva recibida para {parsed.Args.Reservation.Date} a las {parsed.Args.Reservation.Time}. ¿Para cuántas personas?";
    }
}
