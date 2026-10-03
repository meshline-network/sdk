import { MeshlineClient, type AccountSigner, type NetworkContext, type RelayRegistry, type SecretProtector } from '@meshline/sdk';

export interface ApplicationDependencies {
    readonly context: NetworkContext;
    readonly accountId: string;
    readonly registry: RelayRegistry;
    readonly secretProtector: SecretProtector;
    readonly accountSigner?: AccountSigner;
}

/** The application owns these resources; cleanup attempts every resource and retains failures. */
export function ownedSession(client: MeshlineClient, pool: { dispose(): Promise<void> }, store: { dispose(): Promise<void> }) {
    return { client, async dispose() {
        const errors: unknown[] = [];
        for (const resource of [client, pool, store]) { try { await resource.dispose(); } catch (error) { errors.push(error); } }
        if (errors.length) throw new AggregateError(errors, 'Client session cleanup failed.');
    } };
}
