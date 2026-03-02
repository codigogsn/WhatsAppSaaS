using FluentValidation;
using WhatsAppSaaS.Application.DTOs;

namespace WhatsAppSaaS.Application.Validators;

public sealed class WebhookPayloadValidator : AbstractValidator<WebhookPayload>
{
    public WebhookPayloadValidator()
    {
        RuleFor(x => x.Object)
            .NotEmpty()
            .WithMessage("Webhook object field is required.");

        RuleFor(x => x.Entry)
            .NotNull()
            .WithMessage("Entry array is required.");
    }
}
