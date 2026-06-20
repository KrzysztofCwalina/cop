class Base {
    init() {}
}

// An exported class — flagged "exported".
export class Service {
    run() {}
}

// A non-exported helper — not flagged.
class InternalHelper {
}

// Exported and extends a base class — flagged by both checks.
export class Widget extends Base {
    render() {}
}
