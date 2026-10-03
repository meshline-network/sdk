import { createAbortController } from './runtime/abort.js';
import { AccountManager } from './components/account.js';
import { ChannelManager } from './components/channel.js';
import { ClientComponent, type ClientOptions } from './components/component.js';
import { DeviceManager } from './components/device.js';
import { GroupManager } from './components/group.js';
import { MessageManager } from './components/message.js';
import { ProfileManager } from './components/profile.js';
import { ClientAccountOperations, type AccountEstablishmentOptions, type AccountRecoveryOptions } from './client/account-operations.js';
import { ClientConversations, type ConversationChange, type ConversationQuery } from './client/conversations.js';
import type { RandomSource } from './crypto/primitives.js';
import type { AccountSigner, SecretProtector } from './interactions.js';
import { accountDeviceStateCodec } from './models/identity.js';
import { AsyncGate } from './runtime/async-gate.js';
import { AsyncPulse } from './runtime/async-pulse.js';
import { EventHub, type EventListener } from './runtime/events.js';
import type { MeshlineStore } from './storage/store.js';
import type { RelayClientPool } from './transport/pool.js';

export type { AccountEstablishmentOptions, AccountRecoveryOptions } from './client/account-operations.js';
export type { Conversation, ConversationChange, ConversationKind, ConversationQuery, ConversationSummary } from './client/conversations.js';
export interface MeshlineClientEvents { readonly conversationChanged: ConversationChange }
export interface MeshlineClientOptions extends ClientOptions {
    readonly store: MeshlineStore; readonly relayClients: RelayClientPool; readonly secretProtector: SecretProtector;
    readonly accountSigner?: AccountSigner; readonly random?: RandomSource;
}

