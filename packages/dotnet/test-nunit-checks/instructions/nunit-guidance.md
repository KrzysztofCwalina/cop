# NUnit Testing Guidance

NUnit-specific patterns and best practices for unit testing in C#.

## Assert.That Constraint Model

NUnit's constraint model provides expressive, fluent assertions:

```csharp
// Prefer: Constraint model (fluent and readable)
Assert.That(result, Is.EqualTo(42));
Assert.That(collection, Has.Count.EqualTo(3));
Assert.That(value, Is.GreaterThan(0).And.LessThan(100));
Assert.That(text, Does.Contain("expected"));
Assert.That(items, Is.Empty);

// Avoid: Classic assertions (less readable)
Assert.AreEqual(42, result);
Assert.IsTrue(collection.Count == 3);
Assert.IsNotNull(value);
```

**Advantages of Assert.That**:
- Clear intent with method names (Is, Has, Does, Throws)
- Chainable constraints for complex conditions
- Better error messages on failure
- Easier to read like specifications

## Test Lifecycle Attributes

NUnit provides attributes for test setup and teardown at different scopes:

```csharp
[TestFixture]
public class CalculatorTests
{
    private Calculator _calculator;
    private static DatabaseConnection _sharedConnection;
    
    // Runs once before any tests in this fixture
    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        _sharedConnection = new DatabaseConnection("test");
    }
    
    // Runs before each test
    [SetUp]
    public void SetUp()
    {
        _calculator = new Calculator();
    }
    
    // Runs after each test
    [TearDown]
    public void TearDown()
    {
        _calculator?.Dispose();
    }
    
    // Runs once after all tests in this fixture
    [OneTimeTearDown]
    public static void OneTimeTearDown()
    {
        _sharedConnection?.Close();
    }
    
    [Test]
    public void Add_TwoNumbers_ReturnsSum()
    {
        var result = _calculator.Add(2, 3);
        Assert.That(result, Is.EqualTo(5));
    }
}
```

**Use [OneTimeSetUp] for**:
- Expensive setup (database connection, file I/O)
- Shared read-only resources
- Static configuration

**Use [SetUp] for**:
- Fresh instance initialization
- Per-test state reset
- Dependency injection

## Parameterized Tests

Run the same test with multiple inputs:

```csharp
[TestFixture]
public class CalculatorTests
{
    // [TestCase] for inline parameters
    [TestCase(2, 3, 5)]
    [TestCase(0, 0, 0)]
    [TestCase(-1, 1, 0)]
    [TestCase(int.MaxValue, 0, int.MaxValue)]
    public void Add_WithVariousInputs_ReturnsCorrectSum(int a, int b, int expected)
    {
        var calc = new Calculator();
        Assert.That(calc.Add(a, b), Is.EqualTo(expected));
    }
    
    // [Values] for single parameter variations
    [Test]
    public void IsPositive_WithMultipleValues_ReturnsCorrectly(
        [Values(-1, 0, 1, 100)] int value)
    {
        Assert.That(Calculator.IsPositive(value), Is.EqualTo(value > 0));
    }
    
    // [TestCaseSource] for complex test data
    [TestCaseSource(nameof(DivisionTestCases))]
    public void Divide_WithTestData_ReturnsExpectedResult(double numerator, double denominator, double expected)
    {
        var calc = new Calculator();
        Assert.That(calc.Divide(numerator, denominator), Is.EqualTo(expected));
    }
    
    private static IEnumerable<TestCaseData> DivisionTestCases()
    {
        yield return new TestCaseData(10, 2, 5).SetName("Divide_10by2_Returns5");
        yield return new TestCaseData(1, 3, 0.333).SetName("Divide_1by3_Returns0.333");
        yield return new TestCaseData(0, 5, 0).SetName("Divide_0by5_Returns0");
    }
}
```

## Category Conventions

Use [Category] attributes for test organization and filtering:

