namespace SampleApp;

public class StorageClientOptions
{
    public string ConnectionString { get; set; } = "";
}

public class StorageClient
{
    public StorageClient(StorageClientOptions options) { }
    public void Upload(string path) { }
}

public class SearchClient
{
    public SearchClient(string endpoint) { }
    public void Search(string query) { }
}

public class CacheClient
{
    public CacheClient() { }
    public void Get(string key) { }
    public void Set(string key, string value) { }
}
