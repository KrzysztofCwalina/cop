"""
Mock Cop provider for testing the ProcessObjectProvider (Python).
Implements the length-prefixed JSON protocol over stdin/stdout.
"""

import sys
import json


def main():
    stdin = sys.stdin.buffer

    while True:
        # Read headers
        content_length = None
        while True:
            line = stdin.readline()
            if not line:
                return  # EOF
            line_str = line.decode("utf-8").rstrip("\r\n")
            if line_str == "":
                break
            if line_str.lower().startswith("content-length:"):
                content_length = int(line_str[len("content-length:"):].strip())

        if content_length is None:
            continue

        # Read body
        body = b""
        while len(body) < content_length:
            chunk = stdin.read(content_length - len(body))
            if not chunk:
                return
            body += chunk

        request = json.loads(body)
        method = request.get("method")

        if method == "getSchema":
            response = {
                "types": [
                    {
                        "name": "TestItem",
                        "properties": [
                            {"name": "Name"},
                            {"name": "Value", "type": "int"},
                        ],
                    }
                ],
                "collections": [{"name": "Items", "itemType": "TestItem"}],
            }
        elif method == "query":
            response = {
                "Items": [
                    {"Name": "one", "Value": 10},
                    {"Name": "two", "Value": 20},
                ]
            }
        else:
            response = {"error": f"Unknown method: {method}"}

        send_response(response)


def send_response(obj):
    body = json.dumps(obj).encode("utf-8")
    header = f"Content-Length: {len(body)}\r\n\r\n".encode("utf-8")
    sys.stdout.buffer.write(header + body)
    sys.stdout.buffer.flush()


if __name__ == "__main__":
    main()
