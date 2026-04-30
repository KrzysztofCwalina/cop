---
name: http
version: 1.0.0
title: HTTP Server Provider
description: Streaming HTTP server provider for building web APIs with cop pipelines
authors: cop-team
tags: http, server, streaming, api
provider: clr
providerEntry: Cop.Providers.Http.HttpProvider
---

# HTTP Server Provider

Provides a streaming HTTP server that yields incoming requests as an async collection.
Import with `import http` to build web APIs using cop's pipeline syntax.

## Usage

```cop
import http

function handle(Request) => Path
  ? '/api/hello' => ok({ message = 'Hello, World!' })
  | _ => notFound({ error = 'Not found' })

command serve = foreach http.Receive => handle => http.Send
```

## Collections

- `http.Receive` — streaming collection of incoming HTTP requests

## Sinks

- `http.Send` — sends the transformed result as an HTTP response

## Helper Functions

- `ok(Request)` — 200 OK response
- `notFound(Request)` — 404 Not Found response
- `created(Request)` — 201 Created response
- `badRequest(Request)` — 400 Bad Request response
- `serverError(Request)` — 500 Internal Server Error response
