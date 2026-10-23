import type { AccountEstablishmentDetails, AccountEstablishmentReason } from '../client/account-establishment.js';

/** Domain outcome translated by the owning client; no workflow state is stored here. */
export class DeviceAuthorizationError extends Error {
    constructor(readonly reason: AccountEstablishmentReason, message: string, readonly details: AccountEstablishmentDetails = {}, options?: ErrorOptions) { super(message, options); }
}

/** Only thrown around an attempted publication, never around discovery or signing. */
export class SubmissionOutcomeError extends Error {}
