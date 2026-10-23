/**
 * Cop Provider SDK for Node.js
 */

export interface ProviderSchema {
  types: TypeSchema[];
  collections: CollectionSchema[];
}

export interface TypeSchema {
  name: string;
  base?: string;
  properties: PropertySchema[];
}

export interface PropertySchema {
  name: string;
  type?: string;
  optional?: boolean;
  collection?: boolean;
}

export interface CollectionSchema {
  name: string;
  itemType: string;
}

export interface QueryParams {
  rootPath?: string;
  collection?: string;
  /** Optional optimization hints. Return a superset if a filter is unsupported;
   * the host applies the exact residual predicate. Never negate a partially supported filter. */
  filter?: FilterExpression;
  collectionFilters?: Record<string, FilterExpression>;
  /** Legacy clients may use requestedCollections; the current host sends collection. */
  requestedCollections?: string[];
  excludedDirectories?: string[];
  options?: Record<string, string>;
}

/** Numeric op values follow the CLR wire contract. */
export type StringOperation = 0 | 1 | 2 | 3 | 4 | 5 | 6;
export type CompareOperation = 0 | 1 | 2 | 3 | 4;
export type FilterExpression =
  | { kind: "property"; property: string; value: boolean }
  | { kind: "string"; property: string; op: StringOperation; value: string }
  | { kind: "comparison" | "collectionCount"; property: string; op: CompareOperation; value: number }
  | { kind: "and" | "or"; conditions: FilterExpression[] }
  | { kind: "not"; inner: FilterExpression }
  | { kind: "containsAny" | "in"; property: string; values: string[] }
  | { kind: "collectionContains"; property: string; value: string }
  | { kind: "collectionAny"; property: string; itemFilter: FilterExpression }
  | { kind: "flags"; property: string; op: 0 | 1; value: number };

export interface ProviderOptions {
  /**
   * Returns the provider schema describing types and collections.
   */
  schema: () => ProviderSchema | Promise<ProviderSchema>;

  /**
   * Handles a query request and returns collection data.
   * The result is a map of collection name to array of items.
   */
  query: (params: QueryParams) => Record<string, any[]> | Promise<Record<string, any[]>>;
}

/**
 * Defines and starts a Cop provider. This function does not return —
 * it starts the stdin/stdout message loop.
 */
export function defineProvider(options: ProviderOptions): void;
