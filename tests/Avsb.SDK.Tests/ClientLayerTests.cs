using System.Text.Json;
using Avsb.SDK.Core;
using Avsb.SDK.Sticky;
using Avsb.SDK.Tracking;
using FluentAssertions;

namespace Avsb.SDK.Tests;

/// <summary>
/// Client-layer behaviour: readiness, runtime overrides, sticky, typed coercion, the endpoint
/// contract, key-shape validation, and the tracker's wire bodies.
///
/// Every server is built with Polling and Tracking off and Bootstrap set, so the tests never touch
/// the network and never need reflection to inject a datafile.
/// </summary>
public sealed class ClientLayerTests
{
    private const string SdkKey = "sdk_test_conformancefixture01";

    private const string Datafile = """
        {
          "version": 2,
          "projectType": "FEATURE_FLAG",
          "sdkKey": "sdk_test_conformancefixture01",
          "environmentKey": "test",
          "publishedAt": "2026-05-29T00:00:00.000Z",
          "collectEndpoint": "https://ingest.avsb.cloud",
          "flags": [
            {
              "id": "f1", "key": "bool-flag", "type": "boolean", "enabled": true,
              "defaultVariationId": "var_off",
              "variations": [
                {"id": "var_off", "key": "off", "value": false},
                {"id": "var_on",  "key": "on",  "value": true}
              ],
              "overrides": [],
              "rules": [
                {
                  "id": "rule_1", "type": "targeted_delivery", "enabled": true,
                  "audienceIds": [], "hashAttribute": "user.key", "trafficAllocation": null,
                  "variations": [{"variationId": "var_on", "percentage": 1}]
                }
              ]
            },
            {
              "id": "f2", "key": "string-flag", "type": "string", "enabled": true,
              "defaultVariationId": "var_a",
              "variations": [{"id": "var_a", "key": "variant_a", "value": "hello"}],
              "overrides": [], "rules": []
            },
            {
              "id": "f3", "key": "number-flag", "type": "number", "enabled": true,
              "defaultVariationId": "var_n",
              "variations": [{"id": "var_n", "key": "tuned", "value": 42}],
              "overrides": [], "rules": []
            },
            {
              "id": "f4", "key": "json-flag", "type": "json", "enabled": true,
              "defaultVariationId": "var_j",
              "variations": [{"id": "var_j", "key": "doc", "value": {"plan": "pro", "amount": 19.5}}],
              "overrides": [], "rules": []
            },
            {
              "id": "f5", "key": "ab-flag", "type": "string", "enabled": true,
              "defaultVariationId": "var_ctrl",
              "variations": [
                {"id": "var_ctrl", "key": "control",   "value": "old"},
                {"id": "var_trt",  "key": "treatment", "value": "new"}
              ],
              "overrides": [],
              "rules": [
                {
                  "id": "rule_ab", "type": "ab_test", "enabled": true,
                  "audienceIds": [], "hashAttribute": "user.key", "trafficAllocation": 1,
                  "variations": [
                    {"variationId": "var_ctrl", "percentage": 0.5},
                    {"variationId": "var_trt",  "percentage": 0.5}
                  ]
                }
              ]
            }
          ],
          "audiences": []
        }
        """;

    private static AvsbServer Loaded(
        IStickyBucketService? sticky = null,
        IReadOnlyDictionary<string, string>? overrides = null,
        int decisionLogCapacity = DecisionLog.DefaultCapacity) =>
        new(new AvsbServerOptions
        {
            SdkKey = SdkKey,
            Bootstrap = Datafile,
            Polling = false,
            Tracking = false,
            StickyBucketService = sticky,
            RuntimeOverrides = overrides,
            DecisionLogCapacity = decisionLogCapacity,
        });

    // -----------------------------------------------------------------------
    // Readiness.
    // -----------------------------------------------------------------------

