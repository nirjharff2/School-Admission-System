# School Admission System

Students (or their guardians) apply for a seat in a class. An application first **holds** a seat for a limited time; paying the admission fee **confirms** it, cancelling or letting the time run out **releases** it. The backend guarantees that a class is never over-booked, however many people apply at the same moment.

- Backend: C# / .NET 8 minimal API, Entity Framework Core, PostgreSQL 16
- Frontend: React 19 + TypeScript (Vite), served by nginx in Docker
- Everything runs with Docker; nothing else has to be installed to try it.

## Quick start (Docker only)

Prerequisite: **Docker** with the Compose plugin (Docker Desktop on Windows/macOS, or Docker Engine + `docker compose` on Linux). No `.env` file, no other tools.

```bash
git clone <this repository>
cd <repository folder>
docker compose up --build
```

The first run downloads the base images and builds both applications (several minutes on a slow connection); later runs start in seconds. When the command settles, open:

| What | URL |
|---|---|
| **Web UI (start here)** | **http://localhost:3000** |
| Backend API | http://localhost:8080 (try http://localhost:8080/classes) |
| Second backend API (same database) | http://localhost:8081 |

PostgreSQL is only reachable inside the compose network (it is not published on a host port, so it cannot clash with a PostgreSQL you already run). For a SQL shell: `docker compose exec postgres psql -U admission admission`.

Stop with `Ctrl+C`, or `docker compose down` when started with `-d`. **Data is kept** in the named volume `pgdata`: `docker compose down` followed by `docker compose up` still shows every class and application. `docker compose down -v` deletes the data.

## Using the web UI

1. Enter a **student ID** (the 17-digit birth registration number) in the box at the top. It is saved in the browser, so it survives a refresh and a browser restart. Invalid IDs are flagged and block applying, paying and cancelling.
2. **Create Class**: as the school admin, add a class with a name and a number of seats.
3. **Classes**: shows total, held, confirmed and available seats (refreshed every 4 seconds without reloading). **Apply (আবেদন করুন)** holds a seat for the current student and opens the application.
4. **Application**: shows the status and a live countdown. **Pay admission fee (ভর্তি ফি দিন)** confirms the seat (no real payment, it always succeeds); **Cancel (বাতিল)** releases it. When the countdown reaches zero the status turns to `EXPIRED` and both buttons are disabled, without a reload.
5. **My Applications**: every application of the current student with its status; works straight after a refresh.

Errors (no seats left, hold expired, already applied, invalid input, backend unreachable) are shown as plain messages on the screen.

## Settings

All settings are optional environment variables for `docker compose` (or lines in a `.env` file next to `docker-compose.yml`).

| Variable | Default | Meaning |
|---|---|---|
| `HOLD_DURATION_SECONDS` | `600` | how long a seat hold lasts before it expires |
| `EXPIRY_SWEEP_INTERVAL_SECONDS` | `2` | how often the backend stores lapsed holds as `EXPIRED` |
| `FRONTEND_PORT` / `BACKEND_PORT` | `3000` / `8080` | published host ports; after changing `BACKEND_PORT` run `docker compose up --build` (the frontend image contains the API address) |
| `BACKEND2_PORT` | `8081` | published host port of the second backend container |
| `VITE_API_BASE_URL` | `http://localhost:8080` (follows `BACKEND_PORT`) | API address the **browser** uses; baked into the frontend image at build time, so rebuild (`--build`) after changing it |
| `POSTGRES_DB` / `POSTGRES_USER` / `POSTGRES_PASSWORD` | `admission` | database name and credentials |

The backend itself reads the connection string from `ConnectionStrings__Default` (an Npgsql connection string) and refuses to start without it; compose fills it in.

### Watching a hold expire (fast expiry)

Use a short hold time, then apply in the UI and watch the countdown:

```bash
# Linux / macOS / Git Bash
HOLD_DURATION_SECONDS=20 docker compose up --build

# Windows PowerShell
$env:HOLD_DURATION_SECONDS = 20; docker compose up --build
# afterwards, in the same window: Remove-Item Env:HOLD_DURATION_SECONDS

# Windows cmd
set "HOLD_DURATION_SECONDS=20" && docker compose up --build
```

After 20 seconds the application shows `EXPIRED`, Pay is disabled and the seat is available in the class list again, with no reload and no request from you: the backend frees the seat by itself.

## API

JSON over HTTP on port 8080. Student endpoints take the 17-digit ID in the `X-Student-Id` header. Errors are `{ "error": "<code>", "message": "<sentence>" }`; the codes and statuses are listed in [ASSUMPTIONS.md](ASSUMPTIONS.md).

