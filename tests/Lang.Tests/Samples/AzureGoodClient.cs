// Sample: well-designed Azure SDK client that passes all Azure checks
// Exercises: AZC0002, AZC0003, AZC0004, AZC0005, AZC0006, AZC0007,
//            AZC0008, AZC0009, AZC0010, AZC0016, AZC0017, AZC0021,
//            AZC0030-0033, AZC0036, AZC0015

public abstract class ClientOptions
{
}

public class TokenCredential { }

public class CancellationToken { }

public class RequestContext { }

public class GoodServiceClientOptions : ClientOptions
{
    public enum ServiceVersion
    {
        V1_0,
        V2_0,
        V2_1,
    }

    public GoodServiceClientOptions(ServiceVersion version = ServiceVersion.V2_1)
    {
    }
}

public sealed class GoodServiceClient
{
    public GoodServiceClient(string endpoint, TokenCredential credential, GoodServiceClientOptions options)
    {
    }

    public GoodServiceClient(string endpoint, TokenCredential credential)
    {
    }

    protected GoodServiceClient()
    {
    }

    public virtual Task<string> GetItemAsync(string id, CancellationToken cancellationToken)
    {
        return Task.FromResult(id);
    }

    public virtual string GetItem(string id, CancellationToken cancellationToken)
    {
        return id;
    }

    public virtual Task DeleteItemAsync(string id, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    public virtual void DeleteItem(string id, CancellationToken cancellationToken)
    {
    }

    // Valid protocol method — returns Response
    public virtual Response GetRawItem(string id, RequestContext context)
    {
        return new Response();
    }
}

public class Response { }

// Good model types — no banned suffixes
public class ItemData { }
public class ItemResult { }
public class ServiceConfiguration { }