    [Fact]
    public void BeforeADatafileLoads_EveryGetterReportsNotReady()
    {
        var server = new AvsbServer(new AvsbServerOptions
        {
            SdkKey = SdkKey,
            Polling = false,
            Tracking = false,
        });
        server.IsReady.Should().BeFalse();

        Flag<bool> flag = server.GetBoolFlag("any-flag", true, EvalContext.User("alice"));
        flag.Source.Should().Be(EvaluationSource.NotReady,
            "not_found means the flag does not exist; not_ready means the SDK never started");
        flag.Value.Should().BeTrue("the caller default survives, type intact");
        flag.IsEnabled().Should().BeFalse();
        flag.Exists().Should().BeFalse();
        flag.Reasons.Should().ContainSingle().Which.Should().Contain("InitAsync");
    }

    [Fact]
    public void BootstrapMakesFlagsWorkBeforeTheFirstFetch()
    {
        AvsbServer server = Loaded();
        server.IsReady.Should().BeTrue();
        server.GetBoolFlag("bool-flag", false, EvalContext.User("alice")).Value.Should().BeTrue();
    }

    [Fact]
    public void AMissingFlagReportsNotFoundWithTheCallerDefault()
    {
        AvsbServer server = Loaded();
        Flag<bool> flag = server.GetBoolFlag("no-such-flag", true, EvalContext.User("alice"));
        flag.Source.Should().Be(EvaluationSource.NotFound);
        flag.Value.Should().BeTrue();
        flag.Exists().Should().BeFalse();
    }

    // -----------------------------------------------------------------------
    // Typed getters.
    // -----------------------------------------------------------------------

    [Fact]
    public void EachTypedGetterReturnsItsOwnType()
    {
        AvsbServer server = Loaded();
        EvalContext ctx = EvalContext.User("alice");

        server.GetBoolFlag("bool-flag", false, ctx).Value.Should().BeTrue();
        server.GetStringFlag("string-flag", "fallback", ctx).Value.Should().Be("hello");
        server.GetNumberFlag("number-flag", 0, ctx).Value.Should().Be(42.0);
    }

    [Fact]
    public void AJsonFlagDeserialisesDirectlyIntoACallerType()
    {
        // GetJsonFlag used to read the flag as a STRING first and parse that, so a JSON flag
        // holding a real object resolved to null and every caller silently got their default.
        AvsbServer server = Loaded();
        Flag<Pricing?> pricing = server.GetJsonFlag<Pricing>(
            "json-flag", new Pricing("standard", 9.99), EvalContext.User("alice"));

        pricing.Source.Should().Be(EvaluationSource.Default,
            "the json flag has no rules, so it serves its default variation");
        pricing.Value.Should().NotBeNull();
        pricing.Value!.Plan.Should().Be("pro");
        pricing.Value.Amount.Should().Be(19.5);
    }

    [Fact]
    public void ATypeMismatchFallsBackToTheCallerDefaultInsteadOfCoercing()
    {
        AvsbServer server = Loaded();
        Flag<bool> asBool = server.GetBoolFlag("string-flag", false, EvalContext.User("alice"));
        asBool.Source.Should().Be(EvaluationSource.NotFound);
        asBool.Value.Should().BeFalse();
        asBool.Reasons.Should().ContainSingle().Which.Should().Contain("string-flag");

        Flag<double> asNumber = server.GetNumberFlag("string-flag", -1, EvalContext.User("alice"));
        asNumber.Value.Should().Be(-1);
    }

    private sealed record Pricing(string Plan, double Amount);

    // -----------------------------------------------------------------------
    // Runtime overrides.
    // -----------------------------------------------------------------------

    [Fact]
    public void ARuntimeOverrideWinsAndReportsItsOwnSource()
    {
        AvsbServer server = Loaded(overrides: new Dictionary<string, string> { ["bool-flag"] = "off" });
        Flag<bool> flag = server.GetBoolFlag("bool-flag", true, EvalContext.User("alice"));

        flag.Source.Should().Be(EvaluationSource.RuntimeOverride);
        flag.Value.Should().BeFalse("the override's variation value, not the rule's");
        flag.VariationKey.Should().Be("off");
        flag.IsEnabled().Should().BeFalse("a falsy override is not enabled");
    }

