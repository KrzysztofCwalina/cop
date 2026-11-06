using System.Net;
using System.Text;
using System.Text.Json;
using Cop.Core;
using Cop.Lang;
using Cop.Lang.Interpreter;
using NUnit.Framework;

namespace Cop.Tests.Lang;

[TestFixture]
public sealed class CoreProductionContractTests
{
    [Test]
    public void PackageDiscovery_PropagatesCallerCancellationWithoutFallbackRequests()
    {
        using CancellationTokenSource cancellation = new();
        using ResponseHandler handler = new((_, token) =>
        {
            cancellation.Cancel();
            throw new OperationCanceledException(token);
        });
        using HttpClient client = new(handler);
        GitHubPackageSource source = new(client);

        OperationCanceledException exception = Assert.CatchAsync<OperationCanceledException>(
            async () => await source.GetPackageMetadataAsync(Reference(), cancellation.Token))!;

        Assert.That(exception.CancellationToken, Is.EqualTo(cancellation.Token));
        Assert.That(handler.Requests, Is.EqualTo(1));
    }

    [Test]
    public void PackageDiscovery_PropagatesTransportFailureInsteadOfReportingMissingPackage()
    {
        using ResponseHandler handler = new((_, _) =>
            throw new HttpRequestException("offline"));
        using HttpClient client = new(handler);
        GitHubPackageSource source = new(client);

        HttpRequestException exception = Assert.ThrowsAsync<HttpRequestException>(
            async () => await source.GetPackageMetadataAsync(Reference()))!;

        Assert.That(exception.Message, Is.EqualTo("offline"));
        Assert.That(handler.Requests, Is.EqualTo(1));
    }

    [Test]
    public void PackageDiscovery_ReportsActualNotFoundAndDisposesEveryProbeResponse()
    {
        using ResponseHandler handler = new((_, _) => new(HttpStatusCode.NotFound));
        using HttpClient client = new(handler);
        GitHubPackageSource source = new(client);

        PackageNotFoundException exception = Assert.ThrowsAsync<PackageNotFoundException>(
            async () => await source.GetPackageMetadataAsync(Reference()))!;

        Assert.That(exception.Message,
            Is.EqualTo("Package 'demo' not found in owner/repository"));
        Assert.That(handler.Requests, Is.EqualTo(3));
        Assert.That(handler.DisposedContents, Is.EqualTo(3));
    }

    [Test]
    public void PackageDiscovery_PreservesRateLimitStatusAndDoesNotSearchFallbackLocations()
    {
        using ResponseHandler handler = new((_, _) =>
        {
            HttpResponseMessage response = new(HttpStatusCode.TooManyRequests);
            response.Headers.Add("X-RateLimit-Remaining", "0");
            return response;
        });
        using HttpClient client = new(handler);
        GitHubPackageSource source = new(client);

        HttpRequestException exception = Assert.ThrowsAsync<HttpRequestException>(
            async () => await source.GetPackageMetadataAsync(Reference()))!;

        Assert.That(exception.StatusCode, Is.EqualTo(HttpStatusCode.TooManyRequests));
        Assert.That(exception.Message, Is.EqualTo(
            "GitHub API rate limit exceeded. Please try again later or set a GitHub token " +
            "(GITHUB_TOKEN) for higher rate limits."));
        Assert.That(handler.Requests, Is.EqualTo(1));
        Assert.That(handler.DisposedContents, Is.EqualTo(1));
    }

    [Test]
    public async Task PackageMetadata_DisposesResponsesAfterReadingTheirContent()
    {
        const string metadata = """
            {"name":"demo","version":"1.0.0","title":"Demo",
             "description":"A test package","authors":"Cop"}
            """;
        string payload = JsonSerializer.Serialize(new
        {
            content = Convert.ToBase64String(Encoding.UTF8.GetBytes(metadata)),
            encoding = "base64"
        });
        using ResponseHandler handler = new((_, _) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(payload)
        });
        using HttpClient client = new(handler);
        GitHubPackageSource source = new(client);

        PackageMetadata result = await source.GetPackageMetadataAsync(Reference());

