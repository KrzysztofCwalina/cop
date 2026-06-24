using System;

namespace Sample.Fixture
{
    // Small, stable fixture used as the analysis target when the doc-snippet
    // EXECUTE harness runs documented ```cop programs end-to-end. Keep the shape
    // (a public *Client type, a Blob* type, a method, a Console call, a swallowed
    // exception) stable: doc snippets reference these names.
    public class BlobClient
    {
        public BlobClient() { }

        public void Download(string path)
        {
            Console.WriteLine($"downloading {path}");
            try
            {
                Process(path);
            }
            catch (Exception)
            {
                // swallowed on purpose (exercises error-handling rules)
            }
        }

        private void Process(string path)
        {
            var x = 1;
            var y = 2;
            var z = x + y;
            Console.WriteLine(z);
        }
    }

    public sealed class BlobOptions
    {
        public int Retries { get; set; }
    }
}