    [Fact]
    public void ARuntimeOverrideNamingAnUnknownVariationIsIgnoredRatherThanInvented()
    {
        AvsbServer server = Loaded(
            overrides: new Dictionary<string, string> { ["bool-flag"] = "no-such-variation" });
        Flag<bool> flag = server.GetBoolFlag("bool-flag", false, EvalContext.User("alice"));
        flag.Source.Should().Be(EvaluationSource.Rule);
    }

    // -----------------------------------------------------------------------
    // Sticky bucketing.
    // -----------------------------------------------------------------------

    [Fact]
    public void AnAbTestDecisionSavesTheVariationIdNeverTheKey()
    {
        var sticky = new InMemoryStickyBucketService();
        AvsbServer server = Loaded(sticky);

        Flag<string> first = server.GetStringFlag("ab-flag", "fallback", EvalContext.User("alice"));
        first.Source.Should().Be(EvaluationSource.Rule);

        StickyAssignment? saved = sticky.Lookup("alice", "ab-flag");
        saved.Should().NotBeNull("an ab_test decision must be persisted");
        // Saving the key corrupts the store: Lookup resolves by id and would never match again.
        saved!.VariationId.Should().StartWith("var_");
        saved.RuleId.Should().Be("rule_ab");
        saved.RuleType.Should().Be(RuleType.AbTest);
    }

    [Fact]
    public void AStoredAssignmentIsServedOnTheNextEvaluation()
    {
        var sticky = new InMemoryStickyBucketService();
        sticky.Save("alice", "ab-flag", new StickyAssignment
        {
            VariationId = "var_trt",
            RuleId = "rule_ab",
            RuleType = RuleType.AbTest,
            AssignedAt = 1,
        });
        AvsbServer server = Loaded(sticky);

        Flag<string> flag = server.GetStringFlag("ab-flag", "fallback", EvalContext.User("alice"));
        flag.Source.Should().Be(EvaluationSource.Sticky, "nothing ever called Lookup before this");
        flag.Value.Should().Be("new");
        flag.VariationKey.Should().Be("treatment");
        flag.RuleId.Should().Be("rule_ab");
    }

    [Fact]
    public void AStoredAssignmentForADeletedVariationIsAMissNotAnError()
    {
        var sticky = new InMemoryStickyBucketService();
        sticky.Save("alice", "ab-flag", new StickyAssignment
        {
            VariationId = "var_deleted",
            RuleId = "rule_ab",
            RuleType = RuleType.AbTest,
            AssignedAt = 1,
        });
        AvsbServer server = Loaded(sticky);

        server.GetStringFlag("ab-flag", "fallback", EvalContext.User("alice"))
            .Source.Should().Be(EvaluationSource.Rule);
    }

    [Fact]
    public void ARolloutAndADefaultAreNeverPersistedAsAssignments()
    {
        var sticky = new InMemoryStickyBucketService();
        AvsbServer server = Loaded(sticky);

        server.GetBoolFlag("bool-flag", false, EvalContext.User("alice"));
        sticky.Lookup("alice", "bool-flag").Should().BeNull(
            "a targeted delivery is a rollout, not an assignment");

        server.GetStringFlag("string-flag", "fallback", EvalContext.User("alice"));
        sticky.Lookup("alice", "string-flag").Should().BeNull();
    }

    // -----------------------------------------------------------------------
    // Decision log.
    // -----------------------------------------------------------------------

    [Fact]
    public void TheDecisionLogRecordsAndIsBounded()
    {
        AvsbServer server = Loaded(decisionLogCapacity: 2);
        server.GetBoolFlag("bool-flag", false, EvalContext.User("alice"));
        server.GetStringFlag("string-flag", "x", EvalContext.User("alice"));
        server.GetNumberFlag("number-flag", 0, EvalContext.User("alice"));

        IReadOnlyList<DecisionLogEntry> entries = server.DecisionLogEntries;
        entries.Should().HaveCount(2, "the oldest entry is dropped rather than growing forever");
        entries[0].FlagKey.Should().Be("string-flag");
        entries[1].FlagKey.Should().Be("number-flag");
    }

