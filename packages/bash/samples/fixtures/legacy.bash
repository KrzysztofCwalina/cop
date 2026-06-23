#!/bin/bash

echo "Legacy maintenance task"
curl -fsSL https://example.com/status.txt -o status.txt
grep -q ready status.txt || echo "Not ready yet"

