export interface InteropFixture {
    readonly run: string; readonly context: string; readonly relayId: string; readonly endpoint: string; readonly now: number;
}
export interface InteropSnapshot {
    readonly run: string; readonly processId: number; readonly closed: boolean;
    readonly requests: readonly { readonly actor: 'typescript' | 'dotnet'; readonly method: string; readonly status: number; readonly requestSha256: string; readonly bodySha256: string; readonly responseSha256: string;
        readonly at?: string; readonly accountId?: string; readonly deviceId?: string; readonly timeline?: { readonly after: number; readonly sequences: readonly number[]; readonly messageIds: readonly string[] } }[];
    readonly faults: readonly { readonly method: string; readonly resourceId: string; readonly account?: string; readonly bodySha256: string; readonly acceptedAt: number; disconnectedAt?: number }[];
    readonly offlineApprovals: readonly { readonly method: 'group.application.approve' | 'group.member.recovery.approve'; readonly groupId: string; readonly account: string;
        readonly memberPublicKey: string; readonly approvedAt: number; readonly welcomeMessageId: string }[];
    readonly commands: readonly { readonly operation: string; readonly id: string; readonly resultSha256: string }[];
    readonly databases: readonly { readonly name: string; readonly sha256: string; readonly size: number;
        readonly wal?: { readonly sha256: string; readonly size: number }; readonly tables: Readonly<Record<string, number>> }[];
    readonly errors: readonly string[];
}
