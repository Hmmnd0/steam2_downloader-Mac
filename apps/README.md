# App definitions

A Steam2 depot is not a game. A game is several depots at particular versions, and that mapping
exists nowhere in the archive — the blobs record only what is inside one depot, never which depots
belong together. It was held on Steam's side and is not part of the dump.

So it has to be written down by hand. That is what this folder is: one JSON file per app, keyed by
its **Steam appid**, listing the depots and versions each build is made of. The app shows them as a
pack you can install from.

## Adding or correcting one

One file per app, named `<appid>.json`. Open a pull request with it — a check runs on the PR and
tells you if the file is malformed or points at a depot or version the archive does not have.

Keeping one app per file means two people adding different apps never conflict.

## Format

```json
{
  "appid": 240,
  "name": "Counter-Strike: Source",
  "builds": [
    {
      "id": "v34",
      "name": "v34",
      "date": "2006-05-31",
      "notes": "what this build is, and how you established it",
      "depots": [
        { "depot": 241, "version": 56, "role": "client" },
        { "depot": 242, "version": 71, "role": "content" }
      ]
    }
  ]
}
```

| field | required | meaning |
|---|---|---|
| `appid` | yes | Steam appid. Must match the file name. |
| `name` | yes | Product name as people know it. |
| `builds` | yes | At least one. Newest first is the convention. |
| `builds[].id` | yes | Short, unique within the app: `v34`, `2006-05-31`, `beta-2`. |
| `builds[].name` | no | Shown instead of `id` when present. |
| `builds[].date` | no | `YYYY-MM-DD`. Sorts and labels the build. |
| `builds[].notes` | no | Free text. Say how you worked the versions out. |
| `builds[].depots` | yes | At least one. |
| `depots[].depot` | yes | Steam2 depot id, as the archive uses. |
| `depots[].version` | yes | Version within that depot. |
| `depots[].role` | no | `client`, `content`, `localization`, `engine`, `tools`, `other`. |
| `depots[].optional` | no | `true` for language packs and the like: offered, not selected by default. |

## What makes a good entry

Say in `notes` how you established the versions. A build assembled from dated depot versions that
happen to line up is a guess; one taken from a period install or a changelog is evidence. Both are
worth having, but a reader should be able to tell which they are looking at.

Do not guess a version to fill a gap. A build with three depots you are sure of is more useful than
one with six where half are invented — the download either reproduces something real or it does not.

An appid can have no build for a given era. Leaving it out is fine.
