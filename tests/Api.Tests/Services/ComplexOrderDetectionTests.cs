using FluentAssertions;
using WhatsAppSaaS.Application.Services;

namespace WhatsAppSaaS.Api.Tests.Services;

public class ComplexOrderDetectionTests
{
    [Theory]
    [InlineData("quiero 3 hamburguesas clasicas 5 papas grandes unas papas con queso y 3 cocacolas", true)]
    [InlineData("2 hamburguesas 1 coca cola y 3 tequeños", true)]
    [InlineData("dame una hamburguesa dos refrescos y tres empanadas", true)]
    [InlineData("quiero 1 pizza grande con extra queso y 2 refrescos sin hielo", true)]
    [InlineData("2 hamburguesas", false)]
    [InlineData("hola", false)]
    [InlineData("confirmar", false)]
    [InlineData("1 coca cola", false)]
    [InlineData("", false)]
    public void IsComplexOrderMessage_DetectsCorrectly(string input, bool expected)
    {
        WebhookProcessor.IsComplexOrderMessage(input).Should().Be(expected);
    }
}
