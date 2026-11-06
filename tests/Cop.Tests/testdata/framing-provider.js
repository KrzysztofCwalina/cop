// Exercises UTF-8 frame lengths and responses stalled after their headers.
let input = Buffer.alloc(0);
process.stdin.on('end', () => {
    setTimeout(() => process.exit(0), 1000);
});
process.stdin.on('data', chunk => {
    input = Buffer.concat([input, chunk]);
    while (true) {
        const headerEnd = input.indexOf('\r\n\r\n');
        if (headerEnd < 0) return;
        const length = Number(input.subarray(0, headerEnd).toString('ascii')
            .match(/Content-Length:\s*(\d+)/i)[1]);
        const bodyStart = headerEnd + 4;
        if (input.length < bodyStart + length) return;
        const request = JSON.parse(input.subarray(bodyStart, bodyStart + length).toString('utf8'));
        input = input.subarray(bodyStart + length);
        if (request.params?.options?.mode === 'stall') {
            process.stdout.write('Content-Length: 100\r\n\r\n{');
            continue;
        }
        const response = request.method === 'getSchema'
            ? { types: [], collections: [], pid: process.pid }
            : { Value: 'Za\u017c\u00f3\u0142\u0107 \ud83d\ude00' };
        const bytes = Buffer.from(JSON.stringify(response), 'utf8');
        process.stdout.write(`Content-Length: ${bytes.length}\r\n\r\n`);
        process.stdout.write(bytes);
    }
});
