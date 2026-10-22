# React + TypeScript + Vite

## Local HTTP access

The canonical browser UI follows [ADR 32](../../.project/design/32-local-http-access.md).
`src/api/access.ts` owns one memory-only session, pairing verifier and lifecycle.
Every API call, including chat SSE, goes through `src/api/http.ts`. There is no
browser owner secret, cookie session, credential storage or HTTP approval route.
Reloading an ordinary tab locks it.

Archive browsing, keyword search and explicit search-only chat work anonymously.
Settings, preferences and account reads need `config.read`; changes need `mutate`;
AI chat and semantic/hybrid search need `model.invoke`. A partial grant enables
only its granted controls. Locking, expiry, rejected credentials and replacement
instances cancel outstanding work and clear query/editor state.
Local locking advances that fence immediately, before awaiting server revocation.
A failed revocation remains visibly unconfirmed and cannot restore local authority.
Valid same-instance focus/visibility checks leave editors, pending actions and their
feedback mounted; expiry, identity changes and permission loss still clear stale work.

Choose **Unlock Hermes**, then use the displayed code in a foreground terminal:

```text
Hermes.Service pair-ui CODE --config-dir SAME-ABSOLUTE-CONFIG-DIRECTORY --port SELECTED-PORT
```

Use the account and absolute configuration directory that launched the selected
Service. Confirm the displayed browser origin, numeric API audience and permissions
before approving. The page deliberately doesn't disclose the configuration path.
Cancellation discards the verifier; a lost exchange requires a new pairing.

The Vite development proxy keeps `/api/` requests relative to the browser origin
and pins the API audience to `http://127.0.0.1:21742`. For an explicitly trusted
preview proxy targeting another instance, set the build-time
`VITE_HERMES_API_AUDIENCE` to its exact numeric loopback audience. To select direct
API transport instead, use `VITE_HERMES_API_ORIGIN`; it must equal the audience.
The Service must explicitly admit the actual preview origin. Audience identity
doesn't authorise sending credentials to external, MCP or provider URLs.

Original PDF/raster previews also use the shared transport, including a selected
API origin. The detail view binds each preview to its document, file identity and
access generation, validates MIME/signature, then displays an owned blob URL.
Navigation and authority changes abort reads and revoke those URLs; late replies
can't replace the current document. HTML/SVG and mismatched originals produce a
visible format error rather than an executable same-origin frame. PDF frames are
sandboxed without script permission.

Windows WebView uses only the ADR's version-1 `hermes.access.ready`, `.pairing` and
`.status` messages, with the current navigation and instance identifiers. Pairing
notices carry the challenge, never the verifier; authenticated status is sent only
after the verifier exchange. Ordinary browsers use foreground pairing.

### Browser access validation

From this directory:

```bash
npm run test:unit
npm run lint
npm run build
npm run test:discovery
```

Node tests use fake fetches to check protocol validation, cancellation, late replies,
SSE disposal, permission subsets and action acknowledgements. Byte-stream cases
exercise BOM rejection, malformed UTF-8 and the actual bounded response decoder.
Preview cases assert the selected API URL, proxy transport, source races, URL disposal
and format failures. Render/action units compile the real source-card and retained
`SyncConfigPanel` with isolated dependencies; the latter isn't mounted by canonical
`SettingsPage`. These are unit results, not HTTP authentication or OS ownership evidence.

`tests/access.spec.ts` adds real-Service access journeys to the existing canonical
browser suites. `tests/owner.ts` obtains **nonsecret owned-harness metadata** from
`test-results/issue28-harness.json`, validates its synthetic ownership marker and
invokes the actual early CLI with `yes` on stdin. It never obtains a bearer outside
the page or approves over HTTP. Existing UI response/error mocks still exercise UI
behaviour; they don't replace access endpoints or owner approval.
The focus/save and immediate-lock regressions delay delivery of genuine Service
responses without minting authority. `tests/preview.spec.ts` uses synthetic metadata
and file-response fixtures to exercise rendering, visible rejection and blob cleanup
on the canonical document route.

When the backend candidate implements the frozen HTTP and early CLI contract, run:

```bash
HERMES_TEST_SERVICE_DLL=/absolute/path/to/candidate/Hermes.Service.dll npm test
```

