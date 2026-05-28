# Avsb.SDK — .NET

.NET SDK for A vs B feature flags and experimentation.

## Targets

- `netstandard2.1` (Xamarin, Blazor WASM, older frameworks)
- `net8.0` (ASP.NET Core, Worker Services, Console)

## Quick start

```csharp
var server = new AvsbServer(new AvsbServerOptions { SdkKey = "YOUR_SDK_KEY" });
await server.InitAsync();

var ctx = EvalContext.User("alice", attributes: new Dictionary<string, object?> { ["plan"] = "paid" });
var flag = server.GetBoolFlag("my-feature", false, ctx);

if (flag.IsEnabled())
    Console.WriteLine($"Variation: {flag.VariationKey}");
```

## ASP.NET Core integration

```csharp
// Program.cs
builder.Services.AddAvsb(o => o.SdkKey = "YOUR_SDK_KEY");
app.UseMiddleware<AvsbMiddleware>();

// Controller / minimal API
var ctx = httpContext.GetAvsbContext();
var flag = avsbServer.GetBoolFlag("feature", false, ctx);
```

## OpenFeature provider

```csharp
Api.Instance.SetProvider(new AvsbOpenFeatureProvider(avsbServer));
var client = Api.Instance.GetClient();
var value = await client.GetBooleanValueAsync("feature", false);
```

## Optional dependencies

| Feature | Package | Activation |
|---|---|---|
| Redis sticky buckets | `StackExchange.Redis` | Define `AVSB_REDIS_STICKY` |
| DynamoDB sticky buckets | `AWSSDK.DynamoDBv2` | Define `AVSB_DYNAMODB_STICKY` |

## Running tests

```bash
dotnet test packages/avsb-dotnet/tests/Avsb.SDK.Tests/
```

Conformance cases are loaded automatically from
`packages/avsb-conformance/fixtures/manifest.json`.
