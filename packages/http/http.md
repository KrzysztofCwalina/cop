---
name: http
version: 1.0.0
title: HTTP Server Provider
description: HTTP server provider for building web APIs with cop pipelines
authors: cop-team
tags: http, server, api
provider: clr
providerEntry: Cop.Providers.Http.HttpProvider
---

# HTTP Server Provider

Provides an HTTP server that yields incoming requests as a collection.
Import with `import http` to build web APIs using cop's pipeline syntax.

## Usage

```cop
import http

function handle(Request) => Uri
  ? '/api/hello' => ok({ message = 'Hello, World!' })
  | _ => notFound({ error = 'Not found' })

command serve = foreach http.Receive => handle => http.Send
```

## Collections

- `http.Receive` — incoming HTTP requests (`[Request]`). Dequeue from this in a pipe.
- `http.Send` — outgoing HTTP responses (`[Response]`). Enqueue to this in a pipe.

## Helper Functions

- `ok(Request)` — 200 OK response
- `notFound(Request)` — 404 Not Found response
- `created(Request)` — 201 Created response
- `badRequest(Request)` — 400 Bad Request response
- `serverError(Request)` — 500 Internal Server Error response
