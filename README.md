# bohm-runtime

A local-first application runtime.

An application is described declaratively — its data model, permissions, scheduled jobs and
notifications — and the runtime executes that description. Generated UI code runs sandboxed rather
than trusted. Changes to a running application, including those produced from natural-language
intent, arrive as **change sets**: reviewed units that the runtime applies deterministically and
never applies unreviewed. Existing data is preserved across those changes.

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

## License

AGPL-3.0-or-later — see [LICENSE](LICENSE). A commercial license is available for uses that cannot
accept AGPL terms.
