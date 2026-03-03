using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
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

        // Checkout
        public bool RequestedDetailsForm { get; set; }
        public bool DetailsReceived { get; set; }

        public string? CustomerName { get; set; }
        public string? Address { get; set; }
        public string? PaymentMethod { get; set; }

        public string? LocationText { get; set; } // "GPS_PIN" o texto

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

                    // PIN GPS (opcional)
                    if (message.Type == "location")
                    {
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

                    // Captura de planilla antes de responder
                    if (state.Items.Count > 0 && state.RequestedDetailsForm && !IsConfirmCommand(normalized))
                    {
                        TryCaptureCheckoutFromText(state, rawText);
                        state.DetailsReceived = HasMinimumCheckout(state);
                    }

                    // ✅ FALLBACK "modo edición" aunque la IA se equivoque:
                    // Si ya hay items y el texto parece "agrega 2 cocacolas", lo aplicamos aquí.
                    // Importante: lo hacemos ANTES de responder para que no mande mensajes raros.
                    if (state.Items.Count > 0 && TryApplyManualAddItems(state, rawText))
                    {
                        // Si ya está en checkout, damos respuesta corta y seguimos.
                        if (state.RequestedDetailsForm)
                        {
                            await _whatsAppClient.SendTextMessageAsync(
                                new OutgoingMessage
                                {
                                    To = message.From,
                                    Body = "Listo ✅ agregado. Escribe *CONFIRMAR* para finalizar.",
                                    PhoneNumberId = phoneNumberId
                                },
                                cancellationToken);
                            continue;
                        }
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

        // Si estás en checkout, NO dejamos que nada te saque del flujo
        if (state.Items.Count > 0 && state.RequestedDetailsForm && parsed.Intent != RestaurantIntent.OrderCreate)
        {
            return state.DetailsReceived
                ? "Recibido ✅ Escribe *CONFIRMAR* para finalizar."
                : "Sigue llenando la planilla y luego escribe *CONFIRMAR*.";
        }

        // Bienvenida simple (SIN reservas)
        if (!state.Welcomed && parsed.Intent == RestaurantIntent.General)
        {
            state.Welcomed = true;
            return "¡Hola! 👋 ¿Qué deseas ordenar?";
        }

        // TODO lo que no sea OrderCreate se trata como General (SIN reservas)
        if (parsed.Intent != RestaurantIntent.OrderCreate)
            return "¿Qué deseas ordenar?";

        return await BuildOrderReplyAsync(parsed, conversationId);
    }

    private Task<string> BuildOrderReplyAsync(AiParseResult parsed, string conversationId)
    {
        var state = _stateByConversation[conversationId];

        var addedAny = false;

        if (parsed.Args?.Order != null)
        {
            foreach (var item in parsed.Args.Order.Items ?? [])
            {
                if (!string.IsNullOrWhiteSpace(item.Name) && item.Quantity > 0)
                {
                    if (!state.Items.Any(x => x.Name.Equals(item.Name, StringComparison.OrdinalIgnoreCase)))
                    {
                        state.Items.Add((item.Name.Trim(), item.Quantity));
                        addedAny = true;
                    }
                }
            }

            if (!string.IsNullOrWhiteSpace(parsed.Args.Order.DeliveryType))
                state.DeliveryType = NormalizeDeliveryType(parsed.Args.Order.DeliveryType);
        }

        if (state.Items.Count == 0)
            return Task.FromResult("¿Qué deseas ordenar?");

        if (string.IsNullOrWhiteSpace(state.DeliveryType))
            return Task.FromResult("¿Es *pick up* o *delivery*?");

        // Modo edición: si ya mandamos planilla y agrega items
        if (state.RequestedDetailsForm && addedAny)
        {
            var last = state.Items.Last();
            return Task.FromResult($"Listo ✅ agregué *{last.Quantity} {last.Name}*. Escribe *CONFIRMAR* para finalizar.");
        }

        if (!state.RequestedDetailsForm)
        {
            state.RequestedDetailsForm = true;

            return Task.FromResult(
@"Para finalizar envíanos:

👤 Nombre:
🏡 Dirección:
💳 Pago: *EFECTIVO* o *DIVISAS*
📍 Ubicación GPS: (manda el pin) *(opcional)*

Luego escribe *CONFIRMAR*.");
        }

        return state.DetailsReceived
            ? Task.FromResult("Recibido ✅ Escribe *CONFIRMAR* para finalizar.")
            : Task.FromResult("Sigue llenando la planilla y luego escribe *CONFIRMAR*.");
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

Envíanos:
👤 Nombre
🏡 Dirección
💳 Pago: EFECTIVO o DIVISAS

Luego escribe *CONFIRMAR*.";
        }

        var order = new Order
        {
            From = from,
            PhoneNumberId = phoneNumberId,
            DeliveryType = state.DeliveryType!,
            CreatedAtUtc = DateTime.UtcNow,

            CustomerName = state.CustomerName,
            Address = state.Address,
            PaymentMethod = state.PaymentMethod,
            LocationText = state.LocationText,

            CheckoutFormSent = state.RequestedDetailsForm,
            CheckoutCompleted = true,
            CheckoutCompletedAtUtc = DateTime.UtcNow,

            Items = state.Items.Select(i => new WhatsAppSaaS.Domain.Entities.OrderItem
            {
                Name = i.Name,
                Quantity = i.Quantity
            }).ToList()
        };

        await _orderRepository.AddOrderAsync(order, ct);

        var orderNumber = order.Id.ToString("N")[..8].ToUpperInvariant();
        var itemsText = string.Join(", ", state.Items.Select(i => $"{i.Quantity} {i.Name}"));

        var receipt =
$@"✅ *PEDIDO CONFIRMADO*
🧾 Pedido: *#{orderNumber}*

👤 Nombre: {state.CustomerName ?? "—"}
🍽️ Pedido: {itemsText}
🏡 Dirección: {state.Address ?? "—"}
💳 Pago: {state.PaymentMethod ?? "—"}

Gracias 🙌";

        // Reset estado conversacional
        state.Items.Clear();
        state.DeliveryType = null;

        state.RequestedDetailsForm = false;
        state.DetailsReceived = false;

        state.CustomerName = null;
        state.Address = null;
        state.PaymentMethod = null;
        state.LocationText = null;

        return receipt;
    }

    // ============================
    // ✅ MODO EDICIÓN MANUAL (fallback)
    // ============================
    // Acepta ejemplos:
    // "agrega 2 cocacolas"
    // "agregar 1 coca cola"
    // "añade 3 papas"
    // "+2 empanadas"
    // "sumale 2 tequeños"
    private static bool TryApplyManualAddItems(ConversationState state, string rawText)
    {
        var t = Normalize(rawText);

        // Atajos tipo "+2 cocacolas"
        if (t.StartsWith("+"))
            t = "agrega " + t[1..].Trim();

        // Necesita al menos un número
        // Formato: (palabra acción) (cantidad) (producto...)
        // Acción puede estar o no, porque la IA a veces manda solo "2 cocacolas"
        var m = Regex.Match(t, @"^(agrega|agregar|añade|anade|sumale|súmale|sumar|agregame|agrégame)?\s*(\d+)\s+(.+)$",
            RegexOptions.IgnoreCase);

        if (!m.Success)
            return false;

        var qtyStr = m.Groups[2].Value;
        var name = m.Groups[3].Value.Trim();

        if (!int.TryParse(qtyStr, out var qty) || qty <= 0)
            return false;

        // evita capturar cosas tipo "2" sin nombre
        if (string.IsNullOrWhiteSpace(name) || name.Length < 2)
            return false;

        // limpieza básica
        name = name.Trim().Trim('.', ',', ';', ':');

        // si ya existe, NO duplicamos (mantener simple)
        if (!state.Items.Any(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase)))
            state.Items.Add((name, qty));

        return true;
    }

    private static void TryCaptureCheckoutFromText(ConversationState state, string rawText)
    {
        var lines = rawText
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Trim())
            .Where(l => l.Length > 0)
            .ToList();

        foreach (var original in lines)
        {
            var line = StripLeadingNonAlnum(original);
            var lower = line.ToLowerInvariant();

            if (lower.StartsWith("nombre"))
            {
                var v = ExtractValue(line);
                if (!string.IsNullOrWhiteSpace(v)) state.CustomerName = v;
                continue;
            }

            if (lower.StartsWith("direccion") || lower.StartsWith("dirección") || lower.StartsWith("dir"))
            {
                var v = ExtractValue(line);
                if (!string.IsNullOrWhiteSpace(v)) state.Address = v;
                continue;
            }

            if (lower.StartsWith("pago") || lower.StartsWith("forma de pago"))
            {
                var v = ExtractValue(line);
                if (!string.IsNullOrWhiteSpace(v)) state.PaymentMethod = v;
                continue;
            }

            if (lower.StartsWith("ubicacion") || lower.StartsWith("ubicación") || lower.StartsWith("gps"))
            {
                var v = ExtractValue(line);
                if (!string.IsNullOrWhiteSpace(v)) state.LocationText = v;
                continue;
            }
        }
    }

    private static bool HasMinimumCheckout(ConversationState state)
    {
        if (string.IsNullOrWhiteSpace(state.CustomerName)) return false;
        if (string.IsNullOrWhiteSpace(state.Address)) return false;
        if (string.IsNullOrWhiteSpace(state.PaymentMethod)) return false;
        return true;
    }

    private static string StripLeadingNonAlnum(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return string.Empty;

        var s = input.Trim();
        var i = 0;
        while (i < s.Length && !char.IsLetterOrDigit(s[i]))
            i++;

        return i >= s.Length ? string.Empty : s[i..].TrimStart();
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
