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
    private readonly IMenuRepository? _menuRepository;
    private readonly INotificationService? _notificationService;
    private readonly ILogger<WebhookProcessor> _logger;
    private readonly PaymentMobileOptions _paymentMobile;

    public WebhookProcessor(
        IAiParser aiParser,
        IWhatsAppClient whatsAppClient,
        IOrderRepository orderRepository,
        IConversationStateStore stateStore,
        ILogger<WebhookProcessor> logger,
        PaymentMobileOptions? paymentMobile = null,
        IMenuRepository? menuRepository = null,
        INotificationService? notificationService = null)
    {
        _aiParser = aiParser;
        _whatsAppClient = whatsAppClient;
        _orderRepository = orderRepository;
        _stateStore = stateStore;
        _menuRepository = menuRepository;
        _notificationService = notificationService;
        _logger = logger;
        _paymentMobile = paymentMobile ?? new PaymentMobileOptions();
    }

    private const int MaxMessageLength = 4096;

    // Per-request active menu (loaded from DB or fallback to demo)
    private MenuEntry[]? _activeMenu;

    public async Task ProcessAsync(WebhookPayload payload, BusinessContext businessContext, CancellationToken cancellationToken = default)
    {
        if (payload?.Entry is null) return;

        // Load business menu from DB; fallback to demo catalog
        _activeMenu = await LoadBusinessMenuAsync(businessContext.BusinessId, cancellationToken);
        ActiveCatalog = _activeMenu;

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
                                Body = Msg.GpsReceived,
                                PhoneNumberId = phoneNumberId,
                                AccessToken = businessContext.AccessToken
                            }, businessContext.BusinessId, conversationId, cancellationToken);
                        }

                        await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                        continue;
                    }

                    // 2) Media: payment evidence
                    //    Accept proof image when: explicitly requested, OR payment method
                    //    is pago_movil/divisas and we have items (even if not formally requested),
                    //    OR order was confirmed but proof is still pending (post-confirm capture)
                    if (message.Type != "text")
                    {
                        var shouldCaptureProof = !state.PaymentEvidenceReceived
                            && (state.PaymentEvidenceRequested
                                || (state.Items.Count > 0 && state.PaymentMethod is "pago_movil" or "divisas")
                                || state.AwaitingPostConfirmProof);

                        if (shouldCaptureProof && message.Type is "image" or "document")
                        {
                            state.PaymentEvidenceReceived = true;

                            // Capture media ID from image or document message
                            var mediaId = message.Image?.Id ?? message.Document?.Id;
                            if (!string.IsNullOrWhiteSpace(mediaId))
                            {
                                state.PaymentProofMediaId = mediaId;

                                // Post-confirm: update the already-persisted order
                                if (state.AwaitingPostConfirmProof && state.LastOrderId.HasValue)
                                {
                                    await _orderRepository.AttachPaymentProofAsync(
                                        state.LastOrderId.Value, mediaId, cancellationToken);
                                }
                            }

                            state.AwaitingPostConfirmProof = false;

                            await SendAsync(new OutgoingMessage
                            {
                                To = message.From,
                                Body = Msg.PaymentProofReceived,
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
                        state.HumanHandoffAtUtc = DateTime.UtcNow;
                        state.HumanHandoffNotifiedCount = 1;

                        await SendAsync(new OutgoingMessage
                        {
                            To = message.From,
                            Body = Msg.HandoffInitiated,
                            PhoneNumberId = phoneNumberId,
                            AccessToken = businessContext.AccessToken
                        }, businessContext.BusinessId, conversationId, cancellationToken);

                        // Notify staff
                        if (_notificationService is not null)
                            await _notificationService.NotifyHumanHandoffAsync(businessContext, message.From, cancellationToken);

                        await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                        continue;
                    }

                    // 0b) If already in human handoff, pause all bot logic
                    if (state.HumanHandoffRequested)
                    {
                        // Send reminder max 2 more times, then stay silent
                        if (state.HumanHandoffNotifiedCount < 3)
                        {
                            state.HumanHandoffNotifiedCount++;
                            await SendAsync(new OutgoingMessage
                            {
                                To = message.From,
                                Body = Msg.HandoffWaiting,
                                PhoneNumberId = phoneNumberId,
                                AccessToken = businessContext.AccessToken
                            }, businessContext.BusinessId, conversationId, cancellationToken);

                            // Notify staff on first follow-up only (count == 2)
                            if (_notificationService is not null && state.HumanHandoffNotifiedCount == 2)
                                await _notificationService.NotifyCustomerWaitingAsync(businessContext, message.From, cancellationToken);
                        }

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

                        var obsReply = Msg.CheckoutForm;

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
                        var confirmReply = await FinalizeOrderIfPossibleAsync(state, message.From, phoneNumberId, businessContext, cancellationToken);

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
                        // Check business hours before starting order flow
                        if (ScheduleParser.IsClosed(businessContext.Schedule))
                        {
                            var closedMsg = !string.IsNullOrWhiteSpace(businessContext.Schedule)
                                ? Msg.BusinessClosed(
                                    !string.IsNullOrWhiteSpace(businessContext.BusinessName) ? businessContext.BusinessName : "nuestro restaurante",
                                    businessContext.Schedule)
                                : Msg.BusinessClosedNoSchedule(
                                    !string.IsNullOrWhiteSpace(businessContext.BusinessName) ? businessContext.BusinessName : "nuestro restaurante");

                            await SendAsync(new OutgoingMessage
                            {
                                To = message.From,
                                Body = closedMsg,
                                PhoneNumberId = phoneNumberId,
                                AccessToken = businessContext.AccessToken
                            }, businessContext.BusinessId, conversationId, cancellationToken);

                            await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                            continue;
                        }

                        // Abandoned order resume: if customer greets but has items from a stale session
                        // Only resume if: items exist, not in checkout, observation not yet handled,
                        // and last activity was > 10 minutes ago (stale session)
                        if (IsGreeting(t) && state.Items.Count > 0
                            && !state.CheckoutFormSent && !state.ObservationAnswered
                            && state.LastActivityUtc.HasValue
                            && (DateTime.UtcNow - state.LastActivityUtc.Value).TotalMinutes > 10)
                        {
                            var itemsSummary = string.Join(", ", state.Items.Select(i => FormatItemText(i)));
                            state.LastActivityUtc = DateTime.UtcNow;

                            await SendAsync(new OutgoingMessage
                            {
                                To = message.From,
                                Body = Msg.AbandonedResume(itemsSummary),
                                PhoneNumberId = phoneNumberId,
                                AccessToken = businessContext.AccessToken
                            }, businessContext.BusinessId, conversationId, cancellationToken);

                            await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                            continue;
                        }

                        state.ResetAfterConfirm();
                        state.MenuSent = true;

                        await SendGreetingSequenceAsync(message.From, phoneNumberId, businessContext, conversationId, cancellationToken);

                        await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                        continue;
                    }

                    // B2) Reorder intent: "repetir", "lo mismo", "mismo pedido"
                    if (IsReorderRequest(t) && state.Items.Count == 0)
                    {
                        // Check business hours
                        if (ScheduleParser.IsClosed(businessContext.Schedule))
                        {
                            var closedMsg = !string.IsNullOrWhiteSpace(businessContext.Schedule)
                                ? Msg.BusinessClosed(
                                    !string.IsNullOrWhiteSpace(businessContext.BusinessName) ? businessContext.BusinessName : "nuestro restaurante",
                                    businessContext.Schedule)
                                : Msg.BusinessClosedNoSchedule(
                                    !string.IsNullOrWhiteSpace(businessContext.BusinessName) ? businessContext.BusinessName : "nuestro restaurante");

                            await SendAsync(new OutgoingMessage
                            {
                                To = message.From,
                                Body = closedMsg,
                                PhoneNumberId = phoneNumberId,
                                AccessToken = businessContext.AccessToken
                            }, businessContext.BusinessId, conversationId, cancellationToken);

                            await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                            continue;
                        }

                        var lastOrder = await _orderRepository.GetLastCompletedOrderAsync(
                            message.From, businessContext.BusinessId, cancellationToken);

                        if (lastOrder != null && lastOrder.Items.Count > 0)
                        {
                            // Load previous order items into current state
                            foreach (var item in lastOrder.Items)
                            {
                                state.Items.Add(new ConversationItemEntry
                                {
                                    Name = item.Name,
                                    Quantity = item.Quantity
                                });
                            }
                            state.DeliveryType = lastOrder.DeliveryType;
                            state.MenuSent = true;
                            state.LastActivityUtc = DateTime.UtcNow;

                            // Build a summary of what was loaded
                            var reorderSummary = string.Join(", ", state.Items.Select(i => FormatItemText(i)));

                            await SendAsync(new OutgoingMessage
                            {
                                To = message.From,
                                Body = Msg.ReorderConfirmed,
                                PhoneNumberId = phoneNumberId,
                                AccessToken = businessContext.AccessToken
                            }, businessContext.BusinessId, conversationId, cancellationToken);
                        }
                        else
                        {
                            await SendAsync(new OutgoingMessage
                            {
                                To = message.From,
                                Body = Msg.NoReorderAvailable,
                                PhoneNumberId = phoneNumberId,
                                AccessToken = businessContext.AccessToken
                            }, businessContext.BusinessId, conversationId, cancellationToken);
                        }

                        await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                        continue;
                    }

                    // B3) Standalone delivery/pickup answer (no items, just the word)
                    if (state.Items.Count > 0 && string.IsNullOrWhiteSpace(state.DeliveryType))
                    {
                        var standaloneDelivery = NormalizeDeliveryType(rawText);
                        if (standaloneDelivery != null)
                        {
                            state.DeliveryType = standaloneDelivery;
                            var deliveryReply = BuildOrderReplyFromState(state);

                            await SendAsync(new OutgoingMessage
                            {
                                To = message.From,
                                Body = deliveryReply,
                                PhoneNumberId = phoneNumberId,
                                AccessToken = businessContext.AccessToken
                            }, businessContext.BusinessId, conversationId, cancellationToken);

                            state.LastActivityUtc = DateTime.UtcNow;
                            await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                            continue;
                        }
                    }

                    // C) Order modification: add/remove/replace items (typo-tolerant)
                    if (TryParseOrderModification(rawText, out var orderMod))
                    {
                        string modReply;
                        switch (orderMod.Type)
                        {
                            case ModificationType.Add:
                                AddOrIncreaseItem(state, orderMod.ItemName, orderMod.Quantity);
                                modReply = Msg.ItemAdded(orderMod.Quantity, orderMod.ItemName);
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
                                        modReply = Msg.ItemRemoved(orderMod.ItemName);
                                    }
                                    else
                                    {
                                        existing.Quantity -= orderMod.Quantity;
                                        modReply = Msg.ItemReduced(existing.Quantity, orderMod.ItemName);
                                    }
                                }
                                else
                                {
                                    modReply = Msg.ItemNotFound(orderMod.ItemName);
                                }

                                if (state.Items.Count == 0)
                                    modReply += Msg.EmptyCart;
                                else
                                    modReply += " " + Msg.ConfirmPrompt;
                                break;
                            }

                            case ModificationType.Replace:
                            {
                                var existingR = state.Items.FirstOrDefault(
                                    x => x.Name.Equals(orderMod.ItemName, StringComparison.OrdinalIgnoreCase));
                                if (existingR != null)
                                {
                                    existingR.Quantity = orderMod.Quantity;
                                    modReply = Msg.ItemReplaced(orderMod.Quantity, orderMod.ItemName);
                                }
                                else
                                {
                                    AddOrIncreaseItem(state, orderMod.ItemName, orderMod.Quantity);
                                    modReply = Msg.ItemAdded(orderMod.Quantity, orderMod.ItemName);
                                }
                                break;
                            }

                            default:
                                modReply = Msg.ConfirmPrompt;
                                break;
                        }

                        await SendAsync(new OutgoingMessage
                        {
                            To = message.From,
                            Body = modReply,
                            PhoneNumberId = phoneNumberId,
                            AccessToken = businessContext.AccessToken
                        }, businessContext.BusinessId, conversationId, cancellationToken);

                        state.LastActivityUtc = DateTime.UtcNow;
                        await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                        continue;
                    }

                    // D0) Standalone payment method at ANY stage with items in cart
                    //     Only for single-line messages — multiline may be a checkout form
                    if (state.Items.Count > 0 && !IsRestartIntent(t) && !rawText.Contains('\n'))
                    {
                        var payMethod = NormalizePaymentMethod(rawText);
                        if (payMethod is not null && state.PaymentMethod is null)
                        {
                            state.PaymentMethod = payMethod;

                            // Trigger evidence request for pago_movil / divisas
                            if (payMethod is "pago_movil" or "divisas")
                            {
                                if (!state.PaymentEvidenceRequested)
                                {
                                    state.PaymentEvidenceRequested = true;

                                    if (payMethod == "pago_movil")
                                    {
                                        await SendPagoMovilDetailsAsync(message.From, phoneNumberId, businessContext, conversationId, cancellationToken);
                                    }
                                    else
                                    {
                                        await SendAsync(new OutgoingMessage
                                        {
                                            To = message.From,
                                            Body = Msg.DivisasProofRequest,
                                            PhoneNumberId = phoneNumberId,
                                            AccessToken = businessContext.AccessToken
                                        }, businessContext.BusinessId, conversationId, cancellationToken);
                                    }
                                }
                            }

                            await SendAsync(new OutgoingMessage
                            {
                                To = message.From,
                                Body = Msg.CheckoutDataReceived,
                                PhoneNumberId = phoneNumberId,
                                AccessToken = businessContext.AccessToken
                            }, businessContext.BusinessId, conversationId, cancellationToken);

                            await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                            continue;
                        }
                    }

                    // D) Checkout form capture (supports incremental field submission)
                    if (state.CheckoutFormSent && TryParseCheckoutForm(rawText, out var form, isMerge: true))
                    {
                        // Merge: overwrite only null fields with newly-parsed values
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
                                        Body = Msg.DivisasProofRequest,
                                        PhoneNumberId = phoneNumberId,
                                        AccessToken = businessContext.AccessToken
                                    }, businessContext.BusinessId, conversationId, cancellationToken);
                                }
                            }
                        }

                        await SendAsync(new OutgoingMessage
                        {
                            To = message.From,
                            Body = Msg.CheckoutDataReceived,
                            PhoneNumberId = phoneNumberId,
                            AccessToken = businessContext.AccessToken
                        }, businessContext.BusinessId, conversationId, cancellationToken);

                        await _stateStore.SaveAsync(conversationId, state, cancellationToken);
                        continue;
                    }

                    // E0) Quick parse (no AI)
                    if (TryParseQuickOrder(rawText, out var quickItems, out var quickDelivery, out var quickObs))
                    {
                        // TryParseQuickOrder already calls ParseOrderText internally.
                        // Use its returned items directly to avoid double-adding.
                        var parsedRich = ParseOrderText(rawText);
                        foreach (var p in parsedRich)
                            AddOrIncreaseItem(state, p.Name, p.Quantity, p.Modifiers);

                        if (!string.IsNullOrWhiteSpace(quickDelivery))
                            state.DeliveryType = quickDelivery;

                        // Store embedded observation if detected — and skip observation prompt
                        if (!string.IsNullOrWhiteSpace(quickObs))
                        {
                            state.SpecialInstructions = string.IsNullOrWhiteSpace(state.SpecialInstructions)
                                ? quickObs
                                : state.SpecialInstructions + "; " + quickObs;
                            state.ObservationAnswered = true;
                        }

                        var quickReply = BuildOrderReplyFromState(state);

                        await SendAsync(new OutgoingMessage
                        {
                            To = message.From,
                            Body = quickReply,
                            PhoneNumberId = phoneNumberId,
                            AccessToken = businessContext.AccessToken
                        }, businessContext.BusinessId, conversationId, cancellationToken);

                        state.LastActivityUtc = DateTime.UtcNow;
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

                    state.LastActivityUtc = DateTime.UtcNow;
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

        // Message 1: Welcome (custom greeting or default)
        var greeting = !string.IsNullOrWhiteSpace(biz.Greeting)
            ? biz.Greeting
            : Msg.DefaultGreeting(businessName);

        await SendAsync(new OutgoingMessage
        {
            To = to,
            Body = greeting,
            PhoneNumberId = phoneNumberId,
            AccessToken = biz.AccessToken
        }, biz.BusinessId, conversationId, ct);

        // Message 2: Menu (dynamic from DB or demo fallback)
        var menuBody = BuildMenuMessage();

        await SendAsync(new OutgoingMessage
        {
            To = to,
            Body = menuBody,
            PhoneNumberId = phoneNumberId,
            AccessToken = biz.AccessToken
        }, biz.BusinessId, conversationId, ct);

        // Message 3: Prompt
        await SendAsync(new OutgoingMessage
        {
            To = to,
            Body = Msg.MenuPrompt,
            PhoneNumberId = phoneNumberId,
            AccessToken = biz.AccessToken
        }, biz.BusinessId, conversationId, ct);
    }

    private string BuildMenuMessage()
    {
        var catalog = _activeMenu ?? MenuCatalog;

        if (catalog == MenuCatalog)
            return Msg.DemoMenu;

        return Msg.BuildMenu(catalog);
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
            Body = Msg.PagoMovilDetails(bank, payId, phone),
            PhoneNumberId = phoneNumberId,
            AccessToken = biz.AccessToken
        }, biz.BusinessId, conversationId, ct);

        // Message 2: Proof request
        await SendAsync(new OutgoingMessage
        {
            To = to,
            Body = Msg.PagoMovilProofRequest,
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
            return Msg.StillFillingForm;
        }

        return intent switch
        {
            RestaurantIntent.OrderCreate => BuildOrderReply(parsed, state),
            RestaurantIntent.HumanHandoff => Msg.HandoffInitiated,
            _ => Msg.WhatToOrder
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
            return Msg.WhatToOrder;

        if (string.IsNullOrWhiteSpace(state.DeliveryType))
            return Msg.PickupOrDelivery;

        // Observation prompt — after items+delivery, before checkout form
        // Skip if observation was already answered inline (e.g., "sin cebolla" in order text)
        if (!state.ObservationPromptSent && !state.ObservationAnswered)
        {
            state.ObservationPromptSent = true;

            if (!string.IsNullOrWhiteSpace(state.SpecialInstructions))
                return Msg.ObservationDetected(state.SpecialInstructions);

            return Msg.ObservationPrompt;
        }

        if (!state.CheckoutFormSent)
        {
            state.CheckoutFormSent = true;
            // Show order summary with prices, then checkout form
            return Msg.OrderSummaryWithTotal(state.Items) + "\n\n" + Msg.CheckoutForm;
        }

        return Msg.CheckoutDataReceived;
    }

    private async Task<string> FinalizeOrderIfPossibleAsync(
        ConversationFields state,
        string from,
        string phoneNumberId,
        BusinessContext businessContext,
        CancellationToken ct)
    {
        var businessId = businessContext.BusinessId;

        if (state.Items.Count == 0)
            return Msg.EmptyOrder;

        if (string.IsNullOrWhiteSpace(state.DeliveryType))
            return Msg.MissingDeliveryType;

        var missing = new List<string>();
        if (string.IsNullOrWhiteSpace(state.CustomerName)) missing.Add("\u2022 \ud83d\udc64 Nombre:");
        if (string.IsNullOrWhiteSpace(state.CustomerIdNumber)) missing.Add("\u2022 \ud83e\udeaa C\u00e9dula:");
        if (string.IsNullOrWhiteSpace(state.CustomerPhone)) missing.Add("\u2022 \ud83d\udcf1 Tel\u00e9fono:");
        if (string.IsNullOrWhiteSpace(state.Address)) missing.Add("\u2022 \ud83c\udfe1 Direcci\u00f3n:");
        if (string.IsNullOrWhiteSpace(state.PaymentMethod)) missing.Add("\u2022 \ud83d\udcb5 Pago:");
        if (!state.GpsPinReceived) missing.Add("\u2022 \ud83d\udccd Ubicaci\u00f3n GPS (pin)");

        if (missing.Count > 0)
        {
            return Msg.MissingFields(missing);
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

            PaymentProofMediaId = state.PaymentProofMediaId,
            PaymentProofSubmittedAtUtc = !string.IsNullOrWhiteSpace(state.PaymentProofMediaId) ? DateTime.UtcNow : null,

            Items = state.Items.Select(i =>
            {
                var unitPrice = i.UnitPrice;
                // Fallback to catalog lookup if price wasn't tracked in conversation
                if (unitPrice == 0m)
                {
                    var catalog = _activeMenu ?? MenuCatalog;
                    var entry = catalog.FirstOrDefault(m =>
                        m.Canonical.Equals(i.Name, StringComparison.OrdinalIgnoreCase));
                    unitPrice = entry?.Price ?? 0m;
                }

                return new WhatsAppSaaS.Domain.Entities.OrderItem
                {
                    Name = !string.IsNullOrWhiteSpace(i.Modifiers) ? $"{i.Name} ({i.Modifiers})" : i.Name,
                    Quantity = i.Quantity,
                    UnitPrice = unitPrice,
                    LineTotal = unitPrice * i.Quantity
                };
            }).ToList()
        };

        order.RecalculateTotal();

        await _orderRepository.AddOrderAsync(order, ct);

        // Staff notification: order confirmed
        if (_notificationService is not null)
        {
            var itemsSummary = string.Join(", ", state.Items.Select(i => $"{i.Quantity} {i.Name}"));
            var totalText = order.TotalAmount.HasValue ? $"${order.TotalAmount:0.00}" : "N/A";
            await _notificationService.NotifyOrderConfirmedAsync(
                businessContext, state.CustomerName ?? "?", itemsSummary, totalText, ct);
        }

        // Track order for post-confirm proof capture (pago_movil/divisas without proof)
        state.LastOrderId = order.Id;
        if (order.PaymentMethod is "pago_movil" or "divisas"
            && string.IsNullOrWhiteSpace(order.PaymentProofMediaId))
        {
            state.AwaitingPostConfirmProof = true;
        }

        var orderNumber = order.Id.ToString("N")[..8].ToUpperInvariant();

        var receipt = Msg.BuildReceipt(
            orderNumber,
            state.CustomerName!,
            state.CustomerIdNumber!,
            customerPhoneE164!,
            state.Items,
            state.SpecialInstructions,
            state.Address!,
            Msg.PaymentMethodText(state.PaymentMethod),
            state.DeliveryType!);

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

    internal static string Normalize(string input)
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

    internal static bool IsReorderRequest(string t)
    {
        var s = StripAccents(t);
        return s is "repetir" or "lo mismo" or "mismo pedido" or "repetir pedido"
            or "quiero lo mismo" or "lo de siempre" or "el mismo" or "la misma orden"
            || s.StartsWith("repetir ")
            || s.Contains("mismo pedido")
            || s.Contains("lo mismo de");
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
    {
        if (t is "humano" or "agente" or "asesor" or "persona" or "soporte"
            or "operador" or "asistente" or "ayuda")
            return true;

        var s = StripAccents(t);
        return s.Contains("hablar con alguien") || s.Contains("hablar con un humano")
            || s.Contains("hablar con una persona") || s.Contains("ayuda humana")
            || s.Contains("necesito ayuda") || s.Contains("quiero hablar con")
            || s.Contains("atencion humana") || s.Contains("agente humano")
            || s.Contains("operador humano") || s.Contains("soporte humano");
    }

    internal static bool IsNoObservation(string t)
    {
        var s = StripAccents(t);
        return s is "no" or "no tengo" or "ninguna" or "sin observaciones"
            or "nada" or "no gracias" or "ninguno" or "na" or "nop"
            or "no, gracias" or "sin obs";
    }

    internal static bool IsSuggestionAcceptance(string t)
    {
        var s = StripAccents(t);
        return s is "si" or "sí" or "dale" or "va" or "listo" or "ok"
            or "claro" or "por supuesto" or "bueno" or "sale" or "orale"
            or "si dale" or "dale si" or "claro que si" or "mete le"
            or "metele" or "agregalo" or "agregala" or "si porfa"
            or "si por favor" or "si, dale" or "si, porfa";
    }

    internal static bool IsSuggestionDecline(string t)
    {
        var s = StripAccents(t);
        return s is "no" or "no gracias" or "no, gracias" or "nop"
            or "asi esta bien" or "dejalo asi" or "nada mas"
            or "esta bien asi" or "sin mas nada" or "solo eso"
            or "con eso" or "no quiero" or "no gracias asi esta bien"
            or "asi" or "na" or "nel" or "paso";
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

        if (t.Contains("delivery") || t.Contains("domicilio") || t.Contains("envio") || t.Contains("envío"))
            return "delivery";

        if (t.Contains("pick") || t.Contains("pickup") || t.Contains("recoger")
            || t.Contains("retirar") || t.Contains("buscar"))
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
        // Look up unit price from catalog
        var catalog = ActiveCatalog ?? MenuCatalog;
        var entry = catalog.FirstOrDefault(m =>
            m.Canonical.Equals(name, StringComparison.OrdinalIgnoreCase));
        var unitPrice = entry?.Price ?? 0m;

        var existing = state.Items.FirstOrDefault(x => x.Name.Equals(name, StringComparison.OrdinalIgnoreCase));
        if (existing != null)
        {
            existing.Quantity += qty;
            if (existing.UnitPrice == 0m && unitPrice > 0m)
                existing.UnitPrice = unitPrice;
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
            state.Items.Add(new ConversationItemEntry { Name = name, Quantity = qty, Modifiers = modifiers, UnitPrice = unitPrice });
        }
    }

    // ──────────────────────────────────────────
    // AI Assistant: smart suggestions (upsell, combo)
    // ──────────────────────────────────────────

    private async Task TrySendSmartSuggestionAsync(
        ConversationFields state,
        string to, string phoneNumberId,
        BusinessContext biz, string conversationId, CancellationToken ct)
    {
        // Suppress if already suggested, declined, or in checkout/observation/payment stages
        if (state.UpsellSent || state.SuggestionDeclined) return;
        if (state.CheckoutFormSent || state.ObservationPromptSent) return;
        if (state.Items.Count == 0) return;

        // Priority 1: combo suggestion (close-to-combo detection)
        var (comboMsg, comboItem) = BuildComboSuggestion(state, biz.RestaurantType);
        if (comboMsg != null)
        {
            state.ComboSuggestionSent = true;
            state.UpsellSent = true;
            state.LastSuggestedItem = comboItem;
            state.ComboSuggestedCount++;
            await SendAsync(new OutgoingMessage
            {
                To = to, Body = comboMsg,
                PhoneNumberId = phoneNumberId, AccessToken = biz.AccessToken
            }, biz.BusinessId, conversationId, ct);
            return;
        }

        // Priority 2: restaurant-type-aware upsell addon
        var (upsellMsg, upsellItem) = BuildUpsellSuggestion(state, biz.RestaurantType);
        if (upsellMsg != null)
        {
            state.UpsellSent = true;
            state.AddonSuggestionSent = true;
            state.LastSuggestedItem = upsellItem;
            state.UpsellSuggestedCount++;
            await SendAsync(new OutgoingMessage
            {
                To = to, Body = upsellMsg,
                PhoneNumberId = phoneNumberId, AccessToken = biz.AccessToken
            }, biz.BusinessId, conversationId, ct);
        }
    }

    // ──────────────────────────────────────────
    // AI Assistant: upsell, combo, and resume builders
    // ──────────────────────────────────────────

    // Generic upsell pairing rules (fallback when no restaurant template)
    private static readonly Dictionary<string, string[]> UpsellPairings = new(StringComparer.OrdinalIgnoreCase)
    {
        ["hamburguesas"] = ["bebidas", "papas"],
        ["perros calientes"] = ["bebidas", "papas"],
        ["bebidas"] = ["hamburguesas", "papas"],
        ["papas"] = ["bebidas"],
        ["extras"] = ["bebidas"],
        ["salsas"] = ["bebidas"],
    };

    /// <summary>
    /// Resolves the active upsell pairings: restaurant template pairings take priority,
    /// then falls back to generic pairings.
    /// </summary>
    private static Dictionary<string, string[]> GetUpsellPairings(string? restaurantType)
    {
        if (restaurantType is not null)
        {
            var template = RestaurantTemplates.Get(restaurantType);
            if (template?.SuggestedUpsells.Count > 0)
                return template.SuggestedUpsells;
        }
        return UpsellPairings;
    }

    internal (string? message, string? itemName) BuildUpsellSuggestion(ConversationFields state, string? restaurantType = null)
    {
        var catalog = _activeMenu ?? MenuCatalog;
        if (catalog.Length == 0) return (null, null);

        var pairings = GetUpsellPairings(restaurantType);

        var orderedCategories = state.Items
            .Select(i => catalog.FirstOrDefault(m => m.Canonical.Equals(i.Name, StringComparison.OrdinalIgnoreCase)))
            .Where(m => m != null)
            .Select(m => m!.Category ?? "")
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var orderedNames = state.Items
            .Select(i => i.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Find a suggestion from paired categories not already in the order
        foreach (var cat in orderedCategories)
        {
            if (!pairings.TryGetValue(cat, out var targetCats)) continue;

            foreach (var targetCat in targetCats)
            {
                if (orderedCategories.Contains(targetCat)) continue;

                // Prefer mid-range items (not cheapest, not most expensive) for better acceptance
                var candidates = catalog
                    .Where(m => string.Equals(m.Category, targetCat, StringComparison.OrdinalIgnoreCase)
                             && !m.IsCombo
                             && !orderedNames.Contains(m.Canonical))
                    .OrderBy(m => m.Price)
                    .ToList();

                if (candidates.Count == 0) continue;

                // Pick the median-priced item (best value perception)
                var suggestion = candidates[candidates.Count / 2];

                if (suggestion != null)
                {
                    var msg = suggestion.Price > 0
                        ? Msg.UpsellWithPrice(suggestion.Canonical, suggestion.Price)
                        : Msg.Upsell(suggestion.Canonical);
                    return (msg, suggestion.Canonical);
                }
            }
        }

        return (null, null);
    }

    internal (string? message, string? itemName) BuildComboSuggestion(ConversationFields state, string? restaurantType = null)
    {
        var catalog = _activeMenu ?? MenuCatalog;
        if (catalog.Length == 0 || state.ComboSuggestionSent) return (null, null);

        var combos = catalog.Where(m => m.IsCombo).ToList();
        if (combos.Count == 0) return (null, null);

        // Don't suggest if already ordering a combo
        var hasCombo = state.Items.Any(i =>
            catalog.Any(m => m.IsCombo && m.Canonical.Equals(i.Name, StringComparison.OrdinalIgnoreCase)));
        if (hasCombo) return (null, null);

        var orderedNames = state.Items
            .Select(i => i.Name)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        // Close-to-combo detection: if template has combos with known components,
        // check if user is one item away from completing a combo
        if (restaurantType != null)
        {
            var template = RestaurantTemplates.Get(restaurantType);
            if (template != null)
            {
                var mainCats = template.DefaultCategories
                    .Where(c => !c.Name.Equals("Combos", StringComparison.OrdinalIgnoreCase)
                             && !c.Name.Equals("Bebidas", StringComparison.OrdinalIgnoreCase)
                             && !c.Name.Equals("Bebidas Frias", StringComparison.OrdinalIgnoreCase)
                             && !c.Name.Equals("Papas", StringComparison.OrdinalIgnoreCase)
                             && !c.Name.Equals("Extras", StringComparison.OrdinalIgnoreCase)
                             && !c.Name.Equals("Salsas", StringComparison.OrdinalIgnoreCase)
                             && !c.Name.Equals("Acompanamientos", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var drinkCats = template.DefaultCategories
                    .Where(c => c.Name.Contains("Bebida", StringComparison.OrdinalIgnoreCase))
                    .ToList();
                var sideCats = template.DefaultCategories
                    .Where(c => c.Name.Equals("Acompanamientos", StringComparison.OrdinalIgnoreCase)
                             || c.Name.Equals("Papas", StringComparison.OrdinalIgnoreCase)
                             || c.Name.Equals("Extras", StringComparison.OrdinalIgnoreCase))
                    .ToList();

                // Check if user has a main but no drink — suggest drink to "complete combo"
                var orderedCatNames = state.Items
                    .Select(i => catalog.FirstOrDefault(m => m.Canonical.Equals(i.Name, StringComparison.OrdinalIgnoreCase)))
                    .Where(m => m != null)
                    .Select(m => m!.Category ?? "")
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);

                bool hasMain = mainCats.Any(mc => orderedCatNames.Contains(mc.Name.ToLowerInvariant()));
                bool hasDrink = drinkCats.Any(dc => orderedCatNames.Contains(dc.Name.ToLowerInvariant()));
                bool hasSide = sideCats.Any(sc => orderedCatNames.Contains(sc.Name.ToLowerInvariant()));

                // Has main + side but no drink → suggest drink to complete combo
                if (hasMain && hasSide && !hasDrink && combos.Count > 0)
                {
                    var bestCombo = combos.OrderBy(c => c.Price).First();
                    var drink = catalog
                        .Where(m => drinkCats.Any(dc => string.Equals(m.Category, dc.Name, StringComparison.OrdinalIgnoreCase))
                                 && !orderedNames.Contains(m.Canonical) && !m.IsCombo)
                        .OrderBy(m => m.Price)
                        .FirstOrDefault();
                    if (drink != null && bestCombo.Price > 0)
                    {
                        return (Msg.ComboMissing(drink.Canonical, bestCombo.Canonical, bestCombo.Price), drink.Canonical);
                    }
                }

                // Has main + drink but no side → suggest side to complete combo
                if (hasMain && hasDrink && !hasSide && sideCats.Count > 0 && combos.Count > 0)
                {
                    var bestCombo = combos.OrderBy(c => c.Price).First();
                    var side = catalog
                        .Where(m => sideCats.Any(sc => string.Equals(m.Category, sc.Name, StringComparison.OrdinalIgnoreCase))
                                 && !orderedNames.Contains(m.Canonical) && !m.IsCombo)
                        .OrderBy(m => m.Price)
                        .FirstOrDefault();
                    if (side != null && bestCombo.Price > 0)
                    {
                        return (Msg.ComboMissing(side.Canonical, bestCombo.Canonical, bestCombo.Price), side.Canonical);
                    }
                }
            }
        }

        // Fallback: generic combo suggestion based on price savings
        var bestFallbackCombo = combos.OrderByDescending(c => c.Price).First();

        var itemTotal = state.Items.Sum(i =>
        {
            var entry = catalog.FirstOrDefault(m => m.Canonical.Equals(i.Name, StringComparison.OrdinalIgnoreCase));
            return (entry?.Price ?? 0) * i.Quantity;
        });

        if (bestFallbackCombo.Price > 0 && itemTotal > 0 && bestFallbackCombo.Price < itemTotal)
        {
            var savings = itemTotal - bestFallbackCombo.Price;
            return (Msg.ComboUpgrade(bestFallbackCombo.Canonical, savings), bestFallbackCombo.Canonical);
        }

        return bestFallbackCombo.Price > 0
            ? (Msg.ComboUpgradeSimple(bestFallbackCombo.Canonical), bestFallbackCombo.Canonical)
            : (null, null);
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
        public decimal Price { get; init; }
    }

    // Active catalog for current request (set by ProcessAsync, used by static helpers)
    [ThreadStatic]
    internal static MenuEntry[]? ActiveCatalog;

    // Catalog: future menu system will load from DB. For now, realistic burger-restaurant demo.
    internal static readonly MenuEntry[] MenuCatalog =
    {
        // ── Hamburguesas ──
        new() { Canonical = "Hamburguesa Clasica", Aliases = new[] { "hamburguesa", "hamburguesas", "hamburgesa", "hamburgesas",
            "hamburguea", "hamburgueas", "hamburgues", "hamburguez", "hamburgue",
            "hamburga", "hamburgs", "hambur", "burger", "burgers", "burguer", "burguers",
            "hamburguesita", "hamburguesitas", "hamburgueaas", "hamburgueaa",
            "hambuguesa", "hambuguesas", "hamburgusa", "hamburgusas",
            "clasica", "hamburguesa clasica" },
            Category = "hamburguesas", Price = 6.50m },
        new() { Canonical = "Hamburguesa Doble", Aliases = new[] { "doble", "hamburguesa doble", "double", "hamb doble" },
            Category = "hamburguesas", Price = 8.50m },
        new() { Canonical = "Hamburguesa Bacon", Aliases = new[] { "bacon", "hamburguesa bacon", "hamburguesa con bacon",
            "con tocineta", "hamb bacon" },
            Category = "hamburguesas", Price = 9.00m },
        new() { Canonical = "Hamburguesa Especial", Aliases = new[] { "especial", "hamburguesa especial", "la especial",
            "hamb especial" },
            Category = "hamburguesas", Price = 10.50m },
        new() { Canonical = "Hamburguesa BBQ", Aliases = new[] { "bbq", "hamburguesa bbq", "barbacoa", "hamb bbq" },
            Category = "hamburguesas", Price = 9.50m },

        // ── Perros Calientes ──
        new() { Canonical = "Perro Clasico", Aliases = new[] { "perro", "perros", "perro caliente", "perros calientes",
            "hot dog", "hotdog", "hotdogs", "hot dogs", "perro clasico" },
            Category = "perros calientes", Price = 4.50m },
        new() { Canonical = "Perro Especial", Aliases = new[] { "perro especial" },
            Category = "perros calientes", Price = 6.00m },
        new() { Canonical = "Perro con Queso", Aliases = new[] { "perro con queso", "perro queso" },
            Category = "perros calientes", Price = 5.50m },

        // ── Papas (Medianas first — "papas" defaults to medium) ──
        new() { Canonical = "Papas Medianas", Aliases = new[] { "papas", "papa", "papaas", "papaz", "papss",
            "papas medianas", "papas mediana",
            "papas fritas", "fritas", "french fries", "fries", "papitas fritas" },
            Category = "papas", Price = 3.50m },
        new() { Canonical = "Papas Pequenas", Aliases = new[] { "papas pequenas", "papas pequena", "papitas", "papita",
            "papas chicas" },
            Category = "papas", Price = 2.50m },
        new() { Canonical = "Papas Grandes", Aliases = new[] { "papas grandes", "papas grande" },
            Category = "papas", Price = 4.50m },
        new() { Canonical = "Papas con Queso", Aliases = new[] { "papas con queso", "papas queso" },
            Category = "papas", Price = 5.50m },
        new() { Canonical = "Papas Mixtas", Aliases = new[] { "papas mixtas", "papas mixta", "mixtas" },
            Category = "papas", Price = 6.50m },

        // ── Bebidas ──
        new() { Canonical = "Coca Cola", Aliases = new[] { "coca cola", "cocacola", "cocacolas", "coca", "cocas",
            "coca-cola", "coca colas", "refresco", "refrescos", "gaseosa", "gaseosas", "soda",
            "coka", "cokas", "coka cola", "coca cola 355", "coca pequena" },
            Category = "bebidas", Price = 1.50m },
        new() { Canonical = "Coca Cola 1L", Aliases = new[] { "coca grande", "coca cola grande", "coca litro",
            "coca cola 1l" },
            Category = "bebidas", Price = 2.50m },
        new() { Canonical = "Pepsi", Aliases = new[] { "pepsi", "pepsis", "pepsi 355" },
            Category = "bebidas", Price = 1.50m },
        new() { Canonical = "Te Frio", Aliases = new[] { "te frio", "te", "te helado", "iced tea", "te fri" },
            Category = "bebidas", Price = 1.75m },
        new() { Canonical = "Agua", Aliases = new[] { "agua", "aguas", "aguita", "botella de agua", "water" },
            Category = "bebidas", Price = 1.00m },
        new() { Canonical = "Malta", Aliases = new[] { "malta", "maltas", "maltita", "maltin" },
            Category = "bebidas", Price = 1.50m },

        // ── Combos ──
        new() { Canonical = "Combo Clasico", Aliases = new[] { "combo", "combos", "combo clasico", "combo 1",
            "promo", "promocion" },
            Category = "combos", IsCombo = true, Price = 8.50m },
        new() { Canonical = "Combo Doble", Aliases = new[] { "combo doble", "combo 2" },
            Category = "combos", IsCombo = true, Price = 10.50m },
        new() { Canonical = "Combo Bacon", Aliases = new[] { "combo bacon", "combo 3" },
            Category = "combos", IsCombo = true, Price = 11.00m },
        new() { Canonical = "Combo Perro", Aliases = new[] { "combo perro", "combo hot dog", "combo perro caliente" },
            Category = "combos", IsCombo = true, Price = 6.50m },

        // ── Extras ──
        new() { Canonical = "Extra Queso", Aliases = new[] { "extra queso", "queso extra", "mas queso" },
            Category = "extras", Price = 1.00m },
        new() { Canonical = "Extra Tocineta", Aliases = new[] { "extra tocineta", "tocineta extra",
            "extra bacon", "mas tocineta" },
            Category = "extras", Price = 1.50m },
        new() { Canonical = "Extra Carne", Aliases = new[] { "extra carne", "carne extra", "doble carne", "mas carne" },
            Category = "extras", Price = 2.50m },
        new() { Canonical = "Extra Huevo", Aliases = new[] { "extra huevo", "huevo extra", "con huevo", "mas huevo" },
            Category = "extras", Price = 1.00m },

        // ── Salsas ──
        new() { Canonical = "Salsa Ajo", Aliases = new[] { "salsa ajo", "salsa de ajo" },
            Category = "salsas", Price = 0.50m },
        new() { Canonical = "Salsa Tartara", Aliases = new[] { "salsa tartara", "tartara" },
            Category = "salsas", Price = 0.50m },
        new() { Canonical = "Salsa Picante", Aliases = new[] { "salsa picante", "picante" },
            Category = "salsas", Price = 0.50m },
        new() { Canonical = "Salsa Rosada", Aliases = new[] { "salsa rosada", "rosada", "salsa rosa" },
            Category = "salsas", Price = 0.50m },
    };

    private async Task<MenuEntry[]> LoadBusinessMenuAsync(Guid businessId, CancellationToken ct)
    {
        if (_menuRepository is null) return MenuCatalog;

        try
        {
            var dbItems = await _menuRepository.GetAvailableItemsAsync(businessId, ct);
            if (dbItems.Count == 0) return MenuCatalog;

            return dbItems.Select(i => new MenuEntry
            {
                Canonical = i.Name,
                Aliases = i.Aliases.Select(a => a.Alias.ToLowerInvariant()).ToArray(),
                Category = i.Category?.Name?.ToLowerInvariant(),
                Price = i.Price
            }).ToArray();
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to load business menu from DB for {BusinessId}, using demo catalog", businessId);
            return MenuCatalog;
        }
    }

    internal static string? NormalizeMenuItemName(string rawItem)
        => NormalizeMenuItemName(rawItem, ActiveCatalog ?? MenuCatalog);

    internal static string? NormalizeMenuItemName(string rawItem, MenuEntry[] catalog)
    {
        if (string.IsNullOrWhiteSpace(rawItem)) return null;

        var t = rawItem.Trim().ToLowerInvariant();
        t = StripAccents(t);
        var tNoS = t.EndsWith("s") ? t[..^1] : t;

        // Pass 1: exact matches (canonical, aliases) — no ambiguity
        foreach (var entry in catalog)
        {
            var canonLower = StripAccents(entry.Canonical.ToLowerInvariant());
            if (t == canonLower || tNoS == canonLower)
                return entry.Canonical;

            var canonNoS = canonLower.EndsWith("s") ? canonLower[..^1] : canonLower;
            if (t == canonNoS || tNoS == canonNoS)
                return entry.Canonical;

            foreach (var alias in entry.Aliases)
            {
                var a = StripAccents(alias);
                if (t == a || tNoS == a)
                    return entry.Canonical;
            }
        }

        // Pass 2: fuzzy matches (prefix, Levenshtein) — only if no exact match
        if (t.Length >= 5)
        {
            foreach (var entry in catalog)
            {
                var canonLower = StripAccents(entry.Canonical.ToLowerInvariant());

                // Prefix match
                if (canonLower.StartsWith(t) || canonLower.StartsWith(tNoS))
                    return entry.Canonical;

                // Levenshtein distance for close typos
                var maxDist = t.Length >= 8 ? 3 : 2;

                if (canonLower.Length >= 4 && LevenshteinDistance(t, canonLower) <= maxDist)
                    return entry.Canonical;

                foreach (var alias in entry.Aliases)
                {
                    var a = StripAccents(alias);
                    if (a.Length >= 4 && LevenshteinDistance(t, a) <= maxDist)
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

    internal static bool TryParseCheckoutForm(string rawText, out CheckoutForm form, bool isMerge = false)
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

        // In merge mode (checkout already sent), accept even a single field
        return filled >= (isMerge ? 1 : 2);
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

        if (p.Contains("pago") && (p.Contains("mov") || p.Contains("moc") || p.Contains("mobi")))
            return "pago_movil";
        if (p is "pm" or "pagomovil" or "pago movil" or "pago mocil" or "pago mobil")
            return "pago_movil";
        if (p.Contains("divis") || p.Contains("usd") || p.Contains("dolar"))
            return "divisas";
        if (p.Contains("efect") || p is "cash")
            return "efectivo";
        if (p.Contains("zelle"))
            return "zelle";

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
        => Msg.MissingFields(missing);

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

        if (t.Contains("delivery") || t.Contains("domicilio") || t.Contains("envio") || t.Contains("envío"))
            deliveryType = "delivery";
        else if (t.Contains("pick up") || t.Contains("pickup") || t.Contains("recoger")
                 || t.Contains("retirar") || t.Contains("buscar"))
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

    // Convert Spanish word numbers to digits: "una hamburguesa" → "1 hamburguesa"
    // Converts at start of line/segment or after comma/conjunction " y "
    internal static string ConvertWordNumbersToDigits(string text)
    {
        // Process each line independently to preserve newlines
        var lines = text.Split('\n');
        for (int i = 0; i < lines.Length; i++)
        {
            // Replace word numbers at: start of line, after comma, after " y "
            var line = lines[i];
            // Start of line
            line = Regex.Replace(line,
                @"^\s*(un[ao]?|dos|tres|cuatro|cinco|seis|siete|ocho|nueve|diez)\s+",
                m => WordToDigit(m.Groups[1].Value) + " ",
                RegexOptions.IgnoreCase);
            // After comma
            line = Regex.Replace(line,
                @"(?<=,)\s*(un[ao]?|dos|tres|cuatro|cinco|seis|siete|ocho|nueve|diez)\s+",
                m => WordToDigit(m.Groups[1].Value) + " ",
                RegexOptions.IgnoreCase);
            // After " y "
            line = Regex.Replace(line,
                @"(?<=\s+y\s+)(un[ao]?|dos|tres|cuatro|cinco|seis|siete|ocho|nueve|diez)\s+",
                m => WordToDigit(m.Groups[1].Value) + " ",
                RegexOptions.IgnoreCase);
            lines[i] = line;
        }
        return string.Join("\n", lines);
    }

    private static string WordToDigit(string word) => word.ToLowerInvariant() switch
    {
        "un" or "una" or "uno" => "1",
        "dos" => "2",
        "tres" => "3",
        "cuatro" => "4",
        "cinco" => "5",
        "seis" => "6",
        "siete" => "7",
        "ocho" => "8",
        "nueve" => "9",
        "diez" => "10",
        _ => word
    };

    // Core order text parser: splits by conjunctions, handles "N items con/y other items"
    // Supports compound orders like "3 hamburguesas cada una con papas y refresco"
    // where the parent quantity (3) propagates to companion items (papas, refresco).
    internal static List<ParsedItem> ParseOrderText(string rawText)
    {
        var results = new List<ParsedItem>();
        var text = rawText.Trim();

        // Noise words to strip (greetings + filler) — preserve newlines for segment splitting
        var noisePattern = @"\b(hola|buenas?|buenos?\s+d[ií]as?|buenas\s+tardes|buenas\s+noches|hey|epa|saludos|por\s*favor|porfavor|porfa|plis|please|gracias|quiero|quisiera|me\s+das?|dame|necesito|pedimos|para\s+comer|mand[ae]\s*me)\b";
        var cleaned = Regex.Replace(text, noisePattern, " ", RegexOptions.IgnoreCase);
        // Collapse spaces on each line but preserve newlines
        cleaned = string.Join("\n", cleaned.Split('\n').Select(line => Regex.Replace(line, @"[ \t]+", " ").Trim()));
        cleaned = cleaned.Trim();

        // Convert Spanish word numbers to digits at the start of segments
        cleaned = ConvertWordNumbersToDigits(cleaned);

        // Strip delivery/pickup keywords — already captured by TryParseQuickOrder
        cleaned = Regex.Replace(cleaned,
            @"\b(para\s+)?(delivery|pick\s*up|domicilio|a\s+domicilio|env[ií]o|para\s+retirar|voy\s+a\s+buscar|para\s+llevar)\b",
            " ", RegexOptions.IgnoreCase);
        cleaned = string.Join("\n", cleaned.Split('\n').Select(line => Regex.Replace(line, @"[ \t]+", " ").Trim()));
        cleaned = cleaned.Trim();

        // Detect "cada una/uno" propagation pattern — strip phrase, remember to propagate
        bool propagateQty = Regex.IsMatch(cleaned, @"\bcada\s+un[ao]?\b", RegexOptions.IgnoreCase)
                         || Regex.IsMatch(text, @"\b(tod[ao]s?\s+con)\b", RegexOptions.IgnoreCase);
        cleaned = Regex.Replace(cleaned, @"\bcada\s+un[ao]?\b", " ", RegexOptions.IgnoreCase);
        cleaned = string.Join("\n", cleaned.Split('\n').Select(line => Regex.Replace(line, @"[ \t]+", " ").Trim()))
                      .Trim();

        // First, split into segments on "con [menu-item]" and " y " boundaries
        var segments = SplitIntoOrderSegments(cleaned);

        int leadQty = 0; // the quantity from the first segment (for propagation)

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
                    if (results.Count == 1) leadQty = qty; // remember first item's quantity
                    continue;
                }
            }

            // Handle "otra/otro [item]" — means quantity=1 with possible modifier
            var otraMatch = Regex.Match(s, @"^otr[ao]s?\s+(.+)$", RegexOptions.IgnoreCase);
            if (otraMatch.Success)
            {
                var otraItem = ExtractItemAndModifiers(otraMatch.Groups[1].Value.Trim());
                if (otraItem != null)
                {
                    results.Add(otraItem);
                    continue;
                }
            }

            // No quantity prefix — try as a single item
            var singleItem = ExtractItemAndModifiers(s);
            if (singleItem != null)
            {
                // Propagate lead quantity to companion items when pattern detected
                if (propagateQty && leadQty > 1 && results.Count > 0)
                    singleItem.Quantity = leadQty;
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

        // Split on newlines first, then commas — users send multi-line WhatsApp messages
        var lineParts = text.Split(new[] { '\n', '\r' }, StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

        foreach (var lp in lineParts)
        {
            // Then split on commas
            var commaParts = lp.Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries);

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

        var after = m.Groups[2].Value.Trim();

        // "con extra X" / "con todo" / "sin X" are modifiers, never split
        if (Regex.IsMatch(after, @"^(extra\s+|todo\b|sin\s+)", RegexOptions.IgnoreCase))
            return null;

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
            var before = m.Groups[1].Value.Trim();
            // "con [menu-item]" — split into two segments
            return new List<string> { before, after };
        }

        // Not a menu item link — keep as modifier
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
            @"\s+(sin\s+.+|extra\s+.+|con\s+extra\s+.+|con\s+todo|mitad\s+.+|bien\s+(?:cocid|asad|hech|tostad).+|al\s+punto|termino\s+\w+|doble\s+\w+)$",
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