        Assert.That(result.Name, Is.EqualTo("demo"));
        Assert.That(result.Version, Is.EqualTo("1.0.0"));
        Assert.That(handler.Requests, Is.EqualTo(2));
        Assert.That(handler.DisposedContents, Is.EqualTo(2));
    }

    [Test]
    public void PackageMetadata_PreservesTheSpecificBase64Failure()
    {
        using ResponseHandler handler = new((_, _) => new(HttpStatusCode.OK)
        {
            Content = new StringContent("""{"content":"invalid!","encoding":"base64"}""")
        });
        using HttpClient client = new(handler);
        GitHubPackageSource source = new(client);

        PackageNotFoundException exception = Assert.ThrowsAsync<PackageNotFoundException>(
            async () => await source.GetPackageMetadataAsync(Reference()))!;

        Assert.That(exception.InnerException, Is.TypeOf<FormatException>());
        Assert.That(exception.InnerException!.InnerException, Is.TypeOf<FormatException>());
        Assert.That(handler.Requests, Is.EqualTo(2));
        Assert.That(handler.DisposedContents, Is.EqualTo(2));
    }

    [Test]
    public void Restore_PropagatesCancellationEvenForAnEmptyPackageSet()
    {
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();
        using ResponseHandler handler = new((_, _) =>
            throw new AssertionException("A cancelled empty restore must not use HTTP."));
        using HttpClient client = new(handler);
        RestoreEngine engine = new(new GitHubPackageSource(client));

        OperationCanceledException exception = Assert.ThrowsAsync<OperationCanceledException>(
            async () => await engine.RestoreAsync([], "main.cop",
                TestContext.CurrentContext.WorkDirectory, cancellation.Token))!;

        Assert.That(exception.CancellationToken, Is.EqualTo(cancellation.Token));
        Assert.That(handler.Requests, Is.EqualTo(0));
    }

    [Test]
    public void InvalidLocalMetadata_ReportsOneDeduplicatedWarning()
    {
        string directory = Path.Combine(TestContext.CurrentContext.WorkDirectory,
            "core-production-regressions", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        const string invalidJson = "{";
        string message = Assert.Throws<JsonException>(
            () => PackageMetadata.ParseFromJson(invalidJson))!.Message;
        ProviderWarnings.Clear();
        try
        {
            File.WriteAllText(Path.Combine(directory, "cop.json"), invalidJson);

            Assert.That(PackageMetadata.LoadFromDirectoryOrDefault(directory), Is.Null);
            Assert.That(PackageMetadata.LoadFromDirectoryOrDefault(directory), Is.Null);
            Assert.That(ProviderWarnings.Drain(), Is.EqualTo(new[]
            {
                $"Warning: package: Cannot read package metadata: {message}"
            }));
        }
        finally
        {
            ProviderWarnings.Clear();
            Directory.Delete(directory, recursive: true);
        }
    }

    [Test]
    public void RenamedBooleanProperties_PreserveExistingJsonContracts()
    {
        using JsonDocument boolean = JsonDocument.Parse(JsonSerializer.Serialize(CopBool.True));
        Assert.That(boolean.RootElement.GetProperty("Value").GetBoolean(), Is.True);
        Assert.That(boolean.RootElement.TryGetProperty("IsTrue", out _), Is.False);

        using JsonDocument restore = JsonDocument.Parse(JsonSerializer.Serialize(
            new RestoreResult { IsSuccessful = true }));
        Assert.That(restore.RootElement.GetProperty("Success").GetBoolean(), Is.True);
        Assert.That(restore.RootElement.TryGetProperty("IsSuccessful", out _), Is.False);

        ProviderPropertySchema property = new()
        {
            Name = "Children", Type = "Node", IsOptional = true, IsCollection = true
        };
        using JsonDocument schema = JsonDocument.Parse(JsonSerializer.Serialize(property));
        Assert.That(schema.RootElement.GetProperty("Optional").GetBoolean(), Is.True);
        Assert.That(schema.RootElement.GetProperty("Collection").GetBoolean(), Is.True);
        ProviderPropertySchema decoded = JsonSerializer.Deserialize<ProviderPropertySchema>(
            """{"Name":"Children","Optional":true,"Collection":true}""")!;
        Assert.That(decoded.IsOptional, Is.True);
        Assert.That(decoded.IsCollection, Is.True);
    }

    [Test]
    public async Task NestedToolchainScopes_RestoreAmbientOwnershipAfterAsyncDisposal()
    {
        ToolchainCommand command = new("cop-no-such-regression-toolchain",
            [], TestContext.CurrentContext.WorkDirectory);
        await using (ToolchainExecutionScope outer = new())
        {
            await using (ToolchainExecutionScope inner = new())
            {
                ToolchainResult nested = await ToolchainExecutionScope.RequestAsync(command, "{}");
                Assert.That(nested.Status, Is.EqualTo(ToolchainStatus.Unavailable));
            }

            ToolchainResult restored = await ToolchainExecutionScope.RequestAsync(command, "{}");
            Assert.That(restored.Status, Is.EqualTo(ToolchainStatus.Unavailable));
        }

        ToolchainResult standalone = await ToolchainExecutionScope.RequestAsync(command, "{}");
        Assert.That(standalone.Status, Is.EqualTo(ToolchainStatus.Unavailable));
    }

    [Test]
    public void StandardExceptionConstructors_RetainMessagesAndCauses()
    {
        IOException cause = new("underlying failure");
        Exception[] exceptions =
        [
            new AmbiguousCollectionException("ambiguous", cause),
            new CircularDependencyException("circular", cause),
            new PackageNotFoundException("missing", cause),
            new ParseException("parse", cause),
            new CopEvaluationException("evaluation", cause)
        ];
        Assert.That(exceptions.Select(exception => exception.Message), Is.EqualTo(new[]
        {
            "ambiguous", "circular", "missing", "<unknown>(0): parse", "line 0: evaluation"
        }));
        Assert.That(exceptions.Select(exception => exception.InnerException),
            Is.EqualTo(Enumerable.Repeat(cause, 5)));
        Assert.That(new CopEvaluationException("evaluation").Message,
            Is.EqualTo("line 0: evaluation"));
        Assert.That(new CopEvaluationException("evaluation", 7, "input.cop").RawMessage,
            Is.EqualTo("evaluation"));
    }

    private static PackageReference Reference() =>
        PackageReference.Parse("github.com/owner/repository/demo");

    private sealed class ResponseHandler(
        Func<HttpRequestMessage, CancellationToken, HttpResponseMessage> respond) : HttpMessageHandler
    {
        public int Requests { get; private set; }
        public int DisposedContents { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Requests++;
            HttpResponseMessage response = respond(request, cancellationToken);
            byte[] content = await response.Content.ReadAsByteArrayAsync(cancellationToken);
            response.Content.Dispose();
            response.Content = new TrackedContent(content, () => DisposedContents++);
            return response;
        }
    }

    private sealed class TrackedContent(byte[] content, Action disposed) : ByteArrayContent(content)
    {
        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                disposed();
            }
            base.Dispose(disposing);
        }
    }
}
