// Sample: poorly-designed Azure SDK client that violates multiple Azure checks
// Should trigger: AZC0002, AZC0003, AZC0004, AZC0006, AZC0007,
//                 AZC0008, AZC0009, AZC0010, AZC0016, AZC0017, AZC0021,
//                 AZC0030-0033, AZC0036, AZC0015

public abstract class ClientOptions { }

public class TokenCredential { }
public class CancellationToken { }
public class RequestContent { }
public class RequestContext { }
public class HttpResponseMessage { }

// AZC0008 violation: No nested ServiceVersion enum
// AZC0021 violation: Constructor takes too many params
// Options doesn't inherit ClientOptions (but name ends in ClientOptions so it's targeted)
public class BadServiceClientOptions : ClientOptions
{
    public enum ServiceVersion
    {
        // AZC0016 violation: Bad naming pattern
        Latest,
        Preview_1,
    }

    // AZC0009 violation: First param is not ServiceVersion
    // AZC0021 violation: Too many params
    public BadServiceClientOptions(string extra, ServiceVersion version)
    {
    }
}

public class BadServiceClient
{
    // AZC0006 violation: No constructor with options
    // AZC0007 implicitly missing options overload pair
    // Missing TokenCredential (AZC0002 parent check)
    public BadServiceClient(string connectionString)
    {
    }

    // No protected parameterless ctor (AZC0005)

    // AZC0003 violation: Not virtual
    // AZC0002 violation: No CancellationToken
    // AZC0004 violation: No sync counterpart
    public Task<string> GetItemAsync(string id)
    {
        return Task.FromResult(id);
    }

    // AZC0003 violation: Not virtual
    public void DoWork()
    {
    }

    // AZC0017 violation: Convenience method with RequestContent
    public virtual Task SendAsync(RequestContent content, CancellationToken cancellationToken)
    {
        return Task.CompletedTask;
    }

    // AZC0015 violation: Returns banned HTTP type
    public virtual HttpResponseMessage GetResponse(CancellationToken cancellationToken)
    {
        return new HttpResponseMessage();
    }

    // AZC0020 violation: Async call doesn't propagate CancellationToken
    public virtual async Task ProcessAsync(CancellationToken cancellationToken)
    {
        await InnerWorkAsync();
    }

    private Task InnerWorkAsync() => Task.CompletedTask;

    // AZC0018 violation: Protocol method returning a model type instead of Response/Operation
    public virtual Task<MyModel> GetModelAsync(RequestContext context)
    {
        return Task.FromResult(new MyModel());
    }
}

public class MyModel { }

// AZC0012 violation: Single-word public type name
public class Processor { }

// AZC0030 violation: Model with Collection suffix
public class ItemCollection { }

// AZC0031 violation: Model with Request suffix
public class SubmitRequest { }

// AZC0032 violation: Model with Parameter suffix
public class QueryParameters { }

// AZC0036 violation: Model with Resource suffix
public class ComputeResource { }
