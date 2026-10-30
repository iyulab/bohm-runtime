# bohm-runtime

A local-first application runtime.

It runs small applications on the computer they are used on. Each application is a single HTML file, served from
its own origin (`http://<id>.localhost:<port>/`) under a strict content security policy. What an application
keeps is stored next to it in a data folder on this computer and survives every later version of the application.
When an application calls an AI service, it calls the runtime instead (`/__bohm/llm/<provider host>/…`): the runtime
adds the key the person connected, or answers with a model on this computer, so the application never holds a key.
Everything an application sends out is counted.

The runtime is the companion process of a desktop browser shell, which starts it, talks to it over a loopback
control API, and shows its applications.

## Design commitments

- **Data outlives the application.** Application code can be rewritten; the data it holds cannot. Where the two
  conflict, the data wins.
- **Generated code is not trusted.** Each application is isolated in its own origin with a restrictive policy, and
  what it reaches outside is counted.
- **No network dependency for core function.** With a model on this computer, applications keep working on a
  disconnected network.
- **Proposals, not applications.** A change to an application arrives as a proposal the person reviews; nothing
  replaces a running application unreviewed.

The direction beyond this — a declared data model, permissions and scheduled jobs that the runtime carries out — is
not built yet.

## Application authoring

The desktop application writes and revises applications from a request, as proposals the person reviews and never as
silent replacements. That layer is part of the desktop application, not of this runtime. It is assembled from iyulab's
open building blocks around the runtime's control API:

- [IronHive](https://github.com/iyulab/ironhive) — the agent loop, tool approvals and the provider clients (OpenAI,
  Anthropic, Google, OpenAI-compatible servers) this runtime links;
- [LMSupply](https://github.com/iyulab/lm-supply) — models on this computer, so the same work continues on a
  disconnected network;
- [LocalOrigin](https://github.com/iyulab/local-origin) — the per-application origin every result is served from;
- [tauri-kit](https://github.com/iyulab/tauri-kit) — the desktop shell and sidecar plumbing.

This runtime keeps everything an authored application needs to keep running — serving, storage, permissions, the model
proxy — so applications and their data stay usable without the authoring layer. When the layer is present it is found
as `modules/Bohm.Authoring.dll` beside the executable and plugged in through an extension point; without it the routes
that write or change an application answer `501`.

## Status

No standalone releases: the runtime ships inside the desktop application that uses it. The control API, the
application contract, the on-disk layout and the extension point authoring plugs into are not a public contract yet
and change without notice.

## Build and run

Requires the [.NET 10 SDK](https://dotnet.microsoft.com/download). Developed and tested on Windows.

```sh
dotnet build src/Bohm.Runtime.Host -c Release
dotnet test
```

Run it by hand with a data folder and a secret for the control API (32 characters or more):

```sh
BOHM_RUNTIME_SECRET=<a random string of 32+ characters> \
  dotnet run --project src/Bohm.Runtime.Host -- --data-root ./data --port 0
```

When it is listening it writes one line to standard output — `bohm-runtime ready port=<n>` — and everything else to
standard error. Send the secret as a bearer token to `http://127.0.0.1:<n>/__control/…`; without it the control API
is off and answers nothing. `--help` lists every option and environment variable.

Two things are shared by every data folder on the computer, not kept per folder: the keys the person connects (the
Windows Credential Manager, as `<name>.Bohm` — the entries a tauri-kit shell shares; `BOHM_VAULT_PREFIX` moves them elsewhere for a check that must not touch
the person's keys) and downloaded models (the model library's cache).

## Contributing

Contributions are accepted under the Individual Contributor License Agreement in [CLA.md](CLA.md),
which the dual license requires. A pull request is checked for a signature on record; the
"Signing" section of that file describes how to add one. Only bots are exempt.

Everything committed here is public, commit messages included. Before pushing, run the public-text
check with [scripts/public-text.config.js](scripts/public-text.config.js) — local paths and private
hosts — and, if you work from a private workspace, once more with your own list of names kept
outside this repository:

```sh
npx @iyulab/tauri-kit-dev@0.12.0 public-text --config scripts/public-text.config.js .
npx @iyulab/tauri-kit-dev@0.12.0 public-text --config <your private config> .
```

A workaround waiting on a dependency is marked with the package it waits on, and what it needs:
`TODO(upstream: <package> — <what>)`.

## License

AGPL-3.0-or-later — see [LICENSE](LICENSE). A commercial license is available for uses that cannot
accept AGPL terms.