```csharp
[TestFixture]
public class UserServiceTests
{
    [Test]
    [Category("Unit")]
    public void CreateUser_ValidInput_ReturnsUserId()
    {
        // Fast unit test
    }
    
    [Test]
    [Category("Integration")]
    [Category("Database")]
    public void CreateUser_SavesToDatabase_PersistsCorrectly()
    {
        // Slower integration test
    }
    
    [Test]
    [Category("Slow")]
    public void BulkCreateUsers_1000Users_CompletesInReasonableTime()
    {
        // Performance/stress test
    }
}
```

**Standard categories**:
- `Unit`: Fast, isolated tests
- `Integration`: Tests with external dependencies
- `Database`: Tests requiring database
- `Network`: Tests requiring network calls
- `Slow`: Tests that take significant time
- `Smoke`: Quick sanity checks

Run by category: `dotnet test --filter "Category=Unit"` or `dotnet test --filter "Category!=Slow"`

## Async Test Patterns

Test async code properly using async Task:

```csharp
[TestFixture]
public class AsyncServiceTests
{
    private IAsyncService _service;
    
    [SetUp]
    public void SetUp()
    {
        _service = new AsyncService();
    }
    
    // Proper async test: return Task
    [Test]
    public async Task FetchData_ValidId_ReturnsData()
    {
        var result = await _service.FetchDataAsync(123);
        Assert.That(result, Is.Not.Null);
    }
    
    // Test timeout and cancellation
    [Test]
    public void FetchData_Timeout_ThrowsTimeoutException()
    {
        var cts = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        Assert.That(
            async () => await _service.SlowFetchAsync(cts.Token),
            Throws.TypeOf<OperationCanceledException>()
        );
    }
    
    // Test exception from async method
    [Test]
    public void InvalidOperation_ThrowsException()
    {
        Assert.That(
            async () => await _service.FailAsync(),
            Throws.TypeOf<InvalidOperationException>()
        );
    }
}
```

## TestContext Usage for Logging

Use TestContext for runtime information and logging:

```csharp
[TestFixture]
public class DiagnosticTests
{
    [Test]
    public void TestWithLogging()
    {
        TestContext.WriteLine($"Test started at {DateTime.Now}");
        TestContext.WriteLine($"Test name: {TestContext.CurrentContext.Test.Name}");
        
        // Your test code
        var result = SomeOperation();
        
        TestContext.WriteLine($"Result: {result}");
        Assert.That(result, Is.GreaterThan(0));
        
        TestContext.WriteLine("Test completed successfully");
    }
    
    [Test]
    public void TestWithProgress()
    {
        for (int i = 0; i < 100; i++)
        {
            TestContext.Write($"Processing item {i}...");
            ProcessItem(i);
            TestContext.WriteLine(" done");
        }
    }
}
```

Output appears in test results and can help debug test failures.

## AutoFixture and Test Data Generation

Use AutoFixture for automatic test data generation:

```csharp
[TestFixture]
public class OrderServiceTests
{
    private readonly Fixture _fixture = new Fixture();
    
    [Test]
    public void CreateOrder_ValidOrder_ReturnsOrderId()
    {
        // Auto-generate test data
        var order = _fixture.Create<Order>();
        var service = new OrderService();
        
        var orderId = service.CreateOrder(order);
        Assert.That(orderId, Is.GreaterThan(0));
    }
    
    [Test]
    public void CreateOrder_MultipleOrders_AllGetUniqueIds()
    {
        var service = new OrderService();
        var orders = _fixture.CreateMany<Order>(10).ToList();
        
        var orderIds = orders.Select(o => service.CreateOrder(o)).ToList();
        
        Assert.That(orderIds, Is.Unique);
        Assert.That(orderIds, Has.All.GreaterThan(0));
    }
    
    [Test]
    public void ProcessOrder_CustomData_HandlesCorrectly()
    {
        // Customize generated data
        _fixture.Customize<Order>(c => c
            .With(x => x.Status, OrderStatus.Pending)
            .With(x => x.Total, 99.99m)
        );
        
        var order = _fixture.Create<Order>();
        Assert.That(order.Status, Is.EqualTo(OrderStatus.Pending));
        Assert.That(order.Total, Is.EqualTo(99.99m));
    }
}
```

**AutoFixture benefits**:
- Reduces boilerplate test data setup
- Generates realistic anonymous data
- Customizable for specific scenarios
- Encourages focusing on behavior, not data
