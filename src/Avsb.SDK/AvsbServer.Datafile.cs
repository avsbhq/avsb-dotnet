using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Avsb.SDK.Core;
using Avsb.SDK.Logger;
using Avsb.SDK.Tracking;

namespace Avsb.SDK;

// AvsbServer, continued (FU-31): datafile lifecycle, the fetch and the polling loop.
public sealed partial class AvsbServer
{
    // -----------------------------------------------------------------------
    // Lifecycle.
    // -----------------------------------------------------------------------

    /// <summary>
    /// Fetch the datafile and start the background polling loop.
    ///
    /// The polling loop starts EVEN WHEN THE FIRST FETCH FAILS. Previously it started only on
    /// first-fetch success, so any startup blip left the SDK serving defaults forever with no retry
    /// and no bootstrap fallback: a transient network error at deploy time bricked flags until the
    /// process restarted.
    /// </summary>
    public async Task<InitResult> InitAsync(CancellationToken cancellationToken = default)
    {
        InitResult result = await FetchAndApplyAsync(cancellationToken).ConfigureAwait(false);
        StartPollLoop();
        return result;
    }

    /// <summary>Load a datafile from JSON. Used for bootstrap and in tests.</summary>
    public void LoadDatafile(string json)
    {
        FlagDatafile parsed = FlagDatafile.Deserialize(json)
            ?? throw new ArgumentException("Datafile JSON did not parse.", nameof(json));
        _datafile = parsed;
        StartTrackerIfNeeded(parsed);
    }

    /// <summary>The datafile currently in effect, or null before one loads.</summary>
    public FlagDatafile? Datafile => _datafile;

    /// <summary>True once a datafile is loaded and flags can be read.</summary>
    public bool IsReady => _datafile is not null;

    // -----------------------------------------------------------------------
    // Datafile transport.
    // -----------------------------------------------------------------------

    private async Task<InitResult> FetchAndApplyAsync(CancellationToken ct)
    {
        bool hadDatafile = _datafile is not null;
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, _datafileUrl);
            // Datafile reads are keyless by path: the SDK key is the path segment. No
            // Authorization header, no X-SDK-Key, nothing to leak in a proxy log.
            request.Headers.TryAddWithoutValidation("User-Agent", SdkMetadata.UserAgent);

            using HttpResponseMessage response =
                await _http.SendAsync(request, ct).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                _logger.Error(FetchFailureMessage(
                    $"HTTP {(int)response.StatusCode}", (int)response.StatusCode));
                return new InitResult
                {
                    Success = false,
                    Source = InitResultSource.Error,
                    Degraded = hadDatafile,
                };
            }

            string json = await response.Content.ReadAsStringAsync(ct).ConfigureAwait(false);
            FlagDatafile? parsed = FlagDatafile.Deserialize(json);
            if (parsed is null)
            {
                _logger.Error(FetchFailureMessage(
                    "the response body did not parse as a datafile", 0));
                return new InitResult
                {
                    Success = false,
                    Source = InitResultSource.Error,
                    Degraded = hadDatafile,
                };
            }

            _datafile = parsed;
            _logger.Info($"Datafile loaded (version {parsed.Version}, {parsed.Flags.Count} flags).");
            StartTrackerIfNeeded(parsed);
            SendHeartbeat(parsed);
            return new InitResult { Success = true, Source = InitResultSource.Network };
        }
        catch (TaskCanceledException ex)
        {
            _logger.Error(FetchFailureMessage("the request timed out", 0), ex);
            return new InitResult
            {
                Success = false,
                Source = InitResultSource.Timeout,
                Error = ex,
                Degraded = hadDatafile,
            };
        }
        catch (Exception ex)
        {
            _logger.Error(FetchFailureMessage(ex.Message, 0), ex);
            return new InitResult
            {
                Success = false,
                Source = InitResultSource.Error,
                Error = ex,
                Degraded = hadDatafile,
            };
        }
    }

    /// <summary>
    /// A datafile failure names the URL tried, what came back, and hands over a curl that
    /// reproduces it. Three strings is the difference between "flags do not work" and a fix.
    /// </summary>
    private string FetchFailureMessage(string what, int status)
    {
        var message = new StringBuilder();
        message.Append("AvsB datafile fetch failed: ").Append(what)
            .Append(" for ").Append(_datafileUrl).Append(". ");
        if (status == 404)
        {
            message.Append("A 404 means the key is not published in this environment: check the SDK"
                + " key against Environments in the project sidebar, in the A vs B dashboard, and publish"
                + " the environment if it has never been published. ");
        }
        else if (status == 403)
        {
            message.Append("A 403 usually means a proxy or firewall in front of cdn.avsb.cloud is"
                + " blocking the request. ");
        }
        else if (status == 0)
        {
            message.Append("No HTTP status came back, so this is a network, DNS, TLS, or timeout"
                + " problem rather than a rejected request. ");
        }
        message.Append("Reproduce it with: curl -i ").Append(_datafileUrl);
        message.Append(" . Until a datafile loads, every flag returns the default you passed and"
            + " reports source not_ready.");
        return message.ToString();
    }

    private void StartPollLoop()
    {
        if (!_options.Polling || _disposed || _pollLoop is not null) return;
        _pollLoop = PollLoopAsync(_cts.Token);
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        TimeSpan interval = _options.PollInterval ?? DefaultPollInterval;
        if (interval < TimeSpan.FromSeconds(1)) interval = TimeSpan.FromSeconds(1);

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // Jitter each cycle so a fleet that booted together does not stampede the CDN on
                // the same second forever.
                double jitterFactor = 1 + PollJitter * (SharedRandom.NextDouble() * 2 - 1);
                await Task.Delay(interval * jitterFactor, ct).ConfigureAwait(false);
                await FetchAndApplyAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                // A refresh failure must never kill the loop: the next cycle is the retry.
                _logger.Warn("Datafile refresh failed.", ex);
            }
        }
    }
}
