import { requireLength, verifyDevice } from '../crypto/primitives.js';
import { ProtocolError } from '../errors.js';
import { validateIdentifier } from '../identity/identifiers.js';
import { validateAccountId } from '../identity/neo.js';
import { array, boolean, bytes, defineCodec, dictionary, enumeration, integer, text, type ExtensibleModel, type ValueCodec } from '../protocol/codec.js';
import { signingInput, type NetworkContext } from '../protocol/context.js';
import { encodeUtf8, equalBytes } from '../protocol/encoding.js';
import { requireObject, requireSafeInteger } from '../protocol/json.js';
import { validateCompleteObject } from '../protocol/validation.js';
import { containsNonWhitespace } from './content.js';
import { accountDeviceStateCodec, authorizedDevice, certificateId, deviceCertificateCodec, validateCertificate, validateDeviceState, type AccountDeviceState, type DeviceCertificate } from './identity.js';

export interface ContactGrant extends ExtensibleModel { readonly grantor: string; readonly grantee: string; readonly expiresAt?: number; readonly signatures: Readonly<Record<string, Uint8Array>> }
export const contactGrantCodec = defineCodec<ContactGrant>({ grantor: { wire: 'grantor', codec: text }, grantee: { wire: 'grantee', codec: text },
    expiresAt: { wire: 'expires_at', codec: integer, optional: true }, signatures: { wire: 'signatures', codec: dictionary(bytes) } }, 'meshline.contact.grant');
export const contactGrantInput = (value: ContactGrant, context: NetworkContext): Uint8Array => signingInput(contactGrantCodec.encode(value), context, ['signatures']);
export function validateContactGrant(value: ContactGrant, now: number): void {
    validateCompleteObject(contactGrantCodec.encode(value));
    validateAccountId(value.grantor); validateAccountId(value.grantee); requireSafeInteger(now, 0);
    if (value.expiresAt !== undefined) { requireSafeInteger(value.expiresAt, 0); if (value.expiresAt <= now) throw new ProtocolError('expired_grant', 'Contact grant has expired.'); }
    const signatures = Object.entries(value.signatures);
    if (!signatures.length) throw new ProtocolError('invalid_grant', 'A contact grant requires at least one signature.');
    for (const [id, signature] of signatures) { validateIdentifier('device', id); requireLength(signature, 64, 'Grant signature'); }
    contactGrantCodec.encode(value);
}
export function verifyContactGrant(value: ContactGrant, state: AccountDeviceState, context: NetworkContext, now: number): void {
    if (Object.keys(filterContactGrantSignatures(value, state, context, now).signatures).length === 0)
        throw new ProtocolError('unauthorized_grant', 'Contact grant has no valid signature from a currently authorized device.');
}
/** Returns a copy containing only signatures whose device identity is currently authorized by the verified state. */
export function filterContactGrantSignatures(value: ContactGrant, state: AccountDeviceState, context: NetworkContext, now: number): ContactGrant {
    validateContactGrant(value, now); validateDeviceState(state, context);
    if (value.grantor !== state.account) throw new ProtocolError('invalid_identity', 'Contact grant state belongs to another grantor.');
    const input = contactGrantInput(value, context);
    const signatures: Record<string, Uint8Array> = Object.create(null) as Record<string, Uint8Array>;
    for (const certificate of state.certificates) {
        const id = certificateId(certificate, context); const signature = value.signatures[id];
        if (certificate.notBefore <= now && now < certificate.expiresAt && signature !== undefined && verifyDevice(input, signature, certificate.signingPublicKey)) signatures[id] = signature.slice();
    }
    return contactGrantCodec.decode(contactGrantCodec.encode({ ...value, signatures }));
}
export interface ContactGrantSelection { readonly action: 'replace' | 'keep' | 'merge' | 'out_of_scope' | 'invalid'; readonly grant: ContactGrant | undefined; readonly error?: unknown }
/** Saved grants are scoped to one network/account direction. Equal-expiry bodies never replace differing signed extensions. */
export function selectContactGrant(current: ContactGrant | undefined, candidate: ContactGrant, state: AccountDeviceState,
    context: NetworkContext, now: number, candidateContext = context): ContactGrantSelection {
    const previous = current && contactGrantCodec.decode(contactGrantCodec.encode(current));
    if (candidateContext.toString() !== context.toString() || previous && (candidate.grantor !== previous.grantor || candidate.grantee !== previous.grantee))
        return { action: 'out_of_scope', grant: previous };
    let incoming: ContactGrant;
    try {
        incoming = filterContactGrantSignatures(candidate, state, context, now);
        if (Object.keys(incoming.signatures).length === 0) throw new ProtocolError('unauthorized_grant', 'Candidate has no currently authorized signature.');
    } catch (error) { if (!(error instanceof ProtocolError)) throw error; return { action: 'invalid', grant: previous, error }; }
    if (!previous) return { action: 'replace', grant: incoming };
    if (!equalBytes(contactGrantInput(previous, context), contactGrantInput(incoming, context)))
        return (incoming.expiresAt ?? Infinity) > (previous.expiresAt ?? Infinity) ? { action: 'replace', grant: incoming } : { action: 'keep', grant: previous };
    const saved = filterContactGrantSignatures(previous, state, context, now);
    return { action: 'merge', grant: { ...incoming, signatures: { ...incoming.signatures, ...saved.signatures } } };
}
export interface ContactInvite extends ExtensibleModel { readonly inviter: string; readonly expiresAt: number; readonly signerDeviceId: string; readonly deviceSignature: Uint8Array }
export const contactInviteCodec = defineCodec<ContactInvite>({ inviter: { wire: 'inviter', codec: text }, expiresAt: { wire: 'expires_at', codec: integer },
    signerDeviceId: { wire: 'signer_device_id', codec: text }, deviceSignature: { wire: 'device_signature', codec: bytes } }, 'meshline.contact.invite');
