'use strict';

/**
 * Cop Provider SDK for Node.js
 *
 * Handles LSP-style length-prefixed JSON communication over stdin/stdout.
 * Provider authors call defineProvider() with their schema and query functions.
 *
 * Protocol:
 *   Request/Response framing: Content-Length: N\r\n\r\n{json}
 */

/**
 * Defines and starts a Cop provider process.
 *
 * @param {object} options
 * @param {function} options.schema - Returns the provider schema object (types + collections)
 * @param {function} options.query - Receives query params, returns collection data object
 */
function defineProvider({ schema, query }) {
  if (typeof schema !== 'function') throw new Error('schema must be a function');
  if (typeof query !== 'function') throw new Error('query must be a function');

  const handlers = {
    getSchema: () => schema(),
    query: (params) => query(params),
  };

  startMessageLoop(handlers);
}

function startMessageLoop(handlers) {
  const stdin = process.stdin;
  stdin.setEncoding('utf8');

  let buffer = '';

  stdin.on('data', (chunk) => {
    buffer += chunk;
    processBuffer();
  });

  stdin.on('end', () => {
    process.exit(0);
  });

  function processBuffer() {
    while (true) {
      // Look for Content-Length header
      const headerEnd = buffer.indexOf('\r\n\r\n');
      if (headerEnd === -1) return; // Need more data

      const headerSection = buffer.substring(0, headerEnd);
      const match = headerSection.match(/Content-Length:\s*(\d+)/i);
      if (!match) {
        // Skip malformed input up to and including the double CRLF
        buffer = buffer.substring(headerEnd + 4);
        continue;
      }

      const contentLength = parseInt(match[1], 10);
      const bodyStart = headerEnd + 4;

      // Check if we have the full body
      if (buffer.length - bodyStart < contentLength) return; // Need more data

      const body = buffer.substring(bodyStart, bodyStart + contentLength);
      buffer = buffer.substring(bodyStart + contentLength);

      handleMessage(body);
    }
  }

  function handleMessage(json) {
    let request;
    try {
      request = JSON.parse(json);
    } catch (e) {
      sendResponse({ error: `Invalid JSON: ${e.message}` });
      return;
    }

    const { method, params } = request;
    const handler = handlers[method];
    if (!handler) {
      sendResponse({ error: `Unknown method: ${method}` });
      return;
    }

    try {
      const result = handler(params);
      // Support both sync and async handlers
      if (result && typeof result.then === 'function') {
        result.then(sendResponse).catch((err) => {
          sendResponse({ error: err.message || String(err) });
        });
      } else {
        sendResponse(result);
      }
    } catch (err) {
      sendResponse({ error: err.message || String(err) });
    }
  }

  function sendResponse(obj) {
    const json = JSON.stringify(obj);
    const byteLength = Buffer.byteLength(json, 'utf8');
    const header = `Content-Length: ${byteLength}\r\n\r\n`;
    process.stdout.write(header + json);
  }
}

module.exports = { defineProvider };
