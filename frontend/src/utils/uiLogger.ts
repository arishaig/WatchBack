type UiLogLevel = "Debug" | "Information" | "Warning" | "Error";

interface ClientLogEntry {
    event: string;
    message: string;
    level: UiLogLevel;
    timestamp: string;
    data?: string;
}

const queue: ClientLogEntry[] = [];
let flushTimer: ReturnType<typeof setTimeout> | null = null;
const FLUSH_MS = 300;

function flush(): void {
    flushTimer = null;
    if (queue.length === 0) return;
    const batch = queue.splice(0);
    fetch("/api/diagnostics/client-log", {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(batch),
    }).catch(() => { /* intentionally silent — never log the logger */ });
}

export function uiLog(
    event: string,
    message: string,
    data?: Record<string, unknown>,
    level: UiLogLevel = "Debug"
): void {
    const entry: ClientLogEntry = { event, message, level, timestamp: new Date().toISOString() };
    if (data !== undefined) entry.data = JSON.stringify(data);
    queue.push(entry);
    if (flushTimer === null) {
        flushTimer = setTimeout(flush, FLUSH_MS);
    }
}

export function flushNow(): void {
    if (flushTimer !== null) { clearTimeout(flushTimer); flushTimer = null; }
    flush();
}