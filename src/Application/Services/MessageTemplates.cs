using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WhatsAppSaaS.Application.Interfaces;

namespace WhatsAppSaaS.Application.Services;

/// <summary>
/// Centralized conversational copy for WhatsApp bot messages.
/// All user-facing text lives here for consistency and easy tuning.
/// </summary>
internal static class Msg
{
    // ── Greeting ──

    internal static string DefaultGreeting(string businessName)
        => $"Hola, bienvenido a *{businessName}* \ud83d\udc4b";

    internal static string MenuPrompt
        => "\ud83c\udf7d\ufe0f \u00bfQu\u00e9 deseas ordenar?";

    // ── Menu ──

    internal static string DemoMenu
        => "\ud83d\udccb *MEN\u00da (DEMO)*\n\n1. Hamburguesa\n2. Coca Cola\n3. Papas\n\n_Pr\u00f3ximamente: men\u00fa completo del restaurante._";

    internal static string BuildMenu(IReadOnlyList<WebhookProcessor.MenuEntry> catalog)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\ud83d\udccb *MEN\u00da*");
        sb.AppendLine();

        // Group by category if available
        var groups = catalog
            .GroupBy(i => i.Category ?? "")
            .OrderBy(g => g.Key)
            .ToList();

        var useCategories = groups.Count > 1 || (groups.Count == 1 && !string.IsNullOrWhiteSpace(groups[0].Key));

        foreach (var g in groups)
        {
            if (useCategories && !string.IsNullOrWhiteSpace(g.Key))
            {
                var catName = char.ToUpper(g.Key[0]) + g.Key[1..];
                sb.AppendLine($"*{catName}*");
            }

            foreach (var item in g)
            {
                var price = item.Price > 0 ? $"  ${item.Price:0.00}" : "";
                sb.AppendLine($"  \u2022 {item.Canonical}{price}");
            }

            sb.AppendLine();
        }

