import { AppState, type AppStateStatus } from 'react-native';
import { File, Paths } from 'expo-file-system';
import { ExpoSqliteStore, expoRandom } from '@meshline/expo';
import * as sdk from '@meshline/sdk';
import { PortableClientNetwork } from '../portable/client-network.js';
import { context } from '../portable/relay-fixture.js';
import type { VectorResult } from './portable-vectors';

function check(condition: unknown, message: string): asserts condition { if (!condition) throw new Error(message); }
async function bounded<T>(promise: Promise<T>, milliseconds = 30000): Promise<T> {
    let timer: ReturnType<typeof setTimeout> | undefined;
    try { return await Promise.race([promise, new Promise<never>((_, reject) => { timer = setTimeout(() => reject(new Error('Client lifecycle deadline exceeded.')), milliseconds); })]); }
    finally { clearTimeout(timer); }
}
function waitForState(expected: AppStateStatus) {
    let cancel = () => {};
    const promise = new Promise<void>(resolve => {
        const subscription = AppState.addEventListener('change', state => { if (state === expected) { subscription.remove(); resolve(); } });
        cancel = () => subscription.remove();
    });
    return { promise, cancel: () => cancel() };
}

/** The application explicitly stops/starts its client around real AppState events.
 * Relay responses and protocol time are controlled; this is not an OS guarantee
 * of background execution or a socket reconnection/subscription acceptance test. */
export async function runLifecycleChecks(onResult: (result: VectorResult) => void): Promise<void> {
    const network = new PortableClientNetwork({ random: expoRandom, async createStore(path) {
        path ??= `meshline-lifecycle-${Date.now()}.sqlite`; return { path, store: new ExpoSqliteStore({ databaseName: path }) };
    } });
    const states: { phase: string; at: string; appState: AppStateStatus; client: string; children: string[] }[] = [];
    const local = await network.open(); const client = local.client;
    const failures: string[] = [];
    const observeFailure = (failure: sdk.BackgroundFailure) => { failures.push(`${failure.operation}: ${String(failure.error)}`); };
    client.onLifecycle('backgroundError', observeFailure);
    const children = [client.accountManager, client.deviceManager, client.profileManager, client.messageManager, client.channelManager, client.groupManager];
    const every = (state: string) => client.lifecycleState === state && children.every(value => value.lifecycleState === state);
    const stage = (phase: string) => {
        states.push({ phase, at: new Date().toISOString(), appState: AppState.currentState, client: client.lifecycleState, children: children.map(value => value.lifecycleState) });
        new File(Paths.document, 'meshline-native-lifecycle-events.json').write(JSON.stringify(states, null, 2));
    };
    const pass = (name: string) => onResult({ name, passed: true });
    const waits: ReturnType<typeof waitForState>[] = [];
    let detach: (() => void) | undefined;
    try {
        check(network.requests.length === 0 && every('stopped'), 'Initialization performed I/O or started children.'); pass('client initialization stays offline with six stopped managers');
        let rejected = false; try { await bounded(client.start()); } catch (error) { rejected = error instanceof Error && error.message.includes('Create and authorize'); }
        check(rejected && every('stopped'), 'Unauthorized startup did not roll back.'); pass('unauthorized startup rolls all managers back');
        const relayId = network.relays[0]!.descriptor.relayId;
        await bounded(client.establishAccount({ relayId }));
        const deviceId = sdk.certificateId(client.device!, context);
        check(client.route?.relayId === relayId && client.deviceState?.certificates.some(value => sdk.certificateId(value, context) === deviceId), 'Account/device establishment differs.');
        check((await local.store.read([{ collection: 'client_account_operations' }])).sets[0]!.length === 0, 'Establishment was left pending.'); pass('account establishment commits authorized device and route');
        await bounded(client.start()); check(every('running'), 'Not all children started.'); pass('full client starts all six managers');
        await bounded(client.stop());
        let stopping: Promise<void> | undefined;
        detach = client.deviceManager.onLifecycle('stateChanged', async change => { if (change.current === 'running') { stopping = client.stop(); await stopping; } });
        await bounded(client.start()); check(stopping, 'Running-state callback did not execute.'); await bounded(stopping);
        detach(); detach = undefined; check(every('stopped'), 'Reentrant stop did not settle all children.'); pass('running-state observer can await owning client stop');
        await bounded(client.start()); check(every('running'), 'Restart after reentrant stop failed.');
        const publications = network.requests.filter(value => ['device.state.publish', 'account.route.publish'].includes(value.method)).length;
        const background = waitForState('background'); waits.push(background);
        stage('ready-for-background'); pass('client restarts after reentrant stop; ready for Android Home');
        await bounded(background.promise, 90000);
        const active = waitForState('active'); waits.push(active);
        await bounded(client.stop()); check(every('stopped') && AppState.currentState === 'background', 'Background did not stop all managers.');
        stage('background-stopped'); pass('real Android background event stops the full client');
        const syncBefore = network.requests.filter(value => value.method === 'message.timeline.sync').length;
        await bounded(active.promise, 90000); await bounded(client.start());
        check(every('running') && String(AppState.currentState) === 'active', 'Foreground did not restart all managers.'); stage('foreground-running'); pass('real Android foreground event restarts the full client');
        await bounded((async () => {
            while (network.requests.filter(value => value.method === 'message.timeline.sync').length <= syncBefore) await new Promise(resolve => setTimeout(resolve, 20));
        })());
        pass('foreground restart requests account timeline catch-up');
        check(sdk.certificateId(client.device!, context) === deviceId && network.requests.filter(value => ['device.state.publish', 'account.route.publish'].includes(value.method)).length === publications, 'Resume replaced identity or republished authorization.');
        pass('foreground resume preserves identity without republishing authorization');
        await bounded(client.dispose()); check(every('disposed'), 'Client disposal left a child alive.');
        await local.store.read([]); check((await (await local.pool.get(relayId)).getDescriptor()).relayId === relayId, 'Client disposed its caller-owned pool.');
        pass('client disposal releases all children and preserves caller-owned store/pool');
        await local.dispose(); const reopened = await network.open(91, local.path); reopened.client.onLifecycle('backgroundError', observeFailure);
        const payload = sdk.encodeUtf8('native client lifecycle recovered signing key');
        check(sdk.certificateId(reopened.client.device!, context) === deviceId && sdk.verifyDevice(payload, await reopened.client.deviceManager.sign(payload), reopened.client.device!.signingPublicKey), 'Reopened client lost its protected identity.');
        await bounded(reopened.client.start()); await bounded(reopened.client.stop()); await reopened.dispose();
        check(failures.length === 0, `Client reported background failures: ${failures.join('; ')}`);
        pass('new client restores protected identity and completes another start/stop'); stage('completed');
    } catch (error) { onResult({ name: 'client lifecycle workflow', passed: false, error: error instanceof Error ? error.stack ?? String(error) : String(error) }); throw error; }
    finally { detach?.(); for (const wait of waits) wait.cancel(); await network.dispose(); }
}
