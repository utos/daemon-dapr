# Schema conformance corpus

Vendored from [`utos/api`](https://github.com/utos/api) `conformance/schema/` at **v0.0.18**, by
hand — the daemon has no release pipeline that vendors the spec the way the SDK repos do.
`SchemaConformanceTests` runs every case here against the evaluator the daemon validates with.

This is the corpus the spec names the daemon for. Where `conformance/validation/` asks "is this
schema well-formed?" — a bundle rule, `UTOS-H0##`, enforced by `Utos.Workflow.Validation` at load
— this one asks "does this value satisfy that schema?", which is `UTOS-H1##` and is decided here,
at a boundary, while a run proceeds.

Each case is a schema, a value, and what must happen: whether it validates, the defaults that were
filled, and on a failure the `instanceLocation` + `keyword` pairs that must be reported — the
complete set, because reporting every failure rather than the first is the point of the format.
`keywordLocation` is deliberately not asserted.

To update: copy `api/conformance/schema/*.json` over this directory and bump the tag above. The
README of the source directory describes the case format.
