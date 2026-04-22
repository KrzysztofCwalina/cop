// Sample: poorly-designed client that fails multiple checks
public class BadClientOptions { }

public class BadClient
{
    public BadClient(string connectionString)
    {
        var x = connectionString;
    }

    public async Task<string> GetItemAsync(string id)
    {
        var result = "hello";
        Thread.Sleep(100);
        return result;
    }

    public void DoSomething()
    {
        try
        {
            Console.WriteLine("working");
        }
        catch (Exception ex)
        {
            throw;
        }
    }

    public void DoSomethingBad()
    {
        try
        {
            Console.WriteLine("working");
        }
        catch (Exception ex)
        {
            // swallowed — no rethrow
        }
    }
}
