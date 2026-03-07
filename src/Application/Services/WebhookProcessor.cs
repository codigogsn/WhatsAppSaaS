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

                    // 0) Human handoff request
                    if (IsHumanHandoffRequest(t))
                    {
                        state.HumanHandoffRequested = true;

                        await SendAsync(new OutgoingMessage
                        {
                            To = message.From,
                            Body = "\ud83d\ude4b Perfecto. Te pondremos en contacto con un humano para ayudarte con tu pedido.",
                            PhoneNumberId = phoneNumberId,
                            AccessToken = businessContext.AccessToken
                        }, businessContext.BusinessId, conversationId, cancellationToken);

                        await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                        continue;
                    }

                    // A0) Observation answer capture
                    if (state.ObservationPromptSent && !state.ObservationAnswered)
                    {
                        state.ObservationAnswered = true;

                        if (!IsNoObservation(t))
                        {
                            // Append to existing observations
                            state.SpecialInstructions = string.IsNullOrWhiteSpace(state.SpecialInstructions)
                                ? rawText.Trim()
                                : state.SpecialInstructions + "; " + rawText.Trim();
                        }

                        // Now continue to checkout form
                        state.CheckoutFormSent = true;

                        var obsReply = "Para finalizar env\u00edanos:\n\n\ud83d\udc64 *Nombre:*\n\ud83e\udeaa *C\u00e9dula:*\n\ud83d\udcf1 *Tel\u00e9fono:*\n\ud83c\udfe1 *Direcci\u00f3n:*\n\ud83d\udcb5 *Pago:* EFECTIVO / DIVISAS / PAGO M\u00d3VIL\n\ud83d\udccd *Ubicaci\u00f3n GPS:* (manda el pin)\n\u2705 *OBLIGATORIO*\n\nPuedes responder copiando y pegando esta planilla\no enviando los datos en l\u00edneas separadas.\n\nLuego escribe *CONFIRMAR*.";

                        await SendAsync(new OutgoingMessage
                        {
                            To = message.From,
                            Body = obsReply,
                            PhoneNumberId = phoneNumberId,
                            AccessToken = businessContext.AccessToken
                        }, businessContext.BusinessId, conversationId, cancellationToken);

                        await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                        continue;
                    }

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

                    // B) Restart intent: greeting / menu request / new-order intent
                    //    HIGHEST PRIORITY — clears stale checkout state
                    if (IsRestartIntent(t))
                    {
                        state.ResetAfterConfirm();
                        state.MenuSent = true;

                        await SendGreetingSequenceAsync(message.From, phoneNumberId, businessContext, conversationId, cancellationToken);

                        await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                        continue;
                    }

                    // C) Order modification: add/remove/replace items (typo-tolerant)
                    if (TryParseOrderModification(rawText, out var orderMod))
                    {
                        string modReply;
                        switch (orderMod.Type)
                        {
                            case ModificationType.Add:
                                AddOrIncreaseItem(state, orderMod.ItemName, orderMod.Quantity);
                                modReply = $"\u2705 Listo, agregu\u00e9 *{orderMod.Quantity} {orderMod.ItemName}* a tu pedido. Escribe *CONFIRMAR* para finalizar.";
                                break;

                            case ModificationType.Remove:
                            {
                                var existing = state.Items.FirstOrDefault(
                                    x => x.Name.Equals(orderMod.ItemName, StringComparison.OrdinalIgnoreCase));
                                if (existing != null)
                                {
                                    if (orderMod.Quantity >= existing.Quantity)
                                    {
                                        state.Items.Remove(existing);
                                        modReply = $"\u2705 Listo, elimin\u00e9 *{orderMod.ItemName}* del pedido.";
                                    }
                                    else
                                    {
                                        existing.Quantity -= orderMod.Quantity;
                                        modReply = $"\u2705 Listo, quit\u00e9 {orderMod.Quantity}. Ahora tienes *{existing.Quantity} {orderMod.ItemName}*. Escribe *CONFIRMAR* para finalizar.";
                                    }
                                }
                                else
                                {
                                    modReply = $"No tienes *{orderMod.ItemName}* en el pedido.";
                                }

                                if (state.Items.Count == 0)
                                    modReply += "\n\nTu pedido est\u00e1 vac\u00edo. \u00bfQu\u00e9 deseas ordenar?";
                                else
                                    modReply += " Escribe *CONFIRMAR* para finalizar.";
                                break;
                            }

                            case ModificationType.Replace:
                            {
                                var existingR = state.Items.FirstOrDefault(
                                    x => x.Name.Equals(orderMod.ItemName, StringComparison.OrdinalIgnoreCase));
                                if (existingR != null)
                                {
                                    existingR.Quantity = orderMod.Quantity;
                                    modReply = $"\u2705 Listo, ahora son *{orderMod.Quantity} {orderMod.ItemName}*. Escribe *CONFIRMAR* para finalizar.";
                                }
                                else
                                {
                                    AddOrIncreaseItem(state, orderMod.ItemName, orderMod.Quantity);
                                    modReply = $"\u2705 Agregu\u00e9 *{orderMod.Quantity} {orderMod.ItemName}*. Escribe *CONFIRMAR* para finalizar.";
                                }
                                break;
                            }

                            default:
                                modReply = "\u2705 Escribe *CONFIRMAR* para finalizar.";
                                break;
                        }

                        await SendAsync(new OutgoingMessage
                        {
                            To = message.From,
                            Body = modReply,
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
                    if (TryParseQuickOrder(rawText, out var quickItems, out var quickDelivery, out var quickObs))
                    {
                        // Use the richer ParseOrderText to get modifiers
                        var parsedRich = ParseOrderText(rawText);
                        foreach (var p in parsedRich)
                            AddOrIncreaseItem(state, p.Name, p.Quantity, p.Modifiers);
                        // Fallback: if ParseOrderText missed items that regex found, add them
                        var richNames = new HashSet<string>(parsedRich.Select(p => p.Name), StringComparer.OrdinalIgnoreCase);
                        foreach (var (name, qty) in quickItems)
                        {
                            if (!richNames.Contains(name))
                                AddOrIncreaseItem(state, name, qty);
                        }

                        if (!string.IsNullOrWhiteSpace(quickDelivery))
                            state.DeliveryType = quickDelivery;

                        // Store embedded observation if detected
                        if (!string.IsNullOrWhiteSpace(quickObs))
                        {
                            state.SpecialInstructions = string.IsNullOrWhiteSpace(state.SpecialInstructions)
                                ? quickObs
                                : state.SpecialInstructions + "; " + quickObs;
                        }

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
        // Per-business config → global options → env vars → placeholder
        var bank = FirstNonEmpty(biz.PaymentMobileBank, _paymentMobile.Bank,
            Environment.GetEnvironmentVariable("PAYMENT_MOBILE_BANK")) ?? "(no configurado)";
        var payId = FirstNonEmpty(biz.PaymentMobileId, _paymentMobile.Id,
            Environment.GetEnvironmentVariable("PAYMENT_MOBILE_ID")) ?? "(no configurado)";
        var phone = FirstNonEmpty(biz.PaymentMobilePhone, _paymentMobile.Phone,
            Environment.GetEnvironmentVariable("PAYMENT_MOBILE_PHONE")) ?? "(no configurado)";

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

    private static string? FirstNonEmpty(params string?[] values)
    {
        foreach (var v in values)
        {
            if (!string.IsNullOrWhiteSpace(v))
                return v;
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
            return "\ud83d\udecd\ufe0f \u00bfEs *pick up* o *delivery*?";

        // Observation prompt — after items+delivery, before checkout form
        if (!state.ObservationPromptSent)
        {
            state.ObservationPromptSent = true;

            if (!string.IsNullOrWhiteSpace(state.SpecialInstructions))
            {
                return $"\u270d\ufe0f *Observaci\u00f3n detectada:* {state.SpecialInstructions}\n\nSi quieres agregar otra observaci\u00f3n, escr\u00edbela ahora.\nSi no, responde *NO*.";
            }

            return "\u270d\ufe0f Si tu pedido tiene una *observaci\u00f3n especial*, escr\u00edbela ahora.\nEjemplo: *sin cebolla*, *extra queso*, *sin hielo*, *aderezo aparte*.\n\nSi no tienes observaciones, responde *NO*.";
        }

        if (!state.CheckoutFormSent)
        {
            state.CheckoutFormSent = true;

            return "Para finalizar env\u00edanos:\n\n\ud83d\udc64 *Nombre:*\n\ud83e\udeaa *C\u00e9dula:*\n\ud83d\udcf1 *Tel\u00e9fono:*\n\ud83c\udfe1 *Direcci\u00f3n:*\n\ud83d\udcb5 *Pago:* EFECTIVO / DIVISAS / PAGO M\u00d3VIL\n\ud83d\udccd *Ubicaci\u00f3n GPS:* (manda el pin)\n\u2705 *OBLIGATORIO*\n\nPuedes responder copiando y pegando esta planilla\no enviando los datos en l\u00edneas separadas.\n\nLuego escribe *CONFIRMAR*.";
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
        if (string.IsNullOrWhiteSpace(state.CustomerName)) missing.Add("\u2022 \ud83d\udc64 Nombre:");
        if (string.IsNullOrWhiteSpace(state.CustomerIdNumber)) missing.Add("\u2022 \ud83e\udeaa C\u00e9dula:");
        if (string.IsNullOrWhiteSpace(state.CustomerPhone)) missing.Add("\u2022 \ud83d\udcf1 Tel\u00e9fono:");
        if (string.IsNullOrWhiteSpace(state.Address)) missing.Add("\u2022 \ud83c\udfe1 Direcci\u00f3n:");
        if (string.IsNullOrWhiteSpace(state.PaymentMethod)) missing.Add("\u2022 \ud83d\udcb5 Pago:");
        if (!state.GpsPinReceived) missing.Add("\u2022 \ud83d\udccd Ubicaci\u00f3n GPS (pin)");

        if (missing.Count > 0)
        {
            return BuildCanonicalMissingFieldsMessage(missing);
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
            SpecialInstructions = state.SpecialInstructions,

            CheckoutCompleted = true,
            CheckoutCompletedAtUtc = DateTime.UtcNow,
            CheckoutFormSent = state.CheckoutFormSent,

            Items = state.Items.Select(i => new WhatsAppSaaS.Domain.Entities.OrderItem
            {
                Name = !string.IsNullOrWhiteSpace(i.Modifiers) ? $"{i.Name} ({i.Modifiers})" : i.Name,
                Quantity = i.Quantity,
                UnitPrice = 0m
            }).ToList()
        };

        order.RecalculateTotal();

        await _orderRepository.AddOrderAsync(order, ct);

        var orderNumber = order.Id.ToString("N")[..8].ToUpperInvariant();
        var itemsText = string.Join(", ", state.Items.Select(i => FormatItemText(i)));

        var payText = state.PaymentMethod switch
        {
            "pago_movil" => "PAGO M\u00d3VIL (pendiente verificaci\u00f3n)",
            "divisas" => "DIVISAS (pendiente verificaci\u00f3n)",
            _ => "EFECTIVO"
        };

        var obsLine = !string.IsNullOrWhiteSpace(state.SpecialInstructions)
            ? $"\n\u270d\ufe0f Observaci\u00f3n: {state.SpecialInstructions}"
            : "";

        var receipt =
$"\u2705 *PEDIDO CONFIRMADO*\n\ud83e\uddfe Pedido: #{orderNumber}\n\n\ud83d\udc64 Nombre: {state.CustomerName}\n\ud83e\udeaa C\u00e9dula: {state.CustomerIdNumber}\n\ud83d\udcf1 Tel\u00e9fono: {customerPhoneE164}\n\n\ud83c\udf7d\ufe0f Pedido: {itemsText}{obsLine}\n\ud83c\udfe1 Direcci\u00f3n: {state.Address}\n\ud83d\udcb5 Pago: {payText}\n\nGracias \ud83d\ude4c";

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

    // Unified restart intent: greeting OR menu request OR new-order intent
    // This must have HIGHEST PRIORITY over stale checkout state
    internal static bool IsRestartIntent(string t)
        => IsGreeting(t) || IsMenuRequest(t) || IsOrderingIntent(t);

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

    internal static bool IsMenuRequest(string t)
    {
        var s = StripAccents(t);

        // Exact match
        if (s is "menu" or "el menu" or "ver menu")
            return true;

        // Contains patterns for menu requests
        if (s.Contains("mandas el menu") || s.Contains("mandame el menu")
            || s.Contains("manda el menu") || s.Contains("enviar menu")
            || s.Contains("enviar el menu") || s.Contains("enviame el menu")
            || s.Contains("quiero ver el menu") || s.Contains("ver el menu")
            || s.Contains("pasame el menu") || s.Contains("muestrame el menu"))
            return true;

        return false;
    }

    internal static bool IsOrderingIntent(string t)
    {
        var s = StripAccents(t);
        return s.Contains("quisiera") || s.Contains("hacer un pedido")
            || s.Contains("quiero pedir") || s.Contains("para llevar")
            || s.Contains("quiero ordenar") || s.Contains("me gustaria ordenar")
            || s.Contains("me gustaria pedir") || s.Contains("deseo pedir")
            || s.Contains("nuevo pedido") || s.Contains("empezar de nuevo")
            || s.Contains("reiniciar pedido");
    }

    private static bool IsConfirmCommand(string t)
        => t == "confirmar" || t == "confirmado" || t == "listo";

    internal static bool IsHumanHandoffRequest(string t)
        => t is "humano" or "agente" or "asesor" or "persona" or "soporte"
           or "operador" or "asistente";

    internal static bool IsNoObservation(string t)
    {
        var s = StripAccents(t);
        return s is "no" or "no tengo" or "ninguna" or "sin observaciones"
            or "nada" or "no gracias" or "ninguno" or "na" or "nop"
            or "no, gracias" or "sin obs";
    }

    private static bool LooksLikeOrderIntent(string t)
        => t.Contains("pedido")
           || t.Contains("orden")
           || t.Contains("comprar")
           || t.Contains("quiero")
           || t.Contains("hamburg")
           || t.Contains("coca")
           || t.Contains("papas")
           || t.Contains("pizza")
           || t.Contains("sushi")
           || t.Contains("combo")
           || t.Contains("hot dog")
           || t.Contains("hotdog")
           || t.Contains("tequeno")
           || t.Contains("empanada")
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

    internal static string FormatItemText(ConversationItemEntry item)
    {
        var text = $"{item.Quantity} {item.Name}";
        if (!string.IsNullOrWhiteSpace(item.Modifiers))
            text += $" ({item.Modifiers})";
        return text;
    }

    private static void AddOrIncreaseItem(ConversationFields state, string name, int qty, string? modifiers = null)
    {
        var existing = state.Items.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Quantity += qty;
            // Merge modifiers if new ones provided
            if (!string.IsNullOrWhiteSpace(modifiers))
            {
                existing.Modifiers = string.IsNullOrWhiteSpace(existing.Modifiers)
                    ? modifiers
                    : existing.Modifiers + ", " + modifiers;
            }
        }
        else
        {
            state.Items.Add(new ConversationItemEntry { Name = name, Quantity = qty, Modifiers = modifiers });
        }
    }

    // ──────────────────────────────────────────
    // Order modification parser (typo-tolerant)
    // ──────────────────────────────────────────

    internal enum ModificationType { Add, Remove, Replace }

    internal sealed class OrderModification
    {
        public ModificationType Type { get; set; }
        public int Quantity { get; set; }
        public string ItemName { get; set; } = "";
    }

    internal static bool TryParseOrderModification(string rawText, out OrderModification mod)
    {
        mod = new OrderModification();
        var t = rawText.Trim();

        // Pattern 1: verb + qty + item [+ trailing noise]
        // agrega/agregame/agregar/suma/sumale/pon/ponme 3 hamburguesas mas porfavor
        var m = Regex.Match(t,
            @"^(agrega|agregame|agregar|suma|sumale|sumame|pon|ponme|a[nñ]ade|a[nñ]ademe)\s+(\d+)\s+(.+)$",
            RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[2].Value, out var addQty) && addQty > 0)
        {
            var rawItem = StripTrailingNoise(m.Groups[3].Value);
            var resolved = NormalizeMenuItemName(rawItem);
            if (resolved != null)
            {
                mod.Type = ModificationType.Add;
                mod.Quantity = addQty;
                mod.ItemName = resolved;
                return true;
            }
        }

        // Pattern 2: qty + item + "más" (e.g. "2 hamburguesas más")
        m = Regex.Match(t, @"^(\d+)\s+(.+?)\s+m[aá]s\b", RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[1].Value, out var moreQty) && moreQty > 0)
        {
            var resolved = NormalizeMenuItemName(m.Groups[2].Value.Trim());
            if (resolved != null)
            {
                mod.Type = ModificationType.Add;
                mod.Quantity = moreQty;
                mod.ItemName = resolved;
                return true;
            }
        }

        // Pattern 3: remove — quita/quitame/elimina/borra/sin + qty? + item
        //   Handles "una/un" as quantity 1
        m = Regex.Match(t,
            @"^(quita|quitame|quitale|elimina|borra|sin)\s+(?:(?:las?|los?|el|la)\s+)?(?:(\d+|un[ao]?)\s+)?(.+)$",
            RegexOptions.IgnoreCase);
        if (m.Success)
        {
            var rawItem = StripTrailingNoise(m.Groups[3].Value);
            var resolved = NormalizeMenuItemName(rawItem);
            if (resolved != null)
            {
                mod.Type = ModificationType.Remove;
                var qtyStr = m.Groups[2].Value.ToLowerInvariant();
                mod.Quantity = m.Groups[2].Success && m.Groups[2].Length > 0
                    ? (int.TryParse(qtyStr, out var remQty) && remQty > 0 ? remQty
                       : qtyStr is "un" or "una" or "uno" ? 1
                       : int.MaxValue)
                    : int.MaxValue; // "sin las papas" = remove all
                mod.ItemName = resolved;
                return true;
            }
        }

        // Pattern 4: replace — "cambia a N item" / "mejor N item"
        m = Regex.Match(t,
            @"^(cambia\s+a|mejor|en\s+vez\s+de\s+.+\s+pon)\s+(\d+)\s+(.+)$",
            RegexOptions.IgnoreCase);
        if (m.Success && int.TryParse(m.Groups[2].Value, out var repQty) && repQty > 0)
        {
            var rawItem = StripTrailingNoise(m.Groups[3].Value);
            var resolved = NormalizeMenuItemName(rawItem);
            if (resolved != null)
            {
                mod.Type = ModificationType.Replace;
                mod.Quantity = repQty;
                mod.ItemName = resolved;
                return true;
            }
        }

        return false;
    }

    // Strip trailing filler words like "mas", "porfavor", "por favor", "porfa", "plis", "please"
    private static string StripTrailingNoise(string input)
    {
        var result = Regex.Replace(input.Trim(),
            @"\s+(m[aá]s|porfavor|por\s*favor|porfa|plis|please|gracias|xfa|xfavor)\s*$",
            "", RegexOptions.IgnoreCase).Trim();
        // Also strip trailing "mas" at the very end (no space after)
        result = Regex.Replace(result, @"\s+m[aá]s$", "", RegexOptions.IgnoreCase).Trim();
        return result;
    }

    // ──────────────────────────────────────────
    // Menu catalog (extensible, alias-aware)
    // ──────────────────────────────────────────

    internal sealed class MenuEntry
    {
        public string Canonical { get; init; } = "";
        public string[] Aliases { get; init; } = [];
        public string? Category { get; init; }
        public bool IsCombo { get; init; }
    }

    // Catalog: future menu system will load from DB. For now, demo items.
    internal static readonly MenuEntry[] MenuCatalog =
    {
        new() { Canonical = "Hamburguesa", Aliases = new[] { "hamburguesa", "hamburguesas", "hamburgesa", "hamburgesas",
            "hamburguea", "hamburgueas", "hamburgues", "hamburguez", "hamburgue",
            "hamburga", "hamburgs", "hambur", "burger", "burgers", "hamburguesita", "hamburguesitas" },
            Category = "comida" },
        new() { Canonical = "Coca Cola", Aliases = new[] { "coca cola", "cocacola", "cocacolas", "coca", "cocas",
            "coca-cola", "coca colas", "refresco", "refrescos", "gaseosa", "gaseosas", "soda" },
            Category = "bebida" },
        new() { Canonical = "Papas", Aliases = new[] { "papas", "papa", "papaas", "papitas", "papita", "papaz",
            "papas fritas", "fritas", "french fries", "fries" },
            Category = "acompanamiento" },
        new() { Canonical = "Pizza", Aliases = new[] { "pizza", "pizzas", "piza", "pizas", "pizzita", "pizzitas" },
            Category = "comida" },
        new() { Canonical = "Sushi Roll", Aliases = new[] { "sushi roll", "sushi", "sushis", "roll", "rolls",
            "sushi rolls", "maki", "makis" },
            Category = "comida" },
        new() { Canonical = "Combo", Aliases = new[] { "combo", "combos", "combo familiar", "promo", "promocion" },
            Category = "combo", IsCombo = true },
        new() { Canonical = "Hot Dog", Aliases = new[] { "hot dog", "hotdog", "hotdogs", "hot dogs", "perro caliente",
            "perros calientes", "perro", "perros" },
            Category = "comida" },
        new() { Canonical = "Tequeños", Aliases = new[] { "tequenos", "tequeño", "tequeños", "tequeno",
            "tequenitos", "tequeñitos" },
            Category = "comida" },
        new() { Canonical = "Empanada", Aliases = new[] { "empanada", "empanadas", "empanadita", "empanaditas" },
            Category = "comida" },
        new() { Canonical = "Jugo", Aliases = new[] { "jugo", "jugos", "juguito", "juguitos", "juice" },
            Category = "bebida" },
        new() { Canonical = "Agua", Aliases = new[] { "agua", "aguas", "aguita", "botella de agua", "water" },
            Category = "bebida" },
        new() { Canonical = "Cerveza", Aliases = new[] { "cerveza", "cervezas", "birra", "birras", "beer", "beers",
            "chela", "chelas" },
            Category = "bebida" },
    };

    internal static string? NormalizeMenuItemName(string rawItem)
    {
        if (string.IsNullOrWhiteSpace(rawItem)) return null;

        var t = rawItem.Trim().ToLowerInvariant();
        t = StripAccents(t);
        var tNoS = t.EndsWith("s") ? t[..^1] : t;

        foreach (var entry in MenuCatalog)
        {
            foreach (var alias in entry.Aliases)
            {
                var a = StripAccents(alias);
                if (t == a || tNoS == a)
                    return entry.Canonical;
            }

            var canonLower = StripAccents(entry.Canonical.ToLowerInvariant());
            if (t.Length >= 5 && (canonLower.StartsWith(t) || canonLower.StartsWith(tNoS)))
                return entry.Canonical;

            if (t.Length >= 5)
            {
                foreach (var alias in entry.Aliases)
                {
                    var a = StripAccents(alias);
                    if (a.Length >= 4 && LevenshteinDistance(t, a) <= 2)
                        return entry.Canonical;
                }
            }
        }

        return null;
    }

    internal static int LevenshteinDistance(string a, string b)
    {
        if (a.Length == 0) return b.Length;
        if (b.Length == 0) return a.Length;

        var dp = new int[a.Length + 1, b.Length + 1];
        for (var i = 0; i <= a.Length; i++) dp[i, 0] = i;
        for (var j = 0; j <= b.Length; j++) dp[0, j] = j;

        for (var i = 1; i <= a.Length; i++)
        for (var j = 1; j <= b.Length; j++)
        {
            var cost = a[i - 1] == b[j - 1] ? 0 : 1;
            dp[i, j] = Math.Min(
                Math.Min(dp[i - 1, j] + 1, dp[i, j - 1] + 1),
                dp[i - 1, j - 1] + cost);
        }

        return dp[a.Length, b.Length];
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

        // Phase 1: Try labeled extraction for each field
        string? GetLabeledValue(string key)
        {
            foreach (var line in lines)
            {
                var l = line.Trim();
                var stripped = Regex.Replace(l, @"^[\p{So}\p{Cs}\ufe0f\u200d]+\s*", "");
                stripped = stripped.TrimStart('*');
                var ln = StripAccents(stripped.ToLowerInvariant());

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

        form.CustomerName = CleanFieldValue(GetLabeledValue("nombre"));
        form.CustomerIdNumber = CleanFieldValue(GetLabeledValue("cedula"));
        form.CustomerPhone = CleanFieldValue(GetLabeledValue("telefono"));
        form.Address = CleanFieldValue(GetLabeledValue("direccion"));
        var payLabeled = GetLabeledValue("pago");
        var locLabeled = GetLabeledValue("ubicacion");

        if (!string.IsNullOrWhiteSpace(locLabeled))
        {
            form.LocationText = locLabeled;
        }

        if (!string.IsNullOrWhiteSpace(payLabeled))
            form.PaymentMethod = NormalizePaymentMethod(payLabeled);

        // Phase 2: Smart inference for unlabeled lines
        // Collect lines not yet consumed by labeled extraction
        var unlabeled = new List<string>();
        foreach (var line in lines)
        {
            var stripped = Regex.Replace(line.Trim(), @"^[\p{So}\p{Cs}\ufe0f\u200d]+\s*", "").TrimStart('*');
            var ln = StripAccents(stripped.ToLowerInvariant());

            // Skip lines that were consumed by labeled extraction
            bool isLabeled = ln.StartsWith("nombre") || ln.StartsWith("cedula")
                || ln.StartsWith("telefono") || ln.StartsWith("direccion")
                || ln.StartsWith("pago") || ln.StartsWith("ubicacion");
            if (isLabeled && (stripped.Contains(':') || stripped.Contains('-') || stripped.Contains('=')))
                continue;

            // Skip "OBLIGATORIO", "CONFIRMAR", "GPS" template lines
            if (ln is "obligatorio" or "confirmar" or "gps" or "✅ obligatorio"
                || ln.Contains("manda el pin") || ln.Contains("luego escribe"))
                continue;

            unlabeled.Add(line.Trim());
        }

        // Infer fields from unlabeled lines
        foreach (var line in unlabeled)
        {
            var clean = Regex.Replace(line, @"^[\p{So}\p{Cs}\ufe0f\u200d]+\s*", "").TrimStart('*').Trim();
            if (string.IsNullOrWhiteSpace(clean)) continue;

            var cleanLower = StripAccents(clean.ToLowerInvariant());
            var digitsOnly = new string(clean.Where(char.IsDigit).ToArray());

            // Phone detection: Venezuelan mobile patterns (04XX, +58, 58XX)
            if (form.CustomerPhone is null && IsVenezuelanPhone(clean))
            {
                form.CustomerPhone = CleanFieldValue(clean);
                continue;
            }

            // ID detection: 6-10 digit number (not a phone)
            if (form.CustomerIdNumber is null
                && digitsOnly.Length >= 6 && digitsOnly.Length <= 10
                && Regex.IsMatch(clean, @"^[VvEeJjGg]?[\-\.]?\d[\d\.\-]*$"))
            {
                form.CustomerIdNumber = CleanFieldValue(clean);
                continue;
            }

            // Payment detection
            if (form.PaymentMethod is null)
            {
                var payNorm = NormalizePaymentMethod(clean);
                if (payNorm is not null)
                {
                    form.PaymentMethod = payNorm;
                    continue;
                }
            }

            // Address detection: longer text with address-like patterns
            if (form.Address is null && LooksLikeAddress(cleanLower))
            {
                form.Address = CleanFieldValue(clean);
                continue;
            }

            // Name detection: first remaining human-looking text (alpha, short-ish)
            if (form.CustomerName is null && Regex.IsMatch(clean, @"^[A-Za-zÀ-ÿ\s\.\-']+$") && clean.Length <= 80)
            {
                form.CustomerName = CleanFieldValue(clean);
                continue;
            }

            // Fallback: if address not set and it's longer text, treat as address
            if (form.Address is null && clean.Length >= 4)
            {
                form.Address = CleanFieldValue(clean);
                continue;
            }
        }

        var filled =
            (string.IsNullOrWhiteSpace(form.CustomerName) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(form.CustomerIdNumber) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(form.CustomerPhone) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(form.Address) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(form.PaymentMethod) ? 0 : 1)
            + (string.IsNullOrWhiteSpace(form.LocationText) ? 0 : 1);

        return filled >= 2;
    }

    internal static bool IsVenezuelanPhone(string value)
    {
        var digits = new string(value.Where(char.IsDigit).ToArray());
        if (digits.Length < 10 || digits.Length > 13) return false;

        // +584XX, 584XX, 04XX patterns
        if (digits.StartsWith("58") && digits.Length >= 12) return true;
        if (digits.StartsWith("0") && digits.Length >= 10 && digits.Length <= 11)
        {
            var after0 = digits[1..];
            if (after0.StartsWith("4") || after0.StartsWith("2"))
                return true;
        }
        // Also match if value starts with + and has 12-13 digits
        if (value.TrimStart().StartsWith("+") && digits.Length >= 10) return true;

        return false;
    }

    internal static bool LooksLikeAddress(string lowerText)
    {
        // Common Venezuelan address tokens
        return lowerText.Contains("calle") || lowerText.Contains("av ")
            || lowerText.Contains("avenida") || lowerText.Contains("urbaniz")
            || lowerText.Contains("res.") || lowerText.Contains("residencia")
            || lowerText.Contains("quinta") || lowerText.Contains("piso")
            || lowerText.Contains("apto") || lowerText.Contains("local")
            || lowerText.Contains("sector") || lowerText.Contains("zona")
            || lowerText.Contains("centro") || lowerText.Contains("plaza")
            || lowerText.Contains("torre") || lowerText.Contains("edificio")
            || lowerText.Contains("edif") || lowerText.Contains("hatillo")
            || lowerText.Contains("castellana") || lowerText.Contains("altamira")
            || lowerText.Contains("chacao") || lowerText.Contains("baruta")
            || lowerText.Contains("el paraiso") || lowerText.Contains("los palos grandes")
            || lowerText.Contains("carrera") || lowerText.Contains("transversal")
            || lowerText.Contains("vereda") || lowerText.Contains("esquina")
            || lowerText.Contains("casa") || lowerText.Contains("apt ")
            || lowerText.Contains("manzana") || lowerText.Contains("parcela")
            || lowerText.Contains("conjunto");
    }

    internal static string? NormalizePaymentMethod(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return null;

        var p = StripAccents(input.Trim().ToLowerInvariant());
        p = DeduplicateTokens(p);

        // If contains template options separator, don't match
        if (p.Contains('/') && p.Contains("efectivo") && p.Contains("divis"))
            return null;

        if (p.Contains("pago") && p.Contains("mov"))
            return "pago_movil";
        if (p is "pm")
            return "pago_movil";
        if (p.Contains("divis") || p.Contains("usd") || p.Contains("dolar"))
            return "divisas";
        if (p.Contains("efect") || p is "cash")
            return "efectivo";

        return null;
    }

    // Deduplicate repeated token sequences like "pago movil pago movil" → "pago movil"
    internal static string DeduplicateTokens(string input)
    {
        var words = input.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (words.Length < 2) return input;

        // Try halves: if first half equals second half, keep only first half
        if (words.Length % 2 == 0)
        {
            var half = words.Length / 2;
            var firstHalf = string.Join(" ", words.Take(half));
            var secondHalf = string.Join(" ", words.Skip(half));
            if (firstHalf == secondHalf)
                return firstHalf;
        }

        return input;
    }

    internal static string BuildCanonicalMissingFieldsMessage(List<string> missing)
    {
        var sb = new StringBuilder();
        sb.AppendLine("A\u00fan falta informaci\u00f3n para confirmar.");
        sb.AppendLine();
        sb.AppendLine("Env\u00edanos al menos:");
        sb.AppendLine();
        foreach (var field in missing)
            sb.AppendLine(field);
        sb.AppendLine();
        sb.AppendLine("Puedes responder copiando y pegando esta planilla");
        sb.AppendLine("o enviando los datos en l\u00edneas separadas.");
        sb.AppendLine();
        sb.Append("Luego escribe *CONFIRMAR*.");
        return sb.ToString();
    }

    // Clean parsed field values: trim whitespace, collapse spaces, strip template markers
    internal static string? CleanFieldValue(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return null;
        // Collapse multiple spaces
        var cleaned = Regex.Replace(raw.Trim(), @"\s+", " ");
        // Strip bold markers
        cleaned = cleaned.Replace("*", "");
        return string.IsNullOrWhiteSpace(cleaned) ? null : cleaned;
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

    // ──────────────────────────────────────────
    // Quick parser (no AI) — handles compound orders, modifiers, noise
    // ──────────────────────────────────────────

    internal sealed class ParsedItem
    {
        public string Name { get; set; } = "";
        public int Quantity { get; set; } = 1;
        public string? Modifiers { get; set; }
    }

    internal static bool TryParseQuickOrder(
        string rawText,
        out List<(string Name, int Quantity)> items,
        out string? deliveryType,
        out string? embeddedObservation)
    {
        items = new List<(string, int)>();
        deliveryType = null;
        embeddedObservation = null;

        var t = rawText.ToLowerInvariant();

        if (t.Contains("delivery") || t.Contains("domicilio"))
            deliveryType = "delivery";
        else if (t.Contains("pick up") || t.Contains("pickup") || t.Contains("recoger"))
            deliveryType = "pickup";

        var parsed = ParseOrderText(rawText);

        foreach (var p in parsed)
            items.Add((p.Name, p.Quantity));

        // Extract observations from modifiers and from text
        var allObs = new List<string>();
        foreach (var p in parsed)
        {
            if (!string.IsNullOrWhiteSpace(p.Modifiers))
                allObs.Add(p.Modifiers);
        }

        // Also extract standalone observations not attached to items
        var standaloneObs = ExtractEmbeddedObservation(rawText, parsed);
        if (!string.IsNullOrWhiteSpace(standaloneObs))
            allObs.Add(standaloneObs);

        if (allObs.Count > 0)
            embeddedObservation = string.Join("; ", allObs.Distinct());

        return items.Count > 0;
    }

    // Overload for backward compatibility
    private static bool TryParseQuickOrder(
        string rawText,
        out List<(string Name, int Quantity)> items,
        out string? deliveryType)
    {
        return TryParseQuickOrder(rawText, out items, out deliveryType, out _);
    }

    // Core order text parser: splits by conjunctions, handles "N items con/y other items"
    internal static List<ParsedItem> ParseOrderText(string rawText)
    {
        var results = new List<ParsedItem>();
        var text = rawText.Trim();

        // Noise words to strip (greetings + filler)
        var noisePattern = @"\b(hola|buenas?|buenos?\s+d[ií]as?|buenas\s+tardes|buenas\s+noches|hey|epa|saludos|por\s*favor|porfavor|porfa|plis|please|gracias|quiero|quisiera|me\s+das?|dame|necesito|pedimos|para\s+llevar|para\s+comer|mand[ae]\s*me)\b";
        var cleaned = Regex.Replace(text, noisePattern, " ", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\s+", " ").Trim();

        // First, split into segments on "con [menu-item]" and " y " boundaries
        var segments = SplitIntoOrderSegments(cleaned);

        foreach (var seg in segments)
        {
            var s = seg.Trim();
            if (string.IsNullOrWhiteSpace(s)) continue;

            // Try "N items [modifiers]"
            var m = Regex.Match(s,
                @"^(\d+)\s+(.+)$",
                RegexOptions.IgnoreCase);

            if (m.Success && int.TryParse(m.Groups[1].Value, out var qty) && qty > 0)
            {
                var rest = m.Groups[2].Value.Trim();
                var item = ExtractItemAndModifiers(rest);
                if (item != null)
                {
                    item.Quantity = qty;
                    results.Add(item);
                    continue;
                }
            }

            // No quantity prefix — try as a single item
            var singleItem = ExtractItemAndModifiers(s);
            if (singleItem != null)
            {
                results.Add(singleItem);
            }
        }

        return results;
    }

    // Split text into segments, separating on " y " and "con [menu-item]"
    // "3 hamburguesas con papas y coca" -> ["3 hamburguesas", "papas", "coca"]
    // "1 hamburguesa sin cebolla y 1 coca" -> ["1 hamburguesa sin cebolla", "1 coca"]
    internal static List<string> SplitIntoOrderSegments(string text)
    {
        var results = new List<string>();

        // First split on commas
        var commaParts = text.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var cp in commaParts)
        {
            // Split on " y " — always split on conjunction
            var yParts = Regex.Split(cp, @"\s+y\s+", RegexOptions.IgnoreCase);

            foreach (var yp in yParts)
            {
                var part = yp.Trim();
                if (string.IsNullOrWhiteSpace(part)) continue;

                // Check if this segment has "con [menu-item]" pattern
                // e.g. "3 hamburguesas con papas" → split into "3 hamburguesas" and "papas"
                var conSplit = TrySplitOnConMenuItem(part);
                if (conSplit != null)
                {
                    results.AddRange(conSplit);
                }
                else
                {
                    results.Add(part);
                }
            }
        }

        return results;
    }

    // If text contains "con [known-menu-item]", split it
    // "3 hamburguesas con papas" -> ["3 hamburguesas", "papas"]
    // "hamburguesa con extra queso" -> null (not a menu item link)
    private static List<string>? TrySplitOnConMenuItem(string text)
    {
        var m = Regex.Match(text, @"^(.+?)\s+con\s+(.+)$", RegexOptions.IgnoreCase);
        if (!m.Success) return null;

        var before = m.Groups[1].Value.Trim();
        var after = m.Groups[2].Value.Trim();

        // Check if the word(s) after "con" start with a known menu item
        var afterWords = after.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? resolved = null;

        // Try first 1-3 words
        for (int len = Math.Min(afterWords.Length, 3); len >= 1; len--)
        {
            var candidate = string.Join(" ", afterWords.Take(len));
            resolved = NormalizeMenuItemName(candidate);
            if (resolved != null) break;
        }

        if (resolved != null)
        {
            // "con [menu-item]" — split into two segments
            return new List<string> { before, after };
        }

        // "con extra queso" / "con todo" — not a menu item, keep as modifier
        return null;
    }

    // Parse "item_name [modifier_phrase]" from text segment
    internal static ParsedItem? ExtractItemAndModifiers(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;

        var t = text.Trim();

        // Try to extract modifiers: "sin X", "extra Y", "con extra Z", "mitad X mitad Y"
        string? modifiers = null;
        var itemText = t;

        // Check for modifier phrases
        var modMatch = Regex.Match(t,
            @"\s+(sin\s+.+|extra\s+.+|con\s+extra\s+.+|mitad\s+.+|bien\s+(?:cocid|asad|hech).+|al\s+punto|termino\s+\w+|doble\s+\w+)$",
            RegexOptions.IgnoreCase);
        if (modMatch.Success)
        {
            modifiers = modMatch.Value.Trim();
            itemText = t[..modMatch.Index].Trim();
        }

        // Strip trailing noise
        itemText = StripTrailingNoise(itemText);

        // Try matching word by word, expanding to multi-word names
        var words = itemText.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        string? bestMatch = null;
        int bestLen = 0;

        // Try longest multi-word match first (up to 3 words)
        for (int len = Math.Min(words.Length, 3); len >= 1; len--)
        {
            for (int start = 0; start <= words.Length - len; start++)
            {
                var candidate = string.Join(" ", words.Skip(start).Take(len));
                var resolved = NormalizeMenuItemName(candidate);
                if (resolved != null && len > bestLen)
                {
                    bestMatch = resolved;
                    bestLen = len;
                }
            }
        }

        if (bestMatch == null) return null;

        return new ParsedItem
        {
            Name = bestMatch,
            Quantity = 1,
            Modifiers = modifiers
        };
    }

    // Extract standalone observations not already captured as item modifiers
    internal static string? ExtractEmbeddedObservation(string rawText, List<ParsedItem>? parsedItems = null)
    {
        var observations = new List<string>();

        // Match: "sin X", "con extra X", but NOT "con [menu-item]"
        var obsMatches = Regex.Matches(rawText,
            @"(?:un[ao]?\s+|otr[ao]?\s+)?(?:sin\s+[a-záéíóúñü\s]+|con\s+extra\s+[a-záéíóúñü\s]+|extra\s+[a-záéíóúñü]+)",
            RegexOptions.IgnoreCase);

        foreach (Match m in obsMatches)
        {
            var obs = m.Value.Trim();
            if (obs.Length < 6) continue;

            // Don't count "con [menu-item]" as observation — check if the word after "con" is a menu item
            // This is already excluded by the regex above (only matches "con extra")
            // Also skip if this observation was already captured as a per-item modifier
            if (parsedItems != null && parsedItems.Any(p =>
                !string.IsNullOrWhiteSpace(p.Modifiers) && p.Modifiers.Contains(obs, StringComparison.OrdinalIgnoreCase)))
                continue;

            observations.Add(obs);
        }

        if (observations.Count == 0) return null;

        var result = string.Join(", ", observations);
        result = Regex.Replace(result, @"\s+", " ").Trim();
        result = Regex.Replace(result, @"[,\s]+$", "").Trim();

        return string.IsNullOrWhiteSpace(result) ? null : result;
    }
}
