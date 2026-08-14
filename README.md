# Avsb.SDK: .NET

Official .NET SDK for the [A vs B](https://app.avsb.cloud) feature-flag and experimentation platform.

netstandard2.1 and net8.0. Evaluation happens in your process against a datafile read from a globally cached CDN, so a flag check is a few microseconds of local work with no network call and no proxy to operate.

ASP.NET Core wiring and an OpenFeature provider are included in the same package.

---

## 1. Install

```bash
dotnet add package Avsb.SDK
```

Required at runtime: `Microsoft.Extensions.Logging.Abstractions`. Everything else is optional and only needed if you use the matching feature:

| Package | Needed for |
| --- | --- |
| `OpenFeature` | `AvsbOpenFeatureProvider` |
| `StackExchange.Redis` | `RedisStickyBucketService` |
| `AWSSDK.DynamoDBv2` | `DynamoDBStickyBucketService` |
| `Microsoft.AspNetCore.Http.Abstractions` | the ASP.NET Core middleware |

Those four are declared with `PrivateAssets="all"`, so they do not flow into your project. Add the one you need.

---

## 2. Quickstart

```csharp
using Avsb.SDK;

var server = new AvsbServer(new AvsbServerOptions
{
    SdkKey = Environment.GetEnvironmentVariable("AVSB_SDK_KEY")!,
});

InitResult init = await server.InitAsync();
if (!init.Success)
{
    // Flags still work: every call returns the default you pass, and the SDK has already logged
    // the URL it tried, the status it got, and a curl to reproduce it.
}

Flag<bool> flag = server.GetBoolFlag("checkout-v2", false, EvalContext.User("user_42"));
if (flag.IsEnabled())
{
    // new checkout
}

server.Track("signup_completed", EvalContext.User("user_42"));

await server.DisposeAsync();   // stops polling and flushes queued events
```

Copy your SDK key from the A vs B dashboard: Environments in the project sidebar. Keys look like `sdk_production_ttqm0eaj4vth1krcb2xn`. Nothing else is required: the datafile URL, the ingest endpoint, and the heartbeat endpoint are all derived.

---

## 3. What the SDK does for you

| | |
| --- | --- |
| Datafile | Fetched from `https://cdn.avsb.cloud/{sdkKey}/datafile.json` on `InitAsync`, then refreshed every 30 seconds (jittered). |
| Retry | The polling loop starts even when the FIRST fetch fails, so a blip at deploy time costs one interval instead of bricking flags until restart. |
| Evaluation | Local, synchronous, no network call. |
| Events | `Track` and exposures are queued, batched, retried with jittered backoff, and flushed on dispose. |
| Liveness | A heartbeat after each successful datafile load drives the Connected state on the Environments page. |
| Failure | Loud. Every failure names the value involved and what to do about it. |

---

## 4. Options

```csharp
public sealed record AvsbServerOptions
{
    public string SdkKey { get; init; }                                    // required
    public string? CdnHost { get; init; }                                  // default https://cdn.avsb.cloud
    public string? DatafileUrl { get; init; }                              // default derived from the key
    public TimeSpan? PollInterval { get; init; }                           // default 30 seconds
    public TimeSpan? FetchTimeout { get; init; }                           // default 10 seconds
    public bool Polling { get; init; } = true;
    public bool Tracking { get; init; } = true;
    public string? Bootstrap { get; init; }                                // datafile JSON
    public IReadOnlyDictionary<string, string>? RuntimeOverrides { get; init; }
    public ILogger? Logger { get; init; }
    public IStickyBucketService? StickyBucketService { get; init; }
    public IAvsbBanditModelStore? BanditModelStore { get; init; }
    public int DecisionLogCapacity { get; init; } = 1000;                   // 0 turns it off
}
```

Two notes worth reading:

- `Bootstrap` takes datafile JSON and serves it immediately, so flags work before the first fetch completes and keep working if that fetch fails.
- `RuntimeOverrides` maps a flag key to a VARIATION KEY, resolved against the datafile so the served value and its key are real. A variation key that does not exist is logged and ignored rather than serving something invented.

The old `Endpoint` option is gone. It took a base `Uri` and combined it with a leading-slash relative path, and that combine silently discarded the base path: a customer who correctly configured `https://host/v1` still fetched `https://host/datafile/{key}`. Use `CdnHost` for a different origin, or `DatafileUrl` for a completely different URL.

---

## 5. Identity

```csharp
public sealed record SingleContext
{
    public string Kind { get; init; } = "user";
    public string Key { get; init; } = string.Empty;
    public string? Name { get; init; }
    public ContextMeta? Meta { get; init; }
    public IReadOnlyDictionary<string, object?> Attributes { get; init; }
}

public sealed record MultiContext
{
    public IReadOnlyDictionary<string, SingleContext> Contexts { get; init; }
}

public sealed record EvalContext
{
    public SingleContext? Single { get; }
    public MultiContext? Multi { get; }
    public bool IsMulti { get; }

    public static EvalContext User(string key, string? name = null,
        IDictionary<string, object?>? attributes = null, ContextMeta? meta = null);
    public static EvalContext FromSingle(SingleContext ctx);
    public static EvalContext FromMulti(MultiContext ctx);
}
```

```csharp
var ctx = EvalContext.User("user_42", attributes: new Dictionary<string, object?>
{
    ["plan"] = "pro",
    ["seats"] = 25,
});
```

The `Key` is what buckets the user, so it must be stable for the same person. An empty key means every request lands in the same bucket, which corrupts an experiment invisibly, so the SDK warns once per client naming the call to add.

### Multi-context, for organisation-keyed targeting

```csharp
var ctx = EvalContext.FromMulti(new MultiContext
{
    Contexts = new Dictionary<string, SingleContext>
    {
        ["user"] = new() { Kind = "user", Key = "user_42" },
        ["organization"] = new()
        {
            Kind = "organization",
            Key = "org_7",
            Attributes = new Dictionary<string, object?> { ["tier"] = "enterprise" },
        },
    },
});
```

---

## 6. Reading flags

```csharp
Flag<bool>   GetBoolFlag(string key, bool defaultValue, EvalContext? context = null);
Flag<string> GetStringFlag(string key, string defaultValue, EvalContext? context = null);
Flag<double> GetNumberFlag(string key, double defaultValue, EvalContext? context = null);
Flag<T?>     GetJsonFlag<T>(string key, T? defaultValue, EvalContext? context = null);
IReadOnlyDictionary<string, object?> GetAllFlags(EvalContext? context = null);
```

Notes that are not obvious:

- `GetNumberFlag` always resolves to a `double`, never an `int`. The reference engine has a single numeric type and cannot distinguish `3` from `3.0`.
- `GetJsonFlag<T>` evaluates DIRECTLY and deserialises the document into `T`. It used to read the flag as a string first and parse that string, so a JSON flag holding a real object resolved to null and every caller silently got their default.
- `GetAllFlags` returns raw untyped values for bootstrapping a browser client from a server-rendered page, and fires exposures for every decided flag.

### Type safety, and what happens on a mismatch

| Situation | What you get |
| --- | --- |
| Value resolves and the type matches | the value |
| Resolved value is the wrong type | your default, `Source = NotFound`, one reason naming the flag and what it actually resolved to, plus a logged warning |
| Datafile declares a different type than the getter asked for | your default, `Source = NotFound`, one reason naming both types and the getter to use instead |
| No datafile loaded yet | your default, `Source = NotReady` |

It never throws, and it never coerces. A string flag read through `GetBoolFlag` does not become `true`.

```csharp
public sealed record Pricing(string Plan, double Amount);

Flag<Pricing?> pricing = server.GetJsonFlag<Pricing>(
    "pricing", new Pricing("standard", 9.99), EvalContext.User("user_42"));

double amount = pricing.Value!.Amount;
```

### `Flag<T>`

```csharp
public readonly record struct Flag<T>
{
    public T Value { get; init; }                        // your default when the datafile could not answer
    public string? VariationKey { get; init; }           // null when no variation was served
    public EvaluationSource Source { get; init; }
    public string? RuleId { get; init; }                 // null unless a rule or holdout decided
    public RuleType? RuleType { get; init; }             // null unless a rule or holdout decided
    public IReadOnlyList<string> Reasons { get; init; }  // ordered
    public long EvaluatedAt { get; init; }               // ms since the Unix epoch
    public long DurationMicros { get; init; }

    public bool IsEnabled();
    public bool Exists();
}
```

`IsEnabled()` is true when BOTH hold:

1. `Source` is one of `Rule`, `Holdout`, `Bandit`, `RuntimeOverride`, `DatafileOverride`, `Sticky`. A flag serving its DEFAULT variation is not enabled, however truthy that default is. Neither is a disabled flag, a missing flag, or an SDK that has not initialised.
2. The value is truthy under JAVASCRIPT rules, because the reference engine is TypeScript and the answer has to be identical in every A vs B SDK: `false`, `0`, `-0`, `NaN`, `""`, and null are falsy. Everything else is truthy, INCLUDING the string `"false"`, the string `"0"`, an empty list, and an empty dictionary.

`Exists()` is false for `NotFound` and `NotReady`, and true for everything else including `Disabled`.

### `EvaluationSource`

```csharp
public enum EvaluationSource
{
    DatafileOverride,   // "datafileOverride"
    RuntimeOverride,    // "runtimeOverride"
    Sticky,             // "sticky"
    Rule,               // "rule"
    Holdout,            // "holdout"
    Bandit,             // "bandit"
    Default,            // "default"
    NotFound,           // "not_found"
    Disabled,           // "disabled"
    NotReady,           // "not_ready"
}
```

`NotReady` is the one to know about: init has not completed, the fetch failed with no bootstrap, or the client was disposed. It is deliberately NOT `NotFound`, because "the flag does not exist" sent people hunting through the dashboard for a flag that was there all along.

Helpers: `source.WireValue()`, `source.IsDecided()`, `source.IsUnknownFlag()`, and `EvaluationSourceExtensions.FromWire(string)`.

---

## 7. Tracking events

```csharp
void Track(string eventName, EvalContext? context = null, TrackPayload? payload = null);
Task TrackPurchaseAsync(EvalContext? context, PurchaseOrder order);
Task FlushEventsAsync();
```

```csharp
server.Track("signup_completed", ctx);
server.Track("cart_value", ctx, new TrackPayload { Value = 129.50 });
server.Track("upgrade", ctx, new TrackPayload { Revenue = 49.00 });

await server.TrackPurchaseAsync(ctx, new PurchaseOrder
{
    OrderId = "ORD-1001",
    Total = 99.95,
    Currency = "USD",
    Items = new[] { new PurchaseOrderItem { Sku = "SKU-1", Price = 99.95, Quantity = 1 } },
});
```

- `eventName` is the metric key as configured in the dashboard. An unknown key is accepted and stored, so a typo shows up as an empty metric rather than an error.
- Money is DECIMAL MAJOR UNITS as displayed: `99.95`, not `9995`.
- Metric events carry no free-form properties. The ingestion body has no such field, so anything sent under that name would look delivered and be gone. Put the number in `TrackPayload.Value` and segment on registered context attributes instead.
- Purchases are sent immediately rather than batched.

`Track` used to log a line and drop the event. Every AB test, bandit, and holdout decision now also records an exposure automatically; without exposures a server-side experiment produces no results. Set `Tracking = false` to turn all of it off, and `Track` then says so in a warning rather than dropping silently.

---

## 8. Errors and diagnostics

- **Datafile fetch failure** logs the URL tried, the status, what a 404 or 403 usually means, and a `curl -i` line that reproduces it.
- **A key that is not shaped like an SDK key** logs at construction and names what it looks like instead (a URL, an API token, a truncated copy).
- **An empty bucketing key** warns once per client, naming `EvalContext.User("<your user id>")`.
- **A dropped event** names the event and says why sending is not running.
- **A bandit rule that cannot dispatch** appends a reason naming the rule.
- **A type mismatch** logs the flag, the declared type, and the getter to use instead.

Logging goes through `ILogger` when you supply one. When you do not, the SDK does NOT go silent: it writes warnings and errors to standard error, and drops to silence only when `ASPNETCORE_ENVIRONMENT`, `DOTNET_ENVIRONMENT`, or `AVSB_ENV` names the process production. Pass `NullLogger.Instance` to silence it explicitly.

### Decision log

Every evaluation appends one entry, so you have an audit trail for "why did that user see that variation".

```csharp
foreach (DecisionLogEntry entry in server.DecisionLogEntries)
{
    Console.WriteLine($"{entry.FlagKey} -> {entry.VariationKey} ({entry.Source.WireValue()})");
}
```

The log is BOUNDED: it keeps the most recent 1000 entries and drops the oldest, so a service that never reads it pays a fixed cost rather than leaking. Change the bound with `DecisionLogCapacity`, or pass 0 to turn recording off.

---

## 9. ASP.NET Core

```csharp
builder.Services.AddAvsb(options =>
{
    options.SdkKey = builder.Configuration["Avsb:SdkKey"]!;
});

var app = builder.Build();
app.UseMiddleware<AvsbMiddleware>();

app.MapGet("/checkout", (HttpContext http, AvsbServer avsb) =>
{
    EvalContext ctx = http.GetAvsbContext();
    return avsb.GetBoolFlag("checkout-v2", false, ctx).IsEnabled() ? "v2" : "v1";
});
```

`AvsbMiddleware` binds a user context from `HttpContext.User.Identity.Name`. Override it per request with `http.SetAvsbContext(...)` before the middleware runs.

---

## 10. Sticky bucketing

```csharp
public interface IStickyBucketService
{
    StickyAssignment? Lookup(string userId, string flagKey);
    void Save(string userId, string flagKey, StickyAssignment assignment);
}
```

```csharp
options.StickyBucketService = new InMemoryStickyBucketService();
```

How it behaves:

- The lookup runs BEFORE the evaluator, so a stored assignment wins over a rule and over a datafile override. Nothing ever called `Lookup` before this release.
- A save happens ONLY for an `ab_test` rule that served a variation. It used to save on every rule type, including holdouts.
- What is stored is the variation's ID. It used to store the KEY, which corrupts the store, because `Lookup` resolves by id and would never match again.
- A stored variation that has since been deleted is treated as a MISS and the evaluator answers instead.
- A stored variation whose rule is gone is still served, with a warning and no exposure, because the rule it would be attributed to no longer exists.

---

## 11. Bandits

A `bandit` rule dispatches through the bandit engine. With no model store configured the engine runs its documented cold-start policy, so a bandit rule serves a real decision from day one instead of behaving like a rule that never matched (which is exactly what it did before: this evaluator had no `bandit` case at all).

```csharp
options.BanditModelStore = new InMemoryBanditModelStore(new Dictionary<string, BanditEvaluator.BanditModel>
{
    ["checkout-copy-bandit"] = new()
    {
        Version = "v1",
        Algorithm = "ucb1",
        PerAction = new Dictionary<string, BanditEvaluator.PerActionStats>
        {
            ["action_bold"] = new() { Mean = 0.42, Variance = 0.05, Samples = 800 },
            ["action_plain"] = new() { Mean = 0.31, Variance = 0.05, Samples = 790 },
        },
    },
});
```

Every path a bandit rule can take appends a reason: dispatched, no config, unresolvable `banditId`, no actions, no model in a supplied store, or an action pointing at a variation the flag no longer has.

---

## 12. Testing

Use `Bootstrap` with `Polling` and `Tracking` off, so a test never touches the network:

```csharp
var server = new AvsbServer(new AvsbServerOptions
{
    SdkKey = "sdk_test_abcdefgh",
    Bootstrap = datafileJson,
    Polling = false,
    Tracking = false,
});

server.GetBoolFlag("checkout-v2", false, EvalContext.User("alice")).IsEnabled().Should().BeTrue();
```

`InMemoryDatafileLoader.Load(json)` and `.LoadFile(path)` parse a datafile directly when you want to exercise `Evaluator` without a client.

### Conformance

`tests/Avsb.SDK.Tests/ConformanceTests.cs` runs the shared A vs B conformance corpus: the same datafiles, contexts, and expected outcomes every other A vs B SDK runs, generated from the TypeScript reference engine. Values, variation keys, sources, rule ids, rule types, and the exact ordered reason strings are all compared, and values are compared STRUCTURALLY by JSON kind rather than by text.

A missing or empty corpus FAILS the build.

---

## 13. OpenFeature

```csharp
await Api.Instance.SetProviderAsync(new AvsbOpenFeatureProvider(server));
FeatureClient client = Api.Instance.GetClient();

bool enabled = await client.GetBooleanValueAsync("checkout-v2", false,
    EvaluationContext.Builder().SetTargetingKey("user_42").Build());
```

Context mapping, including the kinds OpenFeature has no native concept of:

```csharp
// Multi-kind: organisation-keyed targeting survives the bridge.
EvaluationContext ctx = EvaluationContext.Builder()
    .SetTargetingKey("user_42")
    .Set("kind", "multi")
    .Set("user", new Structure(new Dictionary<string, Value> { ["key"] = new("user_42") }))
    .Set("organization", new Structure(new Dictionary<string, Value>
    {
        ["key"] = new("org_7"),
        ["tier"] = new("enterprise"),
    }))
    .Build();

// Single non-user kind.
EvaluationContext orgCtx = EvaluationContext.Builder()
    .SetTargetingKey("org_7")
    .Set("kind", "organization")
    .Set("tier", "enterprise")
    .Build();
```

Error codes are mapped so an OpenFeature caller can tell the two failures apart: an SDK with no datafile yet reports `PROVIDER_NOT_READY`, and a missing flag reports `FLAG_NOT_FOUND`.

---

## 14. Shutdown

```csharp
await server.DisposeAsync();
```

Stops the polling loop, flushes queued events, and puts the client into a not-ready state. In ASP.NET Core the DI container disposes the singleton for you at shutdown.

---

## 15. Migrating

### From the LaunchDarkly .NET SDK

| LaunchDarkly | A vs B |
| --- | --- |
| `new LdClient(config)` | `new AvsbServer(new AvsbServerOptions { SdkKey = key })` then `await server.InitAsync()` |
| `client.BoolVariation(key, ctx, false)` | `server.GetBoolFlag(key, false, ctx).Value` |
| `client.BoolVariationDetail(...)` | `server.GetBoolFlag(...)` returns the detail already |
| `Context.New("user-key")` | `EvalContext.User("user-key")` |
| `Context.NewMulti(a, b)` | `EvalContext.FromMulti(new MultiContext { ... })` |
| `client.Track("event", ctx)` | `server.Track("event", ctx)` |
| `client.Dispose()` | `await server.DisposeAsync()` |

### From the Statsig .NET SDK

| Statsig | A vs B |
| --- | --- |
| `StatsigServer.Initialize(secret)` | `await new AvsbServer(options).InitAsync()` |
| `StatsigServer.CheckGate(user, "gate")` | `server.GetBoolFlag("gate", false, ctx).IsEnabled()` |
| `StatsigServer.GetConfig(user, "config")` | `server.GetJsonFlag<T>("config", fallback, ctx).Value` |
| `new StatsigUser { UserID = "id" }` | `EvalContext.User("id")` |
| `StatsigServer.LogEvent(user, "event")` | `server.Track("event", ctx)` |
| `StatsigServer.Shutdown()` | `await server.DisposeAsync()` |

---

## 16. Licence

MIT. See [LICENSE](./LICENSE).
