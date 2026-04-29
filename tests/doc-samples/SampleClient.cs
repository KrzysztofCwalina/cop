namespace Sample
{
    public class BlobClient
    {
        public BlobClient(Uri endpoint, TokenCredential credential, BlobClientOptions options) { }
        protected BlobClient() { }

        public async Task<Response> GetBlobAsync(string name, CancellationToken cancellationToken)
        {
            var result = Validate(name);
            return await HttpClient.SendAsync(result);
        }

        public async Task<Response> DeleteBlobAsync(string name, CancellationToken cancellationToken) { throw null; }
        public void Close() { }

        private string Validate(string input) => input.Trim();
    }

    public sealed class QueueClient
    {
        public QueueClient(Uri endpoint, QueueClientOptions options) { }
        public async Task SendAsync(string message) { throw null; }
    }

    // Client without options constructor (for missingOptions predicate test)
    public class EventClient
    {
        public EventClient(Uri endpoint) { }
        public void Send(string data) { Console.WriteLine(data); }
    }

    public class BlobClientOptions : ClientOptions
    {
        public ServiceVersion Version { get; }
        public int Timeout { get; set; }
    }

    internal class InternalHelper
    {
        public static void DoWork() { }
    }

    public class StorageHelper
    {
        internal StorageHelper() { }
        public static void Configure() { }
    }

    public abstract class BaseService
    {
        public abstract Task ProcessAsync();
    }

    public interface IStorageProvider
    {
        Task<byte[]> ReadAsync(string path);
    }

    public enum ServiceVersion
    {
        V2023_01_01,
        V2024_01_01
    }
}