export const contactInviteInput = (value: ContactInvite, context: NetworkContext): Uint8Array => signingInput(contactInviteCodec.encode(value), context, ['device_signature']);
export function validateContactInvite(value: ContactInvite, now: number): void {
    validateCompleteObject(contactInviteCodec.encode(value));
    validateAccountId(value.inviter); requireSafeInteger(now, 0); requireSafeInteger(value.expiresAt, 0);
    validateIdentifier('device', value.signerDeviceId); requireLength(value.deviceSignature, 64, 'Invitation signature');
    if (value.expiresAt <= now) throw new ProtocolError('expired_invite', 'Contact invitation has expired.'); contactInviteCodec.encode(value);
}
export function verifyContactInvite(value: ContactInvite, state: AccountDeviceState, context: NetworkContext, now: number): void {
    validateContactInvite(value, now); validateDeviceState(state, context);
    if (value.inviter !== state.account) throw new ProtocolError('invalid_identity', 'Invitation belongs to another account.');
    const certificate = authorizedDevice(state, value.signerDeviceId, context, now);
    if (!verifyDevice(contactInviteInput(value, context), value.deviceSignature, certificate.signingPublicKey)) throw new ProtocolError('invalid_signature', 'Invalid contact invitation signature.');
}
export type ContactAuthorization = ContactGrant | ContactInvite;
export const contactAuthorizationCodec: ValueCodec<ContactAuthorization> = {
    decode(value) {
        const type = requireObject(value)['$type'];
        if (type === 'meshline.contact.grant') return contactGrantCodec.decode(value);
        if (type === 'meshline.contact.invite') return contactInviteCodec.decode(value);
        throw new ProtocolError('invalid_type', 'Expected a contact grant or invitation.');
    },
    encode(value) { return 'grantor' in value ? contactGrantCodec.encode(value) : contactInviteCodec.encode(value); },
};
export interface SignedDeviceStateQuery extends ExtensibleModel {
    readonly account: string; readonly authorization: ContactAuthorization; readonly signerCertificate: DeviceCertificate; readonly createdAt: number; readonly deviceSignature: Uint8Array;
}
export const signedDeviceStateQueryCodec = defineCodec<SignedDeviceStateQuery>({ account: { wire: 'account', codec: text }, authorization: { wire: 'authorization', codec: contactAuthorizationCodec },
    signerCertificate: { wire: 'signer_certificate', codec: deviceCertificateCodec }, createdAt: { wire: 'created_at', codec: integer }, deviceSignature: { wire: 'device_signature', codec: bytes } }, 'meshline.device.state.resolve');
