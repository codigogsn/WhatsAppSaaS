using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;
using WhatsAppSaaS.Application.Common;
using WhatsAppSaaS.Application.DTOs;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Application.Services;

public sealed class WebhookProcessor : IWebhookProcessor
{
    private readonly IAiParser _aiParser;
    private readonly IWhatsAppClient _whatsAppClient;
    private readonly IOrderRepository _orderRepository;
    private readonly IConversationStateStore _stateStore;
    private readonly ILogger<WebhookProcessor> _logger;
    private readonly PaymentMobileOptions _paymentMobile;

    public WebhookProcessor(
        IAiParser aiParser,
        IWhatsAppClient whatsAppClient,
        IOrderRepository orderRepository,
        IConversationStateStore stateStore,
        ILogger<WebhookProcessor> logger,
        PaymentMobileOptions? paymentMobile = null)
    {
        _aiParser = aiParser;
        _whatsAppClient = whatsAppClient;
        _orderRepository = orderRepository;
        _stateStore = stateStore;
        _logger = logger;
        _paymentMobile = paymentMobile ?? new PaymentMobileOptions();
    }

    private const int MaxMessageLength = 4096;

    public async Task ProcessAsync(WebhookPayload payload, BusinessContext businessContext, CancellationToken cancellationToken = default)
    {
        if (payload?.Entry is null) return;

        foreach (var entry in payload.Entry)
        {
            if (entry?.Changes is null) continue;

            foreach (var change in entry.Changes)
            {
                var value = change?.Value;
                if (value?.Messages is null) continue;

                var phoneNumberId = value.Metadata?.PhoneNumberId;
                if (string.IsNullOrWhiteSpace(phoneNumberId)) continue;

                foreach (var message in value.Messages)
                {
                    if (message is null) continue;
                    if (string.IsNullOrWhiteSpace(message.From)) continue;
                    if (string.IsNullOrWhiteSpace(message.Type)) continue;

                    // Truncate extremely long text messages
                    if (message.Text?.Body != null && message.Text.Body.Length > MaxMessageLength)
                        message.Text.Body = message.Text.Body[..MaxMessageLength];

                    var conversationId = $"{message.From}:{phoneNumberId}";

                    _logger.LogInformation(
                        "Processing message: id={MessageId} from={From} type={Type} conversationId={ConversationId}",
                        message.Id, message.From, message.Type, conversationId);

                    try
                    {
                    // Ensure conversation row exists before idempotency check (FK dependency)
                    var state = await _stateStore.GetOrCreateAsync(conversationId, businessContext.BusinessId, cancellationToken);

                    // Idempotency check
                    var msgId = message.Id;
                    if (!string.IsNullOrWhiteSpace(msgId))
                    {
                        if (await _stateStore.IsMessageProcessedAsync(conversationId, msgId, cancellationToken))
                            continue;

                        await _stateStore.MarkMessageProcessedAsync(conversationId, msgId, cancellationToken);
                    }

                    // 1) Location pin (GPS)
                    if (message.Type == "location")
                    {
                        state.GpsPinReceived = true;

                        if (state.Items.Count > 0 && state.CheckoutFormSent)
                        {
                            await SendAsync(new OutgoingMessage
                            {
                                To = message.From,
                                Body = "\u2705 Ubicaci\u00f3n recibida. Escribe *CONFIRMAR* para finalizar.",
                                PhoneNumberId = phoneNumberId,
                                AccessToken = businessContext.AccessToken
                            }, businessContext.BusinessId, conversationId, cancellationToken);
                        }

                        await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                        continue;
                    }

                    // 2) Media: payment evidence
                    if (message.Type != "text")
                    {
                        if (state.PaymentEvidenceRequested && !state.PaymentEvidenceReceived)
                        {
                            state.PaymentEvidenceReceived = true;

                            await SendAsync(new OutgoingMessage
                            {
                                To = message.From,
                                Body = "\u2705 Comprobante recibido. Un operador verificar\u00e1 el pago y te confirmamos.",
                                PhoneNumberId = phoneNumberId,
                                AccessToken = businessContext.AccessToken
                            }, businessContext.BusinessId, conversationId, cancellationToken);
                        }

                        await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                        continue;
                    }

                    if (string.IsNullOrWhiteSpace(message.Text?.Body)) continue;

                    var rawText = message.Text.Body;
                    var t = Normalize(rawText);

                    // A) Confirmar
                    if (IsConfirmCommand(t))
                    {
                        var confirmReply = await FinalizeOrderIfPossibleAsync(state, message.From, phoneNumberId, businessContext.BusinessId, cancellationToken);

                        await SendAsync(new OutgoingMessage
                        {
                            To = message.From,
                            Body = confirmReply,
                            PhoneNumberId = phoneNumberId,
                            AccessToken = businessContext.AccessToken
                        }, businessContext.BusinessId, conversationId, cancellationToken);

                        await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                        continue;
                    }

                    // B) Greeting -> welcome + menu + prompt
                    if (!state.MenuSent && IsGreeting(t))
                    {
                        state.MenuSent = true;

                        await SendGreetingSequenceAsync(message.From, phoneNumberId, businessContext, conversationId, cancellationToken);

                        await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                        continue;
                    }

                    // C) Manual add: "agrega 2 cocacolas"
                    if (TryParseManualAddItem(rawText, out var addQty, out var addName))
                    {
                        AddOrIncreaseItem(state, addName, addQty);

                        await SendAsync(new OutgoingMessage
                        {
                            To = message.From,
                            Body = "\u2705 Listo, agregado. Escribe *CONFIRMAR* para finalizar.",
                            PhoneNumberId = phoneNumberId,
                            AccessToken = businessContext.AccessToken
                        }, businessContext.BusinessId, conversationId, cancellationToken);

                        await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                        continue;
                    }

                    // D) Checkout form capture
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
                            state.GpsPinReceived = true;
                        }

                        if (state.PaymentMethod is "pago_movil" or "divisas")
                        {
                            if (!state.PaymentEvidenceRequested)
                            {
                                state.PaymentEvidenceRequested = true;

                                if (state.PaymentMethod == "pago_movil")
                                {
                                    await SendPagoMovilDetailsAsync(message.From, phoneNumberId, businessContext, conversationId, cancellationToken);
                                }
                                else
                                {
                                    await SendAsync(new OutgoingMessage
                                    {
                                        To = message.From,
                                        Body = "Para *DIVISAS*, env\u00eda una *foto de los billetes* \u2705",
                                        PhoneNumberId = phoneNumberId,
                                        AccessToken = businessContext.AccessToken
                                    }, businessContext.BusinessId, conversationId, cancellationToken);
                                }
                            }
                        }

                        await SendAsync(new OutgoingMessage
                        {
                            To = message.From,
                            Body = "\u2705 Perfecto. Escribe *CONFIRMAR* para finalizar.",
                            PhoneNumberId = phoneNumberId,
                            AccessToken = businessContext.AccessToken
                        }, businessContext.BusinessId, conversationId, cancellationToken);

                        await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                        continue;
                    }

                    // E0) Quick parse (no AI)
                    if (TryParseQuickOrder(rawText, out var quickItems, out var quickDelivery))
                    {
                        foreach (var (name, qty) in quickItems)
                            AddOrIncreaseItem(state, name, qty);

                        if (!string.IsNullOrWhiteSpace(quickDelivery))
                            state.DeliveryType = quickDelivery;

                        var quickReply = BuildOrderReplyFromState(state);

                        await SendAsync(new OutgoingMessage
                        {
                            To = message.From,
                            Body = quickReply,
                            PhoneNumberId = phoneNumberId,
                            AccessToken = businessContext.AccessToken
                        }, businessContext.BusinessId, conversationId, cancellationToken);

                        await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                        continue;
                    }

                    // E1) Deterministic ordering-intent detection (no AI needed)
                    if (IsOrderingIntent(t))
                    {
                        if (!state.MenuSent)
                        {
                            state.MenuSent = true;
                            await SendGreetingSequenceAsync(message.From, phoneNumberId, businessContext, conversationId, cancellationToken);
                        }
                        else
                        {
                            await SendAsync(new OutgoingMessage
                            {
                                To = message.From,
                                Body = "\ud83c\udf7d\ufe0f \u00bfQu\u00e9 deseas ordenar?",
                                PhoneNumberId = phoneNumberId,
                                AccessToken = businessContext.AccessToken
                            }, businessContext.BusinessId, conversationId, cancellationToken);
                        }

                        await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                        continue;
                    }

                    // E) AI parse
                    _logger.LogInformation(
                        "No quick-match for message in {ConversationId}, falling through to AI parse. Text={Text}",
                        conversationId, rawText.Length > 200 ? rawText[..200] : rawText);
                    var parsed = await _aiParser.ParseAsync(rawText, message.From, conversationId, cancellationToken);

                    var effectiveIntent = parsed.Intent;

                    if (effectiveIntent == RestaurantIntent.ReservationCreate)
                        effectiveIntent = RestaurantIntent.General;

                    if (effectiveIntent == RestaurantIntent.General && LooksLikeOrderIntent(t))
                        effectiveIntent = RestaurantIntent.OrderCreate;

                    var reply = BuildReply(effectiveIntent, parsed, state);

                    await SendAsync(new OutgoingMessage
                    {
                        To = message.From,
                        Body = reply,
                        PhoneNumberId = phoneNumberId,
                        AccessToken = businessContext.AccessToken
                    }, businessContext.BusinessId, conversationId, cancellationToken);

                    await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex,
                            "Failed processing message {MessageId} in conversation {ConversationId}",
                            message.Id, conversationId);
                    }
                }
            }
        }
    }

    // ──────────────────────────────────────────
    // Greeting: 3 messages (welcome, menu, prompt)
    // ──────────────────────────────────────────

    private async Task SendGreetingSequenceAsync(
        string to, string phoneNumberId, BusinessContext biz, string conversationId, CancellationToken ct)
    {
        var businessName = !string.IsNullOrWhiteSpace(biz.BusinessName)
            ? biz.BusinessName
            : "nuestro restaurante";

        // Message 1: Welcome
        await SendAsync(new OutgoingMessage
        {
            To = to,
            Body = $"\ud83d\udc4b Hola, bienvenido a {businessName}",
            PhoneNumberId = phoneNumberId,
            AccessToken = biz.AccessToken
        }, biz.BusinessId, conversationId, ct);

        // Message 2: Menu
        await SendAsync(new OutgoingMessage
        {
            To = to,
            Body = "\ud83d\udccb *MEN\u00da (DEMO)*\n1) Hamburguesa\n2) Coca Cola\n3) Papas\n\n_(En producci\u00f3n aqu\u00ed va foto/PDF del men\u00fa del restaurante)_",
            PhoneNumberId = phoneNumberId,
            AccessToken = biz.AccessToken
        }, biz.BusinessId, conversationId, ct);

        // Message 3: Prompt
        await SendAsync(new OutgoingMessage
        {
            To = to,
            Body = "\ud83c\udf7d\ufe0f \u00bfQu\u00e9 deseas ordenar?",
            PhoneNumberId = phoneNumberId,
            AccessToken = biz.AccessToken
        }, biz.BusinessId, conversationId, ct);
    }

    // ──────────────────────────────────────────
    // Pago Móvil: 2 messages (details + proof request)
    // ──────────────────────────────────────────

    private async Task SendPagoMovilDetailsAsync(
        string to, string phoneNumberId, BusinessContext biz, string conversationId, CancellationToken ct)
    {
        var bank = ResolvePaymentConfig(_paymentMobile.Bank, "PAYMENT_MOBILE_BANK", "PaymentMobile__Bank")
            ?? "(no configurado)";
        var payId = ResolvePaymentConfig(_paymentMobile.Id, "PAYMENT_MOBILE_ID", "PaymentMobile__Id")
            ?? "(no configurado)";
        var phone = ResolvePaymentConfig(_paymentMobile.Phone, "PAYMENT_MOBILE_PHONE", "PaymentMobile__Phone")
            ?? "(no configurado)";

        // Message 1: Payment details
        await SendAsync(new OutgoingMessage
        {
            To = to,
            Body = $"\ud83d\udcb3 *DATOS PARA PAGO M\u00d3VIL*\n\n\u2022 *Banco:* {bank}\n\u2022 *C.I./RIF:* {payId}\n\u2022 *Tel\u00e9fono:* {phone}",
            PhoneNumberId = phoneNumberId,
            AccessToken = biz.AccessToken
        }, biz.BusinessId, conversationId, ct);

        // Message 2: Proof request
        await SendAsync(new OutgoingMessage
        {
            To = to,
            Body = "Para *PAGO M\u00d3VIL*, env\u00eda el *comprobante* (foto) \u2705",
            PhoneNumberId = phoneNumberId,
            AccessToken = biz.AccessToken
        }, biz.BusinessId, conversationId, ct);
    }

    private static string? ResolvePaymentConfig(string optionsValue, params string[] envKeys)
    {
        if (!string.IsNullOrWhiteSpace(optionsValue))
            return optionsValue;

        foreach (var key in envKeys)
        {
            var val = Environment.GetEnvironmentVariable(key);
            if (!string.IsNullOrWhiteSpace(val))
                return val;
        }

        return null;
    }

    private string BuildReply(
        RestaurantIntent intent,
        AiParseResult parsed,
        ConversationFields state)
    {
        if (state.Items.Count > 0 && state.CheckoutFormSent && intent == RestaurantIntent.General)
        {
            return "Sigue llenando la planilla y luego escribe *CONFIRMAR*.";
        }

        return intent switch
        {
            RestaurantIntent.OrderCreate => BuildOrderReply(parsed, state),
            RestaurantIntent.HumanHandoff => "Te paso con un humano",
            _ => "\ud83c\udf7d\ufe0f \u00bfQu\u00e9 deseas ordenar?"
        };
    }

    private string BuildOrderReply(AiParseResult parsed, ConversationFields state)
    {
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

        return BuildOrderReplyFromState(state);
    }

    internal static string BuildOrderReplyFromState(ConversationFields state)
    {
        if (state.Items.Count == 0)
            return "\ud83c\udf7d\ufe0f \u00bfQu\u00e9 deseas ordenar?";

        if (string.IsNullOrWhiteSpace(state.DeliveryType))
            return "\u00bfEs *pick up* o *delivery*?";

        if (!state.CheckoutFormSent)
        {
            state.CheckoutFormSent = true;

            return "Para finalizar env\u00edanos:\n\n\ud83d\udc64 *Nombre:*\n\ud83e\udead *C\u00e9dula:*\n\ud83d\udcf1 *Tel\u00e9fono:*\n\ud83c\udfe1 *Direcci\u00f3n:*\n\ud83d\udcb5 *Pago:* EFECTIVO / DIVISAS / PAGO M\u00d3VIL\n\ud83d\udccd *Ubicaci\u00f3n GPS:* (manda el pin)\n\u2705 *OBLIGATORIO*\n\nLuego escribe *CONFIRMAR*.";
        }

        return "\u2705 Escribe *CONFIRMAR* para finalizar.";
    }

    private async Task<string> FinalizeOrderIfPossibleAsync(
        ConversationFields state,
        string from,
        string phoneNumberId,
        Guid businessId,
        CancellationToken ct)
    {
        if (state.Items.Count == 0)
            return "No hay productos en el pedido.";

        if (string.IsNullOrWhiteSpace(state.DeliveryType))
            return "Indica si es *pick up* o *delivery*.";

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(state.CustomerName)) missing.Add("\ud83d\udc64 Nombre");
        if (string.IsNullOrWhiteSpace(state.CustomerIdNumber)) missing.Add("\ud83e\udead C\u00e9dula");
        if (string.IsNullOrWhiteSpace(state.CustomerPhone)) missing.Add("\ud83d\udcf1 Tel\u00e9fono");
        if (string.IsNullOrWhiteSpace(state.Address)) missing.Add("\ud83c\udfe1 Direcci\u00f3n");
        if (string.IsNullOrWhiteSpace(state.PaymentMethod)) missing.Add("\ud83d\udcb5 Pago");
        if (!state.GpsPinReceived) missing.Add("\ud83d\udccd Ubicaci\u00f3n GPS (pin)");

        if (missing.Count > 0)
        {
            return
$"A\u00fan falta informaci\u00f3n para confirmar.\n\nEnv\u00edanos al menos:\n- {string.Join("\n- ", missing)}\n\nLuego escribe *CONFIRMAR*.";
        }

        var customerPhoneE164 = NormalizeToE164(state.CustomerPhone) ?? NormalizeToE164(from) ?? state.CustomerPhone;

        var order = new Order
        {
            BusinessId = businessId,
            From = from,
            PhoneNumberId = phoneNumberId,
            DeliveryType = state.DeliveryType!,
            CreatedAtUtc = DateTime.UtcNow,

            CustomerName = state.CustomerName,
            CustomerIdNumber = state.CustomerIdNumber,
            CustomerPhone = customerPhoneE164,
            Address = state.Address,
            PaymentMethod = state.PaymentMethod,
            LocationText = state.LocationText,

            CheckoutCompleted = true,
            CheckoutCompletedAtUtc = DateTime.UtcNow,
            CheckoutFormSent = state.CheckoutFormSent,

            Items = state.Items.Select(i => new WhatsAppSaaS.Domain.Entities.OrderItem
            {
                Name = i.Name,
                Quantity = i.Quantity,
                UnitPrice = 0m
            }).ToList()
        };

        order.RecalculateTotal();

        await _orderRepository.AddOrderAsync(order, ct);

        var orderNumber = order.Id.ToString("N")[..8].ToUpperInvariant();
        var itemsText = string.Join(", ", state.Items.Select(i => $"{i.Quantity} {i.Name}"));

        var payText = state.PaymentMethod switch
        {
            "pago_movil" => "PAGO M\u00d3VIL (pendiente verificaci\u00f3n)",
            "divisas" => "DIVISAS (pendiente verificaci\u00f3n)",
            _ => "EFECTIVO"
        };

        var receipt =
