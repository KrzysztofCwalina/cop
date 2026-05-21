// Sample: well-designed client that passes all commands
public abstract class ClientOptions { }

public class GoodClientOptions : ClientOptions { }

public sealed class GoodClient
{
    public GoodClient(GoodClientOptions options, TokenCredential credential)
    {
    }

    protected GoodClient()
    {
    }

    public async Task<string> GetItemAsync(string id, CancellationToken cancellationToken)
    {
        string result = "hello";
        return result;
    }

    public async Task DeleteItemAsync(string id, CancellationToken cancellationToken)
    {
        await Task.Delay(1, cancellationToken);
    }
}

public class TokenCredential { }
