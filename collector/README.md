# ChromeCollector Function App (.NET 8 isolated)

## Prerequisites
- .NET 8 SDK
- Azure Functions Core Tools v4
- Azurite (or an Azure Storage account)

## Local setup
1. Copy template settings:
   - Bash: `cp src/ChromeCollector.FunctionApp/local.settings.json.template src/ChromeCollector.FunctionApp/local.settings.json`
   - PowerShell: `Copy-Item src/ChromeCollector.FunctionApp/local.settings.json.template src/ChromeCollector.FunctionApp/local.settings.json`
2. Fill in `HMAC_KEYS__<keyId>` and optional Sentinel settings.
3. Start Azurite or set `AzureWebJobsStorage` to a real connection string.
4. Run the Function host from `collector/src/ChromeCollector.FunctionApp`:
   - `func start`

## Endpoints
- `GET /api/health`
- `POST /api/v1/chrome/events/batch`

## HMAC verification
Required headers:
- `X-Timestamp`
- `X-Signature`
- `X-Key-Id`

Signature formula:

`base64(HMACSHA256(secretForKeyId, timestamp + "\n" + rawBodyUtf8))`

Accepted key sources:
- `HMAC_KEYS__{keyId}` environment variable pattern
- `HMAC_KEYS_JSON` containing a JSON object map

Timestamp skew must be within ±300 seconds.

## Security controls
- In-memory token bucket rate limiting is applied per `X-Key-Id` (PoC control).
- Request payload size is limited to 1 MB.
- Batch events require `EventType` and `EventTime` fields.
- Logs include correlation id and minimal request metadata only.

Future hardening option (post-PoC): add an API gateway/WAF layer (for example API Management) in front of the Function App for centralized, distributed throttling and policy enforcement.

## Build and test
From `collector/`:
- `dotnet build src/ChromeCollector.FunctionApp/ChromeCollector.FunctionApp.csproj`
- `dotnet test tests/ChromeCollector.FunctionApp.Tests/ChromeCollector.FunctionApp.Tests.csproj`
