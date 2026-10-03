import { requireLength, verifyDevice } from '../crypto/primitives.js';
import { ProtocolError } from '../errors.js';
import { validateAccountId } from '../identity/neo.js';
import { boolean, bytes, defineCodec, integer, text, type ExtensibleModel } from '../protocol/codec.js';
import { signingInput, type NetworkContext } from '../protocol/context.js';
import { encodeUtf8 } from '../protocol/encoding.js';
import { requireSafeInteger } from '../protocol/json.js';
import { validateCompleteObject } from '../protocol/validation.js';
import { containsNonWhitespace, contentReferenceCodec, validateContentReference, type ContentReference } from './content.js';
import { deviceCertificateCodec, validateCertificate, type DeviceCertificate } from './identity.js';

export interface AccountProfile extends ExtensibleModel {
    readonly account: string; readonly nickname?: string; readonly avatar?: ContentReference; readonly bio?: string;
    readonly publicDiscovery: boolean; readonly updatedAt: number; readonly deviceSignature: Uint8Array;
}
export const accountProfileCodec = defineCodec<AccountProfile>({
    account: { wire: 'account', codec: text }, nickname: { wire: 'nickname', codec: text, optional: true },
    avatar: { wire: 'avatar', codec: contentReferenceCodec, optional: true }, bio: { wire: 'bio', codec: text, optional: true },
    publicDiscovery: { wire: 'public_discovery', codec: boolean }, updatedAt: { wire: 'updated_at', codec: integer }, deviceSignature: { wire: 'device_signature', codec: bytes },
}, 'meshline.profile');
export const profileInput = (value: AccountProfile, context: NetworkContext): Uint8Array => signingInput(accountProfileCodec.encode(value), context, ['device_signature']);
export function validateProfile(value: AccountProfile): void {
    validateCompleteObject(accountProfileCodec.encode(value));
    validateAccountId(value.account); requireSafeInteger(value.updatedAt, 0); requireLength(value.deviceSignature, 64, 'Profile signature');
    for (const [field, limit] of [[value.nickname, 256], [value.bio, 2048]] as const) {
        if (field !== undefined && field !== '' && (!containsNonWhitespace(field) || encodeUtf8(field).length > limit))
            throw new ProtocolError('invalid_profile_text', `Nonempty profile text must contain non-whitespace characters and fit ${limit} UTF-8 bytes.`);
    }
    if (value.avatar) {
        validateContentReference(value.avatar);
        if (!value.avatar.contentType.toLowerCase().startsWith('image/')) throw new ProtocolError('invalid_avatar', 'An avatar requires an image media type.');
    }
    if (encodeUtf8(accountProfileCodec.stringify(value)).length > 8192) throw new ProtocolError('invalid_size', 'Profile exceeds 8192 canonical JSON bytes.');
}
export interface ProfileResolveResult extends ExtensibleModel { readonly profile: AccountProfile; readonly signerCertificate: DeviceCertificate }
export const profileResolveResultCodec = defineCodec<ProfileResolveResult>({
    profile: { wire: 'profile', codec: accountProfileCodec }, signerCertificate: { wire: 'signer_certificate', codec: deviceCertificateCodec },
});
/** The certificate is evidence at publication acceptance, not a claim of current device authorization. */
export function validateProfileResult(value: ProfileResolveResult, context: NetworkContext, account = value.profile.account): void {
    validateCompleteObject(profileResolveResultCodec.encode(value));
    validateProfile(value.profile); validateCertificate(value.signerCertificate, context);
    if (value.profile.account !== account || value.signerCertificate.account !== account) throw new ProtocolError('invalid_identity', 'Profile or signer certificate belongs to another account.');
    if (!verifyDevice(profileInput(value.profile, context), value.profile.deviceSignature, value.signerCertificate.signingPublicKey))
        throw new ProtocolError('invalid_signature', 'Invalid profile device signature.');
}
