// Connects HTTP request functions and streaming server endpoints to Cop pipelines.

using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading.Channels;
using Cop.Core;
using Cop.Lang;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;

namespace Cop.Providers.Http;

/// <summary>
/// HTTP data provider for outbound HTTP client functions (Get, Post, Send).
/// This is a sync data provider that exposes helper functions, not collections.
/// </summary>
public class HttpProvider : DataProvider
{

    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema()
    {
        ProviderSchema schema = new ProviderSchema
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
                        new ProviderPropertySchema { Name = "Headers", IsOptional = true },
                    ]
                }
            ],
            Collections = []
        };
        return schema.ToJson();
    }

    private static readonly HttpClient s_httpClient = new() { Timeout = TimeSpan.FromSeconds(30) };

    /// <inheritdoc />
    public override ValueTask<object?> QueryAsync(
        ProviderQuery query, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult<object?>(null);
    }

    /// <inheritdoc />
    public override Dictionary<string,
        Func<List<object?>, CancellationToken, ValueTask<object?>>>? GetFunctions()
    {
        return new(StringComparer.OrdinalIgnoreCase)
        {
            ["Get"] = HttpGetAsync,
            ["Post"] = HttpPostAsync,
            ["Send"] = HttpSendAsync,
        };
    }

    private static ValueTask<object?> HttpGetAsync(
        List<object?> args, CancellationToken cancellationToken)
    {
        if (args.Count < 1)
        {
            throw new InvalidOperationException("http.Get requires at least 1 argument: http.Get(url, headers?)");
        }

        string url = args[0]?.ToString() ?? throw new InvalidOperationException("http.Get: url cannot be null");
        DataObject? headers = args.Count > 1 ? args[1] as DataObject : null;
        return SendRequestAsync(new(HttpMethod.Get, url) { Headers = headers }, cancellationToken);
    }

    private static ValueTask<object?> HttpPostAsync(
        List<object?> args, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            throw new InvalidOperationException("http.Post requires at least 2 arguments: http.Post(url, headers, body)");
        }

        string url = args[0]?.ToString() ?? throw new InvalidOperationException("http.Post: url cannot be null");
        DataObject? headers = args.Count > 1 ? args[1] as DataObject : null;
        object? body = args.Count > 2 ? args[2] : null;
        return SendRequestAsync(
            new(HttpMethod.Post, url) { Body = body, Headers = headers }, cancellationToken);
    }

    private static ValueTask<object?> HttpSendAsync(
        List<object?> args, CancellationToken cancellationToken)
    {
        if (args.Count < 2)
        {
            throw new InvalidOperationException("http.Send requires at least 2 arguments: http.Send(method, url, headers?, body?)");
        }

        string methodStr = args[0]?.ToString() ?? "GET";
        string url = args[1]?.ToString() ?? throw new InvalidOperationException("http.Send: url cannot be null");
        DataObject? headers = args.Count > 2 ? args[2] as DataObject : null;
        object? body = args.Count > 3 ? args[3] : null;
        HttpMethod method = new HttpMethod(methodStr);
        return SendRequestAsync(
            new(method, url) { Body = body, Headers = headers }, cancellationToken);
    }

    private sealed record HttpCall(HttpMethod Method, string Url)
    {
        internal object? Body { get; init; }
        internal DataObject? Headers { get; init; }
    }

    private static async ValueTask<object?> SendRequestAsync(
        HttpCall call, CancellationToken cancellationToken)
    {
        object? body = call.Body;
        DataObject? headers = call.Headers;
        try
        {
            using HttpRequestMessage request = new HttpRequestMessage(call.Method, call.Url);

            if (body is not null)
            {
                byte[] bodyBytes = body switch
                {
                    byte[] b => b,
                    string s => System.Text.Encoding.UTF8.GetBytes(s),
                    DataObject obj => System.Text.Encoding.UTF8.GetBytes(obj.ToJson()),
                    _ => System.Text.Encoding.UTF8.GetBytes(body.ToString() ?? string.Empty)
                };
                request.Content = new ByteArrayContent(bodyBytes);

                if (body is DataObject)
                {
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("application/json") { CharSet = "utf-8" };
                }
                else if (body is string)
                {
                    request.Content.Headers.ContentType = new System.Net.Http.Headers.MediaTypeHeaderValue("text/plain") { CharSet = "utf-8" };
                }
            }

            if (headers is not null)
            {
                foreach ((string? key, object? value) in headers.Fields)
                {
                    string headerValue = value?.ToString() ?? string.Empty;
                    if (key.Equals("Content-Type", StringComparison.OrdinalIgnoreCase) && request.Content is not null)
                    {
                        request.Content.Headers.ContentType = System.Net.Http.Headers.MediaTypeHeaderValue.Parse(headerValue);
                    }
                    else
                    {
                        request.Headers.TryAddWithoutValidation(key, headerValue);
                    }
                }
            }

            using HttpResponseMessage response = await s_httpClient.SendAsync(request, cancellationToken)
                .ConfigureAwait(false);

            byte[] responseBody = await response.Content.ReadAsByteArrayAsync(cancellationToken)
                .ConfigureAwait(false);
            DataObject responseHeaders = new DataObject("Headers");
            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Headers)
            {
                responseHeaders.Set(header.Key, string.Join(", ", header.Value));
            }

            foreach (KeyValuePair<string, IEnumerable<string>> header in response.Content.Headers)
            {
                responseHeaders.Set(header.Key, string.Join(", ", header.Value));
            }

            DataObject result = new DataObject("Response");
            result.Set("StatusCode", (int)response.StatusCode);
            result.Set("Body", responseBody);
            result.Set("ContentType", response.Content.Headers.ContentType?.MediaType ?? string.Empty);
            result.Set("Headers", responseHeaders);
            return result;
        }
        catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
        {
            return new ErrorValue($"HTTP request timed out: {ex.Message}");
        }
        catch (Exception ex) when (ex is HttpRequestException or UriFormatException or InvalidOperationException or FormatException)
        {
            return new ErrorValue($"HTTP request failed: {ex.Message}");
        }
    }
}
