# Assumptions

Decisions the specification left open, each with its reasoning.

## Identifiers

- Class ids are `c_` + 16 lower-case hex characters (64 random bits from a cryptographic RNG), e.g. `c_3f9a1b7c5d2e4a68`. Hold ids will use the same scheme with prefix `h_`.
- Reason: opaque, non-guessable, non-sequential (no count leak), short enough to read out, and generated in the application so no database round trip or sequence is needed. Uniqueness is guaranteed by the primary key; a collision (about 1 in 10^19 per pair) would surface as a 500 rather than silent corruption.

## Error codes and responses

- Every error response is JSON `{ "error": "<machine_code>", "message": "<human sentence>" }`. This includes framework-produced errors (malformed JSON, unknown route, wrong method, unhandled exception → 500 `internal_error`) so the UI only has to parse one shape. The `message` never contains stack traces or internals.
- `POST /classes`: `400 validation_failed` when `name` is missing, blank (after trimming) or longer than 200 characters, or `totalSeats` is missing, `null` or below 1. `400 invalid_request` when the body is not valid JSON or a field has the wrong type (for example `"totalSeats": "many"` or `2.5`). Accepted names are stored trimmed.
- `GET /classes/{classId}`: `404 class_not_found` for an unknown id.
- Unknown route → `404 not_found`; wrong HTTP method → `405 method_not_allowed`.
- Codes for the hold endpoints are recorded here when they are implemented.

## Configuration

- Database: the standard ASP.NET Core key `ConnectionStrings__Default` (Npgsql connection string). The app refuses to start without it. `appsettings.Development.json` points at the Postgres from `docker-compose.yml` on `localhost:5432` for `dotnet run` outside Docker.
- Schema is created and updated by EF Core migrations that run automatically at startup, so a fresh database needs no manual step.
- CORS allows any origin, header and method. There are no cookies or credentials (identity is a plain header), so a wildcard origin carries no extra risk and works for whatever host/port the frontend is served from.
- Time is read from an injected `TimeProvider` (UTC); timestamps are stored as `timestamptz`.

## Hold expiry mechanics

_To be filled in._

## Concurrency strategy

_To be filled in._

## Frontend polling interval

_To be filled in._

## Repository and tooling

- Solution uses the classic `.sln` format (not `.slnx`) so it builds with both the .NET 8 and newer SDKs.
- Frontend scaffolded with the current Vite `react-ts` template (React 19, which satisfies "React 18 or newer").
- Backend integration tests start their own throw-away `postgres:16-alpine` container through Testcontainers, so running `dotnet test backend` needs a running Docker daemon but no manual database setup or port. The tests exercise the real migrations and real SQL; an in-memory fake would not catch Npgsql/EF translation problems.