$"\u2705 *PEDIDO CONFIRMADO*\n\ud83e\uddfe Pedido: #{orderNumber}\n\n\ud83d\udc64 Nombre: {state.CustomerName}\n\ud83e\udead C\u00e9dula: {state.CustomerIdNumber}\n\ud83d\udcf1 Tel\u00e9fono: {customerPhoneE164}\n\n\ud83c\udf7d\ufe0f Pedido: {itemsText}\n\ud83c\udfe1 Direcci\u00f3n: {state.Address}\n\ud83d\udcb5 Pago: {payText}\n\nGracias \ud83d\ude4c";

        state.ResetAfterConfirm();
        return receipt;
    }

    // ──────────────────────────────────────────
    // Send helper — never silent on failure
    // ──────────────────────────────────────────

    private async Task SendAsync(OutgoingMessage msg, Guid businessId, string conversationId, CancellationToken ct)
    {
        var sent = await _whatsAppClient.SendTextMessageAsync(msg, ct);
        if (!sent)
        {
            _logger.LogError(
                "SEND FAILED: to={To} phoneNumberId={PhoneNumberId} businessId={BusinessId} conversation={ConversationId} hasToken={HasToken}",
                msg.To, msg.PhoneNumberId, businessId, conversationId,
                !string.IsNullOrWhiteSpace(msg.AccessToken));
        }
    }

    // ──────────────────────────────────────────
    // Helpers
    // ──────────────────────────────────────────

    private static string Normalize(string input)
        => input.Trim().ToLowerInvariant();

    private static string StripAccents(string input)
    {
        var normalized = input.Normalize(NormalizationForm.FormD);
        var sb = new StringBuilder(normalized.Length);
        foreach (var c in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(c) != UnicodeCategory.NonSpacingMark)
                sb.Append(c);
        }
        return sb.ToString().Normalize(NormalizationForm.FormC);
    }

    internal static bool IsGreeting(string t)
    {
        var s = StripAccents(t);

        // Exact matches
        if (s is "hola" or "buenas" or "buenos dias" or "buen dia"
            or "buenas tardes" or "buenas noches" or "hey" or "epa"
            or "saludos" or "que tal" or "como estas")
            return true;

        // Starts-with (e.g. "hola buenas", "que tal como estas")
        if (s.StartsWith("hola ") || s.StartsWith("que tal")
            || s.StartsWith("como estas") || s.StartsWith("buenas ")
            || s.StartsWith("buenos ") || s.StartsWith("saludos "))
            return true;

        return false;
    }

    private static bool IsOrderingIntent(string t)
    {
        var s = StripAccents(t);
        return s.Contains("quisiera") || s.Contains("hacer un pedido")
            || s.Contains("quiero pedir") || s.Contains("para llevar")
            || s.Contains("quiero ordenar") || s.Contains("me gustaria ordenar")
            || s.Contains("me gustaria pedir");
    }

    private static bool IsConfirmCommand(string t)
        => t == "confirmar" || t == "confirmado" || t == "listo";

    private static bool LooksLikeOrderIntent(string t)
        => t.Contains("pedido")
           || t.Contains("orden")
           || t.Contains("comprar")
           || t.Contains("quiero")
           || t.Contains("hamburg")
           || t.Contains("coca")
           || t.Contains("papas")
           || t.Contains("agrega")
           || t.Contains("agregar");

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

    private static void AddOrIncreaseItem(ConversationFields state, string name, int qty)
    {
        var existing = state.Items.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Quantity += qty;
        }
        else
        {
            state.Items.Add(new ConversationItemEntry { Name = name, Quantity = qty });
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

        if (qty <= 0) return false;
        if (string.IsNullOrWhiteSpace(name)) return false;

        return true;
    }

    internal sealed class CheckoutForm
    {
        public string? CustomerName { get; set; }
        public string? CustomerIdNumber { get; set; }
        public string? CustomerPhone { get; set; }
        public string? Address { get; set; }
        public string? PaymentMethod { get; set; }
        public string? LocationText { get; set; }
    }

    internal static bool TryParseCheckoutForm(string rawText, out CheckoutForm form)
    {
        form = new CheckoutForm();

        var lines = rawText
            .Replace("\r\n", "\n")
            .Replace("\r", "\n")
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

        if (lines.Length == 0) return false;

        string? GetValue(string key)
        {
            foreach (var line in lines)
            {
                var l = line.Trim();
                // Strip leading emoji + whitespace for label matching
                var stripped = Regex.Replace(l, @"^[\p{So}\p{Cs}\ufe0f\u200d]+\s*", "");
                // Also strip leading WhatsApp bold markers
                stripped = stripped.TrimStart('*');
                var ln = stripped.ToLowerInvariant();

                if (ln.StartsWith(key))
                {
                    var idx = stripped.IndexOf(':');
                    if (idx < 0) idx = stripped.IndexOf('-');
                    if (idx < 0) idx = stripped.IndexOf('=');

                    if (idx >= 0 && idx + 1 < stripped.Length)
                        return stripped[(idx + 1)..].Trim().TrimEnd('*');
                }
            }
            return null;
        }

        form.CustomerName = GetValue("nombre");
        form.CustomerIdNumber = GetValue("cedula") ?? GetValue("c\u00e9dula");
        form.CustomerPhone = GetValue("telefono") ?? GetValue("tel\u00e9fono");
        form.Address = GetValue("direccion") ?? GetValue("direcci\u00f3n");
        var pay = GetValue("pago");
        var loc = GetValue("ubicacion") ?? GetValue("ubicaci\u00f3n");

        form.LocationText = string.IsNullOrWhiteSpace(loc) ? null : loc;

        if (!string.IsNullOrWhiteSpace(pay))
        {
            var p = StripAccents(pay.Trim().ToLowerInvariant());

            if (p.Contains("pago") && p.Contains("mov"))
                form.PaymentMethod = "pago_movil";
            else if (p.Contains("divis") || p.Contains("usd") || p.Contains("dolar"))
                form.PaymentMethod = "divisas";
            else if (p.Contains("efect"))
                form.PaymentMethod = "efectivo";
            else
                form.PaymentMethod = p.Replace(" ", "_");
        }

        var filled =
            (string.IsNullOrWhiteSpace(form.CustomerName) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(form.CustomerIdNumber) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(form.CustomerPhone) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(form.Address) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(form.PaymentMethod) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(form.LocationText) ? 0 : 1);

        return filled >= 3;
    }

    private static string? NormalizeToE164(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return null;

        var digits = new string(phone.Where(char.IsDigit).ToArray());
        if (string.IsNullOrWhiteSpace(digits)) return null;

        if (phone.Trim().StartsWith("+"))
            return "+" + digits;

        if (digits.StartsWith("58"))
            return "+" + digits;

        if (digits.Length >= 10 && (digits.StartsWith("0") || digits.StartsWith("4")))
        {
            digits = digits.TrimStart('0');
            return "+58" + digits;
        }

        return "+" + digits;
    }

    // Quick parse (no AI)
    private static bool TryParseQuickOrder(
        string rawText,
        out List<(string Name, int Quantity)> items,
        out string? deliveryType)
    {
        items = new List<(string, int)>();
        deliveryType = null;

        var t = rawText.ToLowerInvariant();

        if (t.Contains("delivery") || t.Contains("domicilio"))
            deliveryType = "delivery";
        else if (t.Contains("pick up") || t.Contains("pickup") || t.Contains("recoger"))
            deliveryType = "pickup";

        var matches = Regex.Matches(
            t,
            @"(\d+)\s*(hamburguesas?|coca\s*cola|cocacola|coca|papas|papitas)",
            RegexOptions.IgnoreCase);

        foreach (Match m in matches)
        {
            if (!int.TryParse(m.Groups[1].Value, out var qty)) continue;
            var key = m.Groups[2].Value.Trim().ToLowerInvariant();

            var name =
                key.StartsWith("hamburg") ? "Hamburguesa" :
                key.Contains("coca") ? "Coca Cola" :
                (key.StartsWith("papa") || key.StartsWith("papit")) ? "Papas" :
                null;

            if (name is null) continue;
            if (qty <= 0) continue;

            items.Add((name, qty));
        }

        return items.Count > 0;
    }
}
