namespace SampleApp;

public class StorageClient
{
    public void Upload(string path) { }
}

public sealed class HttpClient
{
    public void Send(string url) { }
}

public class DatabaseClient
{
    public void Query(string sql) { }
}

public class ClientOptions
{
    public string Endpoint { get; set; } = "";
    public int Timeout { get; set; } = 30;
}

public abstract class BaseClient
{
    public virtual void Connect() { }
}