/** Owns six managers; the application retains ownership of the migrated store, pool, signer and protector. */
export class MeshlineClient extends ClientComponent {
    readonly accountManager: AccountManager; readonly deviceManager: DeviceManager; readonly profileManager: ProfileManager;
    readonly messageManager: MessageManager; readonly channelManager: ChannelManager; readonly groupManager: GroupManager;
    readonly #components: readonly ClientComponent[]; readonly #detach: (() => void)[] = [];
    readonly #accountGate = new AsyncGate(); readonly #accountOperations: ClientAccountOperations;
    readonly #conversations: ClientConversations; readonly #events = new EventHub<MeshlineClientEvents>();
    readonly #conversationLifetime = createAbortController(); readonly #conversationPulse = new AsyncPulse(); readonly #pendingConversations = new Set<string>();
    #conversationIds: ReadonlySet<string> = new Set(); #conversationJob: Promise<void> | undefined;
    constructor(options: MeshlineClientOptions) {
        super(options);
        this.accountManager = new AccountManager(options);
        this.deviceManager = new DeviceManager({ ...options, accountManager: this.accountManager });
        this.profileManager = new ProfileManager({ ...options, accountManager: this.accountManager, deviceSigner: this.deviceManager });
        this.messageManager = new MessageManager({ ...options, accountManager: this.accountManager, deviceManager: this.deviceManager });
        this.channelManager = new ChannelManager({ ...options, deviceManager: this.deviceManager });
        this.groupManager = new GroupManager({ ...options, deviceManager: this.deviceManager, messageManager: this.messageManager });
        this.#components = [this.accountManager, this.deviceManager, this.profileManager, this.messageManager, this.channelManager, this.groupManager];
        for (const component of this.#components) this.#detach.push(component.onLifecycle('backgroundError', error => this.notifyBackgroundError(error)));
        this.#accountOperations = new ClientAccountOperations({ store: options.store, pool: options.relayClients, account: this.accountManager, device: this.deviceManager, profile: this.profileManager,
            context: this.context, accountId: this.accountId, clock: this.clock, report: failure => this.notifyBackgroundError(failure) });
        this.#conversations = new ClientConversations(options.store, this.accountId);
        this.#detach.push(this.messageManager.on('messageReceived', messages => { for (const message of messages) this.#queueConversation(message.key.sender === this.accountId ? message.recipient : message.key.sender); }),
            this.messageManager.on('sendStatusChanged', status => { if (status.state === 'queued') this.#queueConversation(status.recipient); }),
            this.channelManager.on('timelineChanged', value => this.#queueConversation(value.channel.channelId)), this.channelManager.on('followChanged', value => this.#queueConversation(value.channel.channelId)),
            this.groupManager.on('timelineChanged', value => { if (value.message) this.#queueConversation(value.group.groupId); }), this.groupManager.on('groupChanged', value => this.#queueConversation(value.ref.groupId)));
    }
    get route() { return this.accountManager.route; }
    get device() { return this.deviceManager.local; }
    get deviceState() { return this.deviceManager.deviceState; }
    get profile() { return this.profileManager.profile; }
    on<K extends keyof MeshlineClientEvents>(event: K, listener: EventListener<MeshlineClientEvents[K]>): () => void { return this.#events.on(event, listener); }
    getConversations(query: ConversationQuery = {}, signal?: AbortSignal) { const copy = { ...query, ...(query.kinds ? { kinds: [...query.kinds] } : {}) }; return this.runOperation(scope => this.#conversations.list(copy, scope), signal); }
    getConversation(conversationId: string, signal?: AbortSignal) { return this.runOperation(scope => this.#conversations.get(conversationId, scope), signal); }
    async markRead(conversationId: string, signal?: AbortSignal): Promise<void> { if (await this.runOperation(scope => this.#conversations.markRead(conversationId, scope), signal)) this.#queueConversation(conversationId); }
    #queueConversation(id: string): void { if (this.#conversationLifetime.signal.aborted) return; this.#pendingConversations.add(id); this.#conversationPulse.pulse(); }
    async #observeConversations(): Promise<void> {
        const signal = this.#conversationLifetime.signal;
        try { for (;;) {
            await this.#conversationPulse.wait(signal); const pending = [...this.#pendingConversations]; this.#pendingConversations.clear(); if (!pending.length) continue;
            let present: ReadonlySet<string>;
            try { present = await this.#conversations.ids(signal); }
            catch (error) { if (signal.aborted) throw error; this.notifyBackgroundError({ operation: 'synchronize_conversations', error }); for (const id of pending) this.#queueConversation(id); await this.clock.delay(1000, signal); continue; }
            for (const id of pending) {
                if (signal.aborted) return;
                const kind = present.has(id) ? this.#conversationIds.has(id) ? 'updated' : 'created' : this.#conversationIds.has(id) ? 'removed' : undefined;
                if (kind) this.#events.notify('conversationChanged', { conversationId: id, kind }, error => this.notifyBackgroundError({ operation: 'observer', resource: 'conversationChanged', error }));
            }
            // Preserve membership for other queued IDs until their own notification is processed.
            const next = new Set(this.#conversationIds); for (const id of pending) { if (present.has(id)) next.add(id); else next.delete(id); } this.#conversationIds = next;
        } } catch (error) { if (!signal.aborted) this.notifyBackgroundError({ operation: 'observe_conversations', error }); }
    }
    establishAccount(options: AccountEstablishmentOptions = {}, signal?: AbortSignal): Promise<void> {
        const copy = { ...options }; return this.runOperation(scope => this.#accountGate.run(() => this.#accountOperations.establish(copy, scope), scope), signal);
    }
    recoverAccount(options: AccountRecoveryOptions = {}, signal?: AbortSignal): Promise<void> {
        const copy = { ...options, ...(options.previousDeviceState ? { previousDeviceState: accountDeviceStateCodec.decode(accountDeviceStateCodec.encode(options.previousDeviceState)) } : {}) };
        return this.runOperation(scope => this.#accountGate.run(() => this.#accountOperations.recover(copy, scope), scope), signal);
    }
    changeHomeRelay(relayId: string, signal?: AbortSignal): Promise<void> { return this.runOperation(scope => this.#accountGate.run(() => this.#accountOperations.migrate(relayId, scope), scope), signal); }
    protected override async onInitialize(signal: AbortSignal): Promise<void> {
        for (const component of this.#components) await component.initialize(signal);
        this.#conversationIds = await this.#conversations.ids(signal); this.#conversationJob ??= this.#observeConversations();
    }
    protected override async onStart(signal: AbortSignal): Promise<void> {
        await this.accountManager.start(signal);
        await this.#accountGate.run(() => this.#accountOperations.resumeMigration(signal), signal);
        for (const component of this.#components.slice(1)) await component.start(signal);
    }
    protected override async onStop(): Promise<void> {
        const errors: unknown[] = []; for (const component of [...this.#components].reverse()) { try { await component.stop(); } catch (error) { errors.push(error); } }
        if (errors.length) throw new AggregateError(errors, 'One or more client components failed to stop.');
    }
    protected override async onDispose(): Promise<void> {
        this.#conversationLifetime.abort(); await this.#conversationJob;
        const errors: unknown[] = []; for (const component of [...this.#components].reverse()) { try { await component.dispose(); } catch (error) { errors.push(error); } }
        for (const detach of this.#detach) detach();
        this.#events.clear();
        if (errors.length) throw new AggregateError(errors, 'One or more client components failed to dispose.');
    }
}
