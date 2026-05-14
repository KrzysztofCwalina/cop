using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Cop.Core;
using Cop.Lang;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Cop.Providers.Http;

/// <summary>
/// HTTP source provider that exposes incoming HTTP requests as a streaming collection.
/// Starts a Kestrel web server and yields Request DataObjects as they arrive.
/// </summary>
public class HttpSource : SourceProvider
{
    private readonly int _port;
    private readonly Channel<HttpRequestItem> _requestChannel;
    private WebApplication? _app;

    public HttpSource() : this(5000) { }

    public HttpSource(int port)
    {
        _port = port;
        _requestChannel = Channel.CreateUnbounded<HttpRequestItem>();
    }

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
            ],
            Collections =
            [
                new ProviderCollectionSchema { Name = "Requests", ItemType = "Request" }
            ]
        };
        return schema.ToJson();
    }

    public override async IAsyncEnumerable<object> QueryStream(
        ProviderQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureServerStartedAsync(cancellationToken);

        await foreach (var request in _requestChannel.Reader.ReadAllAsync(cancellationToken))
        {
            if (request.ReadError is not null)
            {
                var errorValue = new ErrorValue(request.ReadError);
                errorValue.Set("__responseCompletion", request.ResponseCompletion);
                yield return errorValue;
            }
            else
            {
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
                var tcs = new TaskCompletionSource<HttpResponseItem>();
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

            var response2 = await tcs2.Task;

            ctx.Response.StatusCode = response2.StatusCode;
            ctx.Response.ContentType = response2.ContentType;
            try
            {
                await ctx.Response.WriteAsync(response2.Body, ctx.RequestAborted);
            }
            catch (Exception ex) when (ex is IOException or OperationCanceledException)
            {
                // Client disconnected — silently ignore
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
/// HTTP sink provider that sends responses back to requesting clients.
/// Uses the __responseCompletion TaskCompletionSource embedded in each request item.
/// </summary>
public class HttpSink : SinkProvider
{
    public override string Name => "RESPONSES";

    public override Task WriteAsync(object? originalItem, object result)
    {
        TaskCompletionSource<HttpResponseItem>? tcs = null;
        if (originalItem is DataObject origSo)
            tcs = origSo.GetField("__responseCompletion") as TaskCompletionSource<HttpResponseItem>;

        if (tcs is null)
            throw new InvalidOperationException("http.RESPONSES can only be used with items from http.Requests.");

        HttpResponseItem response;
        if (result is ErrorValue err)
        {
            var message = err.GetField("Message")?.ToString() ?? "Internal Server Error";
            response = new HttpResponseItem { StatusCode = 500, Body = $"{{\"error\":\"{message}\"}}", ContentType = "application/json" };
        }
        else if (result is DataObject so)
        {
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
/// HTTP data provider for outbound HTTP client functions (Get, Post, Send).
/// This is a sync data provider that exposes helper functions, not collections.
/// </summary>
public class HttpProvider : DataProvider
{
    public override DataFormat SupportedFormats => DataFormat.Json;

    public override ReadOnlyMemory<byte> GetSchema()
    {
        var schema = new ProviderSchema
        {
            Types =
            [
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
            Collections = []
        };
        return schema.ToJson();
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

    private static async Task<object?> HttpGetAsync(List<object?> args)
    {
        if (args.Count < 1)
            throw new InvalidOperationException("http.Get requires at least 1 argument: http.Get(url, headers?)");
        var url = args[0]?.ToString() ?? throw new InvalidOperationException("http.Get: url cannot be null");
        var headers = args.Count > 1 ? args[1] as DataObject : null;
        return await SendRequestAsync(HttpMethod.Get, url, body: null, headers);
    }

    private static async Task<object?> HttpPostAsync(List<object?> args)
    {
        if (args.Count < 2)
            throw new InvalidOperationException("http.Post requires at least 2 arguments: http.Post(url, headers, body)");
        var url = args[0]?.ToString() ?? throw new InvalidOperationException("http.Post: url cannot be null");
        var headers = args.Count > 1 ? args[1] as DataObject : null;
        var body = args.Count > 2 ? args[2] : null;
        return await SendRequestAsync(HttpMethod.Post, url, body, headers);
    }

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

                if (body is DataObject)
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                else if (body is string)
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
            }

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
    internal TaskCompletionSource<HttpResponseItem> ResponseCompletion { get; init; } = null!;
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
