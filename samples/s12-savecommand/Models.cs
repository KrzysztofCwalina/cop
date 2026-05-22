namespace SampleApp;

public class Customer
{
    public string Name { get; set; } = "";
    public void Save() { }
    public void Delete() { }
}

public abstract class IService
{
    public abstract void Start();
    public abstract void Stop();
}

public class OrderProcessor
{
    public void Process(int orderId) { }
    public void Cancel(int orderId) { }
    public void Retry(int orderId) { }
}

internal class InternalHelper
{
    public void DoWork() { }
}
