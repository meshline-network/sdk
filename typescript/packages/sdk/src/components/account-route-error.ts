export type AccountRouteFailure = 'signerRequired' | 'signatureDeclined' | 'relayMismatch' | 'operationConflict'
    | 'stateConflict' | 'stateUnavailable' | 'submissionOutcomeUnknown';

/** Route authorization failure from ensureRoute. Existing route and publication records are preserved. */
export class AccountRouteError extends Error {
    override readonly name = 'AccountRouteError';
    constructor(readonly reason: AccountRouteFailure, message: string, options?: ErrorOptions) { super(message, options); }
}