The owned harness selects disposable, non-production ports and creates its configuration
and archive under `test-results/issue28-fixtures/`. Browser output goes exclusively to
`test-results/issue28-canonical/`. Restart tests stop only that harness's child process.
Network tracing is disabled because Playwright traces persist pairing proofs and
Authorization headers. No bearer or verifier belongs in a retained test artifact.
Synthetic provider counters check that denied and search-only calls make no model calls.
Browser-clock expiry cases advance the browser's best-effort clock, not server time.
Actual server expiry and OS pipe ownership need their backend/native integration gates.

The new browser journeys await the real backend contract; discovery alone isn't an
execution result. Windows WebView authentication and macOS/Windows runtime evidence
remain separate required integration gates.
The current harness owns one Service and one provider/control listener, not two
independent Services. Selected-B preview routing is covered by unit requests; a
two-Service archive/preview journey remains a separate integration gate.

Independent frontend revision validation on 2026-10-20:

| Command | Result |
| --- | --- |
| `npm run test:unit` | Passed: 99 Node cases; original 65 retained, no skipped cases |
| `npm run lint` | Passed |
| `npm run build` | Passed: TypeScript and canonical asset-manifest generation |
| `npm run test:discovery` | Passed: existing 45 + original 11 access + 2 access regressions + 2 preview cases; 60 discovered only |
| `node --check tests/serve.mjs` | Passed |
| `node --check tests/assert-discovery.mjs` | Passed |
| `npx tsc --ignoreConfig --noEmit --target es2023 --module esnext --moduleResolution bundler --allowImportingTsExtensions --lib es2023,dom --types node,vite/client --skipLibCheck tests/*.ts tests/unit/*.ts` | Passed: test and helper types |
| `npm test` | Not run: awaiting the real HTTP/early-CLI backend candidate |

TypeScript 6 requires `--ignoreConfig` for the explicit test-file command above;
the initial invocation without it returned TS5112 and was corrected. No dependency
installation was needed. Candidate Service builds must embed this React build and
its matching manifest before running the browser suite.

The first revision check found a TypeScript narrowing error and a widened unit
fixture type; both were corrected before the passing gates above. A standalone PNG
chunk check also rejected the initial browser fixture's CRC; its replacement has
valid chunk CRCs and a valid decompressed one-pixel payload. These checks did not
start a browser or the incomplete backend.

This template provides a minimal setup to get React working in Vite with HMR and some ESLint rules.

Currently, two official plugins are available:

- [@vitejs/plugin-react](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react) uses [Oxc](https://oxc.rs)
- [@vitejs/plugin-react-swc](https://github.com/vitejs/vite-plugin-react/blob/main/packages/plugin-react-swc) uses [SWC](https://swc.rs/)

## React Compiler

The React Compiler is not enabled on this template because of its impact on dev & build performances. To add it, see [this documentation](https://react.dev/learn/react-compiler/installation).

## Expanding the ESLint configuration

If you are developing a production application, we recommend updating the configuration to enable type-aware lint rules:

```js
export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      // Other configs...

      // Remove tseslint.configs.recommended and replace with this
      tseslint.configs.recommendedTypeChecked,
      // Alternatively, use this for stricter rules
      tseslint.configs.strictTypeChecked,
      // Optionally, add this for stylistic rules
      tseslint.configs.stylisticTypeChecked,

      // Other configs...
    ],
    languageOptions: {
      parserOptions: {
        project: ['./tsconfig.node.json', './tsconfig.app.json'],
        tsconfigRootDir: import.meta.dirname,
      },
      // other options...
    },
  },
])
```

You can also install [eslint-plugin-react-x](https://github.com/Rel1cx/eslint-react/tree/main/packages/plugins/eslint-plugin-react-x) and [eslint-plugin-react-dom](https://github.com/Rel1cx/eslint-react/tree/main/packages/plugins/eslint-plugin-react-dom) for React-specific lint rules:

```js
// eslint.config.js
import reactX from 'eslint-plugin-react-x'
import reactDom from 'eslint-plugin-react-dom'

export default defineConfig([
  globalIgnores(['dist']),
  {
    files: ['**/*.{ts,tsx}'],
    extends: [
      // Other configs...
      // Enable lint rules for React
      reactX.configs['recommended-typescript'],
      // Enable lint rules for React DOM
      reactDom.configs.recommended,
    ],
    languageOptions: {
      parserOptions: {
        project: ['./tsconfig.node.json', './tsconfig.app.json'],
        tsconfigRootDir: import.meta.dirname,
      },
      // other options...
    },
  },
])
```
