using Cop.Core;
using Cop.Providers;
using NUnit.Framework;

namespace Cop.Tests;

[TestFixture]
public class ProviderLifetimeTests
{
    [Test]
    public async Task QueryServiceDisposesEachOwnedProviderOnce()
    {
        var provider = new TrackingProvider();
        await using var service = new ProviderQueryService(TestContext.CurrentContext.WorkDirectory);
        service.RegisterOwnedProvider(
            new ProviderLoader.LoadedProvider(provider, Schema(), "first"));
        service.RegisterOwnedProvider(
            new ProviderLoader.LoadedProvider(provider, Schema(), "second"));

        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.That(provider.DisposeCount, Is.EqualTo(1));
    }

    [Test]
    public async Task QueryServiceDoesNotDisposeBorrowedProviders()
    {
        var provider = new TrackingProvider();
        await using var service = new ProviderQueryService(TestContext.CurrentContext.WorkDirectory);
        service.RegisterProvider("borrowed", provider, Schema());

        await service.DisposeAsync();

        Assert.That(provider.DisposeCount, Is.EqualTo(0));
    }

    [Test]
    public async Task QueryServiceAwaitsAsyncDisposalAndPrefersItToSynchronousDisposal()
    {
        var provider = new AsyncTrackingProvider();
        await using var service = new ProviderQueryService(TestContext.CurrentContext.WorkDirectory);
        service.RegisterOwnedProvider(
            new ProviderLoader.LoadedProvider(provider, Schema(), "first"));
        service.RegisterOwnedProvider(
            new ProviderLoader.LoadedProvider(provider, Schema(), "second"));

        await service.DisposeAsync();
        await service.DisposeAsync();

        Assert.That(provider.AsyncDisposeCount, Is.EqualTo(1));
        Assert.That(provider.SyncDisposeCount, Is.EqualTo(0));
    }

    [Test]
    public async Task QueryServiceDisposesOtherProvidersWhenOneDisposalFails()
    {
        var failing = new AsyncTrackingProvider { DisposalError = new IOException("fixture cleanup failed") };
        var other = new AsyncTrackingProvider();
        await using var service = new ProviderQueryService(TestContext.CurrentContext.WorkDirectory);
        service.RegisterOwnedProvider(
            new ProviderLoader.LoadedProvider(failing, Schema(), "first"));
        service.RegisterOwnedProvider(
            new ProviderLoader.LoadedProvider(other, Schema(), "second"));

        var error = Assert.ThrowsAsync<IOException>(async () => await service.DisposeAsync());

        Assert.That(error!.Message, Is.EqualTo("fixture cleanup failed"));
        Assert.That(failing.AsyncDisposeCount, Is.EqualTo(1));
        Assert.That(other.AsyncDisposeCount, Is.EqualTo(1));
    }

    private static ProviderSchema Schema() => ProviderSchema.FromJson(
        """{"namespace":"tracking","types":[],"collections":[]}"""u8.ToArray());

    private sealed class TrackingProvider : DataProvider, IDisposable
    {
        public int DisposeCount { get; private set; }

        public override ReadOnlyMemory<byte> GetSchema() =>
            """{"namespace":"tracking","types":[],"collections":[]}"""u8.ToArray();

        public override ValueTask<object?> QueryAsync(
            ProviderQuery query, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<object?>("{}"u8.ToArray());

        public void Dispose() => DisposeCount++;
    }

    private sealed class AsyncTrackingProvider : DataProvider, IDisposable, IAsyncDisposable
    {
        public int AsyncDisposeCount { get; private set; }
        public int SyncDisposeCount { get; private set; }
        public Exception? DisposalError { get; init; }

        public override ReadOnlyMemory<byte> GetSchema() =>
            """{"namespace":"tracking","types":[],"collections":[]}"""u8.ToArray();

        public override ValueTask<object?> QueryAsync(
            ProviderQuery query, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<object?>("{}"u8.ToArray());

        public void Dispose() => SyncDisposeCount++;

        public async ValueTask DisposeAsync()
        {
            await Task.Yield();
            AsyncDisposeCount++;
            if (DisposalError is { } error)
            {
                throw error;
            }
        }
    }
}
