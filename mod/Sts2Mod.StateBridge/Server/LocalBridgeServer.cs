using System.Net;
using System.Text.Json;
using Sts2Mod.StateBridge.Configuration;
using Sts2Mod.StateBridge.Contracts;
using Sts2Mod.StateBridge.Logging;
using Sts2Mod.StateBridge.Providers;

namespace Sts2Mod.StateBridge.Server;

public sealed class LocalBridgeServer : IAsyncDisposable
{
    private readonly BridgeOptions _options;
    private readonly IGameStateProvider _provider;
    private readonly IBridgeLogger _logger;
    private readonly HttpListener _listener;
    private readonly JsonSerializerOptions _jsonOptions;
    private CancellationTokenSource? _cts;
    private Task? _loopTask;

    public LocalBridgeServer(BridgeOptions options, IGameStateProvider provider, IBridgeLogger logger)
    {
        _options = options;
        _provider = provider;
        _logger = logger;
        _listener = new HttpListener();
        _listener.Prefixes.Add(options.Prefix);
        _jsonOptions = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true,
        };
    }

    public Task StartAsync(CancellationToken cancellationToken = default)
    {
        if (_listener.IsListening)
        {
            return Task.CompletedTask;
        }

        AgentStatusStateStore.Clear();
        _cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _listener.Start();
        _loopTask = Task.Run(() => RunLoopAsync(_cts.Token), CancellationToken.None);
        _logger.Info($"Local bridge listening on {_options.Prefix}");
        return Task.CompletedTask;
    }

    public async Task StopAsync()
    {
        if (!_listener.IsListening)
        {
            return;
        }

        _cts?.Cancel();
        _listener.Stop();
        if (_loopTask is not null)
        {
            await _loopTask.ConfigureAwait(false);
        }
        AgentStatusStateStore.Clear();
        _logger.Info("Local bridge stopped");
    }

    private async Task RunLoopAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext? context = null;
            try
            {
                context = await _listener.GetContextAsync().WaitAsync(cancellationToken).ConfigureAwait(false);
                await HandleAsync(context, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (HttpListenerException) when (!_listener.IsListening)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.Error("Unhandled bridge loop failure", ex);
                if (context is not null)
                {
                    await WriteAsync(context.Response, 500, new ErrorResponse("bridge_error", ex.Message), cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }

    private async Task HandleAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        var request = context.Request;
        var path = request.Url?.AbsolutePath?.TrimEnd('/').ToLowerInvariant() ?? string.Empty;
        if (string.Equals(request.HttpMethod, "POST", StringComparison.OrdinalIgnoreCase))
        {
            await HandlePostAsync(context, path, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(request.HttpMethod, "DELETE", StringComparison.OrdinalIgnoreCase))
        {
            await HandleDeleteAsync(context, path, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(request.HttpMethod, "GET", StringComparison.OrdinalIgnoreCase))
        {
            await WriteAsync(context.Response, 405, new ErrorResponse("method_not_allowed", "Only GET, POST, and DELETE are supported."), cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var phase = request.QueryString["phase"];
            object payload = path switch
            {
                "/health" => _provider.GetHealth(),
                "/snapshot" => _provider.GetSnapshot(phase),
                "/actions" => _provider.GetActions(phase),
                "/agent-status" => AgentStatusStateStore.GetCurrent(),
                "/action-log" => ActionLogStore.GetAll(),
                _ => new ErrorResponse("not_found", $"Unknown endpoint: {path}")
            };
            var statusCode = payload is ErrorResponse ? 404 : 200;
            await WriteAsync(context.Response, statusCode, payload, cancellationToken).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.Error("Request handling failed", ex);
            await WriteAsync(context.Response, 500, new ErrorResponse("state_export_failed", ex.Message), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandlePostAsync(HttpListenerContext context, string path, CancellationToken cancellationToken)
    {
        if (string.Equals(path, "/agent-status", StringComparison.Ordinal))
        {
            await HandleAgentStatusPostAsync(context, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (!string.Equals(path, "/apply", StringComparison.Ordinal))
        {
            await WriteAsync(context.Response, 404, new ErrorResponse("not_found", $"Unknown endpoint: {path}"), cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            var request = await JsonSerializer.DeserializeAsync<ActionRequest>(
                context.Request.InputStream,
                _jsonOptions,
                cancellationToken).ConfigureAwait(false);

            if (request is null)
            {
                await WriteAsync(context.Response, 400, new ErrorResponse("invalid_request", "Request body is required."), cancellationToken).ConfigureAwait(false);
                return;
            }

            var response = _provider.ApplyAction(request);
            var statusCode = string.Equals(response.Status, "accepted", StringComparison.OrdinalIgnoreCase) ? 200 : 409;
            if (string.Equals(response.Status, "failed", StringComparison.OrdinalIgnoreCase))
            {
                statusCode = 500;
            }

            await WriteAsync(context.Response, statusCode, response, cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await WriteAsync(context.Response, 400, new ErrorResponse("invalid_json", ex.Message), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task HandleDeleteAsync(HttpListenerContext context, string path, CancellationToken cancellationToken)
    {
        if (string.Equals(path, "/agent-status", StringComparison.Ordinal))
        {
            await WriteAsync(context.Response, 200, AgentStatusStateStore.Clear(), cancellationToken).ConfigureAwait(false);
            return;
        }

        if (string.Equals(path, "/action-log", StringComparison.Ordinal))
        {
            await WriteAsync(context.Response, 200, ActionLogStore.Clear(), cancellationToken).ConfigureAwait(false);
            return;
        }

        await WriteAsync(context.Response, 404, new ErrorResponse("not_found", $"Unknown endpoint: {path}"), cancellationToken).ConfigureAwait(false);
    }

    private async Task HandleAgentStatusPostAsync(HttpListenerContext context, CancellationToken cancellationToken)
    {
        try
        {
            var request = await JsonSerializer.DeserializeAsync<AgentStatusUpdateRequest>(
                context.Request.InputStream,
                _jsonOptions,
                cancellationToken).ConfigureAwait(false);

            if (!AgentStatusStateStore.TryValidate(request, out var message))
            {
                await WriteAsync(context.Response, 400, new ErrorResponse("invalid_request", message), cancellationToken).ConfigureAwait(false);
                return;
            }

            await WriteAsync(context.Response, 200, AgentStatusStateStore.Update(request!), cancellationToken).ConfigureAwait(false);
        }
        catch (JsonException ex)
        {
            await WriteAsync(context.Response, 400, new ErrorResponse("invalid_json", ex.Message), cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task WriteAsync(HttpListenerResponse response, int statusCode, object payload, CancellationToken cancellationToken)
    {
        response.StatusCode = statusCode;
        response.ContentType = "application/json; charset=utf-8";
        await JsonSerializer.SerializeAsync(response.OutputStream, payload, _jsonOptions, cancellationToken).ConfigureAwait(false);
        response.OutputStream.Close();
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync().ConfigureAwait(false);
        _listener.Close();
        _cts?.Dispose();
    }
}
