package shop;

// A data-transfer type done right — an immutable record. Not flagged.
public record OrderDto(int id, double total) {}

// A data-transfer type that should be a record but isn't — flagged.
class CustomerDto {
    String name;
    String email;
}

// An enum — recovered from the Java-specific AST (the common model also flattens it).
enum Status {
    ACTIVE, INACTIVE
}

// A plain domain class — neither a DTO nor an enum.
class Inventory {
    int count;
}
