## typespec

TypeSpec API specification type graph. &nbsp; `import typespec`

**Source:** [`packages/typespec/src/typespec.cop`](../../packages/typespec/src/typespec.cop)

---

### Collections

| Collection | Type | Description |
|---|---|---|
| `Models` | [`[TspModel]`](#tspmodel) | All models in the spec |
| `Operations` | [`[TspOperation]`](#tspoperation) | All operations |
| `Interfaces` | [`[TspInterface]`](#tspinterface) | All interfaces |
| `Enums` | [`[TspEnum]`](#tspenum) | All enums |
| `Unions` | [`[TspUnion]`](#tspunion) | All unions |
| `Scalars` | [`[TspScalar]`](#tspscalar) | All scalars |
| `Namespaces` | [`[TspNamespace]`](#tspnamespace) | All namespaces |

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
| `Decorators` | [`[TspDecorator]`](#tspdecorator) | Applied decorators |

#### TspModel

| Property | Type | Description |
|---|---|---|
| `Name` | string | Model name |
| `Namespace` | string? | Containing namespace |
| `Properties` | [`[TspProperty]`](#tspproperty) | Model properties |
| `BaseModel` | string? | Parent model |
| `Decorators` | [`[TspDecorator]`](#tspdecorator) | Applied decorators |

#### TspOperation

| Property | Type | Description |
|---|---|---|
| `Name` | string | Operation name |
| `Namespace` | string? | Containing namespace |
| `Interface` | string? | Parent interface |
| `Parameters` | [`[TspProperty]`](#tspproperty) | Input parameters |
| `ReturnType` | string | Return type expression |
| `Decorators` | [`[TspDecorator]`](#tspdecorator) | Applied decorators |

#### TspInterface

| Property | Type | Description |
|---|---|---|
| `Name` | string | Interface name |
| `Namespace` | string? | Containing namespace |
| `Operations` | [`[TspOperation]`](#tspoperation) | Contained operations |
| `Decorators` | [`[TspDecorator]`](#tspdecorator) | Applied decorators |

#### TspEnumMember

| Property | Type | Description |
|---|---|---|
| `Name` | string | Member name |
| `Value` | string? | Explicit value |
| `Decorators` | [`[TspDecorator]`](#tspdecorator) | Applied decorators |

#### TspEnum

| Property | Type | Description |
|---|---|---|
| `Name` | string | Enum name |
| `Namespace` | string? | Containing namespace |
| `Members` | [`[TspEnumMember]`](#tspenummember) | Enum members |
| `Decorators` | [`[TspDecorator]`](#tspdecorator) | Applied decorators |

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
| `Variants` | [`[TspUnionVariant]`](#tspunionvariant) | Union variants |
| `Decorators` | [`[TspDecorator]`](#tspdecorator) | Applied decorators |

#### TspScalar

| Property | Type | Description |
|---|---|---|
| `Name` | string | Scalar name |
| `Namespace` | string? | Containing namespace |
| `BaseScalar` | string? | Base scalar type |
| `Decorators` | [`[TspDecorator]`](#tspdecorator) | Applied decorators |

#### TspNamespace

| Property | Type | Description |
|---|---|---|
| `Name` | string | Short name |
| `FullName` | string | Fully qualified name |
| `Decorators` | [`[TspDecorator]`](#tspdecorator) | Applied decorators |
