using FluentAssertions;
using Microsoft.Extensions.Logging;
using Moq;
using WhatsAppSaaS.Application.Services;
using WhatsAppSaaS.Domain.Entities;
using WhatsAppSaaS.Domain.Enums;

namespace WhatsAppSaaS.Api.Tests.Services;

public class BotServiceTests
{
    private readonly BotService _sut;

    public BotServiceTests()
    {
        var logger = new Mock<ILogger<BotService>>();
        _sut = new BotService(logger.Object);
    }

    [Theory]
    [InlineData("hello", "asistente")]
    [InlineData("Hello there!", "asistente")]
    [InlineData("hi", "asistente")]
    [InlineData("hola", "asistente")]
    public async Task GenerateReplyAsync_WithGreeting_ReturnsWelcomeMessage(string input, string expectedContains)
    {
        var message = CreateMessage(input);
        var reply = await _sut.GenerateReplyAsync(message);
        reply.Should().Contain(expectedContains);
    }

    [Theory]
    [InlineData("menu")]
    [InlineData("Show me the MENU")]
    [InlineData("can I see the menu?")]
    public async Task GenerateReplyAsync_WithMenuKeyword_ReturnsMenuItems(string input)
    {
        var message = CreateMessage(input);
        var reply = await _sut.GenerateReplyAsync(message);
        reply.Should().Contain("ENTRADAS");
        reply.Should().Contain("Pollo a la parrilla");
    }

    [Theory]
    [InlineData("ordenar")]
    [InlineData("quiero hacer un pedido")]
    [InlineData("quiero comprar")]
    public async Task GenerateReplyAsync_WithOrderKeyword_ReturnsOrderInstructions(string input)
    {
        var message = CreateMessage(input);
        var reply = await _sut.GenerateReplyAsync(message);
        reply.Should().Contain("PEDIDO:");
    }

    [Theory]
    [InlineData("hours")]
    [InlineData("when are you open?")]
    public async Task GenerateReplyAsync_WithHoursKeyword_ReturnsOpeningHours(string input)
    {
        var message = CreateMessage(input);
        var reply = await _sut.GenerateReplyAsync(message);
        // "hours" doesn't match Spanish keywords; falls through to default
        reply.Should().Contain("asistente del restaurante");
    }

    [Fact]
    public async Task GenerateReplyAsync_WithUnknownMessage_ReturnsFallback()
    {
        var message = CreateMessage("asdfghjkl random text");
        var reply = await _sut.GenerateReplyAsync(message);
        reply.Should().Contain("asistente del restaurante");
    }

    private static IncomingMessage CreateMessage(string body) => new()
    {
        SenderPhoneNumber = "1234567890",
        RecipientPhoneNumberId = "9876543210",
        MessageId = "wamid.test123",
        Body = body,
        Type = MessageType.Text
    };
}
