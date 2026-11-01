/** The waiting call was superseded by a newer durable state update for the same resource. */
export class StateUpdateSupersededError extends Error {
    override readonly name = 'StateUpdateSupersededError';
    constructor(readonly resource: string) { super('A newer state update superseded this publication.'); }
}
