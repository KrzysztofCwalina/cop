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
                        new ProviderPropertySchema { Name = "Uri" },
                        new ProviderPropertySchema { Name = "Body", Type = "bytes" },
                        new ProviderPropertySchema { Name = "ContentType", Optional = true },
                    ]
                },
                new ProviderTypeSchema
                {
                    Name = "Response",
                    Properties =
                    [
                        new ProviderPropertySchema { Name = "StatusCode", Type = "int" },
                        new ProviderPropertySchema { Name = "Body", Type = "bytes" },
                        new ProviderPropertySchema { Name = "ContentType" },
                        new ProviderPropertySchema { Name = "Headers", Optional = true },
                    ]
                }
            ],
            Collections =
            [
                new ProviderCollectionSchema { Name = "Requests", ItemType = "Request" }
            ]
        };
        return schema.ToJson();
    }

    public override IEnumerable<DataSink>? GetSinks()
    {
        yield return new HttpSendSink();
    }

    private static readonly HttpClient _httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    public override Dictionary<string, Func<List<object?>, Task<object?>>>? GetProviderFunctions()
    {
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["Get"] = HttpGetAsync,
            ["Post"] = HttpPostAsync,
            ["Send"] = HttpSendAsync,
        };
    }

    /// <summary>
    /// http.Get(url, headers?) — performs an HTTP GET request.
    /// </summary>
    private static async Task<object?> HttpGetAsync(List<object?> args)
    {
        if (args.Count < 1)
            throw new InvalidOperationException("http.Get requires at least 1 argument: http.Get(url, headers?)");
        var url = args[0]?.ToString() ?? throw new InvalidOperationException("http.Get: url cannot be null");
        var headers = args.Count > 1 ? args[1] as DataObject : null;
        return await SendRequestAsync(HttpMethod.Get, url, body: null, headers);
    }

    /// <summary>
    /// http.Post(url, headers, body) — performs an HTTP POST request.
    /// </summary>
    private static async Task<object?> HttpPostAsync(List<object?> args)
    {
        if (args.Count < 2)
            throw new InvalidOperationException("http.Post requires at least 2 arguments: http.Post(url, headers, body)");
        var url = args[0]?.ToString() ?? throw new InvalidOperationException("http.Post: url cannot be null");
        var headers = args.Count > 1 ? args[1] as DataObject : null;
        var body = args.Count > 2 ? args[2] : null;
        return await SendRequestAsync(HttpMethod.Post, url, body, headers);
    }

    /// <summary>
    /// http.Send(method, url, headers, body) — performs an HTTP request with any method.
    /// </summary>
    private static async Task<object?> HttpSendAsync(List<object?> args)
    {
        if (args.Count < 2)
            throw new InvalidOperationException("http.Send requires at least 2 arguments: http.Send(method, url, headers?, body?)");
        var methodStr = args[0]?.ToString() ?? "GET";
        var url = args[1]?.ToString() ?? throw new InvalidOperationException("http.Send: url cannot be null");
        var headers = args.Count > 2 ? args[2] as DataObject : null;
        var body = args.Count > 3 ? args[3] : null;
        var method = new HttpMethod(methodStr);
        return await SendRequestAsync(method, url, body, headers);
    }

    private static async Task<object?> SendRequestAsync(HttpMethod method, string url, object? body, DataObject? headers)
    {
        try
        {
            using var request = new HttpRequestMessage(method, url);

            // Set request body
            if (body is not null)
            {
                byte[] bodyBytes = body switch
                {
                    byte[] b => b,
                    string s => System.Text.Encoding.UTF8.GetBytes(s),
                    DataObject obj => System.Text.Encoding.UTF8.GetBytes(obj.ToJson()),
                    _ => System.Text.Encoding.UTF8.GetBytes(body.ToString() ?? "")
                };
                request.Content = new ByteArrayContent(bodyBytes);

                // Default content type based on body type
                if (body is DataObject)
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                else if (body is string)
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
            }

            // Set request headers from DataObject fields
            if (headers is not null)
            {
                foreach (var (key, value) in headers.Fields)
                {
                    var headerValue = value?.ToString() ?? "";
                    if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && request.Content is not null)
                        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(headerValue);
                    else
                        request.Headers.TryAddWithoutValidation(key, headerValue);
                }
            }

            using var response = await _httpClient.SendAsync(request);

            // Build response DataObject
            var responseBody = await response.Content.ReadAsByteArrayAsync();
            var responseHeaders = new DataObject("Headers");
            foreach (var header in response.Headers)
                responseHeaders.Set(header.Key, string.Join(", ", header.Value));
            foreach (var header in response.Content.Headers)
                responseHeaders.Set(header.Key, string.Join(", ", header.Value));

            var result = new DataObject("Response");
            result.Set("StatusCode", (int)response.StatusCode);
            result.Set("Body", responseBody);
            result.Set("ContentType", response.Content.Headers.ContentType?.MediaType ?? "");
            result.Set("Headers", responseHeaders);
            return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or UriFormatException or InvalidOperationException or FormatException)
        {
            return new ErrorValue($"HTTP request failed: {ex.Message}");
        }
    }

    public override async IAsyncEnumerable<object> QueryStream(
        ProviderQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        // Start Kestrel if not already running
        await EnsureServerStartedAsync(cancellationToken);

        await foreach (var request in _requestChannel.Reader.ReadAllAsync(cancellationToken))
        {
            if (request.ReadError is not null)
            {
                // Emit ErrorValue for failed request reads — pipeline can handle via Error overload
                var errorValue = new ErrorValue(request.ReadError);
                errorValue.Set("__responseCompletion", request.ResponseCompletion);
                yield return errorValue;
            }
            else
            {
                // Wrap as DataObject so the cop evaluator can access properties
                var so = new DataObject("Request");
                so.Set("Method", request.Method);
                so.Set("Uri", request.Uri);
                so.Set("Body", request.Body);
                so.Set("ContentType", request.ContentType);
                so.Set("__responseCompletion", request.ResponseCompletion);
                yield return so;
            }
        }
    }

    private async Task EnsureServerStartedAsync(CancellationToken cancellationToken)
    {
        if (_app != null) return;

        var builder = WebApplication.CreateSlimBuilder();
        _app = builder.Build();

        _app.Map("{**path}", async (HttpContext ctx) =>
        {
            byte[] body = [];
            try
            {
                if (ctx.Request.ContentLength > 0)
                {
                    using var ms = new MemoryStream();
                    await ctx.Request.Body.CopyToAsync(ms, cancellationToken);
                    body = ms.ToArray();
                }
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // Client disconnected during request body read — emit ErrorValue into pipeline
                var tcs = new TaskCompletionSource<HttpResponseItem>();
                var errorObj = new DataObject("Request");
                errorObj.Set("__responseCompletion", tcs);
                var errorItem = new HttpRequestItem
                {
                    Method = ctx.Request.Method,
                    Uri = (ctx.Request.Path.Value ?? "/") + (ctx.Request.QueryString.Value ?? ""),
                    Body = [],
                    ContentType = "",
                    ResponseCompletion = tcs,
                    ReadError = $"Request read failed: {ex.Message}"
                };
                await _requestChannel.Writer.WriteAsync(errorItem, CancellationToken.None);
                var response = await tcs.Task;
                ctx.Response.StatusCode = response.StatusCode;
                ctx.Response.ContentType = response.ContentType;
                try { await ctx.Response.WriteAsync(response.Body); } catch { }
                return;
            }

            var tcs2 = new TaskCompletionSource<HttpResponseItem>();
            var requestItem = new HttpRequestItem
            {
                Method = ctx.Request.Method,
                Uri = (ctx.Request.Path.Value ?? "/") + (ctx.Request.QueryString.Value ?? ""),
                Body = body,
                ContentType = ctx.Request.ContentType ?? "",
                ResponseCompletion = tcs2
            };

            await _requestChannel.Writer.WriteAsync(requestItem, ctx.RequestAborted);

            // Wait for the cop pipeline to produce a response
            var response2 = await tcs2.Task;

            ctx.Response.StatusCode = response2.StatusCode;
            ctx.Response.ContentType = response2.ContentType;
            try
            {
                await ctx.Response.WriteAsync(response2.Body, ctx.RequestAborted);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // Client disconnected before response could be written — silently ignore
            }
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
    public string Uri { get; init; } = "/";
    public byte[] Body { get; init; } = [];
    public string ContentType { get; init; } = "";

    // Hidden from cop scripts — used by the sink to deliver the response
    internal TaskCompletionSource<HttpResponseItem> ResponseCompletion { get; init; } = null!;

    // Set when request body reading failed (network error)
    internal string? ReadError { get; init; }
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
/// Registered as "http.RESPONSES".
/// </summary>
public class HttpSendSink : DataSink
{
    public override string Name => "RESPONSES";

    public override Task WriteAsync(object? originalItem, object result)
    {
        // Extract the response completion from the original request DataObject
        TaskCompletionSource<HttpResponseItem>? tcs = null;
        if (originalItem is DataObject origSo)
            tcs = origSo.GetField("__responseCompletion") as TaskCompletionSource<HttpResponseItem>;

        if (tcs is null)
            throw new InvalidOperationException("http.RESPONSES can only be used with items from http.Requests.");

        HttpResponseItem response;
        if (result is ErrorValue err)
        {
            // Error propagated through pipeline — return 500 with error message
            var message = err.GetField("Message")?.ToString() ?? "Internal Server Error";
            response = new HttpResponseItem { StatusCode = 500, Body = $"{{\"error\":\"{message}\"}}", ContentType = "application/json" };
        }
        else if (result is DataObject so)
        {
            // Extract StatusCode, Body, ContentType from cop object
            var statusCode = so.GetField("StatusCode") is int sc ? sc : 200;
            var bodyField = so.GetField("Body");
            string body;
            if (bodyField is byte[] bytes)
                body = System.Text.Encoding.UTF8.GetString(bytes);
            else if (bodyField is DataObject bodyObj)
                body = bodyObj.ToJson();
            else
                body = bodyField?.ToString() ?? SerializeToJson(so);
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

    public string CollectionName => "Requests";

    public IAsyncEnumerable<object> QueryStream(CancellationToken cancellationToken = default)
    {
        return _provider.QueryStream(new ProviderQuery(), cancellationToken);
    }
}
