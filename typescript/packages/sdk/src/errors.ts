/** A local encoding, identity, signature, or authorization violation. */
export class ProtocolError extends Error {
    override readonly name = 'ProtocolError';

    constructor(readonly code: string, message: string, options?: ErrorOptions) {
        super(message, options);
    }
}

/** A persisted operation cannot safely overwrite the current revision. */
export class StateConflictError extends Error {
    override readonly name = 'StateConflictError';
}
