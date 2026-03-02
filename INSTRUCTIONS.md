# WhatsApp SaaS API - Setup & Deployment

## Prerequisites

- .NET 8 SDK: https://dotnet.microsoft.com/download/dotnet/8.0
- A Meta Developer account with a WhatsApp Business API app
- ngrok (for local webhook testing): https://ngrok.com

## Local Setup

```bash
# 1. Clone / navigate to the project
cd WhatsAppSaaS

# 2. Restore dependencies
dotnet restore

# 3. Configure your tokens in appsettings.Development.json
#    OR use environment variables:
export WhatsApp__VerifyToken="my-verify-token"
export WhatsApp__AccessToken="EAAG..."
export WhatsApp__PhoneNumberId="123456789"

# 4. Run the API
dotnet run --project src/Api

# API starts at http://localhost:5000
```

## Run Tests

```bash
dotnet test
```

## Expose Local Server for Meta Webhook

```bash
# In a separate terminal:
ngrok http 5000

# Copy the HTTPS URL (e.g., https://abc123.ngrok-free.app)
# Configure it in Meta Developer Dashboard:
#   Webhook URL: https://abc123.ngrok-free.app/webhook
#   Verify Token: (same as WhatsApp__VerifyToken)
```

## Example curl Requests

### Webhook Verification (simulates Meta's GET handshake)

```bash
curl -X GET "http://localhost:5000/webhook?hub.mode=subscribe&hub.verify_token=dev-verify-token-123&hub.challenge=challenge_accepted"
```

Expected response: `challenge_accepted` (HTTP 200)

### Incoming Message (simulates WhatsApp webhook POST)

```bash
curl -X POST http://localhost:5000/webhook \
  -H "Content-Type: application/json" \
  -d '{
    "object": "whatsapp_business_account",
    "entry": [{
      "id": "WHATSAPP_BUSINESS_ACCOUNT_ID",
      "changes": [{
        "value": {
          "messaging_product": "whatsapp",
          "metadata": {
            "display_phone_number": "15551234567",
            "phone_number_id": "123456789"
          },
          "contacts": [{
            "profile": { "name": "John Doe" },
            "wa_id": "5511999999999"
          }],
          "messages": [{
            "from": "5511999999999",
            "id": "wamid.HBgNNTUxMTk5OTk5OTk5ORUCEA",
            "timestamp": "1677777777",
            "text": { "body": "hello" },
            "type": "text"
          }]
        },
        "field": "messages"
      }]
    }]
  }'
```

Expected response: HTTP 200 (empty body, message processed asynchronously)

### Test Menu Request

```bash
curl -X POST http://localhost:5000/webhook \
  -H "Content-Type: application/json" \
  -d '{
    "object": "whatsapp_business_account",
    "entry": [{
      "id": "123",
      "changes": [{
        "value": {
          "messaging_product": "whatsapp",
          "metadata": {
            "display_phone_number": "15551234567",
            "phone_number_id": "123456789"
          },
          "messages": [{
            "from": "5511999999999",
            "id": "wamid.test",
            "timestamp": "1234567890",
            "text": { "body": "menu" },
            "type": "text"
          }]
        },
        "field": "messages"
      }]
    }]
  }'
```

### Health Check

```bash
curl http://localhost:5000/health
```

Expected response: `Healthy` (HTTP 200)

## Environment Variables (Production)

| Variable | Description |
|---|---|
| `WhatsApp__VerifyToken` | Token you define; must match Meta dashboard |
| `WhatsApp__AccessToken` | Permanent token from Meta Business settings |
| `WhatsApp__PhoneNumberId` | Your WhatsApp phone number ID |
| `WhatsApp__ApiVersion` | Graph API version (default: v21.0) |
| `WhatsApp__AppSecret` | App secret for signature validation |
| `WhatsApp__RequireSignatureValidation` | `true` to enforce X-Hub-Signature-256 |

## Deploy to Render

1. Push to GitHub
2. Create a new **Web Service** on Render
3. Set:
   - **Build Command**: `dotnet publish src/Api/Api.csproj -c Release -o out`
   - **Start Command**: `dotnet out/Api.dll`
   - **Environment**: Set all `WhatsApp__*` env vars
   - **Port**: `8080` (set `ASPNETCORE_URLS=http://+:8080`)
4. Set the Render URL as webhook in Meta Dashboard

