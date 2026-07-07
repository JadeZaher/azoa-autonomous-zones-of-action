# Building a dApp on an Azoa node you don't operate

This is a quickstart for a **third-party dApp developer** onboarding onto an Azoa
node run by someone else (the "many devs, one node" model). You do not run the
backend — a node operator does. You get an avatar on their node, issue a scoped
API key, point the SDK at their node URL, and start authoring dApps.

All the code below uses the published SDK, `azoa-sdk` (TypeScript). Install it:

```bash
npm install azoa-sdk
```

## 1. Register / login an avatar against the remote node

Point the client at the operator's node URL and create (or restore) an avatar.
Authentication with email/password yields a JWT the SDK manages for you.

```ts
import { AzoaClient, isOk } from "azoa-sdk";

const NODE_URL = "https://azoa.some-operator.example"; // the node you were given

const azoa = new AzoaClient({ apiUrl: NODE_URL });

// First time: register. Returning: use azoa.auth.login(email, password).
const reg = await azoa.auth.register({
  email: "dev@example.com",
  password: "correct-horse-battery-staple",
  username: "dev",
});
if (!isOk(reg)) throw new Error(reg.error.message);

console.log("my avatarId:", azoa.auth.avatarId);
```

## 2. Issue a `dapp:develop` API key

Authoring holons, quests, and dApp-series is gated by the `dapp:develop` scope.
Mint a key that carries exactly that scope so your automation doesn't run as a
full-access key.

**Discover what you can self-issue** (the same list the api-keys page renders as
checkboxes):

```ts
const scopes = await azoa.api.listIssuableScopes();
if (isOk(scopes)) {
  for (const s of scopes.value) console.log(s.scope, "—", s.description);
}
```

**Create the key** (raw value is returned ONCE — store it securely):

```ts
const created = await azoa.api.createApiKey({
  name: "my-dapp-ci",
  scopes: "dapp:develop", // omit `scopes` entirely for a legacy full-access key
  expiresInDays: 90,       // optional
});
if (!isOk(created)) throw new Error(created.error.message);

const rawKey = created.value.key; // e.g. "azoa_…" — you won't see it again
```

You can also do all of this from the node's web UI: the **API Keys** page has a
scope-checkbox picker and a one-time key reveal, plus a **Rotate** action
(`azoa.api.rotateApiKey(id)`) that mints a successor key inheriting the name,
scopes, and expiry while revoking the old one.

> If a scoped key is missing `dapp:develop`, the write endpoints reply `403` with
> an actionable body: *"This API key lacks the 'dapp:develop' scope required to
> author holons/quests/dApp-series. Rotate the key with that scope."* — surfaced
> on the returned `SdkError.message`.

## 3. Point the SDK at the node with your key

For server-to-server automation, construct the client with `apiKey` instead of a
JWT. The key is sent as the `X-Api-Key` header on every request.

```ts
const dapp = new AzoaClient({
  apiUrl: NODE_URL,
  apiKey: rawKey,
});
```

## 4. First dApp end-to-end: series → quest → publish

A **DappSeries** is an ordered collection of quests that compose into a
deployable dApp. Create one, author a quest, add it to the series, and publish the
quest to the marketplace.

```ts
// 4a. Create the dApp-series (dapp:develop required)
const series = await dapp.api.createDappSeries({
  name: "Hello Azoa",
  description: "My first dApp on a shared node",
});
if (!isOk(series)) throw new Error(series.error.message);
const seriesId = series.value.id;

// 4b. Author a minimal quest (one entry node)
const quest = await dapp.api.createQuest({
  name: "Greeting quest",
  description: "A one-step starter quest",
  nodes: [
    { name: "start", nodeType: "Gate", isEntry: true, isTerminal: true },
  ],
  edges: [],
  isPublic: true, // marketplace-visible once published
});
if (!isOk(quest)) throw new Error(quest.error.message);
const questId = quest.value.id;

// 4c. Add the quest to the series at order 1
const entry = await dapp.api.addSeriesQuest(seriesId, { questId, order: 1 });
if (!isOk(entry)) throw new Error(entry.error.message);

// 4d. Publish the quest (validates the DAG, flips Draft → Active)
const published = await dapp.api.publishQuest(questId);
if (!isOk(published)) throw new Error(published.error.message);
```

Manage the series' quests as your dApp grows:

- `dapp.api.listSeriesQuests(seriesId)` — the ordered entries
- `dapp.api.reorderSeriesQuest(seriesId, questId, newOrder)` — resequence
- `dapp.api.updateSeriesMappings(seriesId, questId, inputMappingsJson)` — wire
  cross-quest data flow
- `dapp.api.removeSeriesQuest(seriesId, questId)` — drop an entry
- `dapp.api.listDappSeries()` / `dapp.api.getDappSeries(id)` /
  `dapp.api.updateDappSeries(id, {...})` / `dapp.api.deleteDappSeries(id)`

## 5. Others can browse your published quest

Once a quest is `isPublic` and Active, any avatar on the node can discover it via
the public marketplace and start it under their own avatar:

```ts
const catalog = await azoa.api.listPublicQuests(); // GET /api/quest/public
if (isOk(catalog)) {
  for (const q of catalog.value) console.log(q.name, q.id);
}

// A non-owner starts one — the run is owned by the caller, with provenance
// (originAvatarId / sourceQuestId) pointing back to you.
await azoa.api.executeQuest(someQuestId);
```

In the node's web UI, the **Quests → Start a Quest** tab renders this catalog as a
browsable grid of cards (no id-pasting required).

## Where to go next

- `PROVIDERS.md` — the full API surface and provider architecture
- `docs/NODE-HOST.md` — for operators: how to stand up the node you're building on
- `README.md` — project overview and the identity / quest / dApp layering
