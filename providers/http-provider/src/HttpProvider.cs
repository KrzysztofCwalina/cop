using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Cop.Core;
using Cop.Lang;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Cop.Providers.Http;

/// <summary>
/// HTTP provider that exposes incoming HTTP requests as a streaming collection
/// and provides a sink for sending responses back.
/// </summary>
public class HttpProvider : DataProvider
{
    private readonly int _port;
    private readonly Channel<HttpRequestItem> _requestChannel;
    private WebApplication? _app;

    public HttpProvider() : this(5000) { }

    public HttpProvider(int port)
    {
        _port = port;
        _requestChannel = Channel.CreateUnbounded<HttpRequestItem>();
    }

    public override DataFormat SupportedFormats => DataFormat.AsyncStream;

    public override ReadOnlyMemory<byte> GetSchema()
    {
        var schema = new ProviderSchema
        {
            Types =
            [
                new ProviderTypeSchema
                {
                    Name = "Request",
                    Properties =
                    [
                        new ProviderPropertySchema { Name = "Method" },
                        new ProviderPropertySchema { Name = "Path" },
                        new ProviderPropertySchema { Name = "Body" },
                        new ProviderPropertySchema { Name = "ContentType", Optional = true },
                    ]
                },
                new ProviderTypeSchema
                {
                    Name = "Response",
                    Properties =
                    [
                        new ProviderPropertySchema { Name = "StatusCode", Type = "int" },
                        new ProviderPropertySchema { Name = "Body" },
                        new ProviderPropertySchema { Name = "ContentType" },
                    ]
                }
            ],
            Collections =
            [
                new ProviderCollectionSchema { Name = "Receive", ItemType = "Request" }
            ]
        };
        return schema.ToJson();
    }

    public override IEnumerable<DataSink>? GetSinks()
    {
        yield return new HttpSendSink();
    }

    public override async IAsyncEnumerable<object> QueryStream(
        ProviderQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Start Kestrel if not already running
        await EnsureServerStartedAsync(cancellationToken);

        await foreach (var request in _requestChannel.Reader.ReadAllAsync(cancellationToken))
        {
            // Wrap as DataObject so the cop evaluator can access properties
            var so = new DataObject("Request");
            so.Set("Method", request.Method);
            so.Set("Path", request.Path);
            so.Set("Body", request.Body);
            so.Set("ContentType", request.ContentType);
            so.Set("Query", request.Query);
            so.Set("__responseCompletion", request.ResponseCompletion);
            yield return so;
        }
    }

    private async Task EnsureServerStartedAsync(CancellationToken cancellationToken)
    {
        if (_app != null) return;

        var builder = WebApplication.CreateSlimBuilder();
        _app = builder.Build();

        _app.Map("{**path}", async (HttpContext ctx) =>
        {
            var body = "";
            if (ctx.Request.ContentLength > 0)
            {
                using var reader = new StreamReader(ctx.Request.Body);
                body = await reader.ReadToEndAsync(cancellationToken);
            }

            var tcs = new TaskCompletionSource<HttpResponseItem>();
            var requestItem = new HttpRequestItem
            {
                Method = ctx.Request.Method,
                Path = ctx.Request.Path.Value ?? "/",
                Body = body,
                ContentType = ctx.Request.ContentType ?? "",
                Query = ctx.Request.QueryString.Value ?? "",
                ResponseCompletion = tcs
            };

            await _requestChannel.Writer.WriteAsync(requestItem, ctx.RequestAborted);

            // Wait for the cop pipeline to produce a response
            var response = await tcs.Task;

            ctx.Response.StatusCode = response.StatusCode;
            ctx.Response.ContentType = response.ContentType;
            await ctx.Response.WriteAsync(response.Body, ctx.RequestAborted);
        });

        await _app.StartAsync(cancellationToken);
    }

    public async Task StopAsync()
    {
        if (_app != null)
        {
            _requestChannel.Writer.Complete();
            await _app.StopAsync();
            await _app.DisposeAsync();
            _app = null;
        }
    }
}

/// <summary>
/// Represents an incoming HTTP request in the streaming pipeline.
/// Carries a TaskCompletionSource to allow the sink to route the response back.
/// </summary>
public class HttpRequestItem
{
    public string Method { get; init; } = "";
    public string Path { get; init; } = "";
    public string Body { get; init; } = "";
    public string ContentType { get; init; } = "";
    public string Query { get; init; } = "";

    // Hidden from cop scripts — used by the sink to deliver the response
    internal TaskCompletionSource<HttpResponseItem> ResponseCompletion { get; init; } = null!;
}

/// <summary>
/// Represents the HTTP response to send back to the client.
/// </summary>
public class HttpResponseItem
{
    public int StatusCode { get; init; } = 200;
    public string Body { get; init; } = "";
    public string ContentType { get; init; } = "application/json";
}

/// <summary>
/// Sink that sends HTTP responses back to the requesting client.
/// Registered as "http.Send".
/// </summary>
public class HttpSendSink : DataSink
{
    public override string Name => "Send";

    public override Task WriteAsync(object? originalItem, object result)
    {
        // Extract the response completion from the original request DataObject
        TaskCompletionSource<HttpResponseItem>? tcs = null;
        if (originalItem is DataObject origSo)
            tcs = origSo.GetField("__responseCompletion") as TaskCompletionSource<HttpResponseItem>;

        if (tcs is null)
            throw new InvalidOperationException("http.Send can only be used with items from http.Receive.");

        HttpResponseItem response;
        if (result is DataObject so)
        {
            // Extract StatusCode, Body, ContentType from cop object
            var statusCode = so.GetField("StatusCode") is int sc ? sc : 200;
            var body = so.GetField("Body")?.ToString() ?? SerializeToJson(so);
            var contentType = so.GetField("ContentType")?.ToString() ?? "application/json";
            response = new HttpResponseItem { StatusCode = statusCode, Body = body, ContentType = contentType };
        }
        else
        {
            // Simple string or primitive result
            response = new HttpResponseItem { Body = result?.ToString() ?? "", ContentType = "text/plain" };
        }

        tcs.TrySetResult(response);
        return Task.CompletedTask;
    }

    private static string SerializeToJson(DataObject so)
    {
        var dict = new Dictionary<string, object?>();
        foreach (var (key, value) in so.Fields)
            dict[key] = value;
        return JsonSerializer.Serialize(dict);
    }
}

/// <summary>
/// Streaming collection source adapter for the HTTP provider.
/// Wraps the provider's QueryStream as an IStreamingCollectionSource.
/// </summary>
public class HttpStreamingSource : IStreamingCollectionSource
{
    private readonly HttpProvider _provider;

    public HttpStreamingSource(HttpProvider provider)
    {
        _provider = provider;
    }

    public string CollectionName => "Receive";

    public IAsyncEnumerable<object> QueryStream(CancellationToken cancellationToken = default)
    {
        return _provider.QueryStream(new ProviderQuery(), cancellationToken);
    }
}
