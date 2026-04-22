# Python Library Package Reference

The `python-library` package provides design convention checks for Python packages. It covers type hint requirements for public API parameters and return types.

**Source:** [`packages/python/python-library/src/`](../../packages/python/python-library/src/)

## Import

```ruby
import python-library
```

This also brings `python` into scope.

## Predicates

Defined in `definitions.cop`:

| Predicate | Matches |
|---|---|
| `untypedParam(Parameter)` | Parameter without type hint (excludes `*args`, `**kwargs`) |
| `publicMethodMissingParamTypes(Method)` | Public method with untyped parameter |
| `untypedParams(Type)` | Type with public methods having untyped parameters |
| `publicMethodMissingReturnType(Method)` | Public method without return type annotation |
| `missingReturnTypes(Type)` | Type with public methods missing return types |

## Checks

Defined in `checks.cop`:

| Check | Severity | Message |
|---|---|---|
| `untyped-public-params` | warning | {Type.Name} has public methods with missing parameter type hints |
| `missing-return-types` | warning | {Type.Name} has public methods without return type annotations |

All checks are combined into the `python-library` array.

## Usage

```ruby
import python-library

# Run all Python library design checks
CHECK(python-library)
```