export const deviceStateQueryInput = (value: SignedDeviceStateQuery, context: NetworkContext): Uint8Array => signingInput(signedDeviceStateQueryCodec.encode(value), context, ['device_signature']);
export function validateDeviceStateQuery(value: SignedDeviceStateQuery, context: NetworkContext, now: number): void {
    validateCompleteObject(signedDeviceStateQueryCodec.encode(value));
    validateAccountId(value.account); validateCertificate(value.signerCertificate, context); requireSafeInteger(value.createdAt, 0);
    if (value.account === value.signerCertificate.account) throw new ProtocolError('invalid_authorization', 'Signed device queries cannot target the caller account.');
    if ('grantor' in value.authorization) {
        validateContactGrant(value.authorization, now);
        if (value.authorization.grantor !== value.account || value.authorization.grantee !== value.signerCertificate.account) throw new ProtocolError('invalid_authorization', 'Contact grant does not authorize this query.');
    } else { validateContactInvite(value.authorization, now); if (value.authorization.inviter !== value.account) throw new ProtocolError('invalid_authorization', 'Invitation is for another account.'); }
    if (!verifyDevice(deviceStateQueryInput(value, context), value.deviceSignature, value.signerCertificate.signingPublicKey)) throw new ProtocolError('invalid_signature', 'Invalid signed device query.');
}
export interface ContactConsent extends ExtensibleModel { readonly deviceState: AccountDeviceState; readonly grant: ContactGrant; readonly note?: string }
export const contactConsentCodec = defineCodec<ContactConsent>({ deviceState: { wire: 'device_state', codec: accountDeviceStateCodec }, grant: { wire: 'grant', codec: contactGrantCodec }, note: { wire: 'note', codec: text, optional: true } }, 'meshline.contact.consent');
export function validateContactConsent(value: ContactConsent, context: NetworkContext, now: number): void {
    validateCompleteObject(contactConsentCodec.encode(value));
    if (value.note !== undefined && (!containsNonWhitespace(value.note) || encodeUtf8(value.note).length > 1024)) throw new ProtocolError('invalid_note', 'Consent note must contain non-whitespace text and fit 1024 UTF-8 bytes.');
    verifyContactGrant(value.grant, value.deviceState, context, now); contactConsentCodec.encode(value);
}
export interface ContactRecord extends ExtensibleModel {
    readonly account: string; readonly alias?: string; readonly status: 'active' | 'deleted';
    readonly grantFromContact?: ContactGrant; readonly grantToContact?: ContactGrant; readonly updatedAt: number;
}
export const contactRecordCodec = defineCodec<ContactRecord>({ account: { wire: 'account', codec: text }, alias: { wire: 'alias', codec: text, optional: true }, status: { wire: 'status', codec: enumeration('active', 'deleted') },
    grantFromContact: { wire: 'grant_from_contact', codec: contactGrantCodec, optional: true }, grantToContact: { wire: 'grant_to_contact', codec: contactGrantCodec, optional: true }, updatedAt: { wire: 'updated_at', codec: integer } });
export function validateContactRecord(value: ContactRecord, now: number): void {
    validateCompleteObject(contactRecordCodec.encode(value));
    validateAccountId(value.account); requireSafeInteger(value.updatedAt, 0); requireSafeInteger(now, 0);
    if (value.updatedAt > now + 300) throw new ProtocolError('invalid_time', 'Contact update is more than five minutes in the future.');
    if (value.alias !== undefined && value.alias !== '' && (!containsNonWhitespace(value.alias) || encodeUtf8(value.alias).length > 256)) throw new ProtocolError('invalid_alias', 'Nonempty contact alias must contain non-whitespace text and fit 256 UTF-8 bytes.');
    if (value.status === 'deleted' && (value.grantFromContact || value.grantToContact)) throw new ProtocolError('invalid_contact', 'Deleted contact records cannot contain grants.');
    contactRecordCodec.encode(value);
}
export interface AccountContactSync extends ExtensibleModel { readonly records?: readonly ContactRecord[]; readonly requestSnapshot?: boolean }
export const accountContactSyncCodec = defineCodec<AccountContactSync>({ records: { wire: 'records', codec: array(contactRecordCodec), optional: true }, requestSnapshot: { wire: 'request_snapshot', codec: boolean, optional: true } }, 'meshline.account.contacts.sync');
export function validateAccountContactSync(value: AccountContactSync, now: number, ownerAccount?: string): void {
    validateCompleteObject(accountContactSyncCodec.encode(value));
    const accounts = new Set<string>(); let owner = ownerAccount;
    for (const record of value.records ?? []) {
        validateContactRecord(record, now);
        if (accounts.has(record.account)) throw new ProtocolError('duplicate_contact', 'A contact sync batch cannot contain duplicate accounts.'); accounts.add(record.account);
        if (record.grantFromContact) {
            const grant = record.grantFromContact; validateContactGrant(grant, now);
            if (grant.grantor !== record.account || owner !== undefined && grant.grantee !== owner) throw new ProtocolError('invalid_identity', 'Incoming contact grant has inconsistent accounts.'); owner = grant.grantee;
        }
        if (record.grantToContact) {
            const grant = record.grantToContact; validateContactGrant(grant, now);
            if (grant.grantee !== record.account || owner !== undefined && grant.grantor !== owner) throw new ProtocolError('invalid_identity', 'Outgoing contact grant has inconsistent accounts.'); owner = grant.grantor;
        }
    }
    if (owner !== undefined && accounts.has(owner)) throw new ProtocolError('invalid_identity', 'A contact sync batch cannot contain its owner.'); accountContactSyncCodec.encode(value);
}
