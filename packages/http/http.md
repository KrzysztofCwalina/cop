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

function handle(Request:Uri:eq('/hello')) => ok({ message: 'Hello, World!' })

async foreach Requests => handle => RESPONSES
```

## Globals

- `Requests` — incoming HTTP requests (`[Request]`)
- `Send` — outgoing HTTP responses (`[Response]`)

## Helper Functions

- `ok(Request, body)` — 200 OK response with JSON body (accepts string or inline object)
- `notFound(Request)` — 404 Not Found response
- `created(Request)` — 201 Created response
- `badRequest(Request)` — 400 Bad Request response
- `serverError(Request)` — 500 Internal Server Error response
