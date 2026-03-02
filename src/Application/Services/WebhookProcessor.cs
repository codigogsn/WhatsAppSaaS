using System.Collections.Concurrent;
using System.Text;
using System.Text.RegularExpressions;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Application.Services;

public sealed class WebhookProcessor : IWebhookProcessor
{
    private readonly IAiParser _aiParser;
    private readonly IWhatsAppClient _whatsAppClient;
    private readonly IOrderRepository _orderRepository;

    // --------------------------------------------
    // In-memory state (MVP). Production SaaS:
    // replace with Redis/DB (multi-tenant + multi-instance).
    // --------------------------------------------
    private static readonly ConcurrentDictionary<string, ConversationState> _stateByConversation = new();

    // Cleanup config
    private static readonly TimeSpan ConversationTtl = TimeSpan.FromMinutes(45);

    private sealed class ConversationState
    {
        public bool Welcomed { get; set; }

        // Normalized items by key (prevents duplicates)
        public Dictionary<string, OrderLine> ItemsByKey { get; } = new(StringComparer.OrdinalIgnoreCase);

        // "pickup" | "delivery"
        public string? DeliveryType { get; set; }

        // We only send the form ONCE per conversation "order cycle"
        public bool RequestedDetailsForm { get; set; }

        // The user already sent a filled form (parsed)
        public bool DetailsReceived { get; set; }

        public CustomerDetails Details { get; } = new();

        // Idempotency: prevent Meta retries / duplicate webhooks from duplicating processing
        // Keep last N message ids per conversation
        public LinkedList<string> RecentMessageIds { get; } = new();
        public HashSet<string> RecentMessageIdSet { get; } = new(StringComparer.Ordinal);
        public object Gate { get; } = new();

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        // Order cycle id (helps when you later persist to Redis)
        public int Cycle { get; set; } = 1;
    }

    private sealed class OrderLine
    {
        public required string DisplayName { get; init; }
        public int Quantity { get; set; }
    }

    private sealed class CustomerDetails
    {
        public string? FullName { get; set; }
        public string? IdNumber { get; set; }
        public string? LocalPhone { get; set; }
        public string? MobilePhone { get; set; }
        public string? Address { get; set; }
        public string? ReceivesSelf { get; set; }
        public string? PaymentMethod { get; set; }
        public string? Gps { get; set; }
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

                    var rawText = message.Text!.Body!;
                    var from = message.From;
                    var msgId = message.Id ?? ""; // Meta sends wamid.* typically

                    // Multi-tenant safe conversation id:
                    // user + phone number id (each WABA phone is a "tenant boundary" in practice)
                    var conversationId = $"{from}:{phoneNumberId}";

                    var state = _stateByConversation.GetOrAdd(conversationId, _ => new ConversationState());

                    // Expire old conversations (prevents state ghosts)
                    if (DateTime.UtcNow - state.UpdatedAtUtc > ConversationTtl)
                    {
                        ResetOrderCycle(state, keepWelcome: false);
                    }
                    state.UpdatedAtUtc = DateTime.UtcNow;

                    // Idempotency guard: ignore duplicates (Meta retries / webhook duplicates)
                    if (!string.IsNullOrWhiteSpace(msgId))
                    {
                        lock (state.Gate)
                        {
                            if (state.RecentMessageIdSet.Contains(msgId))
                                continue;

                            state.RecentMessageIds.AddLast(msgId);
                            state.RecentMessageIdSet.Add(msgId);

                            // keep bounded
                            while (state.RecentMessageIds.Count > 120)
                            {
                                var oldest = state.RecentMessageIds.First!.Value;
                                state.RecentMessageIds.RemoveFirst();
                                state.RecentMessageIdSet.Remove(oldest);
                            }
                        }
                    }

                    var parsed = await _aiParser.ParseAsync(
                        rawText,
                        from,
                        conversationId,
                        cancellationToken);

                    var replyText = await BuildReplyAsync(
                        parsed,
                        rawText,
                        conversationId,
                        from,
                        phoneNumberId,
                        cancellationToken);

                    // If reply is empty, don't send anything
                    if (string.IsNullOrWhiteSpace(replyText)) continue;

