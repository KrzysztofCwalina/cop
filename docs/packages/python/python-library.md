## python-library

Design convention checks for Python packages. &nbsp; `import python-library`

**Source:** [`packages/python/python-library/src/`](../../packages/python/python-library/src/) &nbsp; **Depends on:** python

---

### Predicates

| Predicate | Applies To | Matches |
|---|---|---|
| `untypedParam` | Parameter | Parameter without type hint (excludes `*args`, `**kwargs`) |
| `publicMethodMissingParamTypes` | Method | Public method with untyped parameter |
| `untypedParams` | Type | Type with public methods having untyped parameters |
| `publicMethodMissingReturnType` | Method | Public method without return type annotation |
| `missingReturnTypes` | Type | Type with public methods missing return types |

---

### Checks

| Check | Severity | Message |
|---|---|---|
| `untyped-public-params` | warning | {item.Name} has public methods with missing parameter type hints |
| `missing-return-types` | warning | {item.Name} has public methods without return type annotations |
