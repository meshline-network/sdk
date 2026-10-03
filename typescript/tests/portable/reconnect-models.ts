import type { ChannelRef, JsonObject } from '@meshline/sdk';

export interface ReconnectEvent {
    readonly index: number; readonly at: number; readonly kind: string;
    readonly connection?: number; readonly method?: string; readonly params?: JsonObject;
    readonly action?: string; readonly sequence?: number; readonly code?: number;
}
export interface ReconnectSnapshot {
    readonly relayId: string; readonly endpoint: string; readonly channel: ChannelRef;
    readonly groups: readonly string[]; readonly events: readonly ReconnectEvent[];
    readonly errors: readonly string[]; readonly openConnections: readonly number[];
    readonly heldAuthentication: number; readonly heldSubscriptions: number;
}