        return sb.ToString().TrimEnd();
    }

    // ── Order flow ──

    internal static string WhatToOrder
        => "\ud83c\udf7d\ufe0f \u00bfQu\u00e9 deseas ordenar?";

    internal static string PickupOrDelivery
        => "\u00bfC\u00f3mo lo quieres?\n\n\ud83d\ude97 *delivery* \u2014 te lo llevamos\n\ud83c\udfe0 *pick up* \u2014 lo recoges en tienda";

    internal static string ObservationPrompt
        => "\u270d\ufe0f Si tu pedido tiene una *observaci\u00f3n especial*, escr\u00edbela ahora.\nEjemplo: _sin cebolla, extra queso, sin hielo_\n\nSi no tienes, responde *NO*.";

    internal static string ObservationDetected(string obs)
        => $"\u270d\ufe0f *Observaci\u00f3n detectada:* _{obs}_\n\n\u00bfDeseas agregar otra? Si no, responde *NO*.";

    // ── Checkout form ──

    internal static string CheckoutForm
        => "Para finalizar env\u00edanos:\n\n"
         + "\ud83d\udc64 *Nombre:*\n"
         + "\ud83e\udeaa *C\u00e9dula:*\n"
         + "\ud83d\udcf1 *Tel\u00e9fono:*\n"
         + "\ud83c\udfe1 *Direcci\u00f3n:*\n"
         + "\ud83d\udcb5 *Pago (PAGO M\u00d3VIL / EFECTIVO / DIVISAS / ZELLE):*\n"
         + "\ud83d\udccd *Ubicaci\u00f3n GPS:* (manda el pin)\n"
         + "\u2705 *OBLIGATORIO*\n\n"
         + "Puedes responder copiando y pegando esta planilla\n"
         + "o enviando los datos en l\u00edneas separadas.\n\n"
         + "Luego escribe *CONFIRMAR*.";

    internal static string CheckoutDataReceived
        => "\u2705 Datos recibidos. Escribe *CONFIRMAR* para finalizar.";

    internal static string MissingFields(List<string> missing)
    {
        var sb = new StringBuilder();
        sb.AppendLine("A\u00fan falta informaci\u00f3n para confirmar.");
        sb.AppendLine();
        foreach (var f in missing)
            sb.AppendLine(f);
        sb.AppendLine();
        sb.AppendLine("Puedes responder copiando y pegando esta planilla");
        sb.AppendLine("o enviando los datos en l\u00edneas separadas.");
        sb.AppendLine();
        sb.Append("Luego escribe *CONFIRMAR*.");
        return sb.ToString();
    }

    // ── GPS & payment evidence ──

    internal static string GpsReceived
        => "\u2705 Ubicaci\u00f3n recibida. Escribe *CONFIRMAR* para finalizar.";

    internal static string PaymentProofReceived
        => "\u2705 Comprobante recibido. Verificaremos el pago y te confirmamos.";

    internal static string PagoMovilDetails(string bank, string id, string phone)
        => $"\ud83d\udcb3 *DATOS PARA PAGO M\u00d3VIL*\n\n"
         + $"\u2022 *Banco:* {bank}\n"
         + $"\u2022 *C.I./RIF:* {id}\n"
         + $"\u2022 *Tel\u00e9fono:* {phone}";

    internal static string PagoMovilProofRequest
        => "Env\u00eda el *comprobante* (foto del pago) \ud83d\udcf8";

    internal static string DivisasProofRequest
        => "Para *DIVISAS*, env\u00eda una *foto de los billetes* \ud83d\udcf8";

    // ── Order confirmation / receipt ──

    internal static string EmptyOrder
        => "No hay productos en tu pedido. Env\u00eda lo que deseas pedir.";

    internal static string MissingDeliveryType
        => "\u00bfEs *pick up* o *delivery*?";

    internal static string BuildReceipt(
        string orderNumber,
        string customerName,
        string customerIdNumber,
        string customerPhone,
        IReadOnlyList<ConversationItemEntry> items,
        string? specialInstructions,
        string address,
        string paymentText,
        string deliveryType)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\u2705 *PEDIDO CONFIRMADO*");
        sb.AppendLine($"\ud83e\uddfe Pedido: #{orderNumber}");
        sb.AppendLine();

        // Customer info
        sb.AppendLine($"\ud83d\udc64 Nombre: {customerName}");
        sb.AppendLine($"\ud83e\udeaa C\u00e9dula: {customerIdNumber}");
        sb.AppendLine($"\ud83d\udcf1 Tel\u00e9fono: {customerPhone}");
        sb.AppendLine();

        // Items
        var itemsText = string.Join(", ", items.Select(i => FormatItemText(i)));
        sb.AppendLine($"\ud83c\udf7d\ufe0f Pedido: {itemsText}");

        // Special instructions
        if (!string.IsNullOrWhiteSpace(specialInstructions))
            sb.AppendLine($"\u270d\ufe0f Observaci\u00f3n: {specialInstructions}");

        sb.AppendLine($"\ud83c\udfe1 Direcci\u00f3n: {address}");
        sb.AppendLine($"\ud83d\udcb5 Pago: {paymentText}");
        sb.AppendLine($"\ud83d\ude97 {FormatDeliveryType(deliveryType)}");

        sb.AppendLine();
        sb.Append("Gracias \ud83d\ude4c");

        return sb.ToString();
    }

    private static string FormatItemText(ConversationItemEntry item)
    {
        var text = $"{item.Quantity} {item.Name}";
        if (!string.IsNullOrWhiteSpace(item.Modifiers))
            text += $" ({item.Modifiers})";
        return text;
    }

    private static string FormatDeliveryType(string dt)
        => dt switch
        {
            "delivery" => "Delivery",
            "pickup" => "Pick up en tienda",
            _ => dt
        };

    // ── Order modifications ──

    internal static string ItemAdded(int qty, string name)
        => $"\u2705 Listo, agregu\u00e9 *{qty} {name}* a tu pedido. Escribe *CONFIRMAR* para finalizar.";

    internal static string ItemRemoved(string name)
        => $"\u2705 Listo, elimin\u00e9 *{name}* del pedido.";

    internal static string ItemReduced(int remaining, string name)
        => $"\u2705 Listo, ahora tienes *{remaining} {name}*. Escribe *CONFIRMAR* para finalizar.";

    internal static string ItemNotFound(string name)
        => $"No tienes *{name}* en el pedido.";

    internal static string ItemReplaced(int qty, string name)
        => $"\u2705 Listo, ahora son *{qty} {name}*. Escribe *CONFIRMAR* para finalizar.";

    internal static string EmptyCart
        => "\n\nTu pedido est\u00e1 vac\u00edo. \u00bfQu\u00e9 deseas ordenar?";

    internal static string ConfirmPrompt
        => "Escribe *CONFIRMAR* para finalizar.";

    // ── Human handoff ──

    internal static string HandoffInitiated
        => "Entendido. Tu conversaci\u00f3n fue derivada a un agente humano.\n\nUn miembro de nuestro equipo te atender\u00e1 en breve \ud83d\ude4f";

    internal static string HandoffWaiting
        => "Tu consulta est\u00e1 siendo atendida por nuestro equipo. Te responderemos pronto.";

    // ── Checkout flow: fill-in-the-form ──

    internal static string StillFillingForm
        => "Env\u00eda los datos que faltan y luego escribe *CONFIRMAR*.";

    // ── Business hours / closed state ──

    internal static string BusinessClosed(string businessName, string schedule)
        => $"Hola, gracias por escribir a *{businessName}*.\n\n"
         + $"En este momento estamos *cerrados*.\n\n"
         + $"\ud83d\udd52 *Horario:* {schedule}\n\n"
         + "Te esperamos en nuestro horario de atencion.";

    internal static string BusinessClosedNoSchedule(string businessName)
        => $"Hola, gracias por escribir a *{businessName}*.\n\n"
         + "En este momento estamos *cerrados*.\n\n"
         + "Te esperamos pronto.";

    // ── Reorder ──

    internal static string ReorderOffer(string itemsSummary)
        => $"\ud83d\udd01 Tu ultimo pedido fue:\n{itemsSummary}\n\n"
         + "\u00bfDeseas *repetir* este pedido? Responde *SI* o elige algo diferente.";

    internal static string ReorderConfirmed
        => "\u2705 Pedido anterior cargado. Escribe *CONFIRMAR* para finalizar o modifica lo que desees.";

    internal static string NoReorderAvailable
        => "No encontre un pedido anterior. \u00bfQue deseas ordenar?";

    // ── AI Assistant: upsell / combo / resume ──

    internal static string Upsell(string suggestedItem)
        => $"Tambien te puedo agregar *{suggestedItem}* si quieres.";

    internal static string UpsellWithPrice(string suggestedItem, decimal price)
        => $"Te agrego una *{suggestedItem}* por solo *${price:0.00}*?";

    internal static string ComboUpgrade(string comboName, decimal savings)
        => $"Por *${savings:0.00} mas* puedes llevar el *{comboName}*. Te lo agrego?";

    internal static string ComboUpgradeSimple(string comboName)
        => $"Tambien tenemos el *{comboName}*. Te interesa?";

    internal static string ComboMissing(string missingItem, string comboName, decimal comboPrice)
        => $"Si le agregas una *{missingItem}* completas el *{comboName}* por *${comboPrice:0.00}*. Te lo armo?";

    internal static string SuggestionAccepted(string item)
        => $"Listo, agregue *{item}* a tu pedido.";

    internal static string SuggestionDeclined
        => "Perfecto, asi queda tu pedido. Escribe *CONFIRMAR* para finalizar.";

    internal static string AbandonedResume(string itemsSummary)
        => $"Hola de nuevo! Tienes un pedido pendiente:\n{itemsSummary}\n\nQuieres continuar o empezar de nuevo?";

    // ── Saved address ──

    internal static string SavedAddressOffer(string address)
        => $"\ud83c\udfe1 Tu ultima direccion fue:\n_{address}_\n\n"
         + "\u00bfDeseas usar esta misma direccion? Responde *SI* o envia una nueva.";

    // ── Staff notifications ──

    internal static string NotifyNewOrder(string customerName, string customerPhone, string total)
        => "\ud83d\udd14 *Nuevo pedido recibido*\n\n"
         + $"Cliente: {customerName}\n"
         + $"Tel\u00e9fono: {customerPhone}\n"
         + $"Total: {total}\n\n"
         + "Ver en dashboard para detalles.";

    internal static string NotifyOrderConfirmed(string customerName, string itemsSummary, string total)
        => "\u2705 *Pedido confirmado*\n\n"
         + $"Cliente: {customerName}\n"
         + $"Items: {itemsSummary}\n"
         + $"Total: {total}";

    internal static string NotifyHumanHandoff(string customerPhone)
        => "\u26a0\ufe0f *Cliente solicita atenci\u00f3n humana*\n\n"
         + $"Cliente: {customerPhone}\n\n"
         + "Ver conversaci\u00f3n en dashboard.";

    internal static string NotifyCustomerWaiting(string customerPhone)
        => "\u23f3 *Cliente esperando respuesta humana*\n\n"
         + $"Cliente: {customerPhone}";

    // ── Payment text for receipt ──

    internal static string PaymentMethodText(string? method)
        => method switch
        {
            "pago_movil" => "PAGO M\u00d3VIL (pendiente verificaci\u00f3n)",
            "divisas" => "DIVISAS (pendiente verificaci\u00f3n)",
            "efectivo" => "EFECTIVO",
            "zelle" => "ZELLE (pendiente verificaci\u00f3n)",
            _ => "EFECTIVO"
        };
}
