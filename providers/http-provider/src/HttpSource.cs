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
/// HTTP source provider that exposes incoming HTTP requests as a streaming collection.
/// Starts a Kestrel web server and yields Request DataObjects as they arrive.
/// </summary>
public class HttpSource : StreamProvider
{
    private readonly int _port;
    private readonly Channel<HttpRequestItem> _requestChannel;
    private WebApplication? _app;

    /// <summary>Creates an HTTP streaming source listening on port 5000.</summary>
    public HttpSource() : this(5000) { }

    /// <summary>
    /// Creates an HTTP streaming source listening on the specified port.
    /// </summary>
    public HttpSource(int port)
    {
        _port = port;
        _requestChannel = Channel.CreateUnbounded<HttpRequestItem>();
    }

    /// <inheritdoc />
    public override ReadOnlyMemory<byte> GetSchema()
    {
        ProviderSchema schema = new ProviderSchema
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
                        new ProviderPropertySchema { Name = "ContentType", IsOptional = true },
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

    /// <inheritdoc />
    public override async IAsyncEnumerable<object> QueryStream(
        ProviderQuery query, [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await EnsureServerStartedAsync(cancellationToken);

        await foreach (HttpRequestItem request in _requestChannel.Reader.ReadAllAsync(cancellationToken))
        {
            if (request.ReadError is not null)
            {
                ErrorValue errorValue = new ErrorValue(request.ReadError);
                errorValue.Set("__responseCompletion", request.ResponseCompletion);
                yield return errorValue;
            }
            else
            {
                DataObject so = new DataObject("Request");
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
        if (_app != null)
        {
            return;
        }

        WebApplicationBuilder builder = WebApplication.CreateSlimBuilder();
        _app = builder.Build();

        _app.Map("{**path}", async (HttpContext ctx) =>
        {
            using CancellationTokenSource requestLifetime =
                CancellationTokenSource.CreateLinkedTokenSource(
                    cancellationToken, ctx.RequestAborted);
            CancellationToken requestCancellation = requestLifetime.Token;
            byte[] body = [];
            try
            {
                if (ctx.Request.ContentLength > 0)
                {
                    using MemoryStream ms = new MemoryStream();
                    await ctx.Request.Body.CopyToAsync(ms, requestCancellation);
                    body = ms.ToArray();
                }
            }
            catch (IOException ex)
            {
                TaskCompletionSource<HttpResponseItem> tcs =
                    new(TaskCreationOptions.RunContinuationsAsynchronously);
                HttpRequestItem errorItem = new HttpRequestItem
                {
                    Method = ctx.Request.Method,
                    Uri = (ctx.Request.Path.Value ?? "/") + (ctx.Request.QueryString.Value ?? string.Empty),
                    Body = [],
                    ContentType = string.Empty,
                    ResponseCompletion = tcs,
                    ReadError = $"Request read failed: {ex.Message}"
                };
                await _requestChannel.Writer.WriteAsync(errorItem, requestCancellation);
                HttpResponseItem response = await tcs.Task.WaitAsync(requestCancellation);
                ctx.Response.StatusCode = response.StatusCode;
                ctx.Response.ContentType = response.ContentType;
                await ctx.Response.WriteAsync(response.Body, requestCancellation);
                return;
            }

            TaskCompletionSource<HttpResponseItem> tcs2 =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            HttpRequestItem requestItem = new HttpRequestItem
            {
                Method = ctx.Request.Method,
                Uri = (ctx.Request.Path.Value ?? "/") + (ctx.Request.QueryString.Value ?? string.Empty),
                Body = body,
                ContentType = ctx.Request.ContentType ?? string.Empty,
                ResponseCompletion = tcs2
            };

            await _requestChannel.Writer.WriteAsync(requestItem, requestCancellation);

            HttpResponseItem response2 = await tcs2.Task.WaitAsync(requestCancellation);

            ctx.Response.StatusCode = response2.StatusCode;
            ctx.Response.ContentType = response2.ContentType;
            await ctx.Response.WriteAsync(response2.Body, requestCancellation);
        });

        await _app.StartAsync(cancellationToken);
    }

    /// <summary>
    /// Stops accepting requests and releases the active web application.
    /// </summary>
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
