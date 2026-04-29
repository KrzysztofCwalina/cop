// Dynamic route scenario: operations inherited from a template interface.
//
// This demonstrates the pattern where HTTP operations are NOT declared
// directly on the concrete interface — they come from a base template.
// In real-world TypeSpec, libraries like @typespec/rest and
// azure-resource-manager define these templates. The route computation
// often happens at compile time via @autoRoute or custom RouteProducer logic.
//
// This self-contained example demonstrates two patterns the syntactic parser handles:
// 1. Interface extends with explicit @route
// 2. @autoRoute with @key — routes computed from model name + key properties

import "@typespec/http";

using Http;

@service(#{ title: "Customer Service" })
namespace CustomerService;

model Customer {
  @key id: string;
  displayName: string;
  email?: string;
}

model Error {
  code: int32;
  message: string;
}

// Pattern 1: Template interface with explicit @route.
// Operations inherited via extends, route set on the template.
@route("/customers")
interface ResourceOperations<T> {
  @get op list(): T[] | Error;
  @get op read(@path id: string): T | Error;
  @post op create(@body body: T): T | Error;
  @put op update(@path id: string, @body body: T): T | Error;
  @delete op remove(@path id: string): void | Error;
}

// Concrete interface — inherits all operations + the /customers route.
interface Customers extends ResourceOperations<Customer> {}

// Pattern 2: @autoRoute — route computed from model name + @key.
// The parser detects @autoRoute, finds the resource model from extends args,
// pluralizes the model name (Order → /orders), and uses @key props as path params.
model Order {
  @key orderId: string;
  product: string;
  quantity: int32;
}

@autoRoute
interface OrderOperations<T> {
  @get op list(): T[];
  @get op read(orderId: string): T;
  @put op update(orderId: string, @body body: T): T;
  @delete op cancel(orderId: string): void;
}

interface Orders extends OrderOperations<Order> {}
