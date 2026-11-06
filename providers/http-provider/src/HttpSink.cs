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
/// HTTP sink provider that sends responses back to requesting clients.
/// Uses the __responseCompletion TaskCompletionSource embedded in each request item.
/// </summary>
public class HttpSink : SinkProvider
{
    /// <inheritdoc />
    public override string Name => "RESPONSES";

    /// <inheritdoc />
    public override Task WriteAsync(object? originalItem, object result)
    {
        TaskCompletionSource<HttpResponseItem>? tcs = null;
        if (originalItem is DataObject origSo)
        {
            tcs = origSo.GetField("__responseCompletion") as TaskCompletionSource<HttpResponseItem>;
        }

        if (tcs is null)
        {
            throw new InvalidOperationException("http.RESPONSES can only be used with items from http.Requests.");
        }

        HttpResponseItem response;
        if (result is ErrorValue err)
        {
            string message = err.GetField("Message")?.ToString() ?? "Internal Server Error";
            response = new HttpResponseItem { StatusCode = 500, Body = $"{{\"error\":\"{message}\"}}", ContentType = "application/json" };
        }
        else if (result is DataObject so)
        {
            int statusCode = so.GetField("StatusCode") is int sc ? sc : 200;
            object? bodyField = so.GetField("Body");
            string body;
            if (bodyField is byte[] bytes)
            {
                body = System.Text.Encoding.UTF8.GetString(bytes);
            }
            else if (bodyField is DataObject bodyObj)
            {
                body = bodyObj.ToJson();
            }
            else
            {
                body = bodyField?.ToString() ?? SerializeToJson(so);
            }

            string contentType = so.GetField("ContentType")?.ToString() ?? "application/json";
            response = new HttpResponseItem { StatusCode = statusCode, Body = body, ContentType = contentType };
        }
        else
        {
            response = new HttpResponseItem { Body = result?.ToString() ?? string.Empty, ContentType = "text/plain" };
        }

        tcs.TrySetResult(response);
        return Task.CompletedTask;
    }

    private static string SerializeToJson(DataObject so)
    {
        Dictionary<string, object?> dict = new Dictionary<string, object?>();
        foreach ((string? key, object? value) in so.Fields)
        {
            dict[key] = value;
        }

        return JsonSerializer.Serialize(dict);
    }
}
