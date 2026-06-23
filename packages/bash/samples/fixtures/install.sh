#!/usr/bin/env bash
set -euo pipefail

echo "Installing demo tool"
curl -o out.txt https://example.com/archive.tar.gz

# Violations: downloading remote content and piping it directly into a shell.
curl -fsSL https://example.com/install.sh | sh
wget -qO- https://example.com/bootstrap.sh \
  | bash

sudo apt-get update && sudo apt-get install -y demo-tool
PATH=/usr/local/bin:$PATH env DEBUG=1 ./configure