    [Fact]
    public void DecisionLogRecordingCanBeTurnedOffEntirely()
    {
        AvsbServer server = Loaded(decisionLogCapacity: 0);
        server.GetBoolFlag("bool-flag", false, EvalContext.User("alice"));
        server.DecisionLogEntries.Should().BeEmpty();
    }

    // -----------------------------------------------------------------------
    // Endpoint contract.
    // -----------------------------------------------------------------------

    [Fact]
    public void TheDatafileUrlComesFromTheEndpointContract()
    {
        // The old form, new Uri(base, "/datafile/{key}"), discarded the base path, so even a
        // correctly configured endpoint fetched the wrong one.
        Endpoints.DatafileUrl("sdk_production_abc123")
            .Should().Be("https://cdn.avsb.cloud/sdk_production_abc123/datafile.json");
        Endpoints.DatafileUrl("sdk_production_abc123", "https://cdn.example.test/")
            .Should().Be("https://cdn.example.test/sdk_production_abc123/datafile.json");
        Endpoints.CollectUrl("batch")
            .Should().Be("https://ingest.avsb.cloud/v1/collect/batch");
        Endpoints.CollectUrl("orders", "https://ingest.avsb.cloud/v1/collect/batch")
            .Should().Be("https://ingest.avsb.cloud/v1/collect/orders",
                "a collectEndpoint that already carries a path is normalised, never doubled");
    }

    [Fact]
    public void StreamingAndHeartbeatNeverGuessAHost()
    {
        Endpoints.ResolveStreamEndpoint().Available.Should().BeFalse(
            "the stream.avsb.cloud default gave Go customers an infinite reconnect loop");
        Endpoints.ResolveHeartbeatEndpoint(null).Should().BeNull(
            "a proxied deployment publishes its own origin");
        Endpoints.ResolveHeartbeatEndpoint("https://app.avsb.cloud/api/sdk/heartbeat")
            .Should().Be("https://app.avsb.cloud/api/sdk/heartbeat");
    }

    // -----------------------------------------------------------------------
    // Key shape.
    // -----------------------------------------------------------------------

    [Fact]
    public void KeyShapeValidationNamesWhatWasPastedInstead()
    {
        SdkKeyShape.Validate("sdk_production_ttqm0eaj4vth1krcb2xn").Valid.Should().BeTrue();

        SdkKeyShape.Validate("https://app.avsb.cloud/projects/p_1/settings")
            .Message.Should().Contain("URL");
        SdkKeyShape.Validate("avsb_pat_abcdefghijklmnop").Message.Should().Contain("API token");
        SdkKeyShape.Validate("sdk_production_abc").Message.Should().Contain("truncated");
        SdkKeyShape.Validate(null).Message.Should().Contain("no SDK key was passed");

        SdkKeyShape.Describe("sdk_production_ttqm0eaj4vth1krcb2xn")
            .Should().NotContain("ttqm0eaj4vth1krc", "the id body is not reproduced in a message");
    }

    // -----------------------------------------------------------------------
    // Tracker wire bodies.
    // -----------------------------------------------------------------------

    private static ServerTracker.Options TrackerOptions(int batchSize = 1000, int maxQueue = 1000) =>
        new()
        {
            SdkKey = "sdk_production_abc123",
            // Nothing listens here, so a send fails at once instead of reaching the network.
            CollectEndpoint = "http://127.0.0.1:1",
            FlushInterval = TimeSpan.FromHours(1),
            BatchSize = batchSize,
            MaxQueueSize = maxQueue,
            RetryAttempts = 0,
            RequestTimeout = TimeSpan.FromMilliseconds(250),
            Logger = Avsb.SDK.Logger.NullLogger.Instance,
        };

