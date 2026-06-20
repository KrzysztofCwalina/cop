namespace Shop;

// A data-transfer type that SHOULD be an immutable record but isn't — flagged.
public class CustomerDto
{
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

// A data-transfer type done right — an immutable record. Not flagged.
public record OrderDto(int Id, decimal Total);

// A record struct value type.
public record struct Money(decimal Amount, string Currency);

// A partial type — flagged for review (a C#-only concept).
public partial class OrderService
{
    public void Process() { }
}

// A plain domain class — neither a DTO nor partial.
public class Inventory
{
    public int Count { get; set; }
}
