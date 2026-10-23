import type { DeviceCertificate } from '../models/identity.js';
import type { ContentReference } from '../models/content.js';
import type { SecretAccessFailure } from '../errors.js';

/** Creation-only values. Existing remote or retained profiles take precedence. */
export interface AccountInitialProfile {
    readonly nickname?: string;
    readonly bio?: string;
    readonly avatar?: ContentReference;
    readonly publicDiscovery?: boolean;
}

/**
 * Explicit setup or reauthorization. Omitted options resume the persisted operation.
 * Stop synchronization before changing authorization, then call start() separately.
 */
export interface AccountEstablishmentOptions {
    /** Selects a relay for a new account. An existing account retains its original relay. */
    readonly relayId?: string;
    /** Lifetime in seconds, at most 720 days. Defaults to 365 days; valid retained certificates are reused. */
    readonly certificateValiditySeconds?: number;
    /** Lifetime in seconds, at most 3650 days. Defaults to 365 days; valid confirmed routes are reused. */
    readonly routeValiditySeconds?: number;
    /** Remove this exact device in the publication that authorizes the local device. Requires contact continuity. */
    readonly replaceDeviceId?: string;
    /** Used only for an initial account with no remote or retained profile. */
    readonly initialProfile?: AccountInitialProfile;
}

export type AccountEstablishmentReason = 'accountSignerRequired' | 'signatureDeclined' | 'deviceSelectionRequired'
    | 'localKeyAccessRequired' | 'accountStateUnavailable' | 'relayMismatch' | 'contactContinuityUnverifiable'
    | 'submissionOutcomeUnknown' | 'authorizationNotYetEffective' | 'stateConflict' | 'operationParametersConflict';

export interface AccountDeviceInfo { readonly deviceId: string; readonly certificate: DeviceCertificate }
/** Complete verified snapshot; the application chooses an exact device ID, without automatic ranking. */
export interface DeviceSelectionInfo {
    readonly deviceLimit: number;
    readonly revision: number;
    /** Original device of this retained installation, when known; not a recommended replacement. */
    readonly previousLocalDeviceId?: string;
    readonly devices: readonly AccountDeviceInfo[];
}
export interface ContactAuthorizationIssue { readonly accountId?: string; readonly code: string }
export interface AccountEstablishmentDetails {
    readonly deviceSelection?: DeviceSelectionInfo;
    readonly keyIssue?: SecretAccessFailure;
    readonly contactIssues?: readonly ContactAuthorizationIssue[];
}

/** An explicit setup failure. Retain local data and use the reason to decide the next application action. */
export class AccountEstablishmentError extends Error {
    override readonly name = 'AccountEstablishmentError';
    readonly deviceSelection: DeviceSelectionInfo | undefined;
    readonly keyIssue: SecretAccessFailure | undefined;
    readonly contactIssues: readonly ContactAuthorizationIssue[];
    constructor(readonly reason: AccountEstablishmentReason, message: string, details: AccountEstablishmentDetails = {}, options?: ErrorOptions) {
        super(message, options);
        this.deviceSelection = details.deviceSelection;
        this.keyIssue = details.keyIssue;
        this.contactIssues = details.contactIssues ?? [];
    }
}
