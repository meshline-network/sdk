const fallbackReasons = new WeakMap<AbortSignal, DOMException>();
const aborted = () => new DOMException('The operation was aborted.', 'AbortError');

/** Legacy caller signals cannot expose a reason their runtime already discarded. */
export function abortReason(signal: AbortSignal): unknown {
    if (signal.reason !== undefined) return signal.reason;
    let reason = fallbackReasons.get(signal);
    if (!reason) { reason = aborted(); fallbackReasons.set(signal, reason); }
    return reason;
}

/** Keep the platform's real signal and synchronous abort event, adding reason
 * only to SDK-owned legacy signals. Globals and caller-owned signals are untouched. */
export function createAbortController(): AbortController {
    const controller = new AbortController();
    if ('reason' in controller.signal) return controller;
    let reason: unknown;
    Object.defineProperty(controller.signal, 'reason', { get: () => reason });
    return {
        signal: controller.signal,
        abort(value?: unknown) {
            if (controller.signal.aborted) return;
            reason = value === undefined ? aborted() : value;
            controller.abort();
        },
    };
}
