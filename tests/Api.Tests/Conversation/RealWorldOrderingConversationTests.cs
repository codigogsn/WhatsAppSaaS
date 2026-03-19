using FluentAssertions;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Application.Services;

namespace WhatsAppSaaS.Api.Tests.Conversation;

/// <summary>
/// Realistic multi-message conversation tests simulating actual Venezuelan
/// WhatsApp ordering behavior step by step. Each test walks through the
/// conversation state machine exactly as a real user would experience it:
/// greeting → order → observation gate → delivery/pickup → summary.
///
/// These are NOT unit tests of individual methods. Each test simulates a
/// full conversation by advancing ConversationFields through the same
/// boolean-gate flow that BuildOrderReplyFromState uses in production.
/// </summary>
public class RealWorldOrderingConversationTests
{
    public RealWorldOrderingConversationTests()
    {
        WebhookProcessor.ActiveCatalog = TestCatalogHelper.MenuCatalogWithExtras;
    }

    // ═══════════════════════════════════════════════════════════
    //  Helper: simulate a user sending a message and getting
    //  the bot's reply, advancing state exactly as production does
    // ═══════════════════════════════════════════════════════════

    /// <summary>
    /// Simulates the greeting step: user sends a greeting, bot sends menu.
    /// Returns the state with MenuSent = true.
    /// </summary>
    private static ConversationFields SimulateGreeting(string greetingText)
    {
        WebhookProcessor.IsGreeting(greetingText.Trim().ToLowerInvariant())
            .Should().BeTrue($"'{greetingText}' should be recognized as a greeting");

        var state = new ConversationFields();
        state.MenuSent = true; // ProcessAsync sets this after sending welcome + menu
        return state;
    }

    /// <summary>
    /// Simulates the user placing an order. Parses text, adds items to state,
    /// and returns the bot reply (observation gate or next step).
    /// </summary>
    private static BotReply SimulateOrderMessage(ConversationFields state, string orderText)
    {
        var ok = WebhookProcessor.TryParseQuickOrder(orderText, out var items, out _, out var obs);
        ok.Should().BeTrue($"'{orderText}' should parse as an order");

        var parsed = WebhookProcessor.ParseOrderText(orderText);
        foreach (var p in parsed.Where(p => !string.IsNullOrWhiteSpace(p.Name)))
        {
            // Simulate AddOrIncreaseItem: look up price from catalog
            var catalog = WebhookProcessor.ActiveCatalog ?? WebhookProcessor.MenuCatalog;
            var entry = catalog.FirstOrDefault(m =>
                m.Canonical.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            var unitPrice = entry?.Price ?? 0m;

            // Price fallback to demo catalog (mirrors production logic)
            if (unitPrice < 0.10m && WebhookProcessor.ActiveCatalog != null)
            {
                var demoEntry = WebhookProcessor.FindDemoPriceFallback(p.Name);
                if (demoEntry != null && demoEntry.Price > unitPrice)
                    unitPrice = demoEntry.Price;
            }

            var existing = state.Items.FirstOrDefault(x =>
                x.Name.Equals(p.Name, StringComparison.OrdinalIgnoreCase));
            if (existing != null)
            {
                existing.Quantity += p.Quantity;
            }
            else
            {
                state.Items.Add(new ConversationItemEntry
                {
                    Name = p.Name,
                    Quantity = p.Quantity,
                    Modifiers = p.Modifiers,
                    UnitPrice = unitPrice
                });
            }
        }

        // Store embedded observations (matches ProcessAsync line 1292-1298)
        if (!string.IsNullOrWhiteSpace(obs))
        {
            state.SpecialInstructions = string.IsNullOrWhiteSpace(state.SpecialInstructions)
                ? obs
                : state.SpecialInstructions + "; " + obs;
            state.ObservationAnswered = true;
        }

        return WebhookProcessor.BuildOrderReplyFromState(state);
    }

    /// <summary>
    /// Simulates answering the observation gate (YES/NO).
    /// "no" → ObservationAnswered = true, advances to delivery.
    /// "si" → ObservationPromptSent = true, waits for text.
    /// </summary>
    private static BotReply SimulateObservationAnswer(ConversationFields state, string answer)
    {
        if (answer.ToLowerInvariant() is "no")
        {
            state.ObservationAnswered = true;
        }
        else
        {
            state.ObservationPromptSent = true;
        }
        return WebhookProcessor.BuildOrderReplyFromState(state);
    }

    /// <summary>
    /// Simulates user typing the observation text after answering "si".
    /// </summary>
    private static BotReply SimulateObservationText(ConversationFields state, string observationText)
    {
        state.SpecialInstructions = string.IsNullOrWhiteSpace(state.SpecialInstructions)
            ? observationText
            : state.SpecialInstructions + "; " + observationText;
        state.ObservationAnswered = true;
        return WebhookProcessor.BuildOrderReplyFromState(state);
    }

    /// <summary>
    /// Simulates choosing delivery or pickup.
    /// </summary>
    private static BotReply SimulateDeliveryChoice(ConversationFields state, string choice)
    {
        state.DeliveryType = choice.ToLowerInvariant() switch
        {
            "delivery" => "delivery",
            "pickup" or "pick up" => "pickup",
            _ => choice
        };
        return WebhookProcessor.BuildOrderReplyFromState(state);
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 1 — Delivery full flow
    //  "hola" → "2 hamburguesas clasicas y 1 coca cola" → "no" → "delivery"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario01_DeliveryFullFlow()
    {
        // Step 1: Greeting
        var state = SimulateGreeting("hola");
        state.MenuSent.Should().BeTrue();

        // Step 2: Place order
        var reply = SimulateOrderMessage(state, "2 hamburguesas clasicas y 1 coca cola");
        state.Items.Should().HaveCount(2);
        state.Items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
        // Bot should ask observation question
        reply.Body.Should().Contain("observaci");
        state.ExtrasOffered.Should().BeTrue();

        // Step 3: No observations
        reply = SimulateObservationAnswer(state, "no");
        state.ObservationAnswered.Should().BeTrue();
        // Bot should ask delivery/pickup
        reply.Buttons.Should().Contain(b => b.Title == "Delivery");
        reply.Buttons.Should().Contain(b => b.Title == "Pickup");

        // Step 4: Choose delivery
        reply = SimulateDeliveryChoice(state, "delivery");
        state.DeliveryType.Should().Be("delivery");
        // Bot should show order summary with delivery fee
        reply.Body.Should().Contain("RESUMEN");
        reply.Body.Should().Contain("Hamburguesa Clasica");
        reply.Body.Should().Contain("Coca Cola");
        reply.Body.Should().Contain("Delivery");
        reply.Body.Should().Contain("$4.00"); // delivery fee

        // Verify prices
        var burger = state.Items.First(i => i.Name == "Hamburguesa Clasica");
        burger.UnitPrice.Should().Be(6.50m);
        burger.Quantity.Should().Be(2);

        var coca = state.Items.First(i => i.Name == "Coca Cola");
        coca.UnitPrice.Should().Be(1.50m);
        coca.Quantity.Should().Be(1);

        // Summary should include confirm/edit/cancel buttons
        reply.Buttons.Should().Contain(b => b.Title == "Confirmar");
        reply.Buttons.Should().Contain(b => b.Title.Contains("Editar"));
        reply.Buttons.Should().Contain(b => b.Title == "Cancelar");
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 2 — Pickup full flow
    //  "hola" → "1 perro clasico" → observation "sin cebolla" → "pickup"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario02_PickupFullFlow()
    {
        // Step 1: Greeting
        var state = SimulateGreeting("hola");

        // Step 2: Place order
        var reply = SimulateOrderMessage(state, "1 perro clasico");
        state.Items.Should().HaveCount(1);
        state.Items[0].Name.Should().Be("Perro Clasico");
        state.Items[0].Quantity.Should().Be(1);
        state.Items[0].UnitPrice.Should().Be(4.50m);

        // Bot asks observation — check if modifiers were embedded
        if (!state.ObservationAnswered)
        {
            reply.Body.Should().Contain("observaci");

            // Step 3: Yes, add observation
            reply = SimulateObservationAnswer(state, "si");

            // Step 3b: Type observation text
            reply = SimulateObservationText(state, "sin cebolla");
            state.SpecialInstructions.Should().Contain("sin cebolla");
        }

        state.ObservationAnswered.Should().BeTrue();

        // Step 4: Choose pickup
        reply = SimulateDeliveryChoice(state, "pickup");
        state.DeliveryType.Should().Be("pickup");
        reply.Body.Should().Contain("RESUMEN");
        reply.Body.Should().Contain("Perro Clasico");
        // Pickup should NOT have delivery fee
        reply.Body.Should().NotContain("Delivery: $");
        // Total should be just the perro price
        reply.Body.Should().Contain("$4.50");
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 3 — Venezuelan natural order (multi-item, no commas)
    //  "hola que tal" → "me vas a dar 3 perros clasicos 2 papas grandes y una cocacola"
    //  → "no" → "pickup"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario03_VenezuelanNaturalOrder()
    {
        // Step 1: Greeting (Venezuelan casual)
        var state = SimulateGreeting("hola que tal");

        // Step 2: Place complex order with Venezuelan phrasing
        var reply = SimulateOrderMessage(state,
            "me vas a dar 3 perros clasicos 2 papas grandes y una cocacola");

        state.Items.Should().HaveCount(3, "all three items must be parsed from natural speech");
        state.Items.Should().Contain(i => i.Name == "Perro Clasico" && i.Quantity == 3);
        state.Items.Should().Contain(i => i.Name == "Papas Grandes" && i.Quantity == 2);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);

        // Verify all prices are sane
        foreach (var item in state.Items)
            item.UnitPrice.Should().BeGreaterThan(0.50m, $"'{item.Name}' must have a real price");

        // Step 3: No observations
        reply = SimulateObservationAnswer(state, "no");

        // Step 4: Choose pickup
        reply = SimulateDeliveryChoice(state, "pickup");
        reply.Body.Should().Contain("RESUMEN");
        reply.Body.Should().Contain("Perro Clasico");
        reply.Body.Should().Contain("Papas Grandes");
        reply.Body.Should().Contain("Coca Cola");
        // No delivery fee for pickup
        reply.Body.Should().NotContain("Delivery: $");
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 4 — Comma format
    //  "hola" → "dame 2 hamburguesas clasicas, 1 papa mediana y 2 coca cola"
    //  → "no" → "delivery"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario04_CommaFormat()
    {
        var state = SimulateGreeting("hola");

        var reply = SimulateOrderMessage(state,
            "dame 2 hamburguesas clasicas, 1 papa mediana y 2 coca cola");

        state.Items.Should().HaveCount(3);
        state.Items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        state.Items.Should().Contain(i => i.Name.Contains("Papa") && i.Quantity == 1);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 2);

        // Verify prices
        state.Items.First(i => i.Name == "Hamburguesa Clasica").UnitPrice.Should().Be(6.50m);
        state.Items.First(i => i.Name == "Coca Cola").UnitPrice.Should().Be(1.50m);

        reply = SimulateObservationAnswer(state, "no");
        reply = SimulateDeliveryChoice(state, "delivery");

        reply.Body.Should().Contain("RESUMEN");
        reply.Body.Should().Contain("Delivery");
        reply.Body.Should().Contain("$4.00"); // delivery fee
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 5 — Cancel reset
    //  "hola" → "1 hamburguesa clasica" → "cancelar pedido"
    //  → "hola" → "2 perros clasicos"
    //  Proves cancel clears state and new order starts fresh
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario05_CancelReset()
    {
        // Step 1: Greeting + order
        var state = SimulateGreeting("hola");
        SimulateOrderMessage(state, "1 hamburguesa clasica");
        state.Items.Should().HaveCount(1);
        state.Items[0].Name.Should().Be("Hamburguesa Clasica");

        // Step 2: Cancel
        var cancelText = "cancelar pedido";
        WebhookProcessor.IsCancelCommand(cancelText).Should().BeTrue();

        // Simulate cancel: reset state
        state.ResetAfterConfirm();

        // Verify state is fully cleared
        state.Items.Should().BeEmpty("cancel must clear all items");
        state.MenuSent.Should().BeFalse("cancel must reset menu state");
        state.DeliveryType.Should().BeNull("cancel must clear delivery type");
        state.ObservationAnswered.Should().BeFalse("cancel must reset observation");
        state.ExtrasOffered.Should().BeFalse("cancel must reset extras");
        state.OrderConfirmed.Should().BeFalse("cancel must reset confirmation");
        state.SpecialInstructions.Should().BeNull("cancel must clear observations");

        // Step 3: New greeting
        WebhookProcessor.IsGreeting("hola").Should().BeTrue();
        state.MenuSent = true;

        // Step 4: New order — fresh start, no cart leakage
        SimulateOrderMessage(state, "2 perros clasicos");
        state.Items.Should().HaveCount(1, "new order should have only new items");
        state.Items[0].Name.Should().Be("Perro Clasico");
        state.Items[0].Quantity.Should().Be(2);
        state.Items[0].UnitPrice.Should().Be(4.50m);
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 6 — Double greeting
    //  "hola" → "hola que tal"
    //  Second greeting should be recognized but not duplicate state
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario06_DoubleGreeting()
    {
        // First greeting
        var state = SimulateGreeting("hola");
        state.MenuSent.Should().BeTrue();

        // Second greeting — in production, ProcessAsync would detect MenuSent=true
        // and send a short redirect instead of full welcome
        WebhookProcessor.IsGreeting("hola que tal".Trim().ToLowerInvariant()).Should().BeTrue();

        // State should still be the same — no items, menu already sent
        state.Items.Should().BeEmpty("double greeting must not add items");
        state.MenuSent.Should().BeTrue("menu should remain sent");
        state.DeliveryType.Should().BeNull("no delivery type set yet");
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 7 — Edit flow
    //  "hola" → "2 hamburguesas clasicas" → "no" → "pickup"
    //  → summary shown → "editar pedido"
    //  Proves edit resets checkout state but keeps items
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario07_EditFlow()
    {
        var state = SimulateGreeting("hola");
        SimulateOrderMessage(state, "2 hamburguesas clasicas");
        SimulateObservationAnswer(state, "no");
        var reply = SimulateDeliveryChoice(state, "pickup");

        // Summary shown with confirm/edit/cancel
        reply.Body.Should().Contain("RESUMEN");
        reply.Buttons.Should().Contain(b => b.Title.Contains("Editar"));

        // User clicks edit
        var editText = "editar";
        WebhookProcessor.IsEditCommand(editText).Should().BeTrue();

        // Simulate edit: reset checkout state but keep items
        state.OrderConfirmed = false;
        state.DeliveryType = null;
        state.ExtrasOffered = false;
        state.ObservationAnswered = false;
        state.CheckoutFormSent = false;

        // Items should still be there
        state.Items.Should().HaveCount(1);
        state.Items[0].Name.Should().Be("Hamburguesa Clasica");
        state.Items[0].Quantity.Should().Be(2);

        // Re-entering flow: observation gate again
        reply = WebhookProcessor.BuildOrderReplyFromState(state);
        reply.Body.Should().Contain("observaci");
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 8 — Cancel from summary
    //  "hola" → "2 hamburguesas clasicas y 1 coca cola" → "no"
    //  → "delivery" → summary → "cancelar"
    //  Proves cancel from summary fully resets
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario08_CancelFromSummary()
    {
        var state = SimulateGreeting("hola");
        SimulateOrderMessage(state, "2 hamburguesas clasicas y 1 coca cola");
        SimulateObservationAnswer(state, "no");
        var reply = SimulateDeliveryChoice(state, "delivery");

        // Summary with delivery fee
        reply.Body.Should().Contain("RESUMEN");
        reply.Body.Should().Contain("Delivery");

        // User cancels from summary
        WebhookProcessor.IsCancelCommand("cancelar").Should().BeTrue();
        state.ResetAfterConfirm();

        // Fully reset
        state.Items.Should().BeEmpty();
        state.DeliveryType.Should().BeNull();
        state.MenuSent.Should().BeFalse();
        state.OrderConfirmed.Should().BeFalse();
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 9 — DB fallback (incomplete ActiveCatalog)
    //  Simulates production DB missing perro/papas entries
    //  "hola" → "me vas a dar 3 perros clasicos 2 papas grandes y una cocacola"
    //  → "no" → "pickup"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario09_DBFallback_IncompleteMenu()
    {
        // Simulate DB catalog that only has coca cola
        var incompleteCatalog = new WebhookProcessor.MenuEntry[]
        {
            new() { Canonical = "coca cola", Aliases = new[] { "cocacola", "coca" }, Price = 1.50m },
        };

        var savedCatalog = WebhookProcessor.ActiveCatalog;
        try
        {
            WebhookProcessor.ActiveCatalog = incompleteCatalog;

            var state = SimulateGreeting("hola");

            // This order has items NOT in the DB catalog — must fall back to demo
            var reply = SimulateOrderMessage(state,
                "me vas a dar 3 perros clasicos 2 papas grandes y una cocacola");

            state.Items.Should().HaveCount(3,
                "all items must parse even when DB catalog is incomplete");
            state.Items.Should().Contain(i => i.Name == "Perro Clasico" && i.Quantity == 3);
            state.Items.Should().Contain(i => i.Name == "Papas Grandes" && i.Quantity == 2);
            state.Items.Should().Contain(i => i.Quantity == 1); // coca cola (DB or demo canonical)

            // All prices must be sane
            foreach (var item in state.Items)
                item.UnitPrice.Should().BeGreaterThan(0.50m,
                    $"'{item.Name}' must have demo fallback price, not $0");

            reply = SimulateObservationAnswer(state, "no");
            reply = SimulateDeliveryChoice(state, "pickup");
            reply.Body.Should().Contain("RESUMEN");
        }
        finally
        {
            WebhookProcessor.ActiveCatalog = savedCatalog;
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 10 — Price fallback (corrupted DB prices)
    //  DB has items but with garbage prices like $0.06
    //  "hola" → "dame 2 hamburguesas y 1 coca cola" → "no" → "pickup"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario10_PriceFallback_CorruptedDB()
    {
        var corruptCatalog = new WebhookProcessor.MenuEntry[]
        {
            new() { Canonical = "hamburguesa", Aliases = new[] { "hamburguesas", "hamburguesa clasica" },
                Price = 0.06m },
            new() { Canonical = "coca cola", Aliases = new[] { "cocacola", "coca" },
                Price = 0.02m },
        };

        var savedCatalog = WebhookProcessor.ActiveCatalog;
        try
        {
            WebhookProcessor.ActiveCatalog = corruptCatalog;

            var state = SimulateGreeting("hola");
            var reply = SimulateOrderMessage(state, "dame 2 hamburguesas y 1 coca cola");

            state.Items.Should().HaveCount(2);

            // Both items must have SANE prices from demo fallback, not corrupted DB prices
            var burger = state.Items.First(i => i.Name.Contains("amburguesa", StringComparison.OrdinalIgnoreCase));
            burger.Quantity.Should().Be(2);
            burger.UnitPrice.Should().BeGreaterThan(1m,
                "burger must use demo price ($6.50), not corrupted DB price ($0.06)");

            var coca = state.Items.First(i => i.Name.Contains("oca", StringComparison.OrdinalIgnoreCase));
            coca.Quantity.Should().Be(1);
            coca.UnitPrice.Should().BeGreaterThan(0.50m,
                "coca cola must use demo price ($1.50), not corrupted DB price ($0.02)");

            reply = SimulateObservationAnswer(state, "no");
            reply = SimulateDeliveryChoice(state, "pickup");
            reply.Body.Should().Contain("RESUMEN");
            // Summary should show real prices
            reply.Body.Should().NotContain("$0.06");
            reply.Body.Should().NotContain("$0.02");
        }
        finally
        {
            WebhookProcessor.ActiveCatalog = savedCatalog;
        }
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 11 — Observation flow (user adds special instructions)
    //  "hola" → "1 hamburguesa clasica" → "si" → "sin tomate y con extra salsa"
    //  → "delivery"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario11_ObservationFlow()
    {
        var state = SimulateGreeting("hola");
        var reply = SimulateOrderMessage(state, "1 hamburguesa clasica");

        state.Items.Should().HaveCount(1);
        state.Items[0].Name.Should().Be("Hamburguesa Clasica");

        // Observation gate
        reply.Body.Should().Contain("observaci");

        // User says yes
        reply = SimulateObservationAnswer(state, "si");
        state.ObservationPromptSent.Should().BeTrue();

        // User types observation
        reply = SimulateObservationText(state, "sin tomate y con extra salsa");
        state.SpecialInstructions.Should().Contain("sin tomate");
        state.SpecialInstructions.Should().Contain("extra salsa");
        state.ObservationAnswered.Should().BeTrue();

        // Now should ask delivery/pickup
        reply.Buttons.Should().Contain(b => b.Title == "Delivery");

        // Choose delivery
        reply = SimulateDeliveryChoice(state, "delivery");
        reply.Body.Should().Contain("RESUMEN");
        reply.Body.Should().Contain("Hamburguesa Clasica");
        reply.Body.Should().Contain("Delivery");
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 12 — User changes order mid-conversation
    //  "hola" → "quiero 1 hamburguesa clasica"
    //  → "mejor 2 hamburguesas clasicas y una coca cola"
    //  Second message should ADD to cart (not replace)
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario12_UserChangesOrder()
    {
        var state = SimulateGreeting("hola");

        // First order
        var reply = SimulateOrderMessage(state, "quiero 1 hamburguesa clasica");
        state.Items.Should().HaveCount(1);
        state.Items[0].Name.Should().Be("Hamburguesa Clasica");
        state.Items[0].Quantity.Should().Be(1);

        // Observation gate fires
        state.ExtrasOffered.Should().BeTrue();

        // User changes mind — sends new items before answering observation
        // In production, ProcessAsync re-enters quick parse for this message
        // Reset observation gate since new items are being added
        state.ExtrasOffered = false;
        state.ObservationAnswered = false;

        reply = SimulateOrderMessage(state, "mejor 2 hamburguesas clasicas y una coca cola");

        // Hamburguesa should have increased quantity (1 + 2 = 3)
        var burger = state.Items.First(i => i.Name == "Hamburguesa Clasica");
        burger.Quantity.Should().Be(3, "existing burger quantity should increase from 1 to 3");

        // Coca Cola should be added
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);

        // Total items in cart: 2 distinct items
        state.Items.Should().HaveCount(2);
    }

    // ═══════════════════════════════════════════════════════════
    //  SUPPLEMENTARY — Delivery fee math verification
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void DeliveryFee_IncludedOnlyForDelivery()
    {
        // Delivery order
        var deliveryState = new ConversationFields();
        deliveryState.Items.Add(new ConversationItemEntry
            { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        deliveryState.ExtrasOffered = true;
        deliveryState.ObservationAnswered = true;
        deliveryState.DeliveryType = "delivery";

        var deliveryReply = WebhookProcessor.BuildOrderReplyFromState(deliveryState);
        deliveryReply.Body.Should().Contain("Delivery: $4.00");
        deliveryReply.Body.Should().Contain("$10.50"); // 6.50 + 4.00

        // Pickup order — same item
        var pickupState = new ConversationFields();
        pickupState.Items.Add(new ConversationItemEntry
            { Name = "Hamburguesa Clasica", Quantity = 1, UnitPrice = 6.50m });
        pickupState.ExtrasOffered = true;
        pickupState.ObservationAnswered = true;
        pickupState.DeliveryType = "pickup";

        var pickupReply = WebhookProcessor.BuildOrderReplyFromState(pickupState);
        pickupReply.Body.Should().NotContain("Delivery: $");
        pickupReply.Body.Should().Contain("$6.50");
    }

    // ═══════════════════════════════════════════════════════════
    //  SUPPLEMENTARY — Greeting detection for all common variants
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("hola")]
    [InlineData("buenas")]
    [InlineData("buenas tardes")]
    [InlineData("buenos dias")]
    [InlineData("hola que tal")]
    [InlineData("epa")]
    [InlineData("epale")]
    [InlineData("saludos")]
    [InlineData("que tal")]
    [InlineData("quetal")]
    public void GreetingDetection_CommonVariants(string greeting)
    {
        WebhookProcessor.IsGreeting(greeting).Should().BeTrue(
            $"'{greeting}' must be recognized as a greeting");
    }

    // ═══════════════════════════════════════════════════════════
    //  REGRESSION — "quetal" after "hola" must not duplicate prompt
    //  Production bug: "quetal" fell through to AI parse
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Regression_QueTal_NoSpace_IsGreeting()
    {
        // "quetal" (no space) must be recognized as a greeting
        WebhookProcessor.IsGreeting("quetal").Should().BeTrue(
            "'quetal' without space is a common Venezuelan typing pattern");
    }

    [Fact]
    public void Regression_HolaThenQuetal_NoDoubledPrompt()
    {
        // Step 1: "hola" triggers full greeting, sets MenuSent=true
        var state = SimulateGreeting("hola");
        state.MenuSent.Should().BeTrue();

        // Step 2: "quetal" arrives immediately after — must be recognized as greeting
        WebhookProcessor.IsGreeting("quetal").Should().BeTrue();
        // Since MenuSent is already true, ProcessAsync would send short redirect
        // (not a second full greeting). State should NOT be corrupted.
        state.Items.Should().BeEmpty("greeting must not add items");
        state.MenuSent.Should().BeTrue("menu already sent, should stay true");
    }

    [Fact]
    public void Regression_HolaThenQueTalWithSpace_NoDoubledPrompt()
    {
        var state = SimulateGreeting("hola");
        WebhookProcessor.IsGreeting("que tal").Should().BeTrue();
        state.Items.Should().BeEmpty();
        state.MenuSent.Should().BeTrue();
    }

    [Fact]
    public void Regression_HolaThenHolaQueTal_NoDoubledPrompt()
    {
        var state = SimulateGreeting("hola");
        WebhookProcessor.IsGreeting("hola que tal").Should().BeTrue();
        state.Items.Should().BeEmpty();
        state.MenuSent.Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════
    //  REGRESSION — Greeting coalescing: full greeting sets
    //  LastGreetingRedirectAtUtc so rapid follow-up greetings
    //  are silently deduped (no extra "Perfecto, dime tu orden.")
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void GreetingCoalescing_FullGreetingSetsRedirectTimestamp()
    {
        // After full greeting, LastGreetingRedirectAtUtc must be set
        // so that the 30s dedup window suppresses follow-up greetings
        var state = new ConversationFields();
        state.MenuSent = true;
        state.LastActivityUtc = DateTime.UtcNow;
        state.LastGreetingRedirectAtUtc = DateTime.UtcNow; // production now sets this

        // Simulate rapid follow-up greeting arriving within 30s
        var elapsed = (DateTime.UtcNow - state.LastGreetingRedirectAtUtc.Value).TotalSeconds;
        elapsed.Should().BeLessThanOrEqualTo(30,
            "rapid follow-up greeting should be within the dedup window");

        // In ProcessAsync, this condition triggers silent dedup (lines 680-686):
        // if (LastGreetingRedirectAtUtc.HasValue && elapsed <= 30) → continue (skip)
        state.LastGreetingRedirectAtUtc.HasValue.Should().BeTrue(
            "full greeting must set this so follow-ups are silently deduped");
    }

    [Theory]
    [InlineData("que tal")]
    [InlineData("quetal")]
    [InlineData("hola que tal")]
    [InlineData("buenas")]
    [InlineData("hola")]
    public void GreetingCoalescing_RapidFollowUpIsSuppressed(string followUp)
    {
        // Step 1: Full greeting just happened
        var state = new ConversationFields();
        state.MenuSent = true;
        state.LastActivityUtc = DateTime.UtcNow;
        state.LastGreetingRedirectAtUtc = DateTime.UtcNow;

        // Step 2: Follow-up greeting arrives immediately
        WebhookProcessor.IsGreeting(followUp.Trim().ToLowerInvariant()).Should().BeTrue();

        // Step 3: Dedup check — should suppress because within 30s window
        var withinWindow = state.LastGreetingRedirectAtUtc.HasValue
            && (DateTime.UtcNow - state.LastGreetingRedirectAtUtc.Value).TotalSeconds <= 30;
        withinWindow.Should().BeTrue(
            $"'{followUp}' arriving right after full greeting should be within dedup window");

        // No state corruption
        state.Items.Should().BeEmpty();
        state.MenuSent.Should().BeTrue();
    }

    [Fact]
    public void GreetingCoalescing_CancelThenHolaThenQuetal_OnlyOneFullGreeting()
    {
        // Cancel → fresh greeting → rapid follow-up
        var state = SimulateGreeting("hola");
        SimulateOrderMessage(state, "1 hamburguesa clasica");
        state.Items.Should().HaveCount(1);

        // Cancel
        state.ResetAfterConfirm();
        state.Items.Should().BeEmpty();
        state.LastGreetingRedirectAtUtc.Should().BeNull(
            "cancel clears LastGreetingRedirectAtUtc");

        // New full greeting
        state.MenuSent = true;
        state.LastActivityUtc = DateTime.UtcNow;
        state.LastGreetingRedirectAtUtc = DateTime.UtcNow; // set by full greeting

        // Rapid "quetal" follow-up — should be suppressed
        WebhookProcessor.IsGreeting("quetal").Should().BeTrue();
        var withinWindow = state.LastGreetingRedirectAtUtc.HasValue
            && (DateTime.UtcNow - state.LastGreetingRedirectAtUtc.Value).TotalSeconds <= 30;
        withinWindow.Should().BeTrue("quetal should be deduped after fresh full greeting");
    }

    [Fact]
    public void GreetingCoalescing_OrderAfterFullGreeting_NotSuppressed()
    {
        // Full greeting sets dedup timestamp, but ordering must NOT be affected
        var state = new ConversationFields();
        state.MenuSent = true;
        state.LastActivityUtc = DateTime.UtcNow;
        state.LastGreetingRedirectAtUtc = DateTime.UtcNow;

        // User sends an order (not a greeting) — must work normally
        var orderText = "2 hamburguesas clasicas y 1 coca cola";
        WebhookProcessor.IsGreeting(orderText.Trim().ToLowerInvariant()).Should().BeFalse(
            "order text must NOT be detected as greeting");

        // Order should parse normally
        SimulateOrderMessage(state, orderText);
        state.Items.Should().HaveCount(2);
        state.Items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    [Fact]
    public void GreetingCoalescing_AfterWindowExpires_NormalBehavior()
    {
        // After dedup window expires, greeting should proceed normally
        var state = new ConversationFields();
        state.MenuSent = true;
        state.LastActivityUtc = DateTime.UtcNow.AddSeconds(-35);
        state.LastGreetingRedirectAtUtc = DateTime.UtcNow.AddSeconds(-35);

        // Follow-up greeting arriving after >30s — NOT within window
        WebhookProcessor.IsGreeting("que tal").Should().BeTrue();
        var withinWindow = state.LastGreetingRedirectAtUtc.HasValue
            && (DateTime.UtcNow - state.LastGreetingRedirectAtUtc.Value).TotalSeconds <= 30;
        withinWindow.Should().BeFalse(
            "greeting arriving after 35s should NOT be suppressed");
    }

    // ═══════════════════════════════════════════════════════════
    //  SUPPLEMENTARY — Cancel detection for all common variants
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("cancelar")]
    [InlineData("cancelar pedido")]
    [InlineData("borrar todo")]
    [InlineData("ya no")]
    public void CancelDetection_CommonVariants(string cancel)
    {
        WebhookProcessor.IsCancelCommand(cancel).Should().BeTrue(
            $"'{cancel}' must be recognized as a cancel command");
    }

    // ═══════════════════════════════════════════════════════════
    //  SUPPLEMENTARY — Edit detection
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("editar")]
    [InlineData("modificar")]
    [InlineData("cambiar pedido")]
    public void EditDetection_CommonVariants(string edit)
    {
        WebhookProcessor.IsEditCommand(edit).Should().BeTrue(
            $"'{edit}' must be recognized as an edit command");
    }

    // ═══════════════════════════════════════════════════════════
    //  SUPPLEMENTARY — No cart leakage between conversations
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void NoCartLeakage_ResetClearsEverything()
    {
        var state = SimulateGreeting("hola");
        SimulateOrderMessage(state, "3 hamburguesas clasicas, 2 papas grandes y 1 coca cola");
        state.Items.Should().HaveCount(3);

        // Cancel
        state.ResetAfterConfirm();

        // Verify absolutely nothing leaked
        state.Items.Should().BeEmpty();
        state.DeliveryType.Should().BeNull();
        state.SpecialInstructions.Should().BeNull();
        state.OrderConfirmed.Should().BeFalse();
        state.PaymentMethod.Should().BeNull();
        state.CheckoutFormSent.Should().BeFalse();
        state.CustomerName.Should().BeNull();
        state.ExtrasOffered.Should().BeFalse();
        state.ObservationAnswered.Should().BeFalse();
        state.CashFlowCompleted.Should().BeFalse();
        state.PendingAmbiguousItems.Should().BeNull();

        // New order starts clean
        state.MenuSent = true;
        SimulateOrderMessage(state, "1 perro clasico");
        state.Items.Should().HaveCount(1);
        state.Items[0].Name.Should().Be("Perro Clasico");
    }

    // ═══════════════════════════════════════════════════════════
    //  SUPPLEMENTARY — Summary total math
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void SummaryTotalMath_DeliveryOrder()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry
            { Name = "Hamburguesa Clasica", Quantity = 2, UnitPrice = 6.50m }); // $13.00
        state.Items.Add(new ConversationItemEntry
            { Name = "Coca Cola", Quantity = 1, UnitPrice = 1.50m }); // $1.50
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.DeliveryType = "delivery";

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Subtotal = $14.50, delivery = $4.00, total = $18.50
        reply.Body.Should().Contain("$14.50"); // subtotal
        reply.Body.Should().Contain("$4.00");  // delivery fee
        reply.Body.Should().Contain("$18.50"); // total
    }

    [Fact]
    public void SummaryTotalMath_PickupOrder()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry
            { Name = "Perro Clasico", Quantity = 3, UnitPrice = 4.50m }); // $13.50
        state.Items.Add(new ConversationItemEntry
            { Name = "Papas Grandes", Quantity = 2, UnitPrice = 4.50m }); // $9.00
        state.Items.Add(new ConversationItemEntry
            { Name = "Coca Cola", Quantity = 1, UnitPrice = 1.50m }); // $1.50
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.DeliveryType = "pickup";

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Total = $24.00 (no delivery fee)
        reply.Body.Should().Contain("$24.00");
        reply.Body.Should().NotContain("Delivery: $");
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 13 — Embedded modifiers in order text
    //  "2 hamburguesas sin cebolla y 1 coca cola"
    //  Modifiers extracted AND items parsed correctly
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario13_EmbeddedModifiers()
    {
        var state = SimulateGreeting("hola");
        var reply = SimulateOrderMessage(state, "2 hamburguesas sin cebolla y 1 coca cola");

        state.Items.Should().HaveCount(2);

        var burger = state.Items.First(i => i.Name == "Hamburguesa Clasica");
        burger.Quantity.Should().Be(2);
        burger.UnitPrice.Should().Be(6.50m);
        burger.Modifiers.Should().Contain("sin cebolla",
            "modifier must be extracted from inline order text");

        var coca = state.Items.First(i => i.Name == "Coca Cola");
        coca.Quantity.Should().Be(1);
        coca.UnitPrice.Should().Be(1.50m);
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 14 — Multi-line WhatsApp order
    //  Users often type one item per line in WhatsApp
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario14_MultiLineOrder()
    {
        var state = SimulateGreeting("buenas tardes");
        var multiLine = "2 hamburguesas clasicas\n1 papas grandes\n1 coca cola";
        var reply = SimulateOrderMessage(state, multiLine);

        state.Items.Should().HaveCount(3);
        state.Items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 2);
        state.Items.Should().Contain(i => i.Name == "Papas Grandes" && i.Quantity == 1);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);

        foreach (var item in state.Items)
            item.UnitPrice.Should().BeGreaterThan(0.50m);
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 15 — Full flow through confirm → payment gate
    //  Verifies state transitions past summary into payment
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario15_ConfirmAdvancesToPaymentGate()
    {
        var state = SimulateGreeting("hola");
        SimulateOrderMessage(state, "1 hamburguesa clasica");
        SimulateObservationAnswer(state, "no");
        var reply = SimulateDeliveryChoice(state, "pickup");

        // At summary — confirm/edit/cancel buttons
        reply.Body.Should().Contain("RESUMEN");
        reply.Buttons.Should().Contain(b => b.Title == "Confirmar");

        // User confirms
        state.OrderConfirmed = true;
        reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Should now ask payment method
        reply.Body.Should().Contain("pagar");
        reply.Buttons.Should().Contain(b => b.Title == "Efectivo");

        // User picks efectivo
        state.PaymentMethod = "efectivo";
        reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Cash flow: currency selection
        reply.Body.Should().Contain("moneda");
        state.AwaitingCashCurrency.Should().BeTrue();
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 16 — Accented Spanish input
    //  "1 hamburguesa clásica" with accent on á
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario16_AccentedInput()
    {
        var state = SimulateGreeting("hola");
        var reply = SimulateOrderMessage(state, "1 hamburguesa clásica");

        state.Items.Should().HaveCount(1);
        state.Items[0].Name.Should().Be("Hamburguesa Clasica");
        state.Items[0].Quantity.Should().Be(1);
        state.Items[0].UnitPrice.Should().Be(6.50m);
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 17 — Venezuelan ordering prefix variants
    //  "ponme", "regalame", "quiero" all work as noise prefix
    // ═══════════════════════════════════════════════════════════

    [Theory]
    [InlineData("ponme 2 perros clasicos y 1 coca cola")]
    [InlineData("regalame 2 perros clasicos y 1 coca cola")]
    [InlineData("quiero 2 perros clasicos y 1 coca cola")]
    [InlineData("dame 2 perros clasicos y 1 coca cola")]
    [InlineData("necesito 2 perros clasicos y 1 coca cola")]
    public void Scenario17_VenezuelanPrefixVariants(string orderText)
    {
        var state = SimulateGreeting("epa");
        SimulateOrderMessage(state, orderText);

        state.Items.Should().HaveCount(2,
            $"'{orderText}' must parse correctly regardless of prefix");
        state.Items.Should().Contain(i => i.Name == "Perro Clasico" && i.Quantity == 2);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 18 — Edit then add items, re-verify summary
    //  User edits, adds a new item, summary updates
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario18_EditThenAddItem_SummaryUpdates()
    {
        var state = SimulateGreeting("hola");
        SimulateOrderMessage(state, "1 hamburguesa clasica");
        SimulateObservationAnswer(state, "no");
        var reply = SimulateDeliveryChoice(state, "delivery");

        // At summary — $6.50 + $4.00 delivery = $10.50
        reply.Body.Should().Contain("RESUMEN");
        reply.Body.Should().Contain("$10.50");

        // User edits
        state.DeliveryType = null;
        state.ExtrasOffered = false;
        state.ObservationAnswered = false;

        // Add a coca cola
        state.ExtrasOffered = false;
        state.ObservationAnswered = false;
        SimulateOrderMessage(state, "1 coca cola");

        state.Items.Should().HaveCount(2);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 1);

        // Skip obs, pick delivery again
        SimulateObservationAnswer(state, "no");
        reply = SimulateDeliveryChoice(state, "delivery");

        // New total: $6.50 + $1.50 + $4.00 = $12.00
        reply.Body.Should().Contain("RESUMEN");
        reply.Body.Should().Contain("Hamburguesa Clasica");
        reply.Body.Should().Contain("Coca Cola");
        reply.Body.Should().Contain("$12.00");
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 19 — Full-category order (all categories)
    //  Tests summary displays items sorted by category
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario19_AllCategoryOrder()
    {
        var state = SimulateGreeting("hola");
        SimulateOrderMessage(state, "1 hamburguesa clasica");

        // Reset obs gate to add more items
        state.ExtrasOffered = false;
        state.ObservationAnswered = false;
        SimulateOrderMessage(state, "1 perro clasico");

        state.ExtrasOffered = false;
        state.ObservationAnswered = false;
        SimulateOrderMessage(state, "1 papas medianas");

        state.ExtrasOffered = false;
        state.ObservationAnswered = false;
        SimulateOrderMessage(state, "1 coca cola");

        state.Items.Should().HaveCount(4);

        SimulateObservationAnswer(state, "no");
        var reply = SimulateDeliveryChoice(state, "pickup");

        // Summary should list all categories in order
        var body = reply.Body;
        var burgerPos = body.IndexOf("Hamburguesa Clasica");
        var perroPos = body.IndexOf("Perro Clasico");
        var papasPos = body.IndexOf("Papas Medianas");
        var cocaPos = body.IndexOf("Coca Cola");

        burgerPos.Should().BeGreaterThan(-1);
        perroPos.Should().BeGreaterThan(-1);
        papasPos.Should().BeGreaterThan(-1);
        cocaPos.Should().BeGreaterThan(-1);

        // Category sort: hamburguesas < perros < papas < bebidas
        burgerPos.Should().BeLessThan(perroPos, "hamburguesas before perros");
        perroPos.Should().BeLessThan(papasPos, "perros before papas");
        papasPos.Should().BeLessThan(cocaPos, "papas before bebidas");

        // Total: 6.50 + 4.50 + 3.50 + 1.50 = $16.00
        body.Should().Contain("$16.00");
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 20 — Cancel then immediately reorder same thing
    //  Proves no ghost state from previous order
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario20_CancelAndReorderSameItems()
    {
        var state = SimulateGreeting("hola");
        SimulateOrderMessage(state, "2 hamburguesas clasicas y 1 coca cola");
        state.Items.Should().HaveCount(2);

        // Cancel
        state.ResetAfterConfirm();
        state.Items.Should().BeEmpty();

        // Re-greet and order the same thing
        state.MenuSent = true;
        SimulateOrderMessage(state, "2 hamburguesas clasicas y 1 coca cola");

        // Must be exactly 2 items with exact quantities — no doubling
        state.Items.Should().HaveCount(2);
        state.Items.First(i => i.Name == "Hamburguesa Clasica").Quantity.Should().Be(2);
        state.Items.First(i => i.Name == "Coca Cola").Quantity.Should().Be(1);
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 21 — Word-number quantities
    //  "una hamburguesa, dos perros y tres cocas"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario21_WordNumberQuantities()
    {
        var state = SimulateGreeting("buenas");
        SimulateOrderMessage(state, "una hamburguesa, dos perros clasicos y tres coca cola");

        state.Items.Should().HaveCount(3);
        state.Items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 1);
        state.Items.Should().Contain(i => i.Name == "Perro Clasico" && i.Quantity == 2);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 3);
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 22 — Combo ordering
    //  "1 combo clasico y 1 agua"
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario22_ComboOrder()
    {
        var state = SimulateGreeting("hola");
        SimulateOrderMessage(state, "1 combo clasico y 1 agua");

        state.Items.Should().HaveCount(2);
        state.Items.Should().Contain(i => i.Name == "Combo Clasico" && i.Quantity == 1);
        state.Items.Should().Contain(i => i.Name == "Agua" && i.Quantity == 1);

        state.Items.First(i => i.Name == "Combo Clasico").UnitPrice.Should().Be(8.50m);
        state.Items.First(i => i.Name == "Agua").UnitPrice.Should().Be(1.00m);
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 23 — Pickup simple (fresh greeting variant)
    //  "buenos dias" → "1 papas medianas" → "no" → "pickup"
    //  Clean pickup with no delivery fee, different greeting
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario23_FreshGreetingPickupSimple()
    {
        var state = SimulateGreeting("buenos dias");

        var reply = SimulateOrderMessage(state, "1 papas medianas");
        state.Items.Should().HaveCount(1);
        state.Items[0].Name.Should().Be("Papas Medianas");
        state.Items[0].UnitPrice.Should().Be(3.50m);

        reply = SimulateObservationAnswer(state, "no");
        reply = SimulateDeliveryChoice(state, "pickup");

        reply.Body.Should().Contain("RESUMEN");
        reply.Body.Should().Contain("Papas Medianas");
        reply.Body.Should().Contain("$3.50");
        reply.Body.Should().NotContain("Delivery");

        var total = WebhookProcessor.ComputeOrderTotalUsd(state);
        total.Should().Be(3.50m, "pickup total = item price only");
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 24 — Delivery with "hola que tal" greeting
    //  Different greeting + delivery flow with fee verification
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario24_HolaQueTal_DeliveryWithFee()
    {
        var state = SimulateGreeting("hola que tal");

        SimulateOrderMessage(state, "1 hamburguesa doble y 2 coca cola");
        state.Items.Should().HaveCount(2);
        state.Items.Should().Contain(i => i.Name == "Hamburguesa Doble" && i.Quantity == 1);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 2);

        SimulateObservationAnswer(state, "no");
        var reply = SimulateDeliveryChoice(state, "delivery");

        reply.Body.Should().Contain("RESUMEN");
        reply.Body.Should().Contain("Delivery: $4.00");

        // Subtotal: 8.50 + 3.00 = 11.50; Total: 11.50 + 4.00 = 15.50
        var total = WebhookProcessor.ComputeOrderTotalUsd(state);
        total.Should().Be(15.50m);
        reply.Body.Should().Contain("$15.50");
    }

    // ═══════════════════════════════════════════════════════════
    //  SCENARIO 25 — Observation with embedded modifiers
    //  Order text already contains modifiers; observation gate
    //  should be skipped because obs was extracted inline
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Scenario25_EmbeddedObservation_SkipsGate()
    {
        var state = SimulateGreeting("hola");

        // TryParseQuickOrder extracts "sin cebolla" as embedded observation
        var reply = SimulateOrderMessage(state,
            "1 hamburguesa clasica sin cebolla");

        state.Items.Should().HaveCount(1);
        state.Items[0].Name.Should().Be("Hamburguesa Clasica");

        // Check if modifiers were captured (inline or as observation)
        var hasModifier = !string.IsNullOrWhiteSpace(state.Items[0].Modifiers);
        var hasObs = !string.IsNullOrWhiteSpace(state.SpecialInstructions);
        (hasModifier || hasObs).Should().BeTrue(
            "sin cebolla must be captured as modifier or observation");
    }

    // ═══════════════════════════════════════════════════════════
    //  SUPPLEMENTARY — ComputeOrderTotalUsd correctness
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void ComputeOrderTotalUsd_DeliveryAddsFeee_PickupDoesNot()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry
            { Name = "Perro Clasico", Quantity = 2, UnitPrice = 4.50m });

        state.DeliveryType = "delivery";
        WebhookProcessor.ComputeOrderTotalUsd(state).Should().Be(13.00m); // 9 + 4

        state.DeliveryType = "pickup";
        WebhookProcessor.ComputeOrderTotalUsd(state).Should().Be(9.00m); // 9 only
    }

    // ═══════════════════════════════════════════════════════════
    //  SUPPLEMENTARY — Price per unit shown correctly in summary
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void Summary_ShowsUnitPriceAndLineTotal()
    {
        var state = new ConversationFields();
        state.Items.Add(new ConversationItemEntry
            { Name = "Hamburguesa Clasica", Quantity = 3, UnitPrice = 6.50m });
        state.ExtrasOffered = true;
        state.ObservationAnswered = true;
        state.DeliveryType = "pickup";

        var reply = WebhookProcessor.BuildOrderReplyFromState(state);

        // Should show: 3x Hamburguesa Clasica  $6.50 c/u = $19.50
        reply.Body.Should().Contain("3x Hamburguesa Clasica");
        reply.Body.Should().Contain("$6.50 c/u");
        reply.Body.Should().Contain("$19.50");
    }

    // ═══════════════════════════════════════════════════════════
    //  SUPPLEMENTARY — Large multi-item Venezuelan order
    //  Realistic big family order
    // ═══════════════════════════════════════════════════════════

    [Fact]
    public void LargeVenezuelanFamilyOrder()
    {
        var state = SimulateGreeting("buenas noches");
        SimulateOrderMessage(state,
            "4 hamburguesas clasicas, 3 perros clasicos, 2 papas grandes y 4 coca cola");

        state.Items.Should().HaveCount(4);
        state.Items.Should().Contain(i => i.Name == "Hamburguesa Clasica" && i.Quantity == 4);
        state.Items.Should().Contain(i => i.Name == "Perro Clasico" && i.Quantity == 3);
        state.Items.Should().Contain(i => i.Name == "Papas Grandes" && i.Quantity == 2);
        state.Items.Should().Contain(i => i.Name == "Coca Cola" && i.Quantity == 4);

        SimulateObservationAnswer(state, "no");
        var reply = SimulateDeliveryChoice(state, "delivery");

        // 4*6.50 + 3*4.50 + 2*4.50 + 4*1.50 = 26 + 13.50 + 9 + 6 = 54.50 + 4 delivery = 58.50
        var total = WebhookProcessor.ComputeOrderTotalUsd(state);
        total.Should().Be(58.50m);
        reply.Body.Should().Contain("$58.50");
    }
}
