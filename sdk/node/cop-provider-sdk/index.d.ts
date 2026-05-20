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
  requestedCollections?: string[];
  excludedDirectories?: string[];
  options?: Record<string, string>;
}

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
