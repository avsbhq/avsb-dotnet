# Avsb.SDK — .NET

Official .NET SDK for the [A vs B](https://app.avsb.cloud) feature-flag and experimentation platform.

Targets `netstandard2.1` and `net8.0`. ASP.NET Core middleware, `IServiceCollection` DI integration, `IAsyncDisposable` lifecycle. OpenFeature provider included.

---

## 1. Install

```bash
dotnet add package Avsb.SDK
```

NuGet package: `Avsb.SDK`. **Runtime requirement:** .NET 8 (`net8.0`) or any framework supporting `netstandard2.1`.

### ASP.NET Core DI package (included in `Avsb.SDK`)

The DI extension and middleware are in `Avsb.SDK.AspNetCore` — no separate package needed.

---

## 2. Quickstart

```csharp
using Avsb.SDK;

var server = new AvsbServer(new AvsbServerOptions
{
    SdkKey = Environment.GetEnvironmentVariable("AVSB_SDK_KEY")!
});

var result = await server.InitAsync();
if (!result.Success && !result.Degraded)
    throw new InvalidOperationException($"AvsB init failed: {result.Error?.Message}");

var ctx  = EvalContext.User("alice", attributes: new Dictionary<string, object?> { ["plan"] = "pro" });
var flag = server.GetBoolFlag("checkout-v2", false, ctx);

if (flag.IsEnabled())
    Console.WriteLine($"New checkout — variation: {flag.VariationKey}");
```

---

## 3. SDK keys

Get a **server SDK key** from your A vs B project's Environments page. Store it as an environment variable or in .NET secrets — never hard-code it.

```bash
export AVSB_SDK_KEY=sdk-...
# or: dotnet user-secrets set "Avsb:SdkKey" "sdk-..."
```

```csharp
// appsettings.json
{
  "Avsb": { "SdkKey": "" }   // populated via environment / secrets
}
```

Never use the public (client-side) SDK key in server code. Server SDK keys carry elevated trust and must not be included in responses served to browsers.

---

## 4. Identity

`EvalContext` is a sealed record with static factory methods. Two subtypes: `SingleContext` (one kinded identity) and `MultiContext` (keyed collection of single contexts).

### `EvalContext.User(key, ...)` — user context

```csharp
var ctx = EvalContext.User(
    "alice",
    name: "Alice Liddell",
    attributes: new Dictionary<string, object?>
    {
        ["plan"]       = "pro",
        ["betaTester"] = true,
    }
);
```

### `EvalContext.FromSingle(singleContext)` / `EvalContext.FromMulti(multiContext)`

```csharp
var single = new SingleContext { Kind = "device", Key = "d_abc",
    Attributes = new Dictionary<string, object?> { ["os"] = "linux" } };

var ctx = EvalContext.FromSingle(single);
```

### `Track` / `Identify` — additional lifecycle methods

```csharp
server.Identify(ctx);
```

---

## 5. Multi-context

```csharp
var ctx = EvalContext.FromMulti(new MultiContext
{
    Contexts = new Dictionary<string, SingleContext>
    {
        ["user"] = new SingleContext
        {
            Kind = "user",
            Key  = "alice",
            Attributes = new Dictionary<string, object?> { ["plan"] = "pro" },
        },
        ["organization"] = new SingleContext
        {
            Kind = "organization",
            Key  = "acme",
            Attributes = new Dictionary<string, object?> { ["tier"] = "enterprise" },
        },
    }
});

var flag = server.GetBoolFlag("enterprise-feature", false, ctx);
```

When a rule specifies `hashAttribute: "organization.key"`, all members of the same organisation receive the same variation regardless of their user key.

---

## 6. Reading flags

### Typed methods

```csharp
Flag<bool>   boolFlag   = server.GetBoolFlag("promo-banner",  false,   ctx);
Flag<string> stringFlag = server.GetStringFlag("ui-theme",    "light", ctx);
Flag<double> numFlag    = server.GetNumberFlag("max-retries", 3.0,     ctx);
Flag<MyDto?> jsonFlag   = server.GetJsonFlag<MyDto>("api-config", null, ctx);
```

A `defaultValue` is required on every call. It is returned when the flag is not found, the server is not initialised, or evaluation fails.

### `Flag<T>` — full evaluation envelope

```csharp
Flag<bool> flag = server.GetBoolFlag("checkout-v2", false, ctx);

flag.Value           // T — the evaluated value
flag.VariationKey    // string? — matched variation key
flag.Source          // EvaluationSource enum
flag.RuleId          // string? — matched rule id
flag.RuleType        // RuleType? enum
flag.Reasons         // IReadOnlyList<string>
flag.EvaluatedAt     // long — ms epoch
flag.DurationMicros  // long — microseconds in evaluator
flag.IsEnabled()     // bool — Source==Rule && truthy value
flag.Exists()        // bool — Source != NotFound
```

`EvaluationSource` is an enum with values `Rule`, `Holdout`, `Default`, `NotFound`, `DatafileOverride`, `Disabled`, `Sticky`, `Bandit`.

---

## 7. Tracking events

```csharp
using Avsb.SDK;

server.Track("purchase_completed", ctx, new TrackPayload
{
    Value      = 49.99,
    Properties = new Dictionary<string, object?> { ["plan"] = "pro", ["currency"] = "usd" },
});
```

`Value` maps to the numeric metric quantity. `Properties` is forwarded as-is to the collection endpoint. Both are optional.

---

## 8. Error handling

`AvsbServer` accepts `Microsoft.Extensions.Logging.ILogger` via `AvsbServerOptions.Logger`:

```csharp
using Microsoft.Extensions.Logging;

ILogger<AvsbServer> logger = loggerFactory.CreateLogger<AvsbServer>();

var server = new AvsbServer(new AvsbServerOptions
{
    SdkKey = Environment.GetEnvironmentVariable("AVSB_SDK_KEY")!,
    Logger = logger,
});
```

When using the DI extension, the `ILogger<AvsbServer>` is resolved from the container automatically.

`InitAsync` returns an `InitResult` record — it never throws:

```csharp
public sealed record InitResult
{
    public bool              Success  { get; init; }
    public InitResultSource  Source   { get; init; }  // Network | Bootstrap | Timeout | Error
    public bool              Degraded { get; init; }
    public Exception?        Error    { get; init; }
}
```

`Get*Flag` methods never throw. Evaluation failures produce `Source == NotFound` with `Value` equal to the supplied default.

---

## 9. ASP.NET Core integration

### DI registration (`Program.cs`)

```csharp
builder.Services.AddAvsb(options =>
{
    options.SdkKey = builder.Configuration["Avsb:SdkKey"]!;
});

// Initialise at startup (before app.Run):
await app.Services.GetRequiredService<AvsbServer>().InitAsync();
```

### Middleware

```csharp
app.UseAvsb();
```

`AvsbMiddleware` resolves an `EvalContext` from `HttpContext.User.Identity.Name` and stores it in `HttpContext.Items`. Override the context before the middleware runs if you need custom attribute resolution.

### Retrieving the context in a controller or minimal API

```csharp
app.MapGet("/checkout", (HttpContext http, AvsbServer avsb) =>
{
    var ctx  = http.GetAvsbContext();            // extension method on HttpContext
    var flag = avsb.GetBoolFlag("checkout-v2", false, ctx);
    return Results.Ok(new { newCheckout = flag.IsEnabled() });
});
```

Or inject `AvsbServer` as a constructor dependency in a controller:

```csharp
[ApiController]
public class CheckoutController : ControllerBase
{
    private readonly AvsbServer _avsb;

    public CheckoutController(AvsbServer avsb)
    {
        _avsb = avsb;
    }

    [HttpGet("/checkout")]
    public IActionResult Index()
    {
        var ctx  = HttpContext.GetAvsbContext();
        var flag = _avsb.GetBoolFlag("checkout-v2", false, ctx);
        return View(new CheckoutViewModel { UseNewCheckout = flag.IsEnabled() });
    }
}
```

---

## 10. Graceful shutdown

`AvsbServer` implements `IAsyncDisposable`. Register it via `AddAvsb` and the DI container disposes it automatically on application shutdown.

For manual lifecycle management:

```csharp
await using var server = new AvsbServer(options);
await server.InitAsync();
// ... serve requests ...
// DisposeAsync stops polling and flushes buffered decision log entries.
```

Or use `IHostApplicationLifetime` for explicit SIGTERM handling:

```csharp
lifetime.ApplicationStopping.Register(async () =>
{
    await server.DisposeAsync();
});
```

---

## 11. Testing

Use `InMemoryDatafileLoader` to supply a fake datafile without network access:

```csharp
using Avsb.SDK;
using Avsb.SDK.TestUtils;
using Xunit;

public class CheckoutFlagTests
{
    private const string DatafileJson = """
        {
          "version": 2,
          "sdkKey": "sdk-test",
          "flags": [{
            "id": "flag_checkout-v2",
            "key": "checkout-v2",
            "type": "boolean",
            "enabled": true,
            "defaultVariationId": "var_off",
            "variations": [
              {"id": "var_off", "key": "off", "value": false},
              {"id": "var_on",  "key": "on",  "value": true}
            ],
            "rules": []
          }],
          "audiences": []
        }
        """;

    [Fact]
    public void GetBoolFlag_ReturnsFlag_WhenFlagExists()
    {
        var datafile = InMemoryDatafileLoader.Load(DatafileJson);
        var server   = new AvsbServer(new AvsbServerOptions
        {
            SdkKey            = "sdk-test",
            BootstrapDatafile = datafile,
        });

        var ctx  = EvalContext.User("alice");
        var flag = server.GetBoolFlag("checkout-v2", false, ctx);

        Assert.True(flag.Exists());
        Assert.NotNull(flag.VariationKey);
    }
}
```

`InMemoryDatafileLoader.Load(json)` parses a JSON string. `InMemoryDatafileLoader.LoadFile(path)` reads from disk. Both return a `FlagDatafile` that can be passed as `BootstrapDatafile` in `AvsbServerOptions`.

Run the test suite:

```bash
dotnet test packages/avsb-dotnet/tests/Avsb.SDK.Tests/
```

The conformance test loads all fixtures from `packages/avsb-conformance/fixtures/manifest.json` automatically.

---

## 12. Migration from LaunchDarkly / Statsig

### From LaunchDarkly .NET SDK

```csharp
// LaunchDarkly
using LaunchDarkly.Sdk;
using LaunchDarkly.Sdk.Server;

var client  = new LdClient("sdk-key");
var context = Context.New("alice");
var value   = client.BoolVariation("checkout-v2", context, false);
```

```csharp
// A vs B
using Avsb.SDK;

var server = new AvsbServer(new AvsbServerOptions { SdkKey = "sdk-key" });
await server.InitAsync();

var ctx   = EvalContext.User("alice", attributes: new Dictionary<string, object?> { ["plan"] = "pro" });
var value = server.GetBoolFlag("checkout-v2", false, ctx).Value;
```

```csharp
// LaunchDarkly — multi-context
var multi = Context.MultiBuilder()
    .Add(Context.New("alice"))
    .Add(Context.Builder("acme").Kind("organization").Set("tier", "enterprise").Build())
    .Build();
```

```csharp
// A vs B — multi-context
var ctx = EvalContext.FromMulti(new MultiContext
{
    Contexts = new Dictionary<string, SingleContext>
    {
        ["user"]         = new SingleContext { Kind = "user",         Key = "alice" },
        ["organization"] = new SingleContext { Kind = "organization", Key = "acme",
            Attributes = new Dictionary<string, object?> { ["tier"] = "enterprise" } },
    }
});
```

Key differences:
- `GetBoolFlag` requires an explicit `defaultValue`. No untyped `.Variation()` method.
- Returns `Flag<T>` with full metadata. No separate `BoolVariationDetail()` call needed.
- `EvalContext` is a sealed record, not a builder class.
- `InitAsync` returns `InitResult` instead of throwing.

### From Statsig .NET SDK

```csharp
// Statsig
await StatsigServer.Initialize("secret-key");
var user  = new StatsigUser { UserID = "alice" };
var gate  = StatsigServer.CheckGate(user, "checkout-v2");
```

```csharp
// A vs B
var ctx  = EvalContext.User("alice", attributes: new Dictionary<string, object?> { ["plan"] = "pro" });
var gate = server.GetBoolFlag("checkout-v2", false, ctx).IsEnabled();
```

```csharp
// Statsig — log event
StatsigServer.LogEvent(user, "purchase", value: "49.99",
    metadata: new Dictionary<string, string> { ["plan"] = "pro" });
```

```csharp
// A vs B — track event
server.Track("purchase", ctx, new TrackPayload
{
    Value      = 49.99,
    Properties = new Dictionary<string, object?> { ["plan"] = "pro" },
});
```

### OpenFeature provider

```csharp
using OpenFeature;
using Avsb.SDK;

Api.Instance.SetProvider(new AvsbOpenFeatureProvider(server));
var client = Api.Instance.GetClient();

var value = await client.GetBooleanValueAsync("checkout-v2", false,
    new EvaluationContext { TargetingKey = "alice" });
```
