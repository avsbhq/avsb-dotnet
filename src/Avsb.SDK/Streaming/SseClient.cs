namespace Avsb.SDK.Streaming;

/// <summary>
/// HTTP-based Server-Sent Events client. Delivers each event line to a provided callback,
/// reconnecting with exponential back-off on failure.
///
/// NOT WIRED INTO AvsbServer YET, and deliberately so. Streaming needs an endpoint, and the only
/// correct source for one is the datafile's streamEndpoint field resolved through
/// Endpoints.ResolveStreamEndpoint. Defaulting to a guessed host is what gave Go customers an
/// infinite reconnect loop against DNS that does not resolve, so this class stays available and
/// unwired until the streaming session lands. AvsbServer no longer carries the _sseClient field
/// that was declared, disposed, and never assigned. The background polling loop keeps flags fresh
/// in the meantime.
/// </summary>
public sealed class SseClient : IAsyncDisposable
{
    private readonly HttpClient _http;
    private readonly Uri _endpoint;
    private readonly Func<string, Task> _onEvent;
    private readonly CancellationTokenSource _cts = new();
    private Task? _loop;

    public SseClient(HttpClient http, Uri endpoint, Func<string, Task> onEvent)
    {
        _http = http;
        _endpoint = endpoint;
        _onEvent = onEvent;
    }

    public void Start()
    {
        _loop = ReadLoopAsync(_cts.Token);
    }

    private async Task ReadLoopAsync(CancellationToken ct)
    {
        int delayMs = 1_000;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                using var response = await _http.GetAsync(_endpoint,
                    HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);

                response.EnsureSuccessStatusCode();
                await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
                using var reader = new System.IO.StreamReader(stream);

                while (!reader.EndOfStream && !ct.IsCancellationRequested)
                {
                    var line = await reader.ReadLineAsync().ConfigureAwait(false);
                    if (line is null) break;
                    if (line.StartsWith("data:", StringComparison.Ordinal))
                    {
                        var data = line[5..].Trim();
                        if (!string.IsNullOrEmpty(data))
                            await _onEvent(data).ConfigureAwait(false);
                    }
                }

                delayMs = 1_000; // reset on clean disconnect
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch
            {
                // Back-off before reconnect.
                await Task.Delay(Math.Min(delayMs, 30_000), ct).ConfigureAwait(false);
                delayMs = Math.Min(delayMs * 2, 30_000);
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cts.Cancel();
        if (_loop is not null)
        {
            try { await _loop.ConfigureAwait(false); }
            catch { /* ignore */ }
        }
        _cts.Dispose();
    }
}
