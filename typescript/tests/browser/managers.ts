import { IndexedDbStore } from '../../packages/storage-browser/src/index.js';
import { managerRoundtrip as sharedRoundtrip } from '../portable/managers.js';
export function managerRoundtrip() { return sharedRoundtrip({ createStore: () => new IndexedDbStore('manager-roundtrip') }); }
