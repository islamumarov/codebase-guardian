# Server-Implementer Digest: MCP Skills Extension (SEP-2640, FINAL) + MCP Events (DRAFT design sketch)

Compiled 2026-10-04. Tags used throughout:

- **MUST / SHOULD / MAY**: copied from the source (verbatim or near-verbatim).
- **[AMBIGUOUS]**: the source is silent or self-contradictory. You must pick a behaviour.
- **[INFERENCE]**: my derivation, not text from a source.
- **[CONFORMANCE]**: what the official conformance suite (`modelcontextprotocol/conformance`, PR #330, merged 2026-09-11) actually checks on the wire.

Sources read in full:

- `ext-skills/specification/stable/skills.mdx`. This is the source of truth; `skills.md` only includes it.
- `ext-skills/docs/decisions.md`, `docs/implementations.md`, `docs/rationale.md`.
- `experimental-ext-triggers-events/docs/design-sketch-proposal.md`, all 1020 lines. The repo's last commit is 2026-09-08 (PR #1); the doc header says "Draft proposal, 2026-02-19".
- Base spec context:
  - agentskills.io/specification and the `skills-ref` validator source.
  - The MCP `2026-07-28` changelog and these pages: basic/index, versioning, streamable-http, subscriptions, server/discover, resources, caching.
  - The Standard Webhooks spec (raw), plus the conformance suite sources for SEP-2640.

---

## 0. Base protocol 2026-07-28 facts both specs depend on

Neither extension spec restates these, and a server will fail without them.

- **Stateless, no `initialize`.** Every request carries `_meta`:
  - `io.modelcontextprotocol/protocolVersion` (string) and `io.modelcontextprotocol/clientCapabilities` (object) are **required**.
  - `io.modelcontextprotocol/clientInfo` SHOULD be sent.
  - "A request missing any required field is malformed; the server **MUST** reject it with JSON-RPC error code `-32602`… On HTTP, the response status **MUST** be `400 Bad Request`."
  - Servers SHOULD put `io.modelcontextprotocol/serverInfo` in every result's `_meta`.
  - "Servers **MUST NOT** rely on prior requests over the same connection to establish context."
- **`server/discover`** (servers MUST implement). It is where `capabilities.extensions` and `instructions` live:

  ```json
  {"jsonrpc":"2.0","id":"discover-1","result":{"resultType":"complete","supportedVersions":["2026-07-28"],
   "capabilities":{"tools":{},"resources":{}},
   "_meta":{"io.modelcontextprotocol/serverInfo":{"name":"ExampleServer","version":"1.0.0"}},
   "instructions":"This server provides weather and resource utilities.","ttlMs":3600000,"cacheScope":"public"}}
  ```

- **`resultType`.** "The `result` **MUST** include a `resultType` field": `"complete"` or `"input_required"`.
- **CacheableResult (SEP-2549).**
  - `ttlMs` is an integer and "Servers **MUST** provide a `ttlMs` value that is `>= 0`". `0` means immediately stale.
  - `cacheScope` is `"public"` or `"private"`. With `"private"`, caches "**MUST NOT** be shared across authorization contexts".
  - "Servers **MUST** apply the same `cacheScope` to all response pages for a given list request."
- **HTTP standard headers (SEP-2243).**
  - `Mcp-Method` is required on all requests.
  - `Mcp-Name` is required only for `tools/call`, `resources/read` (value = `params.uri`) and `prompts/get`. Non-header-safe values use `=?base64?<b64 of UTF-8>?=`.
  - Mismatch or missing header → HTTP 400 with JSON-RPC `-32020 HeaderMismatch`. Servers "**MUST** decode an encoded `Mcp-Name`… before comparing".
  - The `MCP-Protocol-Version` header must equal the body `_meta` version.
  - An unknown method → HTTP `404` + `-32601`.
- **`subscriptions/listen` (SEP-2575)** replaces `resources/subscribe` and the GET stream.
  - The filter keys are exactly `toolsListChanged`, `promptsListChanged`, `resourcesListChanged` (booleans) and `resourceSubscriptions` (string[]).
  - "The server **MUST NOT** send notification types the client has not explicitly requested."
  - The first message is `notifications/subscriptions/acknowledged`.
  - Every notification carries `_meta["io.modelcontextprotocol/subscriptionId"]` = the JSON-RPC id of the listen request.
- **Cancellation.** HTTP: "Closing the SSE response stream **MUST** be treated by the server as cancellation". stdio: `notifications/cancelled`.
- **Error-code allocation policy (NEW in 2026-07-28).**
  - `-32000..-32019` is "legacy": "New codes **MUST NOT** be allocated in this sub-range, and new implementations **SHOULD NOT** use codes from this sub-range at all".
  - `-32020..-32099` is reserved for the MCP spec: "Implementations **MUST NOT** emit any code from this sub-range that is not defined by this specification."
  - The defined codes are `-32020 HeaderMismatch`, `-32021 MissingRequiredClientCapability` and `-32022 UnsupportedProtocolVersion`. `-32002` and `-32042` are retired.
  - Resource-not-found is now `-32602`. (This matters for Events; see B12.)
- **Extension negotiation (SEP-2133).**
  - "`extensions`… is a map of extension identifiers to per-extension settings objects". Servers declare in the `server/discover` `capabilities`; clients declare in the per-request `_meta["io.modelcontextprotocol/clientCapabilities"].extensions`.
  - "If one party supports an extension but the other does not, the supporting party **MUST** either revert to core protocol behavior or reject the request with an appropriate error."

---

# PART A: SKILLS EXTENSION (`io.modelcontextprotocol/skills`, SEP-2640, Final)

Baseline: "This page specifies the extension against base protocol revision `2026-07-28` or later." Every request MUST include the required request `_meta`.

## A1. Capability declaration

Server side, inside `server/discover` → `result.capabilities`:

```jsonc
{
  "capabilities": {
    "resources": {},
    // Other capabilities...
    "extensions": {
      "io.modelcontextprotocol/skills": {
        "directoryRead": true,
      },
    },
  },
}
```

| Setting | Type | Default | Meaning |
|---|---|---|---|
| `directoryRead` | boolean | `false` | The server implements `resources/directory/read`. |

- "An empty object indicates support for the extension with no optional features." Use `{}`, not `true`.
- A declaring server **MUST** implement `skills/list` and `skills/get`.
- `directoryRead: true` → the server **MUST** implement `resources/directory/read`. "Clients **MUST NOT** call `resources/directory/read` against a server that has not declared `directoryRead: true`."
- "A server declaring this extension therefore **MUST** also declare the `resources` capability" (because files are served via `resources/read`).
- [CONFORMANCE] The settings object must be inline. The keys `config`, `specVersion`, `stability` and `id` inside it are reported as an envelope and FAIL `sep-2640-capability-declaration-inline`. A non-object value (`true`, a string) FAILS `sep-2640-capability-empty-object`. `capabilities.resources` must be an object.

**Client side.** The skills spec defines **no** client-side declaration or settings. The only statement is: "Clients issue `skills/list` and `skills/get` only after observing the server's declaration. To a client that does not implement this extension, `skill://` resources are ordinary resources."

- [CONFORMANCE] The harness sends `clientCapabilities: {sampling:{}, elicitation:{}, roots:{listChanged:true}}`, with no `extensions`. A server that gates `skills/list`/`skills/get` on a client declaration fails `sep-2640-skills-list-implemented` and others.
- **Do not gate.** The implementations list notes that Tachyon "requires clients to advertise the extension capability". That is outside the spec.
- A client that wants to declare it would presumably send `"extensions":{"io.modelcontextprotocol/skills":{}}`. [INFERENCE]

## A2. Skill format (delegated to agentskills.io)

The SEP requires the following:

- A skill **MUST** conform to the Agent Skills spec.
- Every skill **MUST** contain `SKILL.md` at its root.
- `SKILL.md` **MUST** begin with YAML frontmatter containing at minimum `name` and `description`.
- A skill MAY contain other files and subdirectories.

Agent Skills spec, verbatim constraints:

| Field | Required | Constraints |
|---|---|---|
| `name` | Yes | Max 64 characters. Lowercase letters, numbers, and hyphens only. Must not start or end with a hyphen. |
| `description` | Yes | Max 1024 characters. Non-empty. Describes what the skill does and when to use it. |
| `license` | No | License name or reference to a bundled license file. |
| `compatibility` | No | Max 500 characters. Indicates environment requirements (intended product, system packages, network access, etc.). |
| `metadata` | No | Arbitrary key-value mapping for additional metadata (a map from string keys to string values). |
| `allowed-tools` | No | Space-separated string of pre-approved tools the skill may use. (Experimental) |

- **`name` field.**
  - "Must be 1-64 characters".
  - "May only contain unicode lowercase alphanumeric characters (`a-z`, `0-9`) and hyphens (`-`)".
  - "Must not start or end with a hyphen"; "Must not contain consecutive hyphens (`--`)".
  - "Must match the parent directory name".
  - Invalid examples: `PDF-Processing`, `-pdf`, `pdf--processing`.
- **`description` field.** "Must be 1-1024 characters". `compatibility`: "Must be 1-500 characters if provided".
- **Directories.** `scripts/` (executable code), `references/` (docs such as `REFERENCE.md`, `FORMS.md`), `assets/` (templates, images, data). These are recommendations only; "Any additional files or directories" are allowed.
- **Guidance.** Metadata is roughly 100 tokens; instructions are "< 5000 tokens recommended"; "Keep your main `SKILL.md` under 500 lines"; file references use relative paths "one level deep from `SKILL.md`".
- **[AMBIGUOUS] Name character set.**
  - The text says "unicode lowercase alphanumeric" but lists `a-z`, `0-9`.
  - The `skills-ref` validator NFKC-normalizes and accepts any Unicode `isalnum()` lowercase letter.
  - [CONFORMANCE] The suite uses `^(?!.*--)[a-z0-9]([a-z0-9-]{0,62}[a-z0-9])?$`, which is ASCII only.
  - The URI authority SHOULD be an RFC 3986 `reg-name`.
  - → **Use ASCII `[a-z0-9-]` only.**
- **[AMBIGUOUS] Extra frontmatter fields.** `skills-ref validate` rejects any top-level field outside `{name, description, license, allowed-tools, metadata, compatibility}`. MCP requires passing every author-written field through unchanged. Serve them anyway, but expect `skills-ref` to flag them.
- `skills-ref` also accepts a lowercase `skill.md`. MCP URIs require the literal `SKILL.md`.

## A3. URI rules (Resource Mapping)

- Servers **SHOULD** use `skill://<skill-path>/<file-path>`.
  - `<skill-path>` is "one or more segments… It **MAY** be a single segment (`git-workflow`) or nested to arbitrary depth (`acme/billing/refunds`)".
  - `<file-path>` is relative to the skill root, `/`-separated.
- The `SKILL.md` URI is always `skill://<skill-path>/SKILL.md`. The **skill root directory URI is `skill://<skill-path>`**: "the `SKILL.md` URI with the `/SKILL.md` suffix removed and no trailing slash".
- "The final segment of `<skill-path>` **MUST** equal the skill's `name`" and **MUST** satisfy the Agent Skills naming rules.
- Preceding segments are a server-chosen prefix (domain, team, version…).
- "The first segment occupies the URI authority component and **SHOULD** be a valid `reg-name` per RFC 3986. Any other prefix segments **SHOULD** be valid URI path segments." Clients MUST NOT DNS-resolve the authority.
- Other schemes are allowed. "A server **MAY** serve skills under another scheme native to its domain (for example, `github://owner/repo/skills/refunds/SKILL.md`)." The structural constraints (path ends in the name, explicit `SKILL.md`) apply regardless, and `skills/list` enumerates skills regardless of scheme.
  - "A host **MUST NOT** conclude that a resource is a skill merely because its URI carries a particular scheme." Skill-ness comes only from a `skills/list` entry or a `skills/get` answer.
- **Nesting.** "A `SKILL.md` **MAY** appear in a descendant directory of a skill." A nested skill's directory name is its `name`. Publication is flat: the nested skill gets its own ordinary listing entry, and nothing marks nesting.
- **Relative references** resolve against the directory containing that `SKILL.md`. For example, `references/GUIDE.md` in `skill://acme/billing/refunds/SKILL.md` resolves to `skill://acme/billing/refunds/references/GUIDE.md`.
- Examples from the spec: `skill://git-workflow/SKILL.md`, `skill://pdf-processing/references/FORMS.md`, `skill://pdf-processing/scripts/extract.py`, `skill://acme/billing/refunds/SKILL.md`, `skill://acme/billing/refunds/examples/email.md`.
- A skill URI is server-scoped: two servers may both serve `skill://refunds/SKILL.md` as unrelated skills.

## A4. The `Skill` entry (shared by `skills/list` and `skills/get`)

```typescript
interface SkillResource {
  /** Resource URI of the file. */
  uri: string;
  /** SHA-256 digest of the file's raw bytes, formatted as `sha256:{hex}`
   *  where {hex} is 64 lowercase hexadecimal characters. */
  digest: string;
  /** Length in bytes of the file's raw content (the same bytes that `digest` covers). */
  size: number;
}
interface Skill {
  /** Resource URI of the skill's SKILL.md, readable via resources/read. */
  uri: string;
  /** The skill's SKILL.md YAML frontmatter, rendered verbatim as a JSON object.
   *  `name` and `description` are always present; every other field passes through unchanged. */
  frontmatter: { name: string; description: string; [key: string]: unknown; };
  /** Complete enumeration of SKILL.md and every supporting file, or "dynamic". */
  resources: SkillResource[] | "dynamic";
}
```

- **There is no top-level `name`, `description`, `_meta` or `title` on `Skill` or `SkillResource`.** Name and description live only in `frontmatter`. Do not invent extra fields.
- **`frontmatter`.**
  - "The `frontmatter` object **MUST** be identical in content to the frontmatter of the `SKILL.md` it describes."
  - It contains "every field the author wrote, not a curated subset".
  - The final `<skill-path>` segment of `uri` **MUST** equal `frontmatter.name`.
  - Keys in `frontmatter.metadata` prefixed `io.modelcontextprotocol/` are reserved, and none are defined yet.
  - [CONFORMANCE] Any such key gives a WARNING.
- **`resources`.** It is **REQUIRED** on every entry, as an array or the string `"dynamic"`. Any other value, or absence, makes the entry invalid, and hosts **MUST NOT** load it.
  - The array "**MUST** be complete. It lists every file of the skill, each exactly once, including an entry whose `uri` equals the skill's top-level `uri`".
  - "Each `uri` **MUST** be the skill's `SKILL.md` or a file within the skill's directory."
  - "Each entry **MUST** carry `size`."
  - A nested skill's files **MUST** also appear in the enclosing skill's `resources`. The same file may appear in both entries, so changing a nested file changes the enclosing skill's set.
  - Only files are listed. Directories are never listed. [INFERENCE from "every file"]
- **Dynamic skills.** "When a skill's content is generated dynamically, such that stable digests cannot be published, the server **MUST** set `resources` to the string `"dynamic"`." Hosts MAY decline such skills, and "server authors **SHOULD** expect that some hosts will".
  - `docs/decisions.md` (2026-07-15 and 07-16) says dynamic skills "omit `resources`". **That is superseded.** The stable spec says the explicit `"dynamic"` string, and omission is invalid.

## A5. `skills/list`

```typescript
interface ListSkillsRequest extends PaginatedRequest { method: "skills/list"; }   // params: { cursor?: Cursor } — no filters
interface ListSkillsResult extends PaginatedResult, CacheableResult { skills: Skill[]; }
```

Example from the spec (verbatim):

```json
{
  "jsonrpc": "2.0",
  "id": 4,
  "result": {
    "resultType": "complete",
    "skills": [
      {
        "uri": "skill://git-workflow/SKILL.md",
        "frontmatter": {
          "name": "git-workflow",
          "description": "Follow this team's Git conventions for branching and commits"
        },
        "resources": [
          {
            "uri": "skill://git-workflow/SKILL.md",
            "digest": "sha256:b95a384300adeea2d902f7d19cd7c04b378ef58e09759107b9c7db4dcacbaa25",
            "size": 190
          }
        ]
      },
      {
        "uri": "skill://acme/billing/refunds/SKILL.md",
        "frontmatter": {
          "name": "refunds",
          "description": "Process customer refund requests per company policy",
          "license": "Apache-2.0"
        },
        "resources": [
          { "uri": "skill://acme/billing/refunds/SKILL.md", "digest": "sha256:ae7bc7e45f44a6381977ef1f772cbad6e38d8ec78903ea7b87abb373815a8f89", "size": 175 },
          { "uri": "skill://acme/billing/refunds/examples/email.md", "digest": "sha256:a724fa5ed3b9e39c0e731fe1f0e866eb46d23969af25ecf0d5f41982e3e36bea", "size": 58 }
        ]
      },
      {
        "uri": "skill://reports/daily/SKILL.md",
        "frontmatter": { "name": "daily", "description": "Assemble today's operational report from live data" },
        "resources": "dynamic"
      }
    ],
    "ttlMs": 300000,
    "cacheScope": "public"
  }
}
```

Rules:

- `resultType` **MUST** be `"complete"`.
- "The result **MAY** be empty… **MAY** return an empty or partial listing" for large, generated or unenumerable catalogs.
- Pagination mirrors base list methods: optional `cursor` in, `nextCursor` out.
  - "An entry is atomic, and a skill's `resources` set is never split across pages."
  - [CONFORMANCE] The suite follows up to 50 pages. A repeated or never-ending cursor is a FAILURE.
- "`ttlMs` and `cacheScope` are **REQUIRED**". They are a freshness hint and cache scope, "not an integrity property". Use `"private"` if the listing varies by caller (base caching rules).
- Entries for all schemes are served. Within a listing, names "**SHOULD** be unique, but they are not guaranteed to be". [CONFORMANCE] Collisions give a WARNING.
- An entry is a complete manifest: "A host does not need to call `skills/get` to complete a listed entry."

## A6. `skills/get`

```typescript
interface GetSkillRequest extends Request { method: "skills/get"; params: { /** URI of the skill's SKILL.md. */ uri: string; }; }
interface GetSkillResult extends CacheableResult { skill: Skill; }
```

```json
{ "jsonrpc": "2.0", "id": 5, "method": "skills/get", "params": { "uri": "skill://pdf-processing/SKILL.md" } }
```

```json
{
  "jsonrpc": "2.0", "id": 5,
  "result": {
    "resultType": "complete",
    "skill": {
      "uri": "skill://pdf-processing/SKILL.md",
      "frontmatter": { "name": "pdf-processing", "description": "Extract, fill, and assemble PDF documents" },
      "resources": [
        { "uri": "skill://pdf-processing/SKILL.md", "digest": "sha256:99b737495721155ece826d57521e2d66141ebdc1344a400487481ea2642ab19e", "size": 151 },
        { "uri": "skill://pdf-processing/templates/invoice.md", "digest": "sha256:61f4ea6d2c75fde1b4977219e7e3107d491c3c26aefb6686e84d6281c088d9ee", "size": 29 },
        { "uri": "skill://pdf-processing/templates/purchase-order.md", "digest": "sha256:f2ff774b1737ff3dec81c47946f9976f18a1a9f69dda0a81f22eabd95173c158", "size": 35 }
      ]
    },
    "ttlMs": 300000,
    "cacheScope": "public"
  }
}
```

Rules:

- `resultType` **MUST** be `"complete"`. `params.uri` **MUST** be the URI of a skill's `SKILL.md`.
- "If the URI does not identify a skill the server serves, the server **MUST** return error `-32602` (Invalid params)." The spec example:

  ```json
  {"jsonrpc":"2.0","id":5,"error":{"code":-32602,"message":"No skill is served at skill://acme/billing/chargebacks/SKILL.md"}}
  ```

  [CONFORMANCE] The suite probes `skill://mcp-conformance-nonexistent-skill-9f3a2b/SKILL.md` and expects `-32602`. Any success is a FAILURE.
- Treat these as "not a skill" → `-32602` [INFERENCE]: a skill-root URI without `/SKILL.md`, a supporting-file URI, and a nested `SKILL.md` you do not publish as a skill.
- "A server **MUST** answer for every skill it serves, whether or not that skill appears in its `skills/list` result."
- The `skill` object is identical in shape and meaning to a listing entry. [CONFORMANCE] `result.skill.uri` must echo the requested URI exactly.
- The result is a point-in-time snapshot. `ttlMs` and `cacheScope` are **REQUIRED** (`extends CacheableResult`, decision 2026-09-08).
- "The result carries no pagination cursor." [CONFORMANCE] Any `nextCursor` is a FAILURE.
- Dynamic skills carry `"resources": "dynamic"` here too.

## A7. Serving files via `resources/read`

- "Skill files are read via the standard `resources/read` method. No skill-specific read semantics are defined."
- A read of `SKILL.md` does not activate the skill. That is host-side and imposes no server obligation.
- For each `SKILL.md` resource:
  - `mimeType` **SHOULD** be `text/markdown`.
  - The Resource `name` **SHOULD** be the frontmatter `name`, and `description` **SHOULD** be the frontmatter `description`.
  - Extra frontmatter MAY go in the resource `_meta`. Keys **SHOULD** use the prefix `io.modelcontextprotocol.skills/`, which is reserved for this extension.
  - "Other files in the skill use the `mimeType` appropriate to their content."
- Example read result (verbatim). The 151 bytes and the digest are verified below.

  ```json
  {"jsonrpc":"2.0","id":3,"result":{"resultType":"complete","contents":[{"uri":"skill://pdf-processing/SKILL.md","mimeType":"text/markdown",
   "text":"---\nname: pdf-processing\ndescription: Extract, fill, and assemble PDF documents\n---\n\n# PDF processing\n\nChoose the matching template from `templates/`.\n"}],
   "ttlMs":300000,"cacheScope":"public"}}
  ```

- Base rules:
  - An unknown URI → `-32602` (`"Resource not found"`, `data.uri`).
  - "Servers **MUST NOT** return an empty `contents` array for a non-existent resource".
  - `resources/read` results need `ttlMs` and `cacheScope`.
  - "Servers **MUST** sanitize file paths to prevent directory traversal" (base wording is for `file://`). Apply it to `skill://` path mapping too: reject `..`, `.`, `%2F`, double slashes and symlink escapes. [INFERENCE]
- **[AMBIGUOUS] Text vs blob, and how digests are checked.**
  - The digest covers "raw bytes". The spec does not say how a host turns `text` into bytes, but its worked example treats `text` as UTF-8.
  - I recomputed the spec's example digests over UTF-8 and all four match exactly:

    | File | Bytes | Digest |
    |---|---|---|
    | SKILL.md | 151 | `sha256:99b7…b19e` |
    | invoice.md | 29 | `sha256:61f4…d9ee` |
    | purchase-order.md | 35 | `sha256:f2ff…c158` |
    | credit-note.md | 39 | `sha256:766b…26f1` |

  - Server rule [INFERENCE]: serve as `text` only if the file is valid UTF-8 **and** `utf8(text)` reproduces the raw bytes exactly. Do not normalize CRLF, strip a BOM or add a trailing newline.
  - Serve everything else (binaries, non-UTF-8 text, scripts with odd encodings) as `blob` = base64 of the raw bytes.
  - Compute `digest` and `size` from the exact bytes you serve. A mismatch makes the host refuse the content.
- **[AMBIGUOUS] `resources/list`.** Whether skill files must appear in `resources/list` is unspecified. Directory resources "need not appear in `resources/list`". The design intent is that skills may be unenumerated.
  - [CONFORMANCE] The manifest scenario reads the `SKILL.md` Resource `name`/`description` from `resources/list`. If `SKILL.md` is absent there, those SHOULD checks are SKIPPED, not failed.
  - Listing `SKILL.md` (with `name`, `description` and `mimeType: text/markdown`) lets them pass.
- **Resource templates.** The stable spec says nothing. v1 dropped the earlier index "template entries" (archive notes). You MAY register `skill://…` templates under base rules, but they carry no skill semantics. Skill-ness only comes from `skills/list` or `skills/get`.
- **[AMBIGUOUS] `resources/read` on a directory URI.** It is undefined for skills. The base says a server "could return the contents of several files when a directory resource is read". The WG rejected giving `resources/read` directory semantics. Safest choice: return `-32602` or a single entry; never rely on it.
- **[INFERENCE] One content item.** Return exactly one `contents[]` item whose `uri` equals the requested URI. [CONFORMANCE] The suite reads the first text content.

## A8. `resources/directory/read` (only if `directoryRead: true`)

```typescript
interface ReadResourceDirectoryRequest extends PaginatedRequest {
  method: "resources/directory/read";
  params: { /** URI of the directory resource to read. */ uri: string; cursor?: Cursor; };
}
interface ReadResourceDirectoryResult extends PaginatedResult { /** Resource metadata of the directory's direct children. */ resources: Resource[]; }
```

```json
{"jsonrpc":"2.0","id":7,"method":"resources/directory/read","params":{"uri":"skill://pdf-processing/templates"}}
```

```json
{"jsonrpc":"2.0","id":7,"result":{"resultType":"complete","resources":[
  {"uri":"skill://pdf-processing/templates/invoice.md","name":"invoice.md","mimeType":"text/markdown"},
  {"uri":"skill://pdf-processing/templates/purchase-order.md","name":"purchase-order.md","mimeType":"text/markdown"},
  {"uri":"skill://pdf-processing/templates/regional","name":"regional","mimeType":"inode/directory"}]}}
```

- **Directory resources.** "A _directory resource_ is a resource whose `mimeType` is `inode/directory`."
  - Every directory level is one, from the skill root (`skill://pdf-processing`) to each subdirectory.
  - "Directory URIs are written without a trailing slash."
- `resultType` **MUST** be `"complete"`.
- Error cases: "If the URI does not exist, or exists but is not a directory resource, the server **MUST** return error `-32602`." The spec example:

  ```json
  {"jsonrpc":"2.0","id":7,"error":{"code":-32602,"message":"skill://pdf-processing/SKILL.md is not a directory resource"}}
  ```

- "The result contains every direct child of the directory. Files carry their ordinary `Resource` metadata and subdirectories are listed as directory resources (`mimeType: "inode/directory"`). The listing is not recursive."
  - Children have `uri`, `name` (basename in the example), `mimeType`, and optionally `title`, `description`, `size`, etc. Contents are never inlined.
- "An empty directory yields an empty `resources` array." Pagination mirrors `resources/list` (`cursor`/`nextCursor`).
- [AMBIGUOUS] `ReadResourceDirectoryResult` does **not** extend `CacheableResult` in the spec, so `ttlMs`/`cacheScope` are not required. Adding them is harmless.
- "A server that declares `directoryRead` **MUST** support the method for every directory within the skill namespaces it serves as individual files." It **MAY** support it for any directory under any scheme.
- Without `directoryRead`, the server "is not required to recognize" the method; respond with unknown method (`-32601`, HTTP 404).
- A directory read may legitimately disagree with a held manifest (live view vs snapshot). The server has no consistency obligation; the host handles it.
- [CONFORMANCE] The scenario picks the first `skills/list` entry's root.
  - The subdirectory check FAILS as "untestable" unless that root has at least one direct child with `inode/directory`. **Give your first listed skill a subdirectory**, such as `references/`.
  - It also calls the method on the `SKILL.md` URI and expects `-32602`.
  - It round-trips `nextCursor` if present.

## A9. Integrity: what the server must guarantee

- **Digest format.** `sha256:` followed by 64 **lowercase** hex characters, the SHA-256 of the file's raw bytes. [CONFORMANCE] Regex `^sha256:[0-9a-f]{64}$`.
- **Size.** A non-negative integer: the byte length of the same raw bytes. [CONFORMANCE] It must be an integer ≥ 0. On the host side, "A read whose byte length differs from the entry's `size` is a verification failure."
- **Completeness.** Every file appears exactly once, including the `SKILL.md` self-entry, plus all nested-skill files. Every `uri` must be within the skill root. [CONFORMANCE] The suite checks for the self-entry, no duplicates, and that every URI equals the `SKILL.md` URI or starts with `<root>/`.
- **Frontmatter identity.** Hosts "**MUST** parse its YAML frontmatter and compare it field-by-field against the entry's `frontmatter`. Any discrepancy **MUST** be treated as a verification failure… and the skill **MUST NOT** be loaded."
  - [CONFORMANCE] The suite parses with the `yaml` npm package (YAML 1.2 core schema; a leading BOM is tolerated). It deep-compares per key; key order is irrelevant and absent equals null.
  - **[AMBIGUOUS] YAML→JSON typing is unspecified.** `version: 1.0` is a number in YAML 1.2 parsers but `"1.0"` in `skills-ref` (strictyaml). Generate `frontmatter` by parsing with a YAML 1.2 core-schema parser.
  - Prefer quoted string scalars in authored frontmatter. Avoid YAML timestamps, anchors and merge keys, which differ between parsers.
- **Consistency across calls.** `skills/list`, `skills/get` and `resources/read` must describe the same bytes. Any server-side change to a file must change its digest in the next entry. Hosts detect staleness by mismatch, then re-call `skills/get`, and any persisted approval is revoked.
- **Do not churn.** [INFERENCE] Do not regenerate content per request for array-form skills. Use `"dynamic"` instead, or every read will fail verification.
- "Digests are not a trust anchor." That is host-side and imposes no server obligation.
- Verification failures are host-side conditions, not protocol errors. "The only resulting wire traffic is the `skills/get` or `skills/list` call a host makes to refresh the entry."

## A10. Limits, and server-relevant security points

| Limit | Value | Counted over |
|---|---|---|
| Resources per skill | 512 entries | The entries of the skill's `resources`, `SKILL.md` included |
| Total file size per skill | 16 MiB (16,777,216 bytes) | The sum of `size` over the skill's `resources` |

- "Servers **SHOULD NOT** serve a skill that exceeds either limit." Hosts MUST support up to the limits. [CONFORMANCE] Exceeding a limit is a WARNING.
- Limits are per skill. The listing may be arbitrarily large.
- Nested-skill files count toward the enclosing skill's limits too. [INFERENCE, from completeness]

The Security Considerations section is almost entirely host obligations. These are the parts that bind or affect servers:

- The `_meta` prefix `io.modelcontextprotocol.skills/` is reserved for this extension. Intermediaries use their own prefix.
- `allowed-tools` from MCP skills is ignored by hosts unless the user explicitly approves. Never rely on it.
- Hosts **MUST NOT** honour frontmatter that widens permissions.
- Code execution requires per-skill approval.
- Hosts bind reads to the origin server, so one server's skill cannot cause reads on another server. Do not author skills that reference another server's resources.
- `"dynamic"` skills cannot be content-bound, and hosts MAY decline them.
- **Lazy retrieval.** Hosts MUST NOT prefetch files. Expect one `resources/read` per file actually used and no bulk pulls, so size per-file read capacity accordingly.
- The `skills/` method prefix, the method name `resources/directory/read`, and the label `io.modelcontextprotocol/skills` are reserved.

## A11. Instructions pointer, change notifications, subscriptions

- **Instructions pointer.** "A server **MAY** direct the agent to specific skill URIs from its `instructions`. This requires no discovery machinery on the host." `instructions` is the field in the `server/discover` result. The host confirms with `skills/get`, which therefore MUST answer for every URI you mention.
- **There is no skills list-changed notification.**
  - The extension defines no `notifications/skills/*`. `notifications/skills/list_changed` was explicitly declined (archive glossary, rationale).
  - `subscriptions/listen` has no skills filter key.
- **Freshness is via `ttlMs`** on `skills/list` and `skills/get`, plus host-side digest mismatch → `skills/get`. "A host need not poll for changes."
- **Optional base mechanisms.** [INFERENCE] Rationale: resource subscriptions are "inherited for free".
  - If skill files appear in `resources/list` and you declare `resources.listChanged: true`, send `notifications/resources/list_changed` on `subscriptions/listen` streams that requested `resourcesListChanged`.
  - With `resources.subscribe: true`, send `notifications/resources/updated` for skill URIs listed in `resourceSubscriptions`.
  - Neither is required by the extension, and hosts are not told to use them for skills.

## A12. HTTP headers for skills methods

- `skills/list`, `skills/get` and `resources/directory/read` need **`Mcp-Method` only**.
  - The base defines `Mcp-Name` only for `tools/call`, `resources/read` and `prompts/get`, and the skills spec defines no `Mcp-Name` source.
  - [CONFORMANCE] The harness sends no `Mcp-Name` for these, so do **not** require it.
- `resources/read` of a skill file: `Mcp-Name` = the file URI (base64-sentinel if not header-safe). Validate it against `params.uri`; on mismatch return HTTP 400 + `-32020`.

## A13. Error handling (complete list)

| Case | Code |
|---|---|
| `skills/get` with a URI that is not a served skill | `-32602` Invalid params |
| `resources/directory/read` with a URI that does not exist or is not a directory | `-32602` |
| `resources/read` of a skill file not served | `-32602` (base Resources) |
| Internal errors | `-32603` |
| `resources/directory/read` without `directoryRead: true` | base unknown method (`-32601`, HTTP 404) |

"Servers **SHOULD** provide informative error messages."

## A14. Conformance check IDs a server can pass or fail

Every check below is wire-tested.

- **Capability:** `capability-declaration-inline`, `capability-empty-object`, `capability-requires-resources`, `capability-commits-to-methods`.
- **`skills/list`:** `skills-list-implemented`, `skills-list-pagination`, `skills-list-entry-atomic`, `skills-list-cache-attributes` (ttlMs+cacheScope on protocol ≥ 2026-07-28).
- **Entries:**
  - `entry-uri-required`, `entry-frontmatter-required`, `entry-uri-matches-frontmatter-name`.
  - `skill-uri-scheme` (non-`skill://` gives a WARNING).
  - `entry-resources-required`, `resources-complete`, `resources-uri-within-skill`, `resources-digest-format`, `resources-size-required`.
  - `limit-*` (WARNING), `metadata-reserved-prefix` (WARNING), `name-naming-rules` (ASCII regex), `authority-reg-name` (WARNING), `names-should-be-unique` (WARNING).
- **Read-back:** `skillmd-required`, `skillmd-frontmatter`, `entry-frontmatter-identical`.
- **`skills/get`:** `skills-get-implemented`, `-entry-shape`, `-cache-attributes`, `-no-cursor`, `-unknown-uri-invalid-params`.
- **Manifest:** `skillmd-mimetype`, `skillmd-metadata-name`/`-description` (need the `resources/list` listing), `final-segment-equals-name`, `meta-prefix` (bare `_meta` keys give a WARNING).
- **Directory:** `capability-directory-read-flag`, `directory-read-method-registered`, `-result-resources-shape`, `-subdir-mimetype`, `-invalid-params`, `-pagination`.
- All IDs are prefixed `sep-2640-`.

---

# PART B: EVENTS (Triggers & Events WG), DRAFT design sketch

Status: "**Status:** Draft proposal". The repo README says: "Contents are exploratory and do not represent official MCP specifications." Everything here is subject to change. The sketch predates or ignores several 2026-07-28 base rules; conflicts are listed in B15.

## B1. Capability declaration

The sketch, verbatim:

```jsonc
{
  "capabilities": {
    "events": {
      "listChanged": true
    }
  }
}
```

- **This is a top-level `capabilities.events` key, NOT `capabilities.extensions["…"]`.** The sketch defines **no extension identifier**, yet calls itself "this extension" ("The extension is already opt-in").
- **[AMBIGUOUS]** Under 2026-07-28 / SEP-2133, an opt-in extension would normally be declared as `capabilities.extensions["<vendor>/<name>"]`, and the official prefix is `io.modelcontextprotocol`. Implement the literal sketch shape, and be ready for it to move.
- No client-side capability is defined.

## B2. `events/list`

Request: `params` optional, `{ "cursor": "..." }` for pagination. Response (verbatim example, abridged by the source itself with `"..."`):

```jsonc
{
  "events": [
    {
      "name": "email.received",
      "description": "Fires when a new email arrives in the inbox",
      "delivery": ["poll"],
      "inputSchema": { "type": "object", "properties": {
          "from": { "type": "string", "description": "Glob pattern for sender address" },
          "subject_contains": { "type": "string" },
          "redact_pii": { "type": "boolean", "default": false, "description": "Strip PII from event payloads" },
          "include_body_preview": { "type": "boolean", "default": true, "description": "Include a snippet of the email body" } } },
      "payloadSchema": { "type": "object", "properties": {
          "messageId": { "type": "string" }, "from": { "type": "string" }, "subject": { "type": "string" },
          "receivedAt": { "type": "string", "format": "date-time" } } }
    },
    {
      "name": "incident.created",
      "description": "Fires when a new PagerDuty incident is created",
      "delivery": ["webhook", "push", "poll"],
      "inputSchema": { "type": "object", "properties": {
          "severity": { "type": "string", "enum": ["P1", "P2", "P3", "P4"] },
          "service": { "type": "string" },
          "deduplicate_window_seconds": { "type": "integer", "default": 0, "description": "Suppress duplicate alerts within this window" } } },
      "payloadSchema": { "..." : "..." },
      "_meta": { "..." : "..." }   // optional; same semantics as on Tool/Resource/Prompt
    }
  ],
  "nextCursor": "..."   // present when more pages are available; same semantics as tools/list etc.
}
```

Descriptor fields are exactly `name`, `description`, `delivery`, `inputSchema`, `payloadSchema` and `_meta` (optional). **No `title`, no `icons`, no annotations.**

- **`delivery`.** "any non-empty subset of `"poll"`, `"push"`, `"webhook"`. No mode is mandatory."
- **`inputSchema`.** A JSON Schema for subscription `arguments`: filters, transforms or config. It mirrors tools.
- **`payloadSchema`.** "describes the shape of `data` in delivered events".
- **Schema evolution.**
  - Servers **SHOULD** evolve both schemas additively for the lifetime of a `name`. "new optional fields **MAY** be added; existing fields **SHOULD NOT** be removed, renamed, or retyped; enums **SHOULD NOT** be narrowed; and `inputSchema` **SHOULD NOT** be tightened such that previously accepted `arguments` become invalid (a webhook refresh re-sends them and would fail with `-32602`)."
  - A breaking change **SHOULD** use a new event name, served alongside the old one for a migration period.
- **[AMBIGUOUS] Missing base fields.** The examples omit `resultType`, which 2026-07-28 makes mandatory on all results, and `ttlMs`/`cacheScope`, whose required-ness for `events/list` is unspecified. Add `"resultType":"complete"`; caching attributes are your choice.
- Event-name syntax is not constrained; the examples use dotted lowercase. `name` is the key used everywhere.

## B3. List changes and type removal

- "If the set of available event types, or the descriptor of any of them (`description`, `delivery`, `inputSchema`, `payloadSchema`), changes at runtime…, the server sends a `notifications/events/list_changed` notification. The client **SHOULD** re-call `events/list`."
- **[AMBIGUOUS] How it is delivered.** Under 2026-07-28, list-changed notifications travel only on `subscriptions/listen` streams. The server "**MUST NOT** send notification types the client has not explicitly requested", and the filter has no events key (`eventsListChanged` is not defined anywhere). The sketch says nothing about this.
  - The sketch elsewhere advises clients to call `events/list` "occasionally" to bound staleness.
- **Removal or in-place breaking change.**
  - The server **SHOULD** end existing subscriptions with each mode's termination signal: `notifications/events/terminated` on push, a `terminated` envelope on webhook.
  - When the type was removed, `error` = `-32011 NotFound` with `data: {"kind": "event"}`.
  - When it changed in place, `error` = `-32014 Unsupported` with `data: {"feature": "payloadSchema" | "inputSchema", "reason": "schema_changed"}`. Do **not** use `-32012`.
  - "Purely additive changes **MUST NOT** terminate subscriptions."
  - Poll against a removed name → `-32011`. For an in-place change, a server "**MAY** mint cursors that encode a schema epoch and answer a stale one with the same `Unsupported` error".

## B4. `EventOccurrence` (event envelope)

The same shape is used in poll `events[]`, as the `notifications/events/event` params, and as the webhook event body.

| Field | Type | Required | Description |
|---|---|---|---|
| `eventId` | string | yes | Stable identifier for deduplication |
| `name` | string | yes | Event type name |
| `timestamp` | string (ISO 8601) | yes | When the event occurred |
| `data` | object | yes | Payload conforming to the event type's `payloadSchema` |
| `cursor` | string \| null | no | Subscription position after this event (push/webhook only; poll carries cursor at the response level). `null` when the type does not support replay. |
| `_meta` | object | no | Reserved for protocol/extension metadata. Not governed by `payloadSchema`. |

- **`eventId`.**
  - "It is **server-assigned**: when the upstream source provides a stable event identifier (Stripe `evt_*`, GitHub delivery GUID, Kafka offset, Gmail message ID), the server **SHOULD** use that value". The same upstream event reached via several paths must carry the same `eventId`.
  - The SDK auto-generates one only when the author supplies none.
  - There is no stated uniqueness scope [AMBIGUOUS]. Dedup is per client and subscription; make it unique per event type at least.
- **`timestamp`.** ISO 8601 (examples like `"2026-02-19T15:30:00Z"`) giving the occurrence time, not the delivery time.
- **Push notifications** add `_meta["io.modelcontextprotocol/subscriptionId"]`.
- **Webhook bodies** with a top-level `type` key are control envelopes, not occurrences (B9).

## B5. `events/poll`

Each request carries one subscription. Request (verbatim):

```jsonc
{
  "name": "email.received",
  "arguments": { "from": "*@anthropic.com", "redact_pii": true },
  "cursor": null,                 // null = start from now
  "maxAgeMs": 300000,             // optional; do not replay events older than this many milliseconds
  "maxEvents": 50                 // optional; cap events returned
}
```

Result (verbatim; add `resultType` per base):

```jsonc
{
  "events": [ { "eventId": "evt_001", "name": "email.received", "timestamp": "2026-02-19T15:30:00Z",
      "data": { "messageId": "msg_xyz", "from": "dsp@anthropic.com", "subject": "MCP spec review", "receivedAt": "2026-02-19T15:29:58Z" } } ],
  "cursor": "historyId_99842",
  "truncated": false,
  "hasMore": false,
  "nextPollMs": 30000
}
```

Rules:

- **Null cursor.** "In the request, `null` means "start from now" — the server returns no events and provides a fresh cursor". The response `cursor` MAY be `null` for no-replay types; the client then always polls with `null`.
- **`maxEvents`.** If more events are available, return a partial batch with an intermediate cursor and `hasMore: true`, and the client polls again immediately. If omitted, the server uses its own default limit.
- **`nextPollMs`** lets the server adjust the poll rate dynamically. It is ignored when `hasMore` is true. Clients apply a floor (default 1000 ms).
- "Empty `events` array means nothing happened — this is the common case and should be cheap."
- **Statelessness.** "The server holds no protocol-required per-client subscription state. Each poll request is self-contained." Ephemeral SDK state, such as a poll lease or ring buffer, is optional and reconstructable.
- **Errors.** `NotFound`, `Forbidden`, `InvalidParams` and `Unsupported` come back as a standard JSON-RPC error. There is no partial success.
- **`truncated`.** `truncated: true` appears in the result body, "Never a JSON-RPC error".
- **Ordering.** Within a subscription, events come "in the order the server produces them".
- **[AMBIGUOUS] Required fields.** No table marks optionality. Always emit all five fields, plus `resultType`.

## B6. `events/stream` (push)

There is one long-lived request per subscription. On HTTP it is a POST answered by an SSE stream that "carries only this subscription's event notifications (`notifications/events/*`)". On stdio, notifications go to stdout.

```jsonc
{ "jsonrpc": "2.0", "method": "events/stream", "id": 1,
  "params": { "name": "email.received", "arguments": { "from": "*@anthropic.com", "redact_pii": true }, "cursor": null, "maxAgeMs": 300000 } }
```

- **Invalid subscription.** `NotFound`, `Forbidden`, `InvalidParams` or `Unsupported` → "the server responds immediately with a JSON-RPC error and no stream is opened". (`ResourceExhausted` presumably also applies; see B12.)
- **Subscription id.** Every `notifications/events/*` carries `params._meta["io.modelcontextprotocol/subscriptionId"]` = **the JSON-RPC `id` of the `events/stream` request** (string|integer).

Notifications (verbatim):

```jsonc
// Confirmation (first message; repeated mid-stream with truncated:true and a fresh cursor on a gap)
{"jsonrpc":"2.0","method":"notifications/events/active","params":{"cursor":"historyId_99840","truncated":false,"_meta":{"io.modelcontextprotocol/subscriptionId":1}}}
// Event
{"jsonrpc":"2.0","method":"notifications/events/event","params":{"eventId":"evt_001","name":"email.received","timestamp":"2026-02-19T15:30:00Z","data":{"messageId":"msg_xyz","from":"dsp@anthropic.com","subject":"MCP spec review"},"cursor":"historyId_99842","_meta":{"io.modelcontextprotocol/subscriptionId":1}}}
// Transient per-occurrence error (stream stays open)
{"jsonrpc":"2.0","method":"notifications/events/error","params":{"error":{"code":-32603,"message":"UpstreamError","data":{"reason":"Gmail API 503"}},"_meta":{"io.modelcontextprotocol/subscriptionId":1}}}
// Heartbeat
{"jsonrpc":"2.0","method":"notifications/events/heartbeat","params":{"cursor":"historyId_99850","_meta":{"io.modelcontextprotocol/subscriptionId":1}}}
// Termination (subscription ended)
{"jsonrpc":"2.0","method":"notifications/events/terminated","params":{"error":{"code":-32012,"message":"Forbidden","data":{"reason":"Access revoked"}},"_meta":{"io.modelcontextprotocol/subscriptionId":1}}}
// Final frame (StreamEventsResult)
{"jsonrpc":"2.0","id":1,"result":{"_meta":{}}}
```

- **`terminated` shape.** `{error: {code: integer, message: string, data?: object}, _meta: {"io.modelcontextprotocol/subscriptionId": string|integer}}`. It is "identical to `notifications/events/error` but indicates the subscription has ended".
- **`error` vs `terminated`.** `error` = a recoverable failure, after which the server retries and resumes. "Only `notifications/events/terminated` ends the subscription."
- **Gaps are not errors.** Send a fresh `notifications/events/active {cursor:<fresh>, truncated:true}` and continue.
- **Heartbeat.**
  - "The server **MUST** send periodic keepalive messages". It "**SHOULD** send a heartbeat at least every 30 seconds".
  - On HTTP it is an SSE `data:` frame. "The SSE comment form (`: keepalive`) is not used since it cannot carry cursor state."
  - `cursor` is "the position the server has checked up to", or `null` for no-replay types.
  - Clients treat more than 2× the interval without an event or heartbeat as dead and reconnect with the cursor.
- **Cursor advancement.** Each event's `cursor` = the position *after* that event.
- **`StreamEventsResult`.** `{"_meta": {}}`, carrying no information. Send it "whenever the server can write a final frame: on Streamable HTTP only when the server initiates the close; on stdio the server **MAY** send it, and clients **MUST NOT** depend on receiving it". [INFERENCE] Under 2026-07-28 include `"resultType":"complete"`.
- **Cancellation.**
  - HTTP: the client aborts the stream, and "no result is sent".
  - stdio: `notifications/cancelled` with `requestId` = the stream id. The server "**MAY** then send the `StreamEventsResult`; base MCP says servers **SHOULD NOT** respond to cancelled requests, so omitting it is the expected behaviour".
  - In both cases "the server **MUST** stop delivering events and release any associated resources".
- **Concurrency.** "Server SDKs **MUST** exempt `events/stream` from any general request-concurrency cap." A client may hold many streams, one per subscription.
- **Reconnect.** The client issues a new `events/stream` with its last cursor. There is no SSE resumability; the base removed `Last-Event-ID`.
- **Channel isolation.** Non-event notifications (list_changed, progress, logging) never go on this stream.
- **Push-reconnect backlog.** There is no bound on it (no `maxEvents` equivalent). That is a known v1 gap, left to TCP backpressure and server pacing.

## B7. `events/subscribe` / `events/unsubscribe` (webhook only)

Request (verbatim):

```jsonc
{ "jsonrpc": "2.0", "method": "events/subscribe", "id": 2,
  "params": {
    "name": "incident.created",
    "arguments": { "severity": "P1" },
    "delivery": { "mode": "webhook", "url": "https://proxy.example.com/hooks/client123", "secret": "whsec_<base64-of-24-to-64-random-bytes>" },
    "cursor": null,
    "maxAgeMs": 300000,
    "ttlMs": 3600000                  // suggested TTL; omit = server default, null = request no expiry
  } }
```

```jsonc
// Response
{
  "id": "sub_a3f1c8e2b0d49f7e",       // server-derived; stable for this (principal, url, name, arguments)
  "refreshBefore": "2026-02-19T16:30:00Z",  // authoritative grant; SHOULD be ≤ the suggested ttlMs; null = no expiry
  "cursor": "cursor_start_001",       // safe-to-persist watermark
  "truncated": false                  // true if delivery started later than the supplied cursor
  // "deliveryStatus": {...}          // OPTIONAL, on refreshes
}
```

- `events/subscribe` and `events/unsubscribe` are "ONLY used for webhook delivery". `delivery.mode` appears in the subscribe example only. [AMBIGUOUS] Whether `mode` is required, and whether values other than `"webhook"` are valid, is unspecified. Accept `"webhook"` or absence.
- **Authentication.**
  - Both methods "**MUST** be called with an authenticated principal; servers **MUST** reject calls without an authorized principal with `-32012 Forbidden`". Unauthenticated servers may offer poll and push but not webhook.
  - Subscribe-time authorization: the server **MUST** verify that the principal may subscribe to that event and those arguments.
- **`delivery.secret`.** It is **REQUIRED** and client-supplied; "the server never generates one".
  - It "**MUST** be a Standard Webhooks symmetric secret: the literal prefix `whsec_` followed by base64 of 24–64 random bytes. Servers **MUST** reject values that do not satisfy this with `InvalidParams`."
  - [AMBIGUOUS] Padding and alphabet are not stated. Standard Webhooks libraries accept unpadded standard base64, so accept padded or unpadded standard base64 and check the decoded length is 24..64.
- **`delivery.url`.** It must be `https`. Non-`https` or malformed → `-32602`.
- **Subscription key.** `(principal, delivery.url, name, arguments)`.
  - `principal` is the server's canonical subject (OAuth `sub`, API key ID…). "`arguments` is compared by canonical-JSON equality."
  - [AMBIGUOUS] The canonicalization is not named. RFC 8785 JCS is the natural choice [INFERENCE].
  - All four components are immutable. A different value means a different subscription; change by unsubscribing and resubscribing.
- **Derived `id`.**
  - It is "deterministic over the key (e.g., a truncated SHA-256 of the canonical key serialization)". It is stable across refreshes **and restarts**, so do not use random salts.
  - The exact algorithm is **unspecified**; the example format is `sub_` + 16 hex.
  - "This is a routing handle, not a capability… `id` is not accepted as input to any method." It appears in `X-MCP-Subscription-Id`.
- **Idempotent upsert on an existing key.**

  | Field | Behaviour |
  |---|---|
  | `delivery.secret` | Replaced. The server SHOULD dual-sign old and new for a short grace window. There is no server-initiated rotation. |
  | `cursor` | The client's last-persisted position. A no-op if the subscription is live and the cursor is at or behind the in-flight position. If the subscription lapsed or the server restarted, delivery (re)starts here. The server does not store it beyond initiating delivery. |
  | TTL | Re-granted from the new `ttlMs` suggestion (a refresh may convert no-expiry back to finite). |
  | `active` | Set to `true`; resume retrying pending events if suspended. |

  Expired or lost subscription → create a fresh one from the provided cursor.
- **TTL negotiation.**
  - `ttlMs` (optional, nullable integer): omitted = server default; `null` = request no expiry.
  - `refreshBefore` is always present in the response: an ISO 8601 timestamp, or `null`.
  - It "**SHOULD** be less than or equal to the suggestion", except that the server "**MAY** clamp an impractically short suggestion up to its minimum TTL". There is "no rejection path for TTL values".
  - "A server **MUST NOT** return `null` unless the client suggested `ttlMs: null`."
  - Recommended finite grants range from a few minutes to about a day.
  - Unless the grant is no-expiry, the client **MUST** refresh before `refreshBefore`.
- **No-expiry grant obligations.**
  - "the server **MUST** persist no-expiry subscriptions across restarts", along with their verification status.
  - It **MUST NOT** count on receiving `events/unsubscribe`.
  - It **MAY** drop the subscription after sustained delivery failure, and **SHOULD** attempt a `terminated` envelope when it does.
  - Long finite grants also imply that the server "**MUST** retain subscriptions for the lifetime it granted, including across restarts".
- **Response `cursor`.** The safe watermark: never ahead of unacked events, or `null` for no-replay types.
- **Response `truncated`.** True if delivery started later than the supplied cursor.
- **Limits.** `ResourceExhausted` (`-32013`) **SHOULD** be checked before any upstream provisioning (`on_subscribe`).
- **`deliveryStatus`** (OPTIONAL; "servers **MAY** omit it entirely"):
  - Fields: `active` (bool), `lastDeliveryAt` (ISO), `lastError` (null or a category), `failedSince` (ISO, in the failure example), `throttled` (optional bool), `retryAfterMs` (optional int).
  - "`lastError` **MUST** be a server-generated category string — one of `connection_refused`, `timeout`, `tls_error`, `http_4xx`, `http_5xx`, `challenge_failed` — and **MUST NOT** include raw response bodies, headers, or status lines from the endpoint."
  - `throttled` means deliveries are delayed, not failing. Skipped events must be signalled as a gap instead.

  ```jsonc
  {"id":"sub_a3f1c8e2b0d49f7e","refreshBefore":"2026-02-19T17:00:00Z","cursor":"cursor_xyz","truncated":false,
   "deliveryStatus":{"active":false,"lastDeliveryAt":"2026-02-19T15:45:00Z","lastError":"http_4xx","failedSince":"2026-02-19T15:50:00Z"}}
  ```

- **`events/unsubscribe`.** Params are `{name, arguments, delivery: {url}}`; the principal comes from auth; the `id` is not accepted. It is eager cleanup and not required for correctness.
  - Response: "(ack)". The shape is unspecified [AMBIGUOUS]; return an empty result with `resultType`.
  - No matching subscription → `-32011 NotFound` (optionally `data.kind: "subscription"`).

  ```jsonc
  { "jsonrpc": "2.0", "method": "events/unsubscribe", "id": 3,
    "params": { "name": "incident.created", "arguments": { "severity": "P1" }, "delivery": { "url": "https://proxy.example.com/hooks/client123" } } }
  ```

- **No listing method.** "There is no server-side subscription listing method." The client owns the canonical list.

## B8. Webhook delivery and signing (Standard Webhooks profile)

Delivery example (verbatim):

```
POST https://proxy.example.com/hooks/client123
Content-Type: application/json
webhook-id: evt_789
webhook-timestamp: 1739980800
webhook-signature: v1,<base64 HMAC-SHA256(secret, "evt_789.1739980800." + body)>
X-MCP-Subscription-Id: sub_a3f1c8e2b0d49f7e

{"eventId":"evt_789","name":"incident.created","timestamp":"2026-02-19T16:00:00Z",
 "data":{"incidentId":"INC-1234","title":"Database connection pool exhausted","severity":"P1"},"cursor":"cursor_xyz"}
```

**Delivery profile (invariants):**

- Method: "deliveries are HTTP `POST` only". `Content-Type: application/json`.
- Required on every delivery: `webhook-id`, `webhook-timestamp`, `webhook-signature` and `X-MCP-Subscription-Id`. "This is the only MCP-specific header."
- The body **SHOULD** be ≤ 256 KiB. `413` **MUST** be treated as non-retryable for that event.
- `https` is mandatory. The User-Agent is unspecified (pending SEP-1336). Egress IP ranges should be documented out-of-band.
- `webhook-id` = `eventId` for events, or `msg_<type>_<random>` for control envelopes (stable across retries, so receivers can dedup).
- `webhook-timestamp` is Unix **seconds** of the attempt.

**Signature algorithm (exact):**

- Signed content: `webhook-id + "." + webhook-timestamp + "." + body`.
  - "`body` is the raw HTTP request body bytes exactly as received".
  - The prefix is UTF-8.
  - "`secret` is the base64-decoded bytes of the value after the `whsec_` prefix".
- `webhook-signature: v1,<base64(HMAC-SHA256(key, signed_content))>` (standard base64).
- "Multiple space-delimited signatures (`v1,<sigA> v1,<sigB>`)" are allowed during rotation.
- "Each retry attempt **MUST** regenerate the timestamp and signature."
- Server practice: serialize the body once and sign exactly those bytes.
- **Verified test vector** (Standard Webhooks `libraries/python/tests/test_webhooks.py::test_sign_function`, which I also recomputed):
  - secret `whsec_MfKQ9r8GKYqrTwjUPD8ILPZIo2LaLaSw` (24 bytes)
  - id `msg_p5jXN8AQM9LWM0D4loKWxJek`, ts `1614265330`, body `{"test": 2432232314}`
  - → `v1,g0hM9SsE+OTPJTGt/tmIKtSyZlE3uFJELVlNIOLJ1OE=`
- **Optional `v1a,`** (ed25519) is appended alongside `v1,` for server identity. Key discovery is via the SEP-2127 server card (`webhooks.signingKeys` JWK set) **or** `/.well-known/mcp-webhook-jwks.json`. "Exactly one of these will be normative in the final SEP." This is undecided. Keys carry a `kid`.

**Delivery semantics:**

- **Retries.**
  - "The server retries each event independently with exponential backoff on non-`2xx` responses."
  - Servers **SHOULD** cap attempts and the elapsed window, "for example, 3–5 attempts spread over no more than 10–15 minutes". Time spent suspended pauses the window.
  - Exhausted events are abandoned for watermark purposes.
  - Note: Standard Webhooks itself suggests multi-day schedules; the sketch deliberately shortens this.
- **`410 Gone`.** "the server **MUST** treat `410` as a non-retryable failure for that delivery (like `413`)… without affecting the subscription itself." **This deviates from Standard Webhooks**, which says 410 means disable the endpoint.
- **Acknowledgement.** `2xx` = accepted. The timeout is a server detail, "on the order of 5 seconds is common" (Standard Webhooks suggests 15–30 s).
- **Unknown id at the receiver.** The receiver should return `503`/`425`, which the server retries normally.
- **Ordering.** Concurrent deliveries and retries may reorder events. Ordering is best-effort; receivers use `eventId` to dedup and `timestamp` to order.
- **Watermark cursor (server bookkeeping).** The body `cursor` is a safe watermark: "it does not include `cursor_N` in event N's payload until events at positions `< N` have been acked or given up on". Keep per-subscription in-memory state of upstream position plus ack status of in-flight events. Use `cursor: null` for no-replay types.
- **Suspension.** After repeated failures the server MAY set `deliveryStatus.active: false`. The guidance is failure rate > 95% over a rolling 60 min with at least 100 attempts. A successful refresh reactivates delivery.
- **Delivery guarantee.** At-least-once between server and endpoint holds when the upstream is durable. Emit-only types are at-most-once across restarts.

## B9. Control envelopes and endpoint verification

"A body with a top-level `type` field is a control envelope; a body without one is an `EventOccurrence`." Control envelopes are signed and carry headers like events.

| `type` | Body | Sent when |
|---|---|---|
| `gap` | `{"type":"gap","cursor":"<fresh>"}` | A gap is detected between refreshes. The client persists `cursor` and treats it as `truncated: true`. |
| `terminated` | `{"type":"terminated","error":{"code":...,"message":...,"data":...}}` | The subscription has ended (e.g., authorization revoked) and no longer exists server-side. |
| `verification` | `{"type":"verification","challenge":"<nonce>"}` | Sent before activating delivery to an unverified `(principal, url)`. |

**Endpoint verification is mandatory.** "A server **MUST NOT** begin delivering to a callback URL until the endpoint's intent to receive deliveries is confirmed, by **one of**":

- **(a) Handshake.** POST the `verification` envelope carrying a "single-use, short-lived `challenge` nonce", signed and headed like any delivery. The endpoint echoes `{"challenge":"<nonce>"}` in a **2xx body**. Compare in constant time.
- **(b)** A server-configured allowlist.
- **(c)** Prior out-of-band verification (e.g., a dashboard).
- **(d) Receiver-published well-known document.** `https://<callback origin>/.well-known/mcp-webhook-receiver.json`, e.g. `{"receivers": ["/hooks/"]}`, declares accepted path prefixes. Fetch it over the same SSRF-hardened path; it MAY be cached per origin for a bounded period, respecting HTTP caching.

Failure handling:

- A reachable endpoint that does not echo → `-32015 CallbackEndpointError`, `data.reason: "challenge_failed"`.
- An unreachable endpoint → the same code with `connection_refused`, `timeout` or `tls_error`. These are returned from `events/subscribe`, so verification is implicitly synchronous [INFERENCE].
- Later failures surface only as `lastError: "challenge_failed"`.

Caching and rate limits:

- Verification is cached per `(principal, url)` and covers all `arguments` and refreshes for that pair. It is in-memory soft state, re-verified on the next subscribe after a restart. It **MUST** be persisted alongside persisted no-expiry subscriptions.
- The verification POST **MUST** use the SSRF-hardened path and **SHOULD** be rate-limited per destination host.
- [AMBIGUOUS] The handshake precedes the subscribe response, so the receiver has not yet learned the `X-MCP-Subscription-Id` it is asked to route. The sketch has client SDKs pre-register the secret with the gateway, but not the id, so the receiver must match by URL or pending secret.

## B10. SSRF and other server-side security requirements

- "The server **MUST** validate callback URLs."
- **Address blocklist.** Servers **SHOULD** reject resolved IPs that are not globally routable per the IANA IPv4/IPv6 Special-Purpose registries, unless configured otherwise. Examples: `127.0.0.0/8`, `10.0.0.0/8`, `172.16.0.0/12`, `192.168.0.0/16`, `169.254.0.0/16`, `::1`, `fc00::/7`, `fe80::/10`.
- **Delivery-time validation.** "this validation **MUST** be performed at delivery time, not only at subscribe time". Resolve, check, then **connect to the validated IP** while sending the original hostname in `Host`/SNI (anti-DNS-rebinding).
- **No redirects.** "Webhook delivery requests **MUST NOT** follow HTTP redirects." Callback-URL allowlists MAY be added.
- **TLS.** "Callback URLs **MUST** use `https://`" → otherwise `-32602`.
- **Error detail.** No raw endpoint data leaks into `lastError` or errors (anti-oracle).
- **Payload minimality.** Servers SHOULD send triage fields only; content is fetched via tools.
- **Payloads are untrusted.** They carry injection risk (a client concern).
- **Delivery-time authorization.** Servers SHOULD periodically re-verify permissions and terminate on revocation. "Event receipt does NOT constitute authorization to act."
- **Subscription limits.** Enforce them with `-32013 ResourceExhausted` (`data.limit`, optional `data.max`).
- [INFERENCE, from Standard Webhooks] "the message id and the timestamp [must] not be user controlled, or at the very least not be allowed to include any `.`". `webhook-id` = `eventId` may be upstream-derived, so make sure it contains no `.`. The sketch is silent on this.

## B11. Cursor semantics, replay, truncation, ordering

- Cursors are opaque, server-defined strings. **Absent means `null`**, everywhere and in both directions: "a receiver **MUST NOT** fail because it is missing."
- **Initial cursor.** `null` = "start from now". Return a fresh cursor and replay nothing.
- **Replay is optional per type.** You MAY always return `cursor: null`. Servers **SHOULD** be consistent per type (always null, or never null). For no-replay types, `truncated` SHOULD be `false`.
- **Quiet-period advancement.** Poll returns a cursor even on empty results. Push sends a heartbeat `cursor`. Webhook returns the watermark in the subscribe/refresh response.
- **`maxAgeMs`** (all modes).
  - Start from the *later* of the cursor or `now − maxAgeMs`. If the floor passes the cursor, set `truncated: true` on the first response.
  - Seek by time if the upstream supports it. Otherwise filter by `timestamp`, or reset to now with `truncated: true` if scanning is too expensive.
  - Servers MAY apply their own replay ceiling and "**MUST** signal it via `truncated: true`".
  - Ignored when the cursor is `null` or for no-replay types.
- **`truncated: true`** is the single gap signal. Causes are not distinguished. The server resets to a servable position, returns the fresh cursor, and continues without forcing a reconnect.
  - Poll: in the result body.
  - Push: `notifications/events/active` (initially or mid-stream).
  - Webhook: the subscribe/refresh response, plus a `gap` envelope between refreshes.
- **Guarantees.** There are no protocol-level ordering, exactly-once or consistency guarantees, and no cross-subscription ordering.
  - Per-subscription order holds for poll and push; webhook is best-effort.
  - Dedup is the client's job, via `eventId`. At-least-once is achievable with a durable upstream.
- **Emit-only types.** Use an SDK ring buffer (bounded by time and/or count). The cursor is a process-scoped buffer sequence; after a restart, return `truncated: true` with a fresh cursor.

## B12. Error codes (as defined by the sketch)

| Code | Message | Meaning |
|---|---|---|
| `-32602` | `InvalidParams` | Request is statically invalid: arguments don't match `inputSchema`, `delivery.url` is malformed or non-`https`, or `delivery.secret` is not a valid `whsec_` value. |
| `-32011` | `NotFound` | A referenced entity does not exist: an unknown event name, or no matching subscription on `events/unsubscribe`. `data.kind` (`"event"` \| `"subscription"`) MAY disambiguate. |
| `-32012` | `Forbidden` | The principal is not permitted for this event/arguments, or access was revoked. Also used for an unauthenticated subscribe/unsubscribe. |
| `-32013` | `ResourceExhausted` | A server limit or quota was reached. `data.limit` names it (e.g. `"subscriptions"`); `data.max` MAY give the ceiling. |
| `-32014` | `Unsupported` | Well-formed, but a requested capability or option is unsupported, e.g. `{ "feature": "deliveryMode", "value": "push" }`. Also `{"feature":"payloadSchema"\|"inputSchema","reason":"schema_changed"}`. |
| `-32015` | `CallbackEndpointError` | Webhook only. The endpoint failed verification or could not be reached. `data.reason` ∈ `connection_refused`, `timeout`, `tls_error`, `http_4xx`, `http_5xx`, `challenge_failed`. |

`-32603` is used in the `notifications/events/error` example (`"message":"UpstreamError"`).

**⚠ Conflict with the 2026-07-28 base.** The sketch places these "in the JSON-RPC implementation-defined server range `[-32000, -32099]` alongside MCP's existing `-32003`/`-32004`/`-32042`". In 2026-07-28:

- `-32000..-32019` is the legacy band ("New codes **MUST NOT** be allocated in this sub-range").
- `-32003`/`-32004` were renumbered to `-32021`/`-32022`, and `-32042` was retired.
- So `-32011..-32015` contradict current policy and may collide with legacy SDK codes.

Implementer choice: use the sketch's numbers for interop with sketch-based clients and document it, or wait for renumbering.

## B13. Server SDK behaviour worth mirroring

- One "check for changes since cursor" function backs all modes. `null` cursor → return the current position with no events.
- The SDK computes `delivery`:
  - poll is on by default (check function or emit ring buffer);
  - push when the transport streams;
  - webhook when `webhook_ttl` is configured;
  - minus any modes the author disabled.
- **Emit.** Broadcast emit applies per-subscription `match(ctx, event, arguments)` and `transform(…)` hooks, also when serving poll from the ring buffer. Targeted emit goes to a subscription by id.
- **Lifecycle hooks.** `on_subscribe`/`on_unsubscribe` fire across all modes.
  - Push ends when the stream closes; webhook ends on unsubscribe or TTL lapse.
  - Poll uses a lease keyed `(principal-or-null, eventName, canonicalHash(arguments))` that expires without renewal. The window defaults to a small multiple of `nextPollMs`.
  - Hooks must be idempotent; they re-fire after a restart.
- **Durability.** Short-TTL webhook state may live only in memory; scaled-out deployments need a shared store (e.g. Redis with TTL). "SDKs **SHOULD** refuse a no-expiry cap unless the author has wired up durable storage."

## B14. Open questions and points an implementer must decide

The sketch lists five Open Questions:

1. How SDKs expose events to agent frameworks (an SDK question).
2. Whether to fold `resources/subscribe` and the `list_changed` family into reserved `mcp.*` event types.
3. How to publish egress IP ranges (leaning: an SEP-2127 server-card field).
4. Whether one subscription can span multiple event names with one cursor ("wire-breaking if adopted"). Options: (a) the SDK coalesces upstreams, (b) `name` becomes an array, (c) event groups. No lean is given.
5. Whether task state should be exposed as `mcp.task.updated` events.

Explicitly unresolved or deferred elsewhere in the sketch:

- The server-identity key location: the SEP-2127 card vs `/.well-known/mcp-webhook-jwks.json`. "decided at SEP-review time".
- The User-Agent format (SEP-1336).
- Bearer, OAuth or OIDC authentication toward the callback ("may be considered for a future revision").
- Push-reconnect flow control (credit-based control is possible later).
- Event schema versioning or fingerprints ("possible additive follow-on").
- Not in v1: replay from before the subscription, message-queue bindings, rich query languages, cross-server routing, event-bound prompts.

Choices forced by gaps or base-protocol conflicts (my list):

- **Capability location.** `capabilities.events` vs `capabilities.extensions["io.modelcontextprotocol/…"]`. No identifier is defined.
- **`resultType`.** Every result needs `resultType: "complete"`, including `StreamEventsResult` and the unsubscribe ack.
- **`notifications/events/list_changed` delivery.** No `subscriptions/listen` filter key exists for it.
- **Error code range conflict** (see B12).
- **`events/list` caching attributes.** Unspecified.
- **`Mcp-Name` for `events/*`.** Not defined. Send or require only `Mcp-Method`.
- **Canonical JSON and the derived-`id` algorithm.** Unspecified.
- **`delivery.mode` required-ness.** Unspecified.
- **Secret base64 padding.** Unspecified.
- **Poll result field optionality.** Unspecified.
- **Unsubscribe result shape.** Unspecified.
- **`eventId` containing `.`.** Allowed by the sketch but unsafe under Standard Webhooks.
- **410 and timeout deviations** from Standard Webhooks.
- **Verification-POST routing** before the id is known.
- **`ResourceExhausted` on `events/stream`.** It is not in the list of immediate errors, but the error table implies it.

---

## Appendix: one-glance server obligation checklist

**Skills:**

- Declare `capabilities.resources` and `capabilities.extensions["io.modelcontextprotocol/skills"] = {}` or `{"directoryRead": true}`, inline with no envelope.
- Implement `skills/list` (paginated, atomic entries, `resultType`, `ttlMs`, `cacheScope`) and `skills/get` (echo the URI, `ttlMs`/`cacheScope`, no `nextCursor`, `-32602` for an unknown URI).
- Never gate either method on client capabilities.
- Make every entry `{uri, frontmatter (verbatim YAML→JSON), resources (complete {uri,digest,size}[] or "dynamic")}`.
- Make the final path segment equal `name`, using ASCII `[a-z0-9-]`, 1–64 characters, without leading, trailing or consecutive hyphens.
- Compute digests over exactly the bytes served. Use `text` only when its UTF-8 is byte-identical; otherwise use `blob`.
- If `directoryRead` is declared, implement direct children only with `inode/directory` subdirectories, no trailing slash, and `-32602` for a non-directory.
- Keep each skill ≤ 512 files and ≤ 16 MiB.

**Events:**

- Advertise per-type `delivery`.
- Poll is stateless and returns `{events,cursor,truncated,hasMore,nextPollMs}`.
- Push uses `active` → `event`/`error`/`heartbeat` (≤ 30 s) → `terminated`. Every notification carries `_meta["io.modelcontextprotocol/subscriptionId"]` = the request id. The stream is exempt from concurrency caps.
- Webhook requires:
  - an authenticated principal;
  - `whsec_` validation (24–64 bytes) and https-only callbacks;
  - endpoint verification before first delivery;
  - a deterministic `id` and an idempotent upsert on `(principal,url,name,arguments)`;
  - the TTL grant rules (`refreshBefore` ≤ the suggestion, `null` only on request);
  - Standard Webhooks `v1,` signing, re-signed per retry, with a dual-sign rotation window;
  - `X-MCP-Subscription-Id` on every delivery;
  - SSRF checks at delivery time with IP pinning and no redirects;
  - non-retryable handling of 410 and 413;
  - bounded retries and a safe watermark cursor;
  - `lastError` reported as categories only.
