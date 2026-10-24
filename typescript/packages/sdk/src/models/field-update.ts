const deleted: unique symbol = Symbol('Meshline.FieldUpdate.delete');

/** Explicit client-side updates. Omission retains a field; deletion is distinct from assigning null. */
export const FieldUpdate: { readonly unspecified: undefined; readonly delete: typeof deleted } = Object.freeze({
    unspecified: undefined,
    delete: deleted,
});

/** Assign a value directly, leave it unspecified, or use FieldUpdate.delete. */
export type FieldUpdate<T> = T | typeof deleted | undefined;
