namespace WhatsAppSaaS.Application.Interfaces;

public interface ISignatureValidator
{
    bool IsValid(string payload, string signature);
}
