using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;

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

    internal static string ReturningGreeting(string businessName, string customerName)
        => $"Hola de nuevo, *{customerName}*! \ud83d\udc4b Bienvenido a *{businessName}*";

    internal static string MenuPrompt
        => "\ud83c\udf7d\ufe0f \u00bfQu\u00e9 deseas ordenar?\n\n"
         + "_Ejemplo:_\n"
         + "_2 hamburguesas cl\u00e1sicas_\n"
         + "_1 papa mediana_\n"
         + "_2 Coca Cola_\n\n"
         + "_Escribe un producto por l\u00ednea_";

    internal static string MenuPdfPrompt
        => "Perfecto. Env\u00edame tu pedido.\n\n"
         + "_Ejemplo:_\n"
         + "_2 hamburguesas cl\u00e1sicas_\n"
         + "_1 papa mediana_\n"
         + "_2 Coca Cola_\n\n"
         + "_Escribe un producto por l\u00ednea_";

    // ── Menu ──

    internal static string DemoMenu
        => "\ud83d\udccb *MEN\u00da (DEMO)*\n\n"
         + "*Hamburguesas*\n"
         + "  \u2022 Hamburguesa Clasica  $6.50\n"
         + "  \u2022 Hamburguesa Doble  $8.50\n"
         + "  \u2022 Hamburguesa Bacon  $9.00\n"
         + "  \u2022 Hamburguesa Especial  $10.50\n"
         + "  \u2022 Hamburguesa BBQ  $9.50\n\n"
         + "*Perros Calientes*\n"
         + "  \u2022 Perro Clasico  $4.50\n"
         + "  \u2022 Perro Especial  $6.00\n"
         + "  \u2022 Perro con Queso  $5.50\n\n"
         + "*Papas*\n"
         + "  \u2022 Papas Pequenas  $2.50\n"
         + "  \u2022 Papas Medianas  $3.50\n"
         + "  \u2022 Papas Grandes  $4.50\n"
         + "  \u2022 Papas con Queso  $5.50\n"
         + "  \u2022 Papas Mixtas  $6.50\n\n"
         + "*Bebidas*\n"
         + "  \u2022 Coca Cola  $1.50\n"
         + "  \u2022 Coca Cola 1L  $2.50\n"
         + "  \u2022 Pepsi  $1.50\n"
         + "  \u2022 Te Frio  $1.75\n"
         + "  \u2022 Agua  $1.00\n"
         + "  \u2022 Malta  $1.50\n\n"
         + "*Combos*\n"
         + "  \u2022 Combo Clasico  $8.50\n"
         + "  \u2022 Combo Doble  $10.50\n"
         + "  \u2022 Combo Bacon  $11.00\n"
         + "  \u2022 Combo Perro  $6.50\n\n"
         + "*Salsas*\n"
         + "  \u2022 Salsa Ajo  $0.50\n"
         + "  \u2022 Salsa Tartara  $0.50\n"
         + "  \u2022 Salsa Picante  $0.50\n"
         + "  \u2022 Salsa Rosada  $0.50";

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
        => "\ud83c\udf7d\ufe0f \u00bfQu\u00e9 deseas ordenar?\n\n"
         + "_Ejemplo:_\n"
         + "_2 hamburguesas cl\u00e1sicas_\n"
         + "_1 papa mediana_\n"
         + "_2 Coca Cola_\n\n"
         + "_Escribe un producto por l\u00ednea_";

    internal static string ContinueOrder
        => "Perfecto, escr\u00edbeme tu pedido.\n\n"
         + "_Ejemplo:_\n"
         + "_2 hamburguesas cl\u00e1sicas_\n"
         + "_1 papa mediana_\n"
         + "_2 Coca Cola_\n\n"
         + "_Escribe un producto por l\u00ednea_";

    internal static string PickupOrDelivery
        => "\u00bfC\u00f3mo lo quieres?";

    internal static readonly List<ReplyButton> DeliveryButtons = new()
    {
        new("btn_delivery", "Delivery"),
        new("btn_pickup", "Pickup")
    };

    internal static string ObservationQuestion
        => "\u00bfQuieres agregar alguna observaci\u00f3n a tu pedido? (opcional)";

    internal static readonly List<ReplyButton> ObservationButtons = new()
    {
        new("btn_obs_si", "S\u00ed"),
        new("btn_obs_no", "No")
    };

    internal static string ObservationFormat
        => "Escribe tu observaci\u00f3n.\n\n"
         + "_Ejemplo:_\n"
         + "_sin cebolla_\n"
         + "_salsa aparte_\n"
         + "_sin hielo_\n"
         + "_bien tostado_";

    // ── Order confirmation gate ──

    internal static string ConfirmOrderPrompt
        => "\u00bfQu\u00e9 deseas hacer?";

    internal static readonly List<ReplyButton> ConfirmButtons = new()
    {
        new("btn_confirmar", "Confirmar"),
        new("btn_editar", "Editar pedido"),
        new("btn_cancelar", "Cancelar")
    };

    // ── Payment method ──

    internal static string PaymentMethodPrompt
        => "\u00bfC\u00f3mo deseas pagar?";

    internal static readonly List<ReplyButton> PaymentButtons = new()
    {
        new("btn_efectivo", "Efectivo"),
        new("btn_pago_movil", "Pago m\u00f3vil"),
        new("btn_zelle", "Zelle")
    };

    // ── Checkout form ──

    internal static string CheckoutForm
        => "\ud83d\udcdd *DATOS PARA DELIVERY*\n\n"
         + "\ud83d\udc64 *Nombre:*\n"
         + "\ud83e\udeaa *C\u00e9dula:*\n"
         + "\ud83d\udcf1 *Tel\u00e9fono:*\n"
         + "\ud83c\udfe1 *Direcci\u00f3n:*\n\n"
         + "Puedes responder copiando y pegando esta planilla\n"
         + "o enviando los datos en l\u00edneas separadas.\n\n"
         + "Luego presiona *Confirmar*.";

    internal static string CheckoutDataReceived
        => "\u2705 Datos recibidos. \u00bfQu\u00e9 deseas hacer?";

    internal static readonly List<ReplyButton> CheckoutDataButtons = new()
    {
        new("btn_confirmar", "Confirmar"),
        new("btn_editar_datos", "Editar datos")
    };

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
        sb.Append("Luego presiona *Confirmar*.");
        return sb.ToString();
    }

    // ── GPS & payment evidence ──

    internal static string LocationRequestPrompt
        => "\u00a1De acuerdo! Por favor comparte la ubicaci\u00f3n de entrega.";

    internal static string GpsReceived
        => "\u2705 Ubicaci\u00f3n recibida. Presiona *Confirmar* cuando est\u00e9s listo.";

    internal static string PaymentProofReceived
        => "\u2705 Comprobante recibido. Tu pago qued\u00f3 pendiente de verificaci\u00f3n.";

    internal static string PagoMovilDetails(string bank, string id, string phone, string? bsAmount = null)
        => $"\ud83d\udcb3 *DATOS PARA PAGO M\u00d3VIL*\n\n"
         + $"\u2022 *Banco:* {bank}\n"
         + $"\u2022 *C.I./RIF:* {id}\n"
         + $"\u2022 *Tel\u00e9fono:* {phone}"
         + (bsAmount is not null ? $"\n\u2022 *Monto:* Bs. {bsAmount}" : "");

    internal static string PagoMovilProofRequest
        => "Env\u00eda el *comprobante* (foto del pago) \ud83d\udcf8";

    internal static string DivisasProofRequest
        => "Para *DIVISAS*, env\u00eda una *foto de los billetes* \ud83d\udcf8";

    internal static string ZelleDetails(string recipient, string? instructions = null)
        => "\ud83d\udcb3 *DATOS PARA PAGO ZELLE*\n\n"
         + $"\u2022 *Enviar a:* {recipient}"
         + (!string.IsNullOrWhiteSpace(instructions) ? $"\n\u2022 *Instrucciones:* {instructions}" : "")
         + "\n\nRealiza el pago y env\u00eda el *comprobante* (screenshot) \ud83d\udcf8";

    internal static string ZelleProofRequest
        => "Env\u00eda el *comprobante de Zelle* (screenshot) \ud83d\udcf8";

    // ── Order confirmation / receipt ──

    internal static string EmptyOrder
        => "No hay productos en tu pedido. Env\u00eda lo que deseas pedir.";

    internal static string MissingDeliveryType
        => "\u00bfEs *pick up* o *delivery*?";

    // ── Category display priority (for summary/receipt sorting) ──

    private static int CategoryPriority(string itemName)
    {
        var catalog = WebhookProcessor.ActiveCatalog ?? WebhookProcessor.MenuCatalog;
        var entry = catalog.FirstOrDefault(e =>
            e.Canonical.Equals(itemName, StringComparison.OrdinalIgnoreCase));
        var cat = entry?.Category?.ToLowerInvariant() ?? "";
        return cat switch
        {
            "hamburguesas" => 1,
            "perros calientes" => 2,
            "papas" => 3,
            "combos" => 4,
            "salsas" => 5,
            "bebidas" => 7,
            _ => 8
        };
    }

    private static IReadOnlyList<ConversationItemEntry> SortByCategory(IReadOnlyList<ConversationItemEntry> items)
        => items.OrderBy(i => CategoryPriority(i.Name)).ToList();

    // ── Order summary with prices (shown before checkout form) ──

    internal const decimal DeliveryFeeUsd = 4.00m;

    internal static string OrderSummaryWithTotal(IReadOnlyList<ConversationItemEntry> items, ResolvedRate? bcvRate = null, string? deliveryType = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\ud83e\uddfe *RESUMEN DE TU PEDIDO*");
        sb.AppendLine();

        decimal subtotal = 0m;
        foreach (var item in SortByCategory(items))
        {
            var lineTotal = item.UnitPrice * item.Quantity;
            subtotal += lineTotal;

            var line = $"  {item.Quantity}x {item.Name}";
            if (!string.IsNullOrWhiteSpace(item.Modifiers))
                line += $" ({item.Modifiers})";
            if (item.UnitPrice > 0)
                line += $"  ${item.UnitPrice:0.00} c/u = ${lineTotal:0.00}";
            sb.AppendLine(line);
        }

        sb.AppendLine();
        if (subtotal > 0)
        {
            var fee = deliveryType == "delivery" ? DeliveryFeeUsd : 0m;
            var total = subtotal + fee;

            if (fee > 0)
            {
                sb.AppendLine($"Subtotal: ${subtotal:0.00}");
                sb.AppendLine($"\ud83d\ude97 Delivery: ${fee:0.00}");
                sb.AppendLine($"*TOTAL: ${total:0.00}*");
            }
            else
            {
                sb.AppendLine($"*TOTAL: ${subtotal:0.00}*");
            }

            if (bcvRate is not null && bcvRate.Rate > 0)
            {
                var bsTotal = total * bcvRate.Rate;
                var staleTag = bcvRate.IsStale ? " (tasa anterior)" : "";
                sb.AppendLine($"\ud83c\uddfb\ud83c\uddea Ref. BCV {bcvRate.CurrencyLabel}: Bs. {bsTotal:N2}{staleTag}");
            }
        }

        return sb.ToString().TrimEnd();
    }

    internal static string BuildReceipt(
        string orderNumber,
        string customerName,
        string customerIdNumber,
        string customerPhone,
        IReadOnlyList<ConversationItemEntry> items,
        string? specialInstructions,
        string address,
        string paymentText,
        string deliveryType,
        ResolvedRate? bcvRate = null)
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

        // Items with prices — sorted by menu category
        decimal total = 0m;
        foreach (var item in SortByCategory(items))
        {
            var lineTotal = item.UnitPrice * item.Quantity;
            total += lineTotal;
            sb.Append($"  {item.Quantity}x {item.Name}");
            if (!string.IsNullOrWhiteSpace(item.Modifiers))
                sb.Append($" ({item.Modifiers})");
            if (item.UnitPrice > 0)
                sb.Append($"  ${lineTotal:0.00}");
            sb.AppendLine();
        }

        // Observations (before total)
        if (!string.IsNullOrWhiteSpace(specialInstructions))
        {
            sb.AppendLine();
            sb.AppendLine($"\u270d\ufe0f Observaciones: {specialInstructions}");
        }

        if (total > 0)
        {
            var fee = deliveryType == "delivery" ? DeliveryFeeUsd : 0m;
            var grandTotal = total + fee;

            sb.AppendLine();
            if (fee > 0)
            {
                sb.AppendLine($"Subtotal: ${total:0.00}");
                sb.AppendLine($"\ud83d\ude97 Delivery: ${fee:0.00}");
                sb.AppendLine($"\ud83d\udcb0 *TOTAL A PAGAR: ${grandTotal:0.00}*");
            }
            else
            {
                sb.AppendLine($"\ud83d\udcb0 *TOTAL A PAGAR: ${total:0.00}*");
            }

            if (bcvRate is not null && bcvRate.Rate > 0)
            {
                var bsTotal = grandTotal * bcvRate.Rate;
                var staleTag = bcvRate.IsStale ? " (tasa anterior)" : "";
                sb.AppendLine($"\ud83c\uddfb\ud83c\uddea Ref. BCV {bcvRate.CurrencyLabel}: Bs. {bsTotal:N2}{staleTag}");
            }
        }

        // Estimated preparation time
        sb.AppendLine("\u23f1 Tiempo estimado: 30 minutos");

        sb.AppendLine($"\ud83c\udfe1 Direcci\u00f3n: {address}");
        sb.AppendLine($"\ud83d\udcb5 Pago: {paymentText}");
        // Payment instruction for pago movil
        if (paymentText.Contains("PAGO M\u00d3VIL", StringComparison.OrdinalIgnoreCase))
            sb.AppendLine("Cuando env\u00edes el comprobante tu pedido entrar\u00e1 en preparaci\u00f3n.");
        sb.AppendLine($"\ud83d\ude97 {FormatDeliveryType(deliveryType)}");

        sb.AppendLine();
        sb.AppendLine("Tu pedido est\u00e1 siendo preparado \ud83d\udc68\u200d\ud83c\udf73");
        sb.Append("Te avisaremos cuando salga para delivery \ud83d\ude97");

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
        => $"\u2705 Listo, agregu\u00e9 *{qty} {name}* a tu pedido. Presiona *Confirmar* cuando est\u00e9s listo.";

    internal static string ItemRemoved(string name)
        => $"\u2705 Listo, elimin\u00e9 *{name}* del pedido.";

    internal static string ItemReduced(int remaining, string name)
        => $"\u2705 Listo, ahora tienes *{remaining} {name}*. Presiona *Confirmar* cuando est\u00e9s listo.";

    internal static string ItemNotFound(string name)
        => $"No tienes *{name}* en el pedido.";

    internal static string ItemReplaced(int qty, string name)
        => $"\u2705 Listo, ahora son *{qty} {name}*. Presiona *Confirmar* cuando est\u00e9s listo.";

    internal static string ItemSwapped(string oldItem, string newItem)
        => $"\u2705 Listo, cambi\u00e9 *{oldItem}* por *{newItem}*. Presiona *Confirmar* cuando est\u00e9s listo.";

    internal static string EmptyCart
        => "\n\nTu pedido est\u00e1 vac\u00edo. \u00bfQu\u00e9 deseas ordenar?";

    internal static string ConfirmPrompt
        => "Presiona *Confirmar* cuando est\u00e9s listo.";

    // ── Human handoff ──

    internal static string HandoffInitiated
        => "Entendido. Tu conversaci\u00f3n fue derivada a un agente humano.\n\nUn miembro de nuestro equipo te atender\u00e1 en breve \ud83d\ude4f";

    internal static string HandoffWaiting
        => "Tu consulta est\u00e1 siendo atendida por nuestro equipo. Te responderemos pronto.";

    // ── Checkout flow: fill-in-the-form ──

    internal static string StillFillingForm
        => "Env\u00eda los datos que faltan y luego presiona *Confirmar*.";

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
        => "Perfecto, asi queda tu pedido. Presiona *Confirmar* cuando est\u00e9s listo.";

    // ── Conversation recovery (ambiguous/stall responses) ──

    internal static string GentleRedirect
        => "\ud83e\udd14 No estoy seguro de lo que deseas.\n\n"
         + "\u00bfQu\u00e9 deseas ordenar?\n\n"
         + "_Ejemplo:_\n"
         + "_2 hamburguesas cl\u00e1sicas_\n"
         + "_1 papa mediana_\n"
         + "_2 Coca Cola_\n\n"
         + "_Escribe un producto por l\u00ednea_";

    internal static string GentleRedirectWithOrder(IReadOnlyList<ConversationItemEntry> items)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\ud83e\udd14 No logr\u00e9 entender tu mensaje.");
        sb.AppendLine();
        sb.AppendLine("Tu pedido actual:");
        foreach (var item in items)
        {
            sb.Append($"  {item.Quantity}x {item.Name}");
            if (!string.IsNullOrWhiteSpace(item.Modifiers))
                sb.Append($" ({item.Modifiers})");
            sb.AppendLine();
        }
        sb.AppendLine();
        sb.Append("Puedes *agregar*, *quitar* o *cambiar* productos, o presiona *Confirmar* para finalizar.");
        return sb.ToString();
    }

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

    // ── Fashion vertical ──

    internal static string FashionOrderSummary(
        string product, string? size, string? color, int quantity, decimal unitPrice)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\ud83d\udecd\ufe0f *RESUMEN DE TU PEDIDO*");
        sb.AppendLine();
        sb.Append($"  {quantity}x {product}");
        var variants = new List<string>();
        if (!string.IsNullOrWhiteSpace(size)) variants.Add($"Talla: {size}");
        if (!string.IsNullOrWhiteSpace(color)) variants.Add($"Color: {color}");
        if (variants.Count > 0) sb.Append($" ({string.Join(", ", variants)})");
        if (unitPrice > 0)
        {
            var lineTotal = unitPrice * quantity;
            sb.Append($"  ${unitPrice:0.00} c/u = ${lineTotal:0.00}");
        }
        sb.AppendLine();
        if (unitPrice > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"*TOTAL: ${unitPrice * quantity:0.00}*");
        }
        return sb.ToString().TrimEnd();
    }

    internal static string FashionFulfillmentPrompt
        => "\u00bfC\u00f3mo deseas recibir tu pedido?";

    internal static string FashionCheckoutForm
        => "\ud83d\udcdd *DATOS PARA TU PEDIDO*\n\n"
         + "\ud83d\udc64 *Nombre:*\n"
         + "\ud83d\udcf1 *Tel\u00e9fono:*\n"
         + "\ud83c\udfe1 *Direcci\u00f3n de env\u00edo:*\n\n"
         + "Responde con tus datos y luego presiona *Confirmar*.";

    internal static string FashionCheckoutFormPickup
        => "\ud83d\udcdd *DATOS PARA TU PEDIDO*\n\n"
         + "\ud83d\udc64 *Nombre:*\n"
         + "\ud83d\udcf1 *Tel\u00e9fono:*\n\n"
         + "Responde con tus datos y luego presiona *Confirmar*.";

    internal static string FashionReceipt(
        string orderNumber,
        string customerName,
        string customerPhone,
        string product, string? size, string? color, int quantity,
        decimal unitPrice,
        string fulfillmentType,
        string? address,
        string paymentText,
        ResolvedRate? bcvRate = null)
    {
        var sb = new StringBuilder();
        sb.AppendLine("\u2705 *PEDIDO CONFIRMADO*");
        sb.AppendLine($"\ud83e\uddfe Pedido: #{orderNumber}");
        sb.AppendLine();
        sb.AppendLine($"\ud83d\udc64 Nombre: {customerName}");
        sb.AppendLine($"\ud83d\udcf1 Tel\u00e9fono: {customerPhone}");
        sb.AppendLine();

        sb.Append($"  {quantity}x {product}");
        var variants = new List<string>();
        if (!string.IsNullOrWhiteSpace(size)) variants.Add($"Talla: {size}");
        if (!string.IsNullOrWhiteSpace(color)) variants.Add($"Color: {color}");
        if (variants.Count > 0) sb.Append($" ({string.Join(", ", variants)})");
        if (unitPrice > 0) sb.Append($"  ${unitPrice * quantity:0.00}");
        sb.AppendLine();

        var total = unitPrice * quantity;
        if (total > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"\ud83d\udcb0 *TOTAL A PAGAR: ${total:0.00}*");

            if (bcvRate is not null && bcvRate.Rate > 0)
            {
                var bsTotal = total * bcvRate.Rate;
                var staleTag = bcvRate.IsStale ? " (tasa anterior)" : "";
                sb.AppendLine($"\ud83c\uddfb\ud83c\uddea Ref. BCV {bcvRate.CurrencyLabel}: Bs. {bsTotal:N2}{staleTag}");
            }
        }

        if (!string.IsNullOrWhiteSpace(address))
            sb.AppendLine($"\ud83c\udfe1 Direcci\u00f3n: {address}");
        sb.AppendLine($"\ud83d\udcb5 Pago: {paymentText}");
        sb.AppendLine($"\ud83d\udce6 {(fulfillmentType == "shipping" ? "Env\u00edo a domicilio" : "Retiro en tienda")}");

        sb.AppendLine();
        sb.Append("Gracias por tu compra \ud83d\ude4f");

        return sb.ToString();
    }
}
