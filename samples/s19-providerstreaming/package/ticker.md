# ticker

A streaming provider that emits timer-based tick events.
Demonstrates how to build a streaming provider with source and sink.

## Provider: ticker

- **Source**: Emits `Tick` events every second with an incrementing sequence number
- **Sink**: Receives `Ack` responses from the processing pipeline

## Dependencies

- core
