namespace SampleApp;

public class Customer
{
    public string Name { get; set; } = "";
    public int Age { get; set; }
}

public class Order
{
    public string Id { get; set; } = "";
    public decimal Total { get; set; }
}

public class Invoice
{
    public string Number { get; set; } = "";
}
