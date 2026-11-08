# Assumptions

Decisions the specification left open, each with its reasoning.

## Identifiers

- Class ids are `c_` + 16 lower-case hex characters (64 random bits from a cryptographic RNG), e.g. `c_3f9a1b7c5d2e4a68`. Hold ids use the same scheme with prefix `h_`.
- Reason: opaque, non-guessable, non-sequential (no count leak), short enough to read out, and generated in the application so no database round trip or sequence is needed. Uniqueness is guaranteed by the primary key; a collision (about 1 in 10^19 per pair) would surface as a 500 rather than silent corruption.

## Error codes and responses

- Every error response is JSON `{ "error": "<machine_code>", "message": "<human sentence>" }`. This includes framework-produced errors (malformed JSON, unknown route, wrong method, unhandled exception → 500 `internal_error`) so the UI only has to parse one shape. The `message` never contains stack traces or internals.
- `POST /classes`: `400 validation_failed` when `name` is missing, blank (after trimming) or longer than 200 characters, or `totalSeats` is missing, `null` or below 1. `400 invalid_request` when the body is not valid JSON or a field has the wrong type (for example `"totalSeats": "many"` or `2.5`). Accepted names are stored trimmed.
- `GET /classes/{classId}`: `404 class_not_found` for an unknown id.
- Unknown route → `404 not_found`; wrong HTTP method → `405 method_not_allowed`.
- Student identity (`X-Student-Id`, required on `POST /classes/{id}/holds` and `GET /me/holds`): `400 student_id_required` when the header is absent or blank, `400 invalid_student_id` when it is not exactly 17 ASCII digits `0-9` (full-width or other-script digits are rejected). The header is checked before the class is looked up, so a bad header is a 400 even for an unknown class. The messages are written to be shown to a guardian as they are.
- `POST /classes/{classId}/holds`: `404 class_not_found`; `409 already_applied` when the student already has an active hold in that class; `409 no_seats_available` when the class is full. The duplicate check runs first, so a student who already holds the last seat is told they already applied rather than that the class is full.
- `GET /holds/{holdId}`: `404 hold_not_found`. It needs no header and returns exactly the contract fields (no `studentId`), so knowing a hold id does not reveal whose application it is.
- `GET /me/holds`: newest application first; an empty array when the student has none.
- `POST /holds/{holdId}/confirm` and `DELETE /holds/{holdId}` (both need `X-Student-Id`, validated first, then):
  - Unknown hold → `404 hold_not_found`. Another student's hold → `403 not_your_hold` (a 403 rather than a 404: hold ids are random and unguessable, and `GET /holds/{id}` already reveals existence, so hiding it would add nothing and the 403 gives the guardian a clear message). The hold is left untouched.
  - Confirm: `HELD` and unexpired → `200 { holdId, status: "CONFIRMED" }`. Already `CONFIRMED` → `200` again, nothing changes (idempotent, required by the spec). Expired (stored `EXPIRED`, or `HELD` past `expiresAt`) → `409 hold_expired`. `CANCELLED` → `409 hold_cancelled`. An expired hold is never revived; the guardian applies again.
  - Cancel policy: `HELD` and unexpired → `204`, status `CANCELLED`, the seat is free at once and the student may apply again. Already `CANCELLED` → `204` (idempotent: the requested end state already holds). `CONFIRMED` → `409 hold_confirmed`: once the fee is paid the application cannot be withdrawn through this API, because there is no refund flow and silently freeing a paid seat would be wrong. Expired → `409 hold_expired` (nothing to cancel; the status stays `EXPIRED` rather than becoming `CANCELLED`).
  - `409 hold_state_changed` exists only for the race where the hold changes between the conditional update and the follow-up read in a way the update should have caught; the client can simply retry.

## Configuration

- Database: the standard ASP.NET Core key `ConnectionStrings__Default` (Npgsql connection string). The app refuses to start without it. `appsettings.Development.json` points at the Postgres from `docker-compose.yml` on `localhost:5432` for `dotnet run` outside Docker.
- Schema is created and updated by EF Core migrations that run automatically at startup, so a fresh database needs no manual step.
- CORS allows any origin, header and method. There are no cookies or credentials (identity is a plain header), so a wildcard origin carries no extra risk and works for whatever host/port the frontend is served from.
- Time is read from an injected `TimeProvider` (UTC); timestamps are stored as `timestamptz`.

## Hold expiry mechanics

- Lifetime comes from the `HOLD_DURATION_SECONDS` environment variable (whole seconds, at least 1, default 600). An invalid value stops the app at startup instead of failing on the first request.
- `createdAt` is the request time truncated to whole seconds and `expiresAt = createdAt + HOLD_DURATION_SECONDS` exactly, so the serialized value (`2026-11-01T04:10:00Z`, UTC, whole seconds, trailing `Z`) matches the contract example and the stored value. A hold therefore lives up to one second less than the wall-clock time since the request.
- A hold is expired when `now >= expiresAt`. Every read computes the status: a stored `HELD` row past its `expiresAt` is reported `EXPIRED`, and it is not counted as held or as occupying a seat, whether or not any background job has rewritten the row yet.
- Only `CONFIRMED` and unexpired `HELD` holds are "active": they occupy a seat and block a second application by the same student to the same class. `EXPIRED` and `CANCELLED` holds do neither, so a student can apply again after expiry or cancellation.

## Concurrency strategy

- Interim state: applying (`POST /classes/{id}/holds`) checks the duplicate and the free-seat count and then inserts, with no row lock, so two simultaneous requests can both pass the check. This is known and not yet acceptable for the capacity rule; the database-level guarantee is added in a later step and documented here when it lands.
- Confirm and cancel (`POST /holds/{id}/confirm`, `DELETE /holds/{id}`) are each a single conditional `UPDATE ... WHERE id = ? AND student_id = ? AND status = 'HELD' AND expires_at > now`. The row can only move out of `HELD` once, so repeated or simultaneous calls cannot count a seat twice; a test fires 20 parallel confirms at one hold and expects `confirmedSeats` to be 1. Confirming does not change `heldSeats + confirmedSeats`, so it never needs a capacity check.

## Frontend polling interval

_To be filled in._

## Repository and tooling

- Solution uses the classic `.sln` format (not `.slnx`) so it builds with both the .NET 8 and newer SDKs.
- Frontend scaffolded with the current Vite `react-ts` template (React 19, which satisfies "React 18 or newer").
- Backend integration tests start their own throw-away `postgres:16-alpine` container through Testcontainers, so running `dotnet test backend` needs a running Docker daemon but no manual database setup or port. The tests exercise the real migrations and real SQL; an in-memory fake would not catch Npgsql/EF translation problems.
- The API binds `http://0.0.0.0:8080` (all interfaces, so it is reachable from outside a container) only when no address is configured. `--urls`, `ASPNETCORE_URLS` or `ASPNETCORE_HTTP_PORTS` take precedence, so a second instance on another port is `ASPNETCORE_URLS=http://0.0.0.0:8081`.
- Class names are trimmed and must not contain control characters (PostgreSQL rejects NUL in text and the rest are never legitimate in a class name), so bad input gets a `400 validation_failed` instead of a database error.
