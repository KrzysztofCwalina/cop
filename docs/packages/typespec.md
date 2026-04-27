## typespec

TypeSpec API specification type graph. &nbsp; `import typespec`

**Source:** [`packages/typespec/src/typespec.cop`](../../packages/typespec/src/typespec.cop)

---

### Collections

| Collection | Type | Description |
|---|---|---|
| `Models` | `[TspModel]` | All models in the spec |
| `Operations` | `[TspOperation]` | All operations |
| `Interfaces` | `[TspInterface]` | All interfaces |
| `Enums` | `[TspEnum]` | All enums |
| `Unions` | `[TspUnion]` | All unions |
| `Scalars` | `[TspScalar]` | All scalars |
| `Namespaces` | `[TspNamespace]` | All namespaces |

---

### Types

#### TspDecorator

| Property | Type | Description |
|---|---|---|
| `Name` | string | Decorator name |
| `Arguments` | `[string]` | Argument values |

#### TspProperty

| Property | Type | Description |
|---|---|---|
| `Name` | string | Property name |
| `Type` | string | Type expression |
| `Optional` | bool | True if optional |
| `Default` | string? | Default value |
| `Decorators` | `[TspDecorator]` | Applied decorators |

#### TspModel

| Property | Type | Description |
|---|---|---|
| `Name` | string | Model name |
| `Namespace` | string? | Containing namespace |
| `Properties` | `[TspProperty]` | Model properties |
| `BaseModel` | string? | Parent model |
| `Decorators` | `[TspDecorator]` | Applied decorators |

#### TspOperation

| Property | Type | Description |
|---|---|---|
| `Name` | string | Operation name |
| `Namespace` | string? | Containing namespace |
| `Interface` | string? | Parent interface |
| `Parameters` | `[TspProperty]` | Input parameters |
| `ReturnType` | string | Return type expression |
| `Decorators` | `[TspDecorator]` | Applied decorators |

#### TspInterface

| Property | Type | Description |
|---|---|---|
| `Name` | string | Interface name |
| `Namespace` | string? | Containing namespace |
| `Operations` | `[TspOperation]` | Contained operations |
| `Decorators` | `[TspDecorator]` | Applied decorators |

#### TspEnumMember

| Property | Type | Description |
|---|---|---|
| `Name` | string | Member name |
| `Value` | string? | Explicit value |
| `Decorators` | `[TspDecorator]` | Applied decorators |

#### TspEnum

| Property | Type | Description |
|---|---|---|
| `Name` | string | Enum name |
| `Namespace` | string? | Containing namespace |
| `Members` | `[TspEnumMember]` | Enum members |
| `Decorators` | `[TspDecorator]` | Applied decorators |

#### TspUnionVariant

| Property | Type | Description |
|---|---|---|
| `Name` | string | Variant name |
| `Type` | string | Variant type expression |

#### TspUnion

| Property | Type | Description |
|---|---|---|
| `Name` | string | Union name |
| `Namespace` | string? | Containing namespace |
| `Variants` | `[TspUnionVariant]` | Union variants |
| `Decorators` | `[TspDecorator]` | Applied decorators |

#### TspScalar

| Property | Type | Description |
|---|---|---|
| `Name` | string | Scalar name |
| `Namespace` | string? | Containing namespace |
| `BaseScalar` | string? | Base scalar type |
| `Decorators` | `[TspDecorator]` | Applied decorators |

#### TspNamespace

| Property | Type | Description |
|---|---|---|
| `Name` | string | Short name |
| `FullName` | string | Fully qualified name |
| `Decorators` | `[TspDecorator]` | Applied decorators |
