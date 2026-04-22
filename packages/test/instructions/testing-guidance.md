# Testing Guidance

Comprehensive testing principles and patterns for agent-driven development.

## Test Structure: Arrange-Act-Assert

The AAA pattern provides clear test organization:

```csharp
[Test]
public void Add_GivenTwoPositiveNumbers_ReturnsTheirSum()
{
    // Arrange: Set up test data and conditions
    var calculator = new Calculator();
    int a = 5, b = 3;
    
    // Act: Execute the code being tested
    int result = calculator.Add(a, b);
    
    // Assert: Verify the outcome
    Assert.That(result, Is.EqualTo(8));
}
```

**One assertion per test concept**: Each test should verify a single behavior. This makes failures clear and pinpoints exactly what broke.

## Naming Conventions

Follow **MethodName_Scenario_ExpectedResult** pattern:

- `Add_WithPositiveNumbers_ReturnsSum`
- `Divide_ByZero_ThrowsArgumentException`
- `Login_WithValidCredentials_ReturnsAuthToken`
- `ProcessOrder_WhenInventoryEmpty_RejectsOrder`

Test names document intent and expected behavior. They should read like specifications.

## Test Isolation

Tests must be independent and repeatable:

- **No shared mutable state**: Each test owns its data. Avoid static fields with test data.
- **No test ordering dependencies**: Tests must pass in any order or in parallel.
- **Fresh setup for each test**: Initialize test fixtures before every test, not once for the suite.
- **Clean up after tests**: Use [TearDown] or IDisposable to restore system state.

```csharp
[SetUp]
public void Setup()
{
    // Fresh instance for each test
    _repository = new InMemoryRepository();
}

[TearDown]
public void Cleanup()
{
    _repository?.Dispose();
}
```

## Mocking Guidance

Use mocks strategically at system boundaries:

- **Mock external dependencies**: APIs, databases, file systems, time providers.
- **Prefer fakes for simple cases**: Instead of mocking a repository, use an in-memory implementation.
- **Avoid over-mocking**: Mocking your own logic defeats the purpose of testing.
- **Verify behavior, not implementation**: Test what the system does, not how it does it.

```csharp
// Good: Mock external boundary
var mockApiClient = new Mock<IWeatherApi>();
mockApiClient.Setup(x => x.GetTemperature("NYC"))
    .ReturnsAsync(72);

// Less ideal: Unnecessary mock of internal logic
var mockCalculator = new Mock<Calculator>();
```

## Code Coverage Expectations

Aim for **meaningful coverage**, not 100%:

- **Target 80%+** for critical paths and business logic.
- **Cover edge cases and error paths**: These reveal real bugs.
- **Skip trivial getters/setters** unless they contain logic.
- **Don't chase coverage metrics**: Coverage is a diagnostic tool, not a goal.

Meaningful coverage detects real bugs; excessive coverage slows development without value.

## Test Categories

Organize tests by scope and speed:

- **Unit tests** (~ms): Single component, no external I/O. Fast feedback loop.
- **Integration tests** (~seconds): Multiple components, real dependencies (databases, APIs). Slower but high confidence.
- **End-to-end tests** (~seconds-minutes): Full system flow, real infrastructure. Fewer of these.

Use [Category] attributes to organize and run tests by type:

```csharp
[Test]
[Category("Unit")]
public void Add_TwoNumbers_ReturnsSum() { }

[Test]
[Category("Integration")]
public void SaveUser_ToDatabase_PersistsData() { }
```

## Avoiding Flaky Tests

Flaky tests fail intermittently and erode trust:

- **Never use Thread.Sleep**: Causes timeouts in CI. Use async/await or time providers instead.
- **No external dependencies in unit tests**: No real network calls, database access, or file I/O.
- **Avoid relative timing assumptions**: Don't assume operations complete within X milliseconds.
- **Control time in tests**: Inject ISystemClock or similar for deterministic time-based behavior.
- **Isolate randomness**: Seed random generators in tests for reproducibility.

```csharp
// Bad: Flaky and slow
[Test]
public void ProcessQueue_Within5Seconds_CompletesAll()
{
    processor.Start();
    Thread.Sleep(5000);  // Flaky!
    Assert.That(processor.IsComplete);
}

// Good: Deterministic
[Test]
public void ProcessQueue_WhenAllItemsProcessed_Completes()
{
    var mockTime = new MockSystemClock();
    var processor = new QueueProcessor(mockTime);
    processor.Start();
    mockTime.AdvanceBy(TimeSpan.FromSeconds(5));
    Assert.That(processor.IsComplete);
}
```

## Key Principles

1. **Test behavior, not implementation**: Refactoring shouldn't break tests.
2. **Tests are documentation**: They show how to use the code.
3. **Speed matters**: Slow test suites discourage frequent runs.
4. **Clarity over cleverness**: Simple tests are easier to maintain.
5. **Failures are signals**: A failing test should immediately reveal the problem.
