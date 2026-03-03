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

    // In-memory conversation state (MVP). Luego se migra a Redis/DB.
    private static readonly Dictionary<string, ConversationState> _stateByConversation = new();
    private static readonly Dictionary<string, HashSet<string>> _processedMessageIdsByConversation = new();

    private static readonly TimeSpan StateTtl = TimeSpan.FromHours(6);

    private sealed class ConversationState
    {
        public bool MenuSent { get; set; }

        public List<(string Name, int Quantity)> Items { get; } = new();
        public string? DeliveryType { get; set; }

        public bool CheckoutFormSent { get; set; }

        // Captura de planilla
        public string? CustomerName { get; set; }
        public string? CustomerIdNumber { get; set; }
        public string? CustomerPhone { get; set; }
        public string? Address { get; set; }
        public string? PaymentMethod { get; set; } // "efectivo" | "divisas" | "pago_movil"

        // Ubicación (OBLIGATORIA): pin o texto
        public bool GpsPinReceived { get; set; }
        public string? LocationText { get; set; }

        // Evidencia de pago (para humano)
        public bool PaymentEvidenceRequested { get; set; }
        public bool PaymentEvidenceReceived { get; set; }

        public DateTime UpdatedAtUtc { get; set; } = DateTime.UtcNow;

        public void ResetAfterConfirm()
        {
            Items.Clear();
            DeliveryType = null;

            CheckoutFormSent = false;

            CustomerName = null;
            CustomerIdNumber = null;
            CustomerPhone = null;
            Address = null;
            PaymentMethod = null;

            GpsPinReceived = false;
            LocationText = null;

            PaymentEvidenceRequested = false;
            PaymentEvidenceReceived = false;

            // MenuSent se queda true para no spamear menú en el mismo hilo
        }
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

                    // 1) Location pin (GPS) obligatorio: si llega pin, marcamos recibido
                    if (message.Type == "location")
                    {
                        state.GpsPinReceived = true;

                        if (state.Items.Count > 0 && state.CheckoutFormSent)
                        {
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

                    // 2) Media: si estamos esperando evidencia de pago, marcar recibido (MVP)
                    if (message.Type != "text")
                    {
                        if (state.PaymentEvidenceRequested && !state.PaymentEvidenceReceived)
                        {
                            state.PaymentEvidenceReceived = true;

                            await _whatsAppClient.SendTextMessageAsync(
                                new OutgoingMessage
                                {
                                    To = message.From,
                                    Body = "Recibido ✅ Un operador verificará el pago y te confirmamos.",
                                    PhoneNumberId = phoneNumberId
                                },
                                cancellationToken);
                        }

                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(message.Text?.Body)) continue;

                    var rawText = message.Text.Body;
                    var t = Normalize(rawText);

                    // A) Confirmar
                    if (IsConfirmCommand(t))
                    {
                        var confirmReply = await FinalizeOrderIfPossibleAsync(state, message.From, phoneNumberId, cancellationToken);

                        await _whatsAppClient.SendTextMessageAsync(
                            new OutgoingMessage
                            {
                                To = message.From,
                                Body = confirmReply,
                                PhoneNumberId = phoneNumberId
                            },
                            cancellationToken);

                        continue;
                    }

                    // B) Enviar menú apenas diga "hola"/"buenas" (dos mensajes: menú y luego “¿Qué deseas ordenar?”)
                    if (!state.MenuSent && IsGreeting(t))
                    {
                        state.MenuSent = true;

                        await _whatsAppClient.SendTextMessageAsync(
                            new OutgoingMessage
                            {
                                To = message.From,
                                Body =
@"📋 *MENÚ (DEMO)*
1) Hamburguesa
2) Coca Cola
3) Papas

(En producción aquí va foto/PDF del menú del restaurante)",
                                PhoneNumberId = phoneNumberId
                            },
                            cancellationToken);

                        await _whatsAppClient.SendTextMessageAsync(
                            new OutgoingMessage
                            {
                                To = message.From,
                                Body = "¿Qué deseas ordenar?",
                                PhoneNumberId = phoneNumberId
                            },
                            cancellationToken);

                        continue;
                    }

                    // C) Modo edición: "agrega 2 cocacolas" suma items
                    if (TryParseManualAddItem(rawText, out var addQty, out var addName))
                    {
                        AddOrIncreaseItem(state, addName, addQty);

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

                    // D) Captura de planilla (texto) aunque tenga emojis
                    if (state.CheckoutFormSent && TryParseCheckoutForm(rawText, out var form))
                    {
                        state.CustomerName ??= form.CustomerName;
                        state.CustomerIdNumber ??= form.CustomerIdNumber;
                        state.CustomerPhone ??= form.CustomerPhone;
                        state.Address ??= form.Address;
                        state.PaymentMethod ??= form.PaymentMethod;

                        if (!string.IsNullOrWhiteSpace(form.LocationText))
                        {
                            state.LocationText ??= form.LocationText;
                            state.GpsPinReceived = true; // ubicación textual cuenta como ubicación
                        }

                        // Si escogió pago movil o divisas, pedir evidencia (MVP)
                        if (state.PaymentMethod is "pago_movil" or "divisas")
                        {
                            if (!state.PaymentEvidenceRequested)
                            {
                                state.PaymentEvidenceRequested = true;

                                var msg = state.PaymentMethod == "pago_movil"
                                    ? "Para *PAGO MÓVIL*, envía el *comprobante* (foto) ✅"
                                    : "Para *DIVISAS*, envía una *foto de los billetes* ✅";

                                await _whatsAppClient.SendTextMessageAsync(
                                    new OutgoingMessage
                                    {
                                        To = message.From,
                                        Body = msg,
                                        PhoneNumberId = phoneNumberId
                                    },
                                    cancellationToken);
                            }
                        }

                        await _whatsAppClient.SendTextMessageAsync(
                            new OutgoingMessage
                            {
                                To = message.From,
                                Body = "Perfecto ✅ Escribe *CONFIRMAR* para finalizar.",
                                PhoneNumberId = phoneNumberId
                            },
                            cancellationToken);

                        continue;
                    }

                    // E) IA parse (pedido)
                    var parsed = await _aiParser.ParseAsync(rawText, message.From, conversationId, cancellationToken);

                    // Intent efectivo (sin tocar parsed.Intent, porque es init-only)
                    var effectiveIntent = parsed.Intent;

                    // Quitamos reservas por ahora
                    if (effectiveIntent == RestaurantIntent.ReservationCreate)
                        effectiveIntent = RestaurantIntent.General;

                    // Anti-loop: si dice General pero el texto suena a pedido, forzamos OrderCreate
                    if (effectiveIntent == RestaurantIntent.General && LooksLikeOrderIntent(t))
                        effectiveIntent = RestaurantIntent.OrderCreate;

                    var reply = await BuildReplyAsync(effectiveIntent, parsed, rawText, conversationId);

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

    private Task<string> BuildReplyAsync(
        RestaurantIntent intent,
        AiParseResult parsed,
        string rawText,
        string conversationId)
    {
        var state = _stateByConversation[conversationId];

        // Si ya está en checkout, no volver a “qué deseas”
        if (state.Items.Count > 0 && state.CheckoutFormSent && intent == RestaurantIntent.General)
        {
            return Task.FromResult("Sigue llenando la planilla y luego escribe *CONFIRMAR*.");
        }

        return intent switch
        {
            RestaurantIntent.OrderCreate => BuildOrderReplyAsync(parsed, conversationId),
            RestaurantIntent.HumanHandoff => Task.FromResult("Te paso con un humano 🙌"),
            _ => Task.FromResult("¿Qué deseas ordenar?")
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
                    AddOrIncreaseItem(state, item.Name.Trim(), item.Quantity);
            }

            if (!string.IsNullOrWhiteSpace(parsed.Args.Order.DeliveryType))
                state.DeliveryType = NormalizeDeliveryType(parsed.Args.Order.DeliveryType);
        }

        if (state.Items.Count == 0)
            return Task.FromResult("¿Qué deseas ordenar?");

        if (string.IsNullOrWhiteSpace(state.DeliveryType))
            return Task.FromResult("¿Es *pick up* o *delivery*?");

        if (!state.CheckoutFormSent)
        {
            state.CheckoutFormSent = true;

            return Task.FromResult(
@"Para finalizar envíanos:

👤 *Nombre:*
🪪 *Cédula:*
📱 *Teléfono:*
🏡 *Dirección:*
💵 *Pago:* EFECTIVO / DIVISAS / PAGO MÓVIL
📍 *Ubicación GPS:* (manda el pin) ✅ *OBLIGATORIO*

Luego escribe *CONFIRMAR*.");
        }

        return Task.FromResult("Escribe *CONFIRMAR* para finalizar.");
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

        // Requeridos
        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(state.CustomerName)) missing.Add("Nombre");
        if (string.IsNullOrWhiteSpace(state.CustomerIdNumber)) missing.Add("Cédula");
        if (string.IsNullOrWhiteSpace(state.CustomerPhone)) missing.Add("Teléfono");
        if (string.IsNullOrWhiteSpace(state.Address)) missing.Add("Dirección");
        if (string.IsNullOrWhiteSpace(state.PaymentMethod)) missing.Add("Pago");
        if (!state.GpsPinReceived) missing.Add("Ubicación GPS (pin)");

        if (missing.Count > 0)
        {
            return
$@"Aún falta información para confirmar ✅

Envíanos al menos:
- {string.Join("\n- ", missing)}

Luego escribe *CONFIRMAR*.";
        }

        var order = new Order
        {
            From = from,
            PhoneNumberId = phoneNumberId,
            DeliveryType = state.DeliveryType!,
            CreatedAtUtc = DateTime.UtcNow,

            CustomerName = state.CustomerName,
            CustomerIdNumber = state.CustomerIdNumber,
            CustomerPhone = state.CustomerPhone,
            Address = state.Address,
            PaymentMethod = state.PaymentMethod,
            LocationText = state.LocationText,

            CheckoutCompleted = true,
            CheckoutCompletedAtUtc = DateTime.UtcNow,
            CheckoutFormSent = state.CheckoutFormSent,

            Items = state.Items.Select(i => new WhatsAppSaaS.Domain.Entities.OrderItem
            {
                Name = i.Name,
                Quantity = i.Quantity
            }).ToList()
        };

        await _orderRepository.AddOrderAsync(order, ct);

        var orderNumber = order.Id.ToString("N")[..8].ToUpperInvariant();
        var itemsText = string.Join(", ", state.Items.Select(i => $"{i.Quantity} {i.Name}"));

        var payText = state.PaymentMethod switch
        {
            "pago_movil" => "PAGO MÓVIL (pendiente verificación)",
            "divisas" => "DIVISAS (pendiente verificación)",
            _ => "EFECTIVO"
        };

        var receipt =
$@"✅ *PEDIDO CONFIRMADO*
🧾 Pedido: #{orderNumber}

👤 Nombre: {state.CustomerName}
🪪 Cédula: {state.CustomerIdNumber}
📱 Teléfono: {state.CustomerPhone}

🍽️ Pedido: {itemsText}
🏡 Dirección: {state.Address}
💵 Pago: {payText}

Gracias 🙌";

        state.ResetAfterConfirm();
        return receipt;
    }

    // Helpers

    private static string Normalize(string input)
        => input.Trim().ToLowerInvariant();

    private static bool IsGreeting(string t)
        => t == "hola" || t == "buenas" || t == "buenos dias" || t == "buen día" || t == "buenas tardes" || t == "buenas noches" || t.StartsWith("hola ");

    private static bool IsConfirmCommand(string t)
        => t == "confirmar" || t == "confirmado" || t == "listo";

    private static bool LooksLikeOrderIntent(string t)
        => t.Contains("pedido") || t.Contains("orden") || t.Contains("comprar") || t.Contains("quiero") || t.Contains("hamburg") || t.Contains("agrega") || t.Contains("agregar");

    private static string? NormalizeDeliveryType(string? input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var t = input.ToLowerInvariant();

        if (t.Contains("delivery") || t.Contains("domicilio"))
            return "delivery";

        if (t.Contains("pick") || t.Contains("pickup") || t.Contains("recoger"))
            return "pickup";

        return null;
    }

    private static void AddOrIncreaseItem(ConversationState state, string name, int qty)
    {
        var idx = state.Items.FindIndex(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (idx >= 0)
        {
            var current = state.Items[idx];
            state.Items[idx] = (current.Name, current.Quantity + qty);
        }
        else
        {
            state.Items.Add((name, qty));
        }
    }

    private static bool TryParseManualAddItem(string rawText, out int qty, out string name)
    {
        qty = 0;
        name = "";

        var m = Regex.Match(rawText.Trim(), @"^(agrega|agregar)\s+(\d+)\s+(.+)$", RegexOptions.IgnoreCase);
        if (!m.Success) return false;

        if (!int.TryParse(m.Groups[2].Value, out qty)) return false;
        name = m.Groups[3].Value.Trim();

        return qty > 0 && !string.IsNullOrWhiteSpace(name);
    }

    private sealed class CheckoutForm
    {
        public string? CustomerName { get; set; }
        public string? CustomerIdNumber { get; set; }
        public string? CustomerPhone { get; set; }
        public string? Address { get; set; }
        public string? PaymentMethod { get; set; }
        public string? LocationText { get; set; }
    }

    private static bool TryParseCheckoutForm(string rawText, out CheckoutForm form)
    {
        form = new CheckoutForm();

        var text = StripEmojis(rawText);

        var lines = text
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Trim())
            .ToList();

        foreach (var lineRaw in lines)
        {
            var line = lineRaw.Trim();
            var lower = line.ToLowerInvariant();

            if (lower.Contains("nombre"))
            {
                var v = AfterSeparator(line);
                if (!string.IsNullOrWhiteSpace(v)) form.CustomerName ??= v.Trim();
            }

            if (lower.Contains("cédula") || lower.Contains("cedula"))
            {
                var v = AfterSeparator(line);
                if (!string.IsNullOrWhiteSpace(v)) form.CustomerIdNumber ??= v.Trim();
            }

            if (lower.Contains("teléfono") || lower.Contains("telefono"))
            {
                var v = AfterSeparator(line);
                if (!string.IsNullOrWhiteSpace(v)) form.CustomerPhone ??= v.Trim();
            }

            if (lower.Contains("dirección") || lower.Contains("direccion"))
            {
                var v = AfterSeparator(line);
                if (!string.IsNullOrWhiteSpace(v)) form.Address ??= v.Trim();
            }

            if (lower.Contains("pago"))
            {
                var v = AfterSeparator(line);
                var p = Normalize(v);

                if (p.Contains("pago movil") || p.Contains("pagomovil") || p.Contains("pago_movil"))
                    form.PaymentMethod ??= "pago_movil";
                else if (p.Contains("divisa") || p.Contains("dolar") || p.Contains("usd"))
                    form.PaymentMethod ??= "divisas";
                else if (p.Contains("efectivo") || p.Contains("cash"))
                    form.PaymentMethod ??= "efectivo";
            }

            if (lower.Contains("ubicacion") || lower.Contains("ubicación"))
            {
                var v = AfterSeparator(line);
                if (!string.IsNullOrWhiteSpace(v)) form.LocationText ??= v.Trim();
            }
        }

        // consideramos planilla si trae >= 2 campos relevantes
        var score = 0;
        if (!string.IsNullOrWhiteSpace(form.CustomerName)) score++;
        if (!string.IsNullOrWhiteSpace(form.CustomerIdNumber)) score++;
        if (!string.IsNullOrWhiteSpace(form.CustomerPhone)) score++;
        if (!string.IsNullOrWhiteSpace(form.Address)) score++;
        if (!string.IsNullOrWhiteSpace(form.PaymentMethod)) score++;
        if (!string.IsNullOrWhiteSpace(form.LocationText)) score++;

        return score >= 2;
    }

    private static string AfterSeparator(string line)
    {
        var idx = line.IndexOf(':');
        if (idx >= 0 && idx < line.Length - 1) return line[(idx + 1)..].Trim();

        idx = line.IndexOf('-');
        if (idx >= 0 && idx < line.Length - 1) return line[(idx + 1)..].Trim();

        return line;
    }

    private static string StripEmojis(string input)
    {
        var filtered = new string(input.Where(c =>
            char.IsLetterOrDigit(c) ||
            char.IsWhiteSpace(c) ||
            c == ':' || c == '-' || c == '/' || c == '.' || c == ',' || c == '_' ||
            c == '(' || c == ')' || c == '#' || c == '*' || c == '+' || c == '@'
        ).ToArray());

        return filtered;
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
