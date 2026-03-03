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
        public bool RequestedDetailsForm { get; set; }
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

                    if (message.Type == "location")
                    {
                        if (state.Items.Count > 0 && state.RequestedDetailsForm)
                        {
                            state.DetailsReceived = true;

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
📍 Ubicación GPS:

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

        var order = new Order
        {
            From = from,
            PhoneNumberId = phoneNumberId,
            DeliveryType = state.DeliveryType,
            CreatedAtUtc = DateTime.UtcNow,
            Items = state.Items.Select(i => new WhatsAppSaaS.Domain.Entities.OrderItem
            {
                Name = i.Name,
                Quantity = i.Quantity
            }).ToList()
        };

        await _orderRepository.AddOrderAsync(order, ct);

        var receipt =
$@"🧾 *PEDIDO CONFIRMADO* ✅

Items: {string.Join(", ", state.Items.Select(i => $"{i.Quantity} {i.Name}"))}
Tipo: {state.DeliveryType}

Gracias 🙌";

        state.Items.Clear();
        state.DeliveryType = null;
        state.RequestedDetailsForm = false;
        state.DetailsReceived = false;

        return receipt;
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
