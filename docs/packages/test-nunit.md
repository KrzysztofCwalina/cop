# NUnit Test Package Reference

The `test-nunit` package provides NUnit-specific testing guidance and includes the NUnit.Analyzers Roslyn analyzer package for compile-time diagnostics.

**Source:** [`packages/dotnet/test-nunit/`](../../packages/dotnet/test-nunit/)

## Import

```ruby
import test-nunit
```

This also brings `test` into scope.

## Content

This is a guidance package that provides NUnit-specific instructions for coding agents. It also configures the NUnit.Analyzers NuGet package (v4.5.0) for Roslyn-based diagnostics.

- NUnit test framework patterns and conventions
- Assertion patterns (`Assert.That`, constraint model)
- Test organization with `[TestFixture]`, `[Test]`, `[TestCase]`
- Setup/teardown lifecycle management
