# Running the daemon

The daemon is one container of six. Dapr Workflow, which every execution runs on, needs a
sidecar beside the daemon and Dapr's placement and scheduler services; everything that persists
— Dapr's actor state, the daemon's read models, its event streams and its output streams — is
one Postgres database. Binary data needs one thing more, because bytes do not belong in a
database: an object store, which is MinIO here. `compose/docker-compose.yml` is that topology,
and a GitHub release ships it with the image it was built for.

```bash
tar -xzf utos-daemon-<version>-compose.tar.gz && cd utos-daemon-<version>
docker compose up -d
utos context create local http://localhost:5164
utos run ./hello.yaml --follow
```

| Service | Image | Role |
|---|---|---|
| `daemon` | `ghcr.io/utos/dapr-daemon` | The daemon; gRPC on `5164`, published to the host |
| `daemon-dapr` | `daprio/daprd` | The Dapr sidecar, in the daemon's network namespace so both see `localhost` |
| `placement` | `daprio/placement` | Actor placement, which Dapr Workflow is built on |
| `scheduler` | `daprio/scheduler` | Timers and reminders, which workflow delays are built on |
| `postgres` | `postgres:17-alpine` | The one database: Dapr's actor state (the `dapr-statestore` config in the compose file is the component; its tables are prefixed `dapr_`), the read models, the event streams and the output streams. The daemon migrates its own tables on start |
| `blobs` | `quay.io/minio/minio` | The object store binary data goes in — **only bytes**. Which blobs exist, who may read them and when they can go is the daemon's record, in `postgres`. From `quay.io`, not the Docker Hub default: `docker.io/minio/minio` answers an anonymous pull with "access denied" at every tag |

Only the daemon's port reaches the host. Data lives in three named volumes (`postgres-data`,
`scheduler-data`, `blobs-data`); `docker compose down -v` discards it, which is also the answer
to any changelog entry that says "drop the database". Every image runs on every architecture
Docker does, Apple Silicon included.

**The object store is optional**, and that is the spec's design rather than a convenience: a daemon
without one still carries small blobs inline, and refuses anything over its inline threshold by name
(`UTOS-F102`). It is here because meeting binary data for the first time through that refusal is a
poor introduction. To run without it, remove the `blobs` service and the `ConnectionStrings__blobs`
and `Blobs__*` settings beside it.

## Settings

| Variable | Default | |
|---|---|---|
| `DAEMON_IMAGE` | the release's image | A locally built image: `docker build -f deploy/Dockerfile -t utos-daemon:local .` from the repository root, then `DAEMON_IMAGE=utos-daemon:local docker compose up -d` |
| `POSTGRES_PASSWORD` | `utos` | Change it anywhere the compose file is not the only thing reaching the database. Substituted into the daemon's connection string and the Dapr component alike — which is why the component is an inline compose `config` rather than a mounted file: daprd does not expand variables, compose does |
| `DAPR_VERSION` | `1.18.4` | The daemon requires 1.18.4 or later — see the CHANGELOG's Requirements |
| `BLOBS_ACCESS_KEY` / `BLOBS_SECRET_KEY` | `utos` / `utos-secret` | The object store's credentials. Change them anywhere the compose network is not the only thing reaching it |
| `BLOBS_BUCKET` | `utos-blobs` | One bucket holds every object. Not one per run or per tenant: a bucket is not an access boundary the spec recognises — which run trees may read a blob is the daemon's record |
| `MINIO_VERSION` | a pinned release | The object store's image tag |

The daemon reads two connection strings as environment variables, and the compose file sets both:
`ConnectionStrings__utos` for the database, and `ConnectionStrings__blobs` for the object store, in
the form `Endpoint=…;AccessKey=…;SecretKey=…`. An empty `Endpoint` means real S3 at `Blobs__Region`
instead, which is how a deployment points at a provider rather than at MinIO — and that deployment
should also drop `Blobs__CreateBucketIfMissing`, so that a typo in the bucket name fails loudly
rather than quietly filling a new bucket. Everything the daemon sends to Dapr goes to its sidecar on
`localhost:50001`, the SDK's default.

## Building the image

```bash
docker build -f deploy/Dockerfile -t utos-daemon:local --build-arg VERSION=0.0.0-local .
```

Framework-dependent on `mcr.microsoft.com/dotnet/aspnet:10.0`; the build stage restores from the
manifests first so a source change does not re-download packages. The release workflow
(`.github/workflows/release.yml`) builds the same file and pushes it to GHCR as
`ghcr.io/utos/dapr-daemon:<version>` and `:latest`.

## Development

For development the Aspire host (`src/tools/Tools.AppHost`) starts Postgres and MinIO as containers
and a locally installed Dapr (`dapr init --runtime-version 1.18.4`), and writes the same
`state.postgresql` component for the sidecar from the connection string it allocated — the
integration tests randomise ports, so the component cannot be a static file there. That is what the
integration tests run against: the compose topology is the deployment shape, the Aspire host is the
inner loop, and both use the one database and the one object store the same way.

The development store has **no** volume, deliberately, where the compose one does: a test run must
not inherit objects from the last one, and retention is a rule the daemon has to enforce rather than
something a wiped volume can hide.
