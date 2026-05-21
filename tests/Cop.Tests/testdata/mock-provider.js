'use strict';

/**
 * Mock Cop provider for testing the ProcessObjectProvider.
 * Implements the length-prefixed JSON protocol over stdin/stdout.
 */

const stdin = process.stdin;
stdin.setEncoding('utf8');

let buffer = '';

stdin.on('data', (chunk) => {
  buffer += chunk;
  processBuffer();
});

stdin.on('end', () => process.exit(0));

function processBuffer() {
  while (true) {
    const headerEnd = buffer.indexOf('\r\n\r\n');
    if (headerEnd === -1) return;

    const headerSection = buffer.substring(0, headerEnd);
    const match = headerSection.match(/Content-Length:\s*(\d+)/i);
    if (!match) {
      buffer = buffer.substring(headerEnd + 4);
      continue;
    }

    const contentLength = parseInt(match[1], 10);
    const bodyStart = headerEnd + 4;
    if (buffer.length - bodyStart < contentLength) return;

    const body = buffer.substring(bodyStart, bodyStart + contentLength);
    buffer = buffer.substring(bodyStart + contentLength);

    handleMessage(body);
  }
}

function handleMessage(json) {
  const request = JSON.parse(json);
  let response;

  if (request.method === 'getSchema') {
    response = {
      types: [
        {
          name: 'TestItem',
          properties: [
            { name: 'Name' },
            { name: 'Value', type: 'int' },
          ],
        },
      ],
      collections: [{ name: 'Items', itemType: 'TestItem' }],
    };
  } else if (request.method === 'query') {
    response = {
      Items: [
        { Name: 'alpha', Value: 1 },
        { Name: 'beta', Value: 2 },
        { Name: 'gamma', Value: 3 },
      ],
    };
  } else {
    response = { error: `Unknown method: ${request.method}` };
  }

  sendResponse(response);
}

function sendResponse(obj) {
  const json = JSON.stringify(obj);
  const byteLength = Buffer.byteLength(json, 'utf8');
  process.stdout.write(`Content-Length: ${byteLength}\r\n\r\n${json}`);
}
