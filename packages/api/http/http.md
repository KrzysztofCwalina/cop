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

function handle(Request:Uri:equals('/hello')) => ok({ message: 'Hello, World!' })

async foreach Requests => handle => RESPONSES
```

## Globals

- `Requests` — incoming HTTP requests (`[Request]`)
- `RESPONSES` — outgoing HTTP responses sink

## Helper Functions

- `ok(string)` — 200 OK response with the piped string as JSON body
- `ok(Request, body)` — 200 OK response with JSON body (accepts string or inline object)
- `ok(Request)` — 200 OK response with `{"status": "ok"}` body
- `notFound(Request)` — 404 Not Found response
- `created(Request)` — 201 Created response
- `badRequest(Request)` — 400 Bad Request response
- `serverError(Request)` — 500 Internal Server Error response
- `serverError(Error)` — 500 response from an Error value (body = Error.Message)

## Client Functions

- `http.Get(url, headers?)` — HTTP GET request
- `http.Post(url, headers, body)` — HTTP POST request
- `http.Send(method, url, headers?, body?)` — HTTP request with any method

## Error Handling

Network errors (e.g., client disconnects during request) are emitted as `Error` values into the pipeline. Define an `Error` overload on your transform function to handle them:

```cop
import http

function handle(Request:Uri:equals('/hello')) => ok({ message: 'Hello' })
function handle(Error) => print(Error.Message)

async foreach Requests => handle => RESPONSES
```

- If no `Error` overload exists → errors pass to the sink (returns HTTP 500)
- Return null from error handler → swallow the error silently
- Return a response → send that response to the client