## Deploy to Azure App Service

```bash
# Login
az login

# Create resource group
az group create --name whatsapp-saas-rg --location eastus

# Create App Service plan
az appservice plan create --name whatsapp-saas-plan --resource-group whatsapp-saas-rg --sku B1 --is-linux

# Create web app
az webapp create --resource-group whatsapp-saas-rg --plan whatsapp-saas-plan --name whatsapp-saas-api --runtime "DOTNET|8.0"

# Configure environment variables
az webapp config appsettings set --resource-group whatsapp-saas-rg --name whatsapp-saas-api --settings \
  WhatsApp__VerifyToken="your-token" \
  WhatsApp__AccessToken="EAAG..." \
  WhatsApp__PhoneNumberId="123456789" \
  WhatsApp__RequireSignatureValidation="true" \
  WhatsApp__AppSecret="your-secret"

# Deploy
dotnet publish src/Api/Api.csproj -c Release -o ./publish
cd publish && zip -r ../deploy.zip . && cd ..
az webapp deploy --resource-group whatsapp-saas-rg --name whatsapp-saas-api --src-path deploy.zip --type zip
```

## Deploy to Railway

1. Push to GitHub
2. New project on Railway -> Deploy from GitHub
3. Railway auto-detects .NET; set:
   - **Build**: `dotnet publish src/Api/Api.csproj -c Release -o out`
   - **Start**: `dotnet out/Api.dll`
4. Add environment variables in Railway dashboard
5. Railway provides a public URL for webhook configuration

## Deploy with Docker

```bash
# Build from project root
docker build -f src/Api/Dockerfile -t whatsapp-saas .

# Run
docker run -d -p 8080:8080 \
  -e WhatsApp__VerifyToken="your-token" \
  -e WhatsApp__AccessToken="EAAG..." \
  -e WhatsApp__PhoneNumberId="123456789" \
  -e WhatsApp__AppSecret="your-secret" \
  -e WhatsApp__RequireSignatureValidation="true" \
  whatsapp-saas
```

## Project Structure

```
WhatsAppSaaS/
├── WhatsAppSaaS.sln
├── src/
│   ├── Api/                         # ASP.NET Core Web API host
│   │   ├── Controllers/
│   │   │   └── WebhookController.cs # GET verify + POST receive
│   │   ├── Middleware/
│   │   │   ├── GlobalExceptionMiddleware.cs
│   │   │   └── RequestLoggingMiddleware.cs
│   │   ├── Extensions/
│   │   │   └── MiddlewareExtensions.cs
│   │   ├── Program.cs
│   │   ├── appsettings.json
│   │   ├── appsettings.Development.json
│   │   └── Dockerfile
│   ├── Application/                 # Business logic + interfaces
│   │   ├── Common/
│   │   │   └── WhatsAppOptions.cs
│   │   ├── DTOs/
│   │   │   ├── WebhookPayload.cs
│   │   │   └── SendMessageRequest.cs
│   │   ├── Interfaces/
│   │   │   ├── IBotService.cs
│   │   │   ├── IWhatsAppClient.cs
│   │   │   ├── IWebhookProcessor.cs
│   │   │   └── ISignatureValidator.cs
│   │   ├── Services/
│   │   │   ├── BotService.cs
│   │   │   └── WebhookProcessor.cs
│   │   └── Validators/
│   │       └── WebhookPayloadValidator.cs
│   ├── Domain/                      # Core entities
│   │   ├── Entities/
│   │   │   ├── IncomingMessage.cs
│   │   │   └── OutgoingMessage.cs
│   │   ├── Enums/
│   │   │   ├── MessageType.cs
│   │   │   └── MessageDirection.cs
│   │   └── ValueObjects/
│   │       └── PhoneNumber.cs
│   └── Infrastructure/              # External integrations
│       ├── WhatsApp/
│       │   ├── WhatsAppClient.cs
│       │   └── SignatureValidator.cs
│       └── Extensions/
│           └── ServiceCollectionExtensions.cs
└── tests/
    └── Api.Tests/
        ├── Controllers/
        │   └── WebhookControllerIntegrationTests.cs
        ├── Services/
        │   ├── BotServiceTests.cs
        │   └── WebhookProcessorTests.cs
        └── Middleware/
            └── SignatureValidatorTests.cs
```
