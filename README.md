# School Admission System

A seat-hold system for school admissions: students place a time-limited hold on a seat in a class, then confirm or cancel it, and the backend guarantees a class is never over-booked. Backend: .NET 8 minimal API + PostgreSQL. Frontend: React + TypeScript (Vite).

> Status: repository skeleton. Only PostgreSQL runs under Docker so far; the API and UI are placeholders.

## Run with Docker

```bash
docker compose up
```

Currently this starts PostgreSQL only. Backend (http://localhost:8080) and frontend containers are added in a later step.

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
- `docker-compose.yml` — Postgres on named volume `pgdata`
- `ASSUMPTIONS.md`, `AI_USAGE.md`