                    var outgoing = new OutgoingMessage
                    {
                        To = from,
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

        var t = rawText.Trim();
        var tl = t.ToLowerInvariant();

        // 0) Global commands (always available)
        if (IsConfirmCommand(tl))
        {
            return await FinalizeOrderIfPossibleAsync(state, from, phoneNumberId, ct);
        }

        if (IsCancelCommand(tl))
        {
            ResetOrderCycle(state, keepWelcome: true);
            return "Listo ✅ Cancelé el pedido actual. ¿Deseas hacer un *pedido* o una *reservación*?";
        }

        // 1) Anti-loop: if General but text clearly indicates pedido/reserva
        if (parsed.Intent == RestaurantIntent.General && state.ItemsByKey.Count == 0)
        {
            var looksLikeOrder =
                tl.Contains("pedido") ||
                tl.Contains("orden") ||
                tl.Contains("comprar") ||
                tl.Contains("quiero pedir") ||
                tl.Contains("quiero un pedido") ||
                tl.Contains("hacer un pedido");

            var looksLikeReservation =
                tl.Contains("reserv") ||
                tl.Contains("mesa") ||
                tl.Contains("reservación") ||
                tl.Contains("reservacion");

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

        // 2) Welcome (only once)
        if (!state.Welcomed && parsed.Intent == RestaurantIntent.General)
        {
            state.Welcomed = true;
            return "¡Hola! 👋 Para ayudarte rápido: ¿deseas hacer un *pedido* o una *reservación*?";
        }

        // 3) Route intents
        return parsed.Intent switch
        {
            RestaurantIntent.OrderCreate => await BuildOrderReplyAsync(parsed, rawText, conversationId, from, phoneNumberId, ct),
            RestaurantIntent.ReservationCreate => BuildReservationReply(parsed),
            RestaurantIntent.HumanHandoff => "Te paso con un humano en un momento 🙌",
            RestaurantIntent.General => "¿Deseas hacer un *pedido* o una *reservación*?",
            _ => "Gracias por tu mensaje."
        };
    }

    // -------------------------
    // Order flow (pro)
    // -------------------------
    private Task<string> BuildOrderReplyAsync(
        AiParseResult parsed,
        string rawText,
        string conversationId,
        string from,
        string phoneNumberId,
        CancellationToken ct)
    {
        var state = _stateByConversation[conversationId];

        var t = rawText.Trim();
        var tl = t.ToLowerInvariant();

        // A) Capture delivery/pickup if user sends it alone
        if (string.IsNullOrWhiteSpace(state.DeliveryType))
        {
            var maybe = NormalizeDeliveryType(tl);
            if (maybe is not null) state.DeliveryType = maybe;
        }

        // B) If we already asked for the form, try to parse filled form BEFORE anything else
        // This prevents "reprint" and prevents the bot from re-asking.
        if (state.RequestedDetailsForm && !state.DetailsReceived)
        {
            if (TryParseDetailsForm(rawText, state.Details))
            {
                state.DetailsReceived = true;

                // If they still want to add products, they can; if they confirm, we finalize.
                var itemsText = BuildItemsText(state);
                var prettyDelivery = PrettyDelivery(state.DeliveryType);

                return Task.FromResult(
$@"Recibido ✅

🧾 *Resumen actual*
🛒 Items: {itemsText}
🚚 Tipo: {prettyDelivery}

Si quieres *agregar* algo más, escríbelo ahora.
Si ya está listo, escribe *CONFIRMAR* para finalizar.");
            }
        }

        // C) Merge AI order -> state (dedupe & accumulate)
        if (parsed.Args.Order is not null)
        {
            foreach (var item in parsed.Args.Order.Items)
            {
                if (string.IsNullOrWhiteSpace(item.Name) || item.Quantity <= 0) continue;
                AddOrAccumulateItem(state, item.Name, item.Quantity);
            }

            if (!string.IsNullOrWhiteSpace(parsed.Args.Order.DeliveryType))
            {
                state.DeliveryType = NormalizeDeliveryType(parsed.Args.Order.DeliveryType) ?? state.DeliveryType;
            }
        }

        // D) If the message looks like "new item" text (even if AI didn't parse perfectly),
        // allow edit mode additions AFTER form was requested.
        if (state.RequestedDetailsForm && LooksLikeNewItemText(tl))
        {
            // If AI didn't add anything, don't guess wildly.
            // But if it DID add, we show updated summary below.
        }

        // E) If no items yet -> ask what to order
        if (state.ItemsByKey.Count == 0)
        {
            return Task.FromResult("Perfecto 😊 ¿Qué deseas ordenar?");
        }

        var itemsNow = BuildItemsText(state);

        // F) If missing deliveryType -> ask
        if (string.IsNullOrWhiteSpace(state.DeliveryType))
        {
            return Task.FromResult($"Perfecto 👍 Tengo: {itemsNow}\n\n¿Es *pick up* o *delivery*?");
        }

        // G) If form already requested:
        // - If user adds more products: show updated summary, DON'T reprint form
        // - Otherwise: gently remind to fill form/confirm
        if (state.RequestedDetailsForm)
        {
            var prettyDelivery = PrettyDelivery(state.DeliveryType);

            // If details not received yet, keep them focused on filling the form
            if (!state.DetailsReceived)
            {
                // If they are adding products, we update summary (no form reprint)
                if (parsed.Intent == RestaurantIntent.OrderCreate || LooksLikeNewItemText(tl))
                {
                    return Task.FromResult(
$@"Agregado ✅

🧾 *Resumen actual*
🛒 Items: {BuildItemsText(state)}
🚚 Tipo: {prettyDelivery}

Ahora copia y llena la planilla que te envié arriba.
Cuando termines, escribe *CONFIRMAR*.");
                }

                return Task.FromResult("Cuando termines de llenar la planilla, escribe *CONFIRMAR* para finalizar (o agrega más productos).");
            }

            // Details received: allow additions and confirm
            if (parsed.Intent == RestaurantIntent.OrderCreate || LooksLikeNewItemText(tl))
            {
                return Task.FromResult(
$@"Agregado ✅

🧾 *Resumen actual*
🛒 Items: {BuildItemsText(state)}
🚚 Tipo: {prettyDelivery}

Si ya está listo, escribe *CONFIRMAR* para finalizar.");
            }

            return Task.FromResult("Si ya está listo, escribe *CONFIRMAR* para finalizar (o agrega más productos).");
        }

        // H) First time we have items + deliveryType -> ask for form ONCE
        state.RequestedDetailsForm = true;

        var deliveryPretty = PrettyDelivery(state.DeliveryType);

        return Task.FromResult(
$@"Perfecto 👍 *Pedido recibido*
🛒 Items: {itemsNow}
🚚 Tipo: {deliveryPretty}

Para agilizar el proceso, por favor envíanos lo siguiente *(puedes responder copiando y llenando)*:

👤 *Nombre y apellido*: 
🪪 *Cédula de identidad*:
☎️ *Número de teléfono local*: 
📱 *Número de teléfono celular*: 
🏡 *Dirección escrita (Residencia, calle y N° apto/casa)*: 
📝 *Pedido*: {itemsNow}
📦 *¿Recibe usted mismo/a?*: 
💵 *Forma de Pago*: 
📍 *Ubicación GPS*: 

Cuando termines, escribe *CONFIRMAR* para finalizar.");
    }

    private async Task<string> FinalizeOrderIfPossibleAsync(
        ConversationState state,
        string from,
        string phoneNumberId,
        CancellationToken ct)
    {
        // Guard 1: must have items
        if (state.ItemsByKey.Count == 0)
            return "Aún no tengo productos en tu pedido. ¿Qué deseas ordenar?";

        // Guard 2: must have delivery type
        if (string.IsNullOrWhiteSpace(state.DeliveryType))
            return $"Listo ✅ Tengo: {BuildItemsText(state)}\n\n¿Es *pick up* o *delivery*?";

        // Guard 3: if we requested the form, require it (professional flow)
        if (state.RequestedDetailsForm && !state.DetailsReceived)
        {
            return "Antes de confirmar, por favor copia y llena la planilla que te envié arriba. Cuando termines, escribe *CONFIRMAR*.";
        }

        var itemsText = BuildItemsText(state);
        var deliveryType = state.DeliveryType!;
        var prettyDelivery = PrettyDelivery(deliveryType);

        // Persist order
        var order = new Order
        {
            From = from,
            PhoneNumberId = phoneNumberId,
            DeliveryType = deliveryType,
            CreatedAtUtc = DateTime.UtcNow,
            Items = state.ItemsByKey.Values.Select(v => new WhatsAppSaaS.Domain.Entities.OrderItem
            {
                Name = v.DisplayName,
                Quantity = v.Quantity
            }).ToList()
        };

        await _orderRepository.AddOrderAsync(order, ct);

        // Build receipt BEFORE resetting (so we can include details)
        var receipt = BuildReceipt(state, itemsText, prettyDelivery);

        // Reset cycle (do not lose welcome)
        ResetOrderCycle(state, keepWelcome: true);

        return receipt;
    }

    // -------------------------
    // Helpers
    // -------------------------
    private static void ResetOrderCycle(ConversationState state, bool keepWelcome)
    {
        state.ItemsByKey.Clear();
        state.DeliveryType = null;
        state.RequestedDetailsForm = false;
        state.DetailsReceived = false;
        state.Details.FullName = null;
        state.Details.IdNumber = null;
        state.Details.LocalPhone = null;
        state.Details.MobilePhone = null;
        state.Details.Address = null;
        state.Details.ReceivesSelf = null;
        state.Details.PaymentMethod = null;
        state.Details.Gps = null;

        state.UpdatedAtUtc = DateTime.UtcNow;
        state.Cycle++;

        if (!keepWelcome)
            state.Welcomed = false;
    }

    private static void AddOrAccumulateItem(ConversationState state, string rawName, int qty)
    {
        var name = rawName.Trim();
        if (string.IsNullOrWhiteSpace(name) || qty <= 0) return;

        var key = NormalizeItemKey(name);

        if (state.ItemsByKey.TryGetValue(key, out var existing))
        {
            existing.Quantity += qty; // accumulate, prevents duplicates
            return;
        }

        state.ItemsByKey[key] = new OrderLine
        {
            DisplayName = name,
            Quantity = qty
        };
    }

    private static string NormalizeItemKey(string name)
    {
        var s = name.Trim().ToLowerInvariant();

        // light normalization: remove punctuation, collapse spaces
        s = Regex.Replace(s, @"[^\p{L}\p{N}\s]", " ");
        s = Regex.Replace(s, @"\s+", " ").Trim();

        return s;
    }

    private static string BuildItemsText(ConversationState state)
    {
        // Keep stable ordering (by insertion-ish): Dictionary preserves insertion order on .NET 8
        var parts = new List<string>();

        foreach (var v in state.ItemsByKey.Values)
        {
            if (v.Quantity <= 0) continue;
            parts.Add(v.Quantity == 1 ? $"1 {v.DisplayName}" : $"{v.Quantity} {v.DisplayName}");
        }

        return parts.Count == 0 ? "(vacío)" : string.Join(", ", parts);
    }

    private static string? NormalizeDeliveryType(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var t = input.Trim().ToLowerInvariant();

        // delivery synonyms
        if (t == "delivery" ||
            t.Contains("domicilio") ||
            t.Contains("enviar") ||
            t.Contains("envío") ||
            t.Contains("envio"))
            return "delivery";

        // pickup synonyms
        if (t == "pickup" ||
            t == "pick up" ||
            t.Contains("recoger") ||
            t.Contains("retiro") ||
            t.Contains("retirar") ||
            t.Contains("buscar"))
            return "pickup";

        return null;
    }

    private static string PrettyDelivery(string? deliveryType)
    {
        if (string.Equals(deliveryType, "pickup", StringComparison.OrdinalIgnoreCase))
            return "pick up";
        if (string.Equals(deliveryType, "delivery", StringComparison.OrdinalIgnoreCase))
            return "delivery";
        return "(pendiente)";
    }

    private static bool IsConfirmCommand(string tl)
    {
        // Keep strict but friendly
        return tl is "confirmar" or "confirmado" or "listo" or "ok confirmar";
    }

    private static bool IsCancelCommand(string tl)
    {
        return tl is "cancelar" or "anular" or "olvida" or "olvidalo" or "olvídalo";
    }

    private static bool LooksLikeNewItemText(string tl)
    {
        if (string.IsNullOrWhiteSpace(tl)) return false;

        // not commands / not delivery keywords
        if (IsConfirmCommand(tl) || IsCancelCommand(tl)) return false;
        if (tl is "delivery" or "pickup" or "pick up") return false;

        // heuristic: messages with digits are likely "2 coca colas", "1 pizza", etc.
        // or contains typical food words
        if (tl.Any(char.IsDigit)) return true;

        var hints = new[] { "hamburg", "pizza", "coca", "papas", "refresco", "agua", "empan", "arepa", "combo" };
        return hints.Any(h => tl.Contains(h));
    }

    private static bool TryParseDetailsForm(string raw, CustomerDetails details)
    {
        // Heuristic: must contain at least 3 anchors to be considered the filled form
        var tl = raw.ToLowerInvariant();
        var anchors =
            (tl.Contains("nombre") ? 1 : 0) +
            ((tl.Contains("ced") || tl.Contains("céd")) ? 1 : 0) +
            (tl.Contains("direc") ? 1 : 0) +
            (tl.Contains("pago") ? 1 : 0);

        if (anchors < 3) return false;

        // Extract line values like: "Nombre y apellido: Juan Perez"
        details.FullName ??= ExtractAfterColon(raw, new[] { "Nombre y apellido", "Nombre", "Nombre y Apellido" });
        details.IdNumber ??= ExtractAfterColon(raw, new[] { "Cédula de identidad", "Cedula de identidad", "Cédula", "Cedula" });
        details.LocalPhone ??= ExtractAfterColon(raw, new[] { "Número de teléfono local", "Numero de telefono local", "Teléfono local", "Telefono local" });
        details.MobilePhone ??= ExtractAfterColon(raw, new[] { "Número de teléfono celular", "Numero de telefono celular", "Teléfono celular", "Telefono celular", "Celular" });
        details.Address ??= ExtractAfterColon(raw, new[] { "Dirección escrita", "Direccion escrita", "Dirección", "Direccion" });
        details.ReceivesSelf ??= ExtractAfterColon(raw, new[] { "¿Recibe usted mismo/a?", "Recibe usted mismo", "Recibe usted", "Recibe" });
        details.PaymentMethod ??= ExtractAfterColon(raw, new[] { "Forma de Pago", "Forma de pago", "Pago" });
        details.Gps ??= ExtractAfterColon(raw, new[] { "Ubicación GPS", "Ubicacion GPS", "GPS", "Ubicación", "Ubicacion" });

        // If at least name OR phone OR address got filled, accept
        var filledCount =
            (!string.IsNullOrWhiteSpace(details.FullName) ? 1 : 0) +
            (!string.IsNullOrWhiteSpace(details.MobilePhone) ? 1 : 0) +
            (!string.IsNullOrWhiteSpace(details.Address) ? 1 : 0);

        return filledCount >= 2;
    }

    private static string? ExtractAfterColon(string raw, IEnumerable<string> labels)
    {
        foreach (var label in labels)
        {
            // Match: "Label: value" until end of line
            var pattern = $@"(?im)^\s*{Regex.Escape(label)}\s*:\s*(.+?)\s*$";
            var m = Regex.Match(raw, pattern);
            if (m.Success)
            {
                var val = m.Groups[1].Value.Trim();
                if (!string.IsNullOrWhiteSpace(val)) return val;
            }
        }
        return null;
    }

    private static string BuildReceipt(ConversationState state, string itemsText, string prettyDelivery)
    {
        // Clean, professional receipt. No form reprint.
        var d = state.Details;

        var sb = new StringBuilder();
        sb.AppendLine("✅ *Pedido confirmado*");
        sb.AppendLine();
        sb.AppendLine("🧾 *Receipt*");
        sb.AppendLine($"🛒 Items: {itemsText}");
        sb.AppendLine($"🚚 Tipo: {prettyDelivery}");

        // Include details only if present
        if (!string.IsNullOrWhiteSpace(d.FullName)) sb.AppendLine($"👤 Nombre: {d.FullName}");
        if (!string.IsNullOrWhiteSpace(d.IdNumber)) sb.AppendLine($"🪪 Cédula: {d.IdNumber}");
        if (!string.IsNullOrWhiteSpace(d.LocalPhone)) sb.AppendLine($"☎️ Tel local: {d.LocalPhone}");
        if (!string.IsNullOrWhiteSpace(d.MobilePhone)) sb.AppendLine($"📱 Cel: {d.MobilePhone}");
        if (!string.IsNullOrWhiteSpace(d.Address)) sb.AppendLine($"🏡 Dirección: {d.Address}");
        if (!string.IsNullOrWhiteSpace(d.ReceivesSelf)) sb.AppendLine($"📦 Recibe: {d.ReceivesSelf}");
        if (!string.IsNullOrWhiteSpace(d.PaymentMethod)) sb.AppendLine($"💵 Pago: {d.PaymentMethod}");
        if (!string.IsNullOrWhiteSpace(d.Gps)) sb.AppendLine($"📍 GPS: {d.Gps}");

        sb.AppendLine();
        sb.AppendLine("¿Quieres hacer otro *pedido* o una *reservación*?");

        return sb.ToString().TrimEnd();
    }

    private static string BuildReservationReply(AiParseResult parsed)
    {
        if (parsed.Args.Reservation is null)
            return "Perfecto 😊 ¿Para qué fecha deseas reservar?";

        return $"Reserva recibida para {parsed.Args.Reservation.Date} a las {parsed.Args.Reservation.Time}. ¿Para cuántas personas?";
    }
}