    [Fact]
    public void AnExposureBatchCarriesTheLockedFieldNames()
    {
        ServerTracker tracker = ServerTracker.Start(TrackerOptions());
        string body = tracker.ExposureBatchBody(new[]
        {
            new ExposureEvent
            {
                EventId = "evt-1",
                VisitorId = "user_42",
                ExperimentId = "rule_ab_split",
                VariationId = "var_treatment",
                EventName = ExposureEvent.FlagExposure,
                Timestamp = 1780000000000L,
                PageUrl = string.Empty,
                Properties = new Dictionary<string, object?> { ["flagKey"] = "checkout-v2" },
                Attributes = new Dictionary<string, string> { ["plan"] = "pro" },
            },
        });

        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement root = json.RootElement;
        root.GetProperty("sdkKey").GetString().Should().Be("sdk_production_abc123",
            "ingest routes are not key-pathed, so the key rides in the envelope");

        JsonElement evt = root.GetProperty("events")[0];
        evt.GetProperty("eventId").GetString().Should().Be("evt-1");
        evt.GetProperty("eventName").GetString().Should().Be("flag_exposure");
        evt.GetProperty("revenue").ValueKind.Should().Be(JsonValueKind.Null,
            "revenue is required even when it is null");
        evt.GetProperty("pageUrl").GetString().Should().BeEmpty("there is no page on a server");
        evt.TryGetProperty("value", out _).Should().BeFalse(
            "an absent optional field is omitted, never sent as null: the ingestion schema treats"
            + " it as optional, not nullable, so a null fails the whole batch");
    }

    [Fact]
    public void AMetricBatchHasNoPropertiesField()
    {
        ServerTracker tracker = ServerTracker.Start(TrackerOptions());
        string body = tracker.MetricBatchBody(new[]
        {
            new MetricEvent
            {
                EventId = "evt-2",
                VisitorId = "user_42",
                EventName = "signup_completed",
                Value = 1,
                Timestamp = 1780000000000L,
            },
        });

        using JsonDocument json = JsonDocument.Parse(body);
        JsonElement evt = json.RootElement.GetProperty("metricEvents")[0];
        evt.GetProperty("revenue").ValueKind.Should().Be(JsonValueKind.Null);
        evt.GetProperty("value").GetDouble().Should().Be(1);
        evt.TryGetProperty("properties", out _).Should().BeFalse(
            "anything sent under that name is stripped before storage, so it would look delivered"
            + " and be gone");
        evt.TryGetProperty("experimentId", out _).Should().BeFalse(
            "attribution happens server-side from exposures");
    }

    [Fact]
    public void TheQueueIsBoundedAndStampsEventIdsAtEnqueue()
    {
        ServerTracker tracker = ServerTracker.Start(TrackerOptions(batchSize: 1000, maxQueue: 3));
        for (int i = 0; i < 6; i++)
        {
            tracker.Queue(new ExposureEvent { VisitorId = "user_" + i });
        }

        tracker.QueuedExposures.Should().Be(3, "a broken endpoint cannot grow the heap");
        foreach (ExposureEvent evt in tracker.SnapshotExposures())
        {
            evt.EventId.Should().NotBeNullOrEmpty(
                "the id is stamped at enqueue, so a retried batch carries the same ids");
        }
    }

    [Fact]
    public void BackoffClimbsCapsAndHonoursRetryAfter()
    {
        Backoff.State first = Backoff.Next(null, null);
        first.Attempt.Should().Be(1);
        first.DelayMs.Should().BeInRange(800, 1200);

        Backoff.State second = Backoff.Next(first, null);
        second.Attempt.Should().Be(2);
        second.DelayMs.Should().BeInRange(1600, 2400);

        Backoff.State honoured = Backoff.Next(null, 5_000);
        honoured.DelayMs.Should().Be(5_000);
        Backoff.Next(honoured, null).Attempt.Should().Be(2,
            "the next miss climbs the curve rather than restarting it");

        Backoff.ParseRetryAfter("5").Should().Be(5_000);
        Backoff.ParseRetryAfter("Wed, 21 Oct 2026 07:28:00 GMT").Should().BeNull(
            "the HTTP-date form is not supported, and guessing would be worse");
    }
}
