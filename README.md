# School Admission System

A seat-hold system for school admissions: students place a time-limited hold on a seat in a class, then confirm or cancel it, and the backend guarantees a class is never over-booked. Backend: .NET 8 minimal API + PostgreSQL. Frontend: React + TypeScript (Vite).

## Run with Docker

Prerequisite: Docker with the Compose plugin. Nothing else is installed on the host and no `.env` file is needed.

```bash
docker compose up --build
```

| What | Where |
|---|---|
| **Frontend (open this)** | http://localhost:3000 |
| Backend API | http://localhost:8080 (for example `GET /classes`) |
| PostgreSQL | inside the compose network only (not published; use `docker compose exec postgres psql -U admission admission`) |

The backend applies its database migrations at start-up. Data lives in the named volume `pgdata` (project `school-admission`), so `docker compose down` followed by `docker compose up` keeps all classes and applications; `docker compose down -v` deletes them.

Settings (all optional, set as environment variables or in a `.env` file next to `docker-compose.yml`):

| Variable | Default | Meaning |
|---|---|---|
| `HOLD_DURATION_SECONDS` | `600` | how long a seat hold lasts before it expires |
| `EXPIRY_SWEEP_INTERVAL_SECONDS` | `2` | how often the backend stores lapsed holds as `EXPIRED` |
| `FRONTEND_PORT` / `BACKEND_PORT` | `3000` / `8080` | published host ports |
| `VITE_API_BASE_URL` | `http://localhost:8080` (follows `BACKEND_PORT`) | API address the **browser** uses; baked into the frontend image at build time, so rebuild (`--build`) after changing it |
| `POSTGRES_DB` / `POSTGRES_USER` / `POSTGRES_PASSWORD` | `admission` | database name and credentials |

## Run tests

Backend (from the repository root):

```bash
dotnet test backend
```

Prerequisites: .NET 8 SDK or newer, and a **running Docker daemon**. The tests start their own throw-away `postgres:16-alpine` container through Testcontainers (no manual database, no free port needed, the container is removed afterwards), so they run against real PostgreSQL, real migrations and real SQL. The first run pulls the image. The full suite takes about a minute.

The concurrency tests are in `backend/AdmissionSystem.Tests/ConcurrencyTests.cs`; run just them with:

```bash
dotnet test backend --filter "FullyQualifiedName~ConcurrencyTests"
```

- 200 simultaneous applications (distinct 17-digit student ids) for a 10-seat class: exactly 10 x `201`, 190 x `409`, `heldSeats` 10, `availableSeats` 0.
- 50 simultaneous confirms of one hold: all `200`, `confirmedSeats` 1.
- The same 200 applications split over two backend instances sharing one database: still exactly 10 x `201`.

Frontend build check:

```bash
npm --prefix frontend run build
```

## Layout

- `backend/` — `AdmissionSystem.sln`, `AdmissionSystem.Api`, `AdmissionSystem.Tests`
- `frontend/` — Vite + React + TypeScript app
- `docker-compose.yml` — PostgreSQL (named volume `pgdata`), backend, frontend
- `backend/Dockerfile`, `frontend/Dockerfile`
- `ASSUMPTIONS.md`, `AI_USAGE.md`
