# AI Usage

## Tools used

- Claude Code (Anthropic) — scaffolding, implementation and tests, driven feature by feature.

## Where AI was used

- Repository skeleton: backend/frontend scaffolds, docker-compose, docs skeletons.
- Database schema (EF Core entities, migration), class endpoints, uniform error handling, and the Postgres-backed integration tests (Testcontainers).
- Seat holds: student-id validation, `HOLD_DURATION_SECONDS` handling, `POST /classes/{id}/holds`, `GET /holds/{id}`, `GET /me/holds` and their tests.

## Where the AI was wrong, and how it was caught

- Skeleton: `dotnet new sln` on the .NET 10 SDK produced a `.slnx` file by default; caught while planning Docker builds on the .NET 8 SDK and replaced with a classic `.sln`.
- Class endpoints: the first seat-count query projected into a positional record and then filtered with `FirstOrDefaultAsync(r => r.Id == id)`; EF Core could not translate it and every class endpoint returned 500. Caught by the integration tests; fixed by projecting with an object initializer.
- Class endpoints: pinned `Microsoft.EntityFrameworkCore.Design` to 8.0.13 next to Npgsql provider 8.0.11, which produced EF assembly version conflicts (CS1705) in the test project. Caught by the build; aligned both on 8.0.11.
- Local smoke run: the AI first pointed the API at `localhost:5432` and got a password failure that looked like a credentials bug. Two listeners were bound to that port (the compose Postgres and another local server), so the connection reached the wrong one. Caught with `netstat`; the smoke run used a Postgres on another port instead.
- Port 8080: the AI set the default with a `Urls` entry in `appsettings.json` and documented that `ASPNETCORE_URLS` overrides it. It does not (the JSON value won), which would have blocked running a second instance on another port. Caught by the reviewing pass that actually started the API with `ASPNETCORE_URLS`; fixed by applying the default in `Program.cs` only when no address is configured.
- Class endpoints: a class name containing a NUL character passed validation and hit PostgreSQL, returning 500. Found by the same review; control characters are now rejected with 400 and covered by a test.
- Hold endpoints: the first `GET /me/holds` test reused one fixed student id that other tests had already applied with in the shared test database, so it saw foreign holds. Caught by the failing test; it now uses a fresh random id, and the other tests that count rows use their own classes.
- Hold endpoints: the same test asserted JSON property names in the wrong sort order (`className` before `classId` under ordinal ordering). The API output was right, the test was wrong; caught by the failing assertion.
