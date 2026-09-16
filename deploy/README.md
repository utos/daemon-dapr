# Running the daemon

The daemon is one container of seven. Dapr Workflow, which every execution runs on, needs a
sidecar beside the daemon, Dapr's placement and scheduler services, and an actor state store;
the daemon itself needs SQL Server for its read models and KurrentDB for its event streams.
`compose/docker-compose.yml` is that topology, and a GitHub release ships it with the image it
was built for.

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
| `redis` | `redis` | The actor state store (`compose/dapr/components/statestore.yaml`) |
| `sqlserver` | `mcr.microsoft.com/mssql/server` | Execution read models; the daemon migrates the database on start |
| `kurrentdb` | `docker.kurrent.io/kurrent-latest/kurrentdb` | Execution streams and output streams |

Only the daemon's port reaches the host. Data lives in named volumes (`sqlserver-data`,
`kurrentdb-data`, `scheduler-data`); `docker compose down -v` discards it, which is also the
answer to any changelog entry that says "drop the KurrentDB volume and the SQL database".

## Settings

| Variable | Default | |
|---|---|---|
| `DAEMON_IMAGE` | the release's image | A locally built image: `docker build -f deploy/Dockerfile -t utos-daemon:local .` from the repository root, then `DAEMON_IMAGE=utos-daemon:local docker compose up -d` |
| `SQL_SA_PASSWORD` | a development password | Change it anywhere the compose file is not the only thing reaching SQL Server |
| `DAPR_VERSION` | `1.18.4` | The daemon requires 1.18.4 or later — see the CHANGELOG's Requirements |

The daemon reads two connection strings, `WorkflowExecution` (SQL Server) and `eventstore`
(KurrentDB), as `ConnectionStrings__<name>` environment variables; the compose file sets both.
Everything the daemon sends to Dapr goes to its sidecar on `localhost:50001`, the SDK's default.

## Building the image

```bash
docker build -f deploy/Dockerfile -t utos-daemon:local --build-arg VERSION=0.0.0-local .
```

Framework-dependent on `mcr.microsoft.com/dotnet/aspnet:10.0`; the build stage restores from the
manifests first so a source change does not re-download packages. The release workflow
(`.github/workflows/release.yml`) builds the same file and pushes it to GHCR as
`ghcr.io/utos/dapr-daemon:<version>` and `:latest`.

## Development

For development the Aspire host (`src/tools/Tools.AppHost`) starts the same dependencies as
containers with a locally installed Dapr (`dapr init --runtime-version 1.18.4`), which is what
the integration tests use. The compose topology is the deployment shape; the Aspire host is the
inner loop.
