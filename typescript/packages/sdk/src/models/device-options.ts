import type { FieldUpdate } from './field-update.js';

/** Settings for a new local device. Publish its state before treating it as authorized. */
export interface DeviceCreationOptions {
    /** Positive integer seconds, at most 720 days; defaults to 365 days. */
    readonly validitySeconds?: number;
    /** Original complete HTTPS callback URL, at most 512 UTF-8 bytes. Omit for no declaration. */
    readonly pushGateway?: string;
}

/** Renew the existing device while retaining its keys and identity. */
export interface DeviceRenewalOptions {
    /** Positive integer seconds for the new certificate, at most 720 days; defaults to 365 days. */
    readonly validitySeconds?: number;
    /** Omit to retain, assign a URL to set, or use FieldUpdate.delete to remove. Null and empty assignments are invalid. */
    readonly pushGateway?: FieldUpdate<string>;
}
