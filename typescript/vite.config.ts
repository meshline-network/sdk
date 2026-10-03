import { defineConfig } from 'vite';

export default defineConfig({
    // Generated consumers and toolchains are not browser harness inputs. In
    // particular, a running Windows emulator holds an exclusive AVD lock file.
    server: { watch: { ignored: ['**/artifacts/**'] } },
});
