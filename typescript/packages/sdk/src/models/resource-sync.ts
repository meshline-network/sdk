/** Runtime synchronization of one account timeline, group, or channel. */
export type ResourceSyncState = 'idle' | 'synchronizing' | 'caughtUp' | 'blocked';
export type ResourceSyncBlockReason = 'connection' | 'authentication' | 'permission' | 'missingKey' | 'verification' | 'storage' | 'historyUnavailable' | 'unknown';
export interface ResourceSyncStatus {
    /** Relay ID for account messages, group ID for groups, channel ID for channels. */
    readonly resource: string;
    readonly state: ResourceSyncState;
    /** Unix seconds of the last complete successful pass in this component instance. */
    readonly lastSynchronizedAt?: number;
    readonly blockReason?: ResourceSyncBlockReason;
    readonly error?: unknown;
    /** A known retention gap, independent of caughtUp. False does not guarantee complete history. */
    readonly hasRetentionGap: boolean;
}
