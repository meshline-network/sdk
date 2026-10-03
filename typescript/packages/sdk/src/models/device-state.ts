import { ProtocolError } from '../errors.js';
import { defineCodec, enumeration, integer, type ExtensibleModel } from '../protocol/codec.js';
import { requireSafeInteger } from '../protocol/json.js';
import { validateCompleteObject } from '../protocol/validation.js';

export interface DeviceStatePublishResponse extends ExtensibleModel { readonly status: 'accepted' | 'staged'; readonly stagedUntil?: number }
export const deviceStatePublishResponseCodec = defineCodec<DeviceStatePublishResponse>({
    status: { wire: 'status', codec: enumeration('accepted', 'staged') }, stagedUntil: { wire: 'staged_until', codec: integer, optional: true },
});
export function validateDeviceStatePublishResponse(value: DeviceStatePublishResponse, now: number): void {
    validateCompleteObject(deviceStatePublishResponseCodec.encode(value));
    if (value.status === 'accepted') {
        if (value.stagedUntil !== undefined) throw new ProtocolError('invalid_response', 'An accepted device state must omit staged_until.');
    } else if (value.status === 'staged') {
        requireSafeInteger(value.stagedUntil, 1);
        if (value.stagedUntil <= now) throw new ProtocolError('invalid_response', 'The staged device state has already expired.');
    } else throw new ProtocolError('invalid_response', 'Unknown device state publication status.');
}
