import * as sdk from '@meshline/sdk';
import { applyGroupEvent, type GroupProjection } from '../../packages/sdk/dist/groups/state.js';
import { MessagingNetwork } from './messaging-network.js';
import { context } from './relay-fixture.js';
interface HostedGroup { state: GroupProjection; events: sdk.GroupEvent[]; certificates: Map<string, sdk.DeviceCertificate>; clientBoxes: Map<number, Map<string, sdk.GroupSecretBox>>; relayKeys: Map<number, Uint8Array> }
export class GroupNetwork {
    readonly groups = new Map<string, HostedGroup>(); readonly requests: { method: string; body: sdk.JsonObject }[] = [];
    readonly invites = new Map<string, sdk.GroupInviteResolveResult>();
    readonly applications = new Map<string, sdk.GroupApplicationEntry>(); readonly recoveries = new Map<string, sdk.GroupRecoveryEntry>();
    readonly rotations = new Map<string, { owner: string; baseCommitment: string; commitment: string; expiresAt: number; boxes: Map<string, sdk.GroupSecretBox> }>();
    applicationPage: ((value: sdk.GroupApplicationPage) => sdk.GroupApplicationPage) | undefined; recoveryPage: ((value: sdk.GroupRecoveryPage) => sdk.GroupRecoveryPage) | undefined;
    preview: ((value: sdk.GroupState) => sdk.GroupState) | undefined; listPageSize = 100;
    afterPrepare: (() => Promise<void>) | undefined; prepareResponse: ((value: sdk.GroupRotationPrepareResult) => sdk.GroupRotationPrepareResult) | undefined;
    loseResponse: string | undefined; failBeforeAccept: string | undefined; rejectMethod: string | undefined; failReadAfterSend = false; failRead = false; wrongSequence = false; missingSequence = false;
    constructor(readonly network = new MessagingNetwork()) { this.network.handleRequest = (method, body, account, id) => this.#handle(method, body, account, id); }
    async #handle(method: string, body: sdk.JsonObject, account: string, deviceId: string): Promise<Response | undefined> {
        if (!method.startsWith('group.')) return undefined;
        const response = (value?: unknown, status = value === undefined ? 204 : 200): Response => new Response(value === undefined ? null : JSON.stringify(value), { status, ...(value === undefined ? {} : { headers: { 'content-type': 'application/json' } }) });
        const error = (code: string, status = 409) => response({ code, message: code }, status);
        this.requests.push({ method, body }); const id = method === 'group.create' ? String(sdk.requireObject(body.create!).group_id) : method.endsWith('.approve') ? String(sdk.requireObject(body.approval!).group_id) : String(body.group_id); let hosted = this.groups.get(id);
        if (method === 'group.sync') {
            if (this.failRead) throw new Error('Group history unavailable'); if (!hosted) return error('not_found', 404);
            return response(sdk.groupSyncPageCodec.encode({ events: hosted.events.filter(event => event.sequence > Number(body.after ?? -1)), certificates: [...hosted.certificates.values()], hasMore: false }));
        }
        const certificate = sdk.authorizedDevice(this.network.states.get(account)!, deviceId, context, this.network.clock.wall);
        if (method === 'group.resolve') {
            if (!hosted) return error('not_found', 404);
            if (hosted.state.bans.includes(account)) return error('forbidden', 403);
            if (!hosted.state.members.some(member => member.account === account)) {
                const entry = this.invites.get(`${id}|${body.invite_id}`); const invite = entry?.invite;
                if (!invite || invite.expiresAt <= this.network.clock.wall || invite.invitee !== undefined && invite.invitee !== account || entry!.uses >= (invite.invitee === undefined ? invite.maxUses ?? Infinity : 1)) return error('forbidden', 403);
            }
            return response(sdk.groupStateCodec.encode(this.preview?.(hosted.state.state) ?? hosted.state.state));
        }
        if (method === 'group.key.sync') {
            if (!hosted) return error('not_found', 404); if (!hosted.state.members.some(member => member.account === account)) return error('forbidden', 403);
            const keys = [...hosted.relayKeys].filter(([epoch]) => epoch > Number(body.after ?? -1) && hosted!.clientBoxes.get(epoch)!.has(account)).map(([epoch, secret]) => ({ epoch, clientSecretBox: hosted!.clientBoxes.get(epoch)!.get(account)!,
                relaySecretBox: sdk.sealGroupSecret(secret, certificate.encryptionPublicKey, sdk.groupRelaySecretBoxAad({ groupId: id, account, deviceId, epoch }, context)) }));
            return response(sdk.groupKeyPageCodec.encode({ keys, hasMore: false }));
        }
        if (this.rejectMethod === method) return error('state_conflict');
        if (this.failBeforeAccept === method) throw new Error('Connection lost before test relay accepted request');
        const member = hosted?.state.members.find(value => value.account === account); const administrator = member && member.role !== 'member';
        if (method === 'group.secret.rotation.prepare') {
            const request = sdk.groupRotationPrepareRequestCodec.decode(body); sdk.validateGroupRotationPrepareRequest(request);
            if (!hosted) return error('not_found', 404); if (hosted.state.state.owner !== account) return error('forbidden', 403);
            if (hosted.state.state.status !== 'active' || request.baseCommitment !== hosted.state.clientSecretCommitment) return error('state_conflict');
            const entries = Object.entries(request.clientSecretBoxes); if (entries.some(([account]) => !hosted!.state.members.some(member => member.account === account))) return error('bad_request', 400);
            let stage = this.rotations.get(id);
            if (!stage || stage.commitment !== request.clientSecretCommitment) { stage = { owner: account, baseCommitment: request.baseCommitment, commitment: request.clientSecretCommitment, expiresAt: this.network.clock.wall + 300, boxes: new Map() }; this.rotations.set(id, stage); }
            else if (stage.expiresAt <= this.network.clock.wall || stage.owner !== account || stage.baseCommitment !== request.baseCommitment) return error('state_conflict');
            for (const [account, box] of entries) stage.boxes.set(account, box);
            const result = { prepared: stage.boxes.size, expiresAt: stage.expiresAt }; await this.afterPrepare?.();
            if (this.loseResponse === method) throw new Error('Group operation accepted but response lost'); return response(sdk.groupRotationPrepareResultCodec.encode(this.prepareResponse?.(result) ?? result));
        }
        if (method === 'group.application.list' || method === 'group.member.recovery.list') {
            if (!hosted) return error('not_found', 404); if (!administrator) return error('forbidden', 403);
            const start = Number(body.cursor ?? 0); const limit = Math.min(Number(body.limit ?? this.listPageSize), this.listPageSize);
            if (method === 'group.application.list') {
                const all = [...this.applications.values()].filter(value => value.application.groupId === id); const values = all.slice(start, start + limit);
                const page: sdk.GroupApplicationPage = { applications: values, ...(start + limit < all.length ? { next: String(start + limit) } : {}) }; return response(sdk.groupApplicationPageCodec.encode(this.applicationPage?.(page) ?? page));
            }
            const all = [...this.recoveries.values()].filter(value => value.request.groupId === id); const values = all.slice(start, start + limit);
            const page: sdk.GroupRecoveryPage = { requests: values, ...(start + limit < all.length ? { next: String(start + limit) } : {}) }; return response(sdk.groupRecoveryPageCodec.encode(this.recoveryPage?.(page) ?? page));
        }
        if (method === 'group.application.submit' || method === 'group.member.recovery.submit') {
            if (!hosted) return error('not_found', 404); const key = `${id}|${account}`; let result: sdk.GroupRecoverySubmitResult | undefined;
            if (method === 'group.application.submit') {
                const application = sdk.groupApplicationCodec.decode(body); sdk.verifyGroupApplication(application, certificate, context); if (application.account !== account || member || hosted.state.bans.includes(account)) return error('forbidden', 403);
                const invite = this.invites.get(`${id}|${application.inviteId}`); if (!invite) return error('not_found', 404);
                if (invite.invite.invitee !== undefined && invite.invite.invitee !== account || invite.invite.expiresAt <= this.network.clock.wall || invite.invite.maxUses !== undefined && invite.uses >= invite.invite.maxUses) return error('state_conflict');
                const old = this.applications.get(key); if (!old || sdk.groupApplicationCodec.stringify(old.application) !== sdk.groupApplicationCodec.stringify(application)) this.applications.set(key, { application, signerCertificate: certificate, acceptedAt: this.network.clock.wall });
            } else {
                const request = sdk.groupMemberRecoveryRequestCodec.decode(body); sdk.verifyGroupMemberRecoveryRequest(request, certificate, context); if (request.account !== account || !member) return error('forbidden', 403);
                if (sdk.equalBytes(request.memberEncryptionPublicKey, member.memberEncryptionPublicKey)) return error('bad_request', 400);
                let value = this.recoveries.get(key); if (!value || sdk.groupMemberRecoveryRequestCodec.stringify(value.request) !== sdk.groupMemberRecoveryRequestCodec.stringify(request)) { value = { request, signerCertificate: certificate, acceptedAt: this.network.clock.wall, expiresAt: this.network.clock.wall + 3600 }; this.recoveries.set(key, value); }
                result = { acceptedAt: value.acceptedAt, expiresAt: value.expiresAt };
            }
            if (this.loseResponse === method) throw new Error('Group operation accepted but response lost'); return response(result === undefined ? undefined : sdk.groupRecoverySubmitResultCodec.encode(result));
        }
        if (method === 'group.application.reject' || method === 'group.member.recovery.reject') {
            const request = sdk.groupAccountsRequestCodec.decode(body); sdk.validateGroupAccountsRequest(request); const recovery = method === 'group.member.recovery.reject';
            if (!hosted) return error('not_found', 404); if (!administrator && !(recovery && member && request.accounts.every(value => value === account))) return error('forbidden', 403);
            const rows = recovery ? this.recoveries : this.applications; if (request.accounts.some(value => !rows.has(`${id}|${value}`))) return error('not_found', 404);
            for (const target of request.accounts) rows.delete(`${id}|${target}`);
            if (this.loseResponse === method) throw new Error('Group operation accepted but response lost'); return response();
        }
        if (method.startsWith('group.invite.')) {
            if (!hosted) return error('not_found', 404); if (!member || hosted.state.bans.includes(account)) return error('forbidden', 403); const key = `${id}|${body.invite_id}`;
            if (method === 'group.invite.resolve') {
                const value = this.invites.get(key);
                if (value && !administrator && value.invite.inviter !== account) return error('forbidden', 403);
                return value ? response(sdk.groupInviteResolveResultCodec.encode(value)) : error('not_found', 404);
            }
            if (method === 'group.invite.list') {
                const values = [...this.invites.values()].filter(value => value.invite.groupId === id && (administrator || value.invite.inviter === account)).sort((a, b) => a.invite.inviteId.localeCompare(b.invite.inviteId));
                const start = Number(body.cursor ?? 0); const limit = Number(body.limit ?? 100); const page = values.slice(start, start + limit);
                const certificates = [...new Map(page.map(value => [sdk.certificateId(value.signerCertificate, context), value.signerCertificate])).values()];
                return response(sdk.groupInvitePageCodec.encode({ invites: page.map(value => ({ invite: value.invite, uses: value.uses, signerDeviceId: sdk.certificateId(value.signerCertificate, context) })), certificates, ...(start + limit < values.length ? { next: String(start + limit) } : {}) }));
            }
            if (method === 'group.invite.create') {
                const invite = sdk.groupInviteCodec.decode(body); sdk.verifyGroupInvite(invite, certificate, context, this.network.clock.wall);
                const member = hosted.state.members.find(value => value.account === account); if (!member) return error('forbidden', 403);
                if (this.invites.has(key)) return sdk.groupInviteCodec.stringify(this.invites.get(key)!.invite) === sdk.groupInviteCodec.stringify(invite) ? response() : error('state_conflict');
                this.invites.set(key, { invite, uses: 0, signerCertificate: certificate });
            } else if (method === 'group.invite.revoke') this.invites.delete(key);
            else throw new Error(`Unknown invitation fixture method ${method}`);
            if (this.loseResponse === method) throw new Error('Group operation accepted but response lost'); return response();
        }
        const payload = method === 'group.create' ? sdk.requireObject(body.create!) : method.endsWith('.approve') ? sdk.requireObject(body.approval!) : body;
        if (payload.prev_hash !== undefined && payload.prev_hash !== hosted?.state.managementHash) return error('state_conflict');
        const stagedRotation = method === 'group.secret.rotation.commit' ? this.rotations.get(id) : undefined;
        if (method === 'group.secret.rotation.commit' && (!stagedRotation || stagedRotation.owner !== account || stagedRotation.commitment !== payload.client_secret_commitment || stagedRotation.baseCommitment !== hosted!.state.clientSecretCommitment || stagedRotation.expiresAt <= this.network.clock.wall || hosted!.state.members.some(member => !stagedRotation.boxes.has(member.account)))) return error('state_conflict');
        let approval: sdk.GroupApplicationApproveRequest | sdk.GroupRecoveryApproveRequest | undefined;
        if (method === 'group.application.approve' || method === 'group.member.recovery.approve') {
            if (!hosted) return error('not_found', 404);
            const recovery = method === 'group.member.recovery.approve';
            if (recovery) { approval = sdk.groupRecoveryApproveRequestCodec.decode(body); sdk.validateGroupRecoveryApproveRequest(approval); } else { approval = sdk.groupApplicationApproveRequestCodec.decode(body); sdk.validateGroupApplicationApproveRequest(approval); }
            if (approval.clientSecretCommitment !== hosted.state.clientSecretCommitment) return error('state_conflict');
            for (const target of approval.approval.members) {
                const entry = recovery ? this.recoveries.get(`${id}|${target.account}`) : this.applications.get(`${id}|${target.account}`); if (!entry) return error('not_found', 404);
                const request = 'request' in entry ? entry.request : entry.application;
                if (!sdk.equalBytes(request.memberEncryptionPublicKey, target.memberEncryptionPublicKey)) return error('state_conflict');
            }
        }
        const previous = hosted?.events.find(event => sdk.canonicalJson(event.payload) === sdk.canonicalJson(payload));
        if (previous) return method === 'group.message.send' ? response({ sequence: previous.sequence }) : response();
        const sequence = hosted?.events.length ?? 0; let epoch = hosted?.state.epoch ?? 0;
        if (['group.member.remove', 'group.member.leave', 'group.application.approve', 'group.member.recovery.approve', 'group.secret.rotation.commit'].includes(method) || method === 'group.member.ban' && (body.accounts as string[]).some(value => hosted!.state.members.some(member => member.account === value))) epoch++;
        const event = { sequence, epoch, payload, acceptedAt: this.network.clock.wall, signerDeviceId: deviceId }; const ref = { groupId: id, relayId: this.network.descriptor.relayId };
        const applied = applyGroupEvent(hosted?.state, event, certificate, ref, context); if (applied.rejection) throw applied.rejection;
        if (!hosted) { hosted = { state: applied.projection, events: [], certificates: new Map(), clientBoxes: new Map([[0, new Map([[account, sdk.groupCreateRequestCodec.decode(body).clientSecretBox]])]]), relayKeys: new Map() }; this.groups.set(id, hosted); }
        if (!hosted.clientBoxes.has(epoch)) {
            const old = hosted.clientBoxes.get(hosted.state.epoch)!; const next = new Map([...old].filter(([account]) => applied.projection.members.some(member => member.account === account)));
            if (approval) for (const target of approval.approval.members) next.set(target.account, approval.clientSecretBoxes[target.account]!); hosted.clientBoxes.set(epoch, stagedRotation ? new Map(stagedRotation.boxes) : next);
        }
        if (stagedRotation) { this.rotations.delete(id); if (payload.owner_encryption_public_key !== undefined) this.recoveries.delete(`${id}|${account}`); }
        else if (method === 'group.owner.transfer') this.rotations.delete(id);
        else {
            const staged = this.rotations.get(id); if (staged) for (const target of staged.boxes.keys()) {
                const before = hosted.state.members.find(value => value.account === target); const after = applied.projection.members.find(value => value.account === target);
                if (!after || before && !sdk.equalBytes(before.memberEncryptionPublicKey, after.memberEncryptionPublicKey)) staged.boxes.delete(target);
            }
        }
        if (approval) for (const target of approval.approval.members) {
            if (method === 'group.application.approve') { const entry = this.applications.get(`${id}|${target.account}`)!; const invite = this.invites.get(`${id}|${entry.application.inviteId}`)!; this.invites.set(`${id}|${entry.application.inviteId}`, { ...invite, uses: invite.uses + 1 }); this.applications.delete(`${id}|${target.account}`); }
            else this.recoveries.delete(`${id}|${target.account}`);
        }
        hosted.state = applied.projection; hosted.events.push(event); hosted.certificates.set(deviceId, certificate);
        if (!hosted.relayKeys.has(epoch)) hosted.relayKeys.set(epoch, sdk.systemRandom.bytes(32));
        if (this.failReadAfterSend) this.failRead = true;
        if (this.loseResponse === method) throw new Error('Group operation accepted but response lost');
        return method === 'group.message.send' ? response(this.missingSequence ? {} : { sequence: sequence + (this.wrongSequence ? 100 : 0) }) : response();
    }
    async client(seed: number, path?: string) {
        const base = await this.network.client(seed, path); const groups = new sdk.GroupManager({ context, accountId: base.accountId, store: base.store, relayClients: base.pool, deviceManager: base.device, messageManager: base.messages, secretProtector: base.protector, clock: this.network.clock });
        this.network.resources.push(groups); await groups.initialize(); return { ...base, groups, async dispose() { await groups.dispose(); await base.dispose(); } };
    }
    async dispose(): Promise<void> { await this.network.dispose(); for (const group of this.groups.values()) for (const key of group.relayKeys.values()) key.fill(0); }
}
