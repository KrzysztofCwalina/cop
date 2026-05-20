"""
Cop Provider SDK for Python

Handles LSP-style length-prefixed JSON communication over stdin/stdout.
Provider authors call define_provider() with their schema and query functions.

Protocol:
    Request/Response framing: Content-Length: N\\r\\n\\r\\n{json}
"""

import sys
import json


def define_provider(*, schema, query):
    """
    Defines and starts a Cop provider process.

    Args:
        schema: Callable that returns the provider schema dict (types + collections).
        query: Callable that receives query params dict and returns collection data dict.
    """
    if not callable(schema):
        raise TypeError("schema must be a callable")
    if not callable(query):
        raise TypeError("query must be a callable")

    handlers = {
        "getSchema": lambda _: schema(),
        "query": lambda params: query(params or {}),
    }

    _message_loop(handlers)


def _message_loop(handlers):
    """Main stdin/stdout message loop using Content-Length framing."""
    stdin = sys.stdin.buffer  # binary mode for byte-accurate reads

    while True:
        # Read headers
        content_length = _read_headers(stdin)
        if content_length is None:
            break  # EOF

        # Read body
        body = _read_exactly(stdin, content_length)
        if body is None:
            break  # EOF

        # Parse and handle
        try:
            request = json.loads(body)
        except json.JSONDecodeError as e:
            _send_response({"error": f"Invalid JSON: {e}"})
            continue

        method = request.get("method")
        params = request.get("params")

        handler = handlers.get(method)
        if handler is None:
            _send_response({"error": f"Unknown method: {method}"})
            continue

        try:
            result = handler(params)
            _send_response(result)
        except Exception as e:
            _send_response({"error": str(e)})


def _read_headers(stream):
    """Reads headers until empty line. Returns Content-Length value or None on EOF."""
    content_length = None
    while True:
        line = stream.readline()
        if not line:
            return None  # EOF

        line_str = line.decode("utf-8").rstrip("\r\n")
        if line_str == "":
            break  # End of headers

        if line_str.lower().startswith("content-length:"):
            value = line_str[len("content-length:"):].strip()
            content_length = int(value)

    return content_length


def _read_exactly(stream, n):
    """Reads exactly n bytes from stream. Returns None on premature EOF."""
    data = b""
    while len(data) < n:
        chunk = stream.read(n - len(data))
        if not chunk:
            return None
        data += chunk
    return data


def _send_response(obj):
    """Sends a length-prefixed JSON response to stdout."""
    body = json.dumps(obj, ensure_ascii=False).encode("utf-8")
    header = f"Content-Length: {len(body)}\r\n\r\n".encode("utf-8")
    sys.stdout.buffer.write(header + body)
    sys.stdout.buffer.flush()