| Method | Path | Purpose |
|---|---|---|
| `POST` | `/classes` | create a class `{ name, totalSeats }` |
| `GET` | `/classes` | all classes with `heldSeats`, `confirmedSeats`, `availableSeats` |
| `GET` | `/classes/{classId}` | one class |
| `POST` | `/classes/{classId}/holds` | apply: hold a seat (`201`, or `409` when none is left or the student already applied) |
| `GET` | `/holds/{holdId}` | status of an application |
| `POST` | `/holds/{holdId}/confirm` | pay the fee (owner only; repeating it is harmless) |
| `DELETE` | `/holds/{holdId}` | cancel (`204`) |
| `GET` | `/me/holds` | the student's applications |

## How over-booking is prevented

`heldSeats + confirmedSeats <= totalSeats` is enforced inside PostgreSQL, not in application memory: applying runs in one transaction that first locks the class row (`SELECT … FOR UPDATE`), then counts the active holds and inserts only if a seat is left. Confirming and cancelling are single conditional `UPDATE` statements. A partial unique index also guarantees one active application per student per class. Expired holds are released by a background sweeper and are also ignored by every read and every seat count the moment they lapse. Because the lock lives in the database, the guarantee holds with any number of backend processes. The full reasoning, and the measurements behind it, are in [ASSUMPTIONS.md](ASSUMPTIONS.md) (section *Concurrency strategy*).

### Two backend processes on one database

`docker compose up` starts a second API container, `backend2`, on http://localhost:8081, sharing the database with `backend` (8080). The web UI uses `backend`; `backend2` exists to show that the guarantee does not depend on a single process. For example, take the only seat of a class through one instance and ask the other:

```bash
curl -s -X POST localhost:8080/classes -H 'Content-Type: application/json' -d '{"name":"Two backends","totalSeats":1}'
# use the returned id below
curl -s -X POST localhost:8080/classes/<id>/holds -H 'X-Student-Id: 20152691234567890'   # 201, HELD
curl -s -X POST localhost:8081/classes/<id>/holds -H 'X-Student-Id: 20152691234567891'   # 409 no_seats_available
curl -s localhost:8081/classes/<id>                                                       # heldSeats 1, availableSeats 0
```

Under load the result is the same: 200 simultaneous applications for 9 seats, split between the two ports, give exactly 9 × `201` and 191 × `409`, and the seat counts agree on both ports (checked against the running stack).

The automated version is `ConcurrencyTests.Two_backend_instances_on_one_database_together_grant_exactly_the_seats_of_the_class` and `ExpirySweeperTests.Applications_and_confirms_spread_over_two_instances_while_holds_keep_expiring_never_overbook_or_fail` (two in-process hosts, one database). Why it holds, and why running two expiry sweepers is safe, is in [ASSUMPTIONS.md](ASSUMPTIONS.md) (*Several backend instances*).

## Tests

Backend (from the repository root):

```bash
dotnet test backend
```

Prerequisites: the .NET 8 SDK (or a newer SDK plus the .NET 8 runtime, because the tests target `net8.0`) and a **running Docker daemon**. The tests start their own throw-away `postgres:16-alpine` container through Testcontainers (no manual database, no free port needed, the container is removed afterwards), so they run against real PostgreSQL, real migrations and real SQL. The first run pulls the image; the full suite takes about a minute.

The required concurrency tests are in `backend/AdmissionSystem.Tests/ConcurrencyTests.cs`; run just them with:

```bash
dotnet test backend --filter "FullyQualifiedName~ConcurrencyTests"
```

- 200 simultaneous applications (distinct 17-digit student ids) for a 10-seat class: exactly 10 × `201`, 190 × `409`, `heldSeats` 10, `availableSeats` 0.
- 50 simultaneous confirms of one hold: all `200`, `confirmedSeats` 1.
- The same 200 applications split over two backend instances sharing one database: still exactly 10 × `201`.

Frontend: there is no automated test suite; `npm --prefix frontend run build` type-checks and builds it, and `npm --prefix frontend run lint` lints it.

## Running without Docker (development)

You need the .NET 8 SDK, Node 20.19+ or 22.12+, and a PostgreSQL you can reach.

```bash
# backend, on http://localhost:8080 (applies migrations at start-up)
ConnectionStrings__Default="Host=localhost;Port=5432;Database=admission;Username=admission;Password=admission" \
  dotnet run --project backend/AdmissionSystem.Api

# frontend, on http://localhost:5173
npm --prefix frontend ci
VITE_API_BASE_URL=http://localhost:8080 npm --prefix frontend run dev
```

## Repository layout

- `backend/` — `AdmissionSystem.sln`, `AdmissionSystem.Api` (the API), `AdmissionSystem.Tests` (xUnit), `Dockerfile`
- `frontend/` — Vite + React + TypeScript app, `Dockerfile` (build, then nginx)
- `docker-compose.yml` — PostgreSQL (named volume `pgdata`), two backend containers on the same database, frontend
- `ASSUMPTIONS.md` — every decision the brief left open, with the reasoning
- `AI_USAGE.md` — which AI tools were used, where, and where they were wrong
