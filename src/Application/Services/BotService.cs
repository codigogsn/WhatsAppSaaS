using Microsoft.Extensions.Logging;
using WhatsAppSaaS.Application.Interfaces;
using WhatsAppSaaS.Domain.Entities;

namespace WhatsAppSaaS.Application.Services;

public sealed class BotService : IBotService
{
    private readonly ILogger<BotService> _logger;

    public BotService(ILogger<BotService> logger)
    {
        _logger = logger;
    }

    public Task<string> GenerateReplyAsync(IncomingMessage message, CancellationToken cancellationToken = default)
    {
        var original = message.Body ?? "";
        var body = Normalize(original);

        _logger.LogDebug("Generating reply for message from {Sender}: {Body}",
            message.SenderPhoneNumber, body);

        var reply = body switch
        {
            // SALUDOS
            _ when ContainsAny(body, "hola", "buenas", "buenos dias", "buenas tardes", "buenas noches", "hey") =>
                "¡Hola! Soy el asistente del restaurante.\n\n" +
                "Puedes escribir:\n" +
                "- *menu* para ver el menú\n" +
                "- *ordenar* para hacer un pedido\n" +
                "- *horario* para ver nuestros horarios\n" +
                "- *ayuda* para ver opciones\n\n" +
                "También puedes escribir normal, tipo: “quiero una pizza” 😄",

            // MENU
            _ when ContainsAny(body, "menu") =>
                "📋 *Menú*\n\n" +
                "*ENTRADAS*\n" +
                "- Pan con ajo - $5.99\n" +
                "- Ensalada César - $8.99\n" +
                "- Sopa del día - $6.99\n\n" +
                "*PLATOS FUERTES*\n" +
                "- Pollo a la parrilla - $14.99\n" +
                "- Pasta carbonara - $12.99\n" +
                "- Pescado con papas - $13.99\n" +
                "- Hamburguesa vegetariana - $11.99\n\n" +
                "*POSTRES*\n" +
                "- Tiramisú - $7.99\n" +
                "- Torta de chocolate - $6.99\n\n" +
                "Para pedir, escribe *ordenar*.",

            // ORDENAR / PEDIDO
            _ when ContainsAny(body, "ordenar", "orden", "pedido", "comprar") =>
                "Perfecto ✅ Para hacer un pedido, envíame un mensaje en este formato:\n\n" +
                "*PEDIDO:*\n" +
                "Item x cantidad\n" +
                "Item x cantidad\n\n" +
                "Ejemplo:\n" +
                "PEDIDO:\n" +
                "Pollo a la parrilla x 2\n" +
                "Ensalada César x 1\n\n" +
                "Luego te confirmo el total y el tiempo estimado.",

            // HORARIO
            _ when ContainsAny(body, "horario", "horarios", "hora", "abren", "abierto", "cierran", "cierre") =>
                "🕒 *Horarios*\n\n" +
                "Lunes a Viernes: 11:00 AM – 10:00 PM\n" +
                "Sábado: 10:00 AM – 11:00 PM\n" +
                "Domingo: 10:00 AM – 9:00 PM\n\n" +
                "Aceptamos pedidos hasta 30 minutos antes del cierre.",

            // AYUDA
            _ when ContainsAny(body, "ayuda", "help", "opciones") =>
                "✅ *Opciones*\n\n" +
                "- *menu* → ver el menú\n" +
                "- *ordenar* → hacer un pedido\n" +
                "- *horario* → ver horarios\n\n" +
                "También puedes escribir lo que necesitas (ej: “quiero 2 hamburguesas”).",

            // DEFAULT
            _ =>
                "¡Gracias por tu mensaje! Soy el asistente del restaurante.\n\n" +
                "Escribe *menu* para ver el menú,\n" +
                "*ordenar* para hacer un pedido,\n" +
                "o *horario* para ver nuestros horarios.\n\n" +
                "Si quieres, escribe *ayuda*."
        };

        _logger.LogInformation("Generated reply for {Sender}, intent matched: {ReplyLength} chars",
            message.SenderPhoneNumber, reply.Length);

        return Task.FromResult(reply);
    }

    private static string Normalize(string input)
    {
        var s = (input ?? "").Trim().ToLowerInvariant();

        // normaliza acentos: menú -> menu
        s = s.Replace("á", "a").Replace("é", "e").Replace("í", "i").Replace("ó", "o").Replace("ú", "u")
             .Replace("ü", "u").Replace("ñ", "n");

        // colapsa espacios dobles
        while (s.Contains("  "))
            s = s.Replace("  ", " ");

        return s;
    }

    private static bool ContainsAny(string haystack, params string[] needles)
    {
        foreach (var n in needles)
        {
            if (haystack.Contains(n))
                return true;
        }
        return false;
    }
}
