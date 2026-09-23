# bohm-runtime

A local-first application runtime.

An application is described declaratively — its data model, permissions, scheduled jobs and
notifications — and the runtime executes that description. Generated server-side logic runs in an
isolated sandbox rather than in-process; generated UI is isolated on the browser side, by the UI
layer this runtime sits behind. Changes to a running application, including those produced from
natural-language intent, arrive as **change sets**: reviewed units that the runtime applies
deterministically and never applies unreviewed. Existing data is preserved across those changes.

The same runtime runs on a workstation or headless on a server, so an application can move between
the two without being rebuilt.

## Design commitments

- **Data outlives the application.** Application code can be regenerated; the data it holds cannot.
  Where the two conflict, the data wins.
- **Generated code is not trusted.** Sandboxing, network allow-lists and scoped file access are the
  default, not an option.
- **No network dependency for core function.** The runtime works fully on a disconnected network.
- **Proposals, not applications.** Nothing changes a running system without a reviewed change set.

## Status

Early design — no releases, and the public contract is deliberately not open yet. The extension
points, the on-disk project format and the registry protocol are still being shaped, and they will
be documented once they stabilise. Until then, treat everything here as subject to change.

## Contributing

Contributions are accepted under the Individual Contributor License Agreement in [CLA.md](CLA.md),
which the dual license requires. A pull request is checked for a signature on record; the
"Signing" section of that file describes how to add one. Organization members and bots are exempt.

## License

AGPL-3.0-or-later — see [LICENSE](LICENSE). A commercial license is available for uses that cannot
accept AGPL terms.
