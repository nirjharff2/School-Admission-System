# School Admission System

A seat-hold system for school admissions: students place a time-limited hold on a seat in a class, then confirm or cancel it, and the backend guarantees a class is never over-booked. Backend: .NET 8 minimal API + PostgreSQL. Frontend: React + TypeScript (Vite).

> Status: repository skeleton. Only PostgreSQL runs under Docker so far; the API and UI are placeholders.

## Run with Docker

```bash
docker compose up
```

Currently this starts PostgreSQL only. Backend (http://localhost:8080) and frontend containers are added in a later step.

## Run tests

```bash
dotnet test backend
npm --prefix frontend run build
```

## Layout

- `backend/` — `AdmissionSystem.sln`, `AdmissionSystem.Api`, `AdmissionSystem.Tests`
- `frontend/` — Vite + React + TypeScript app
- `docker-compose.yml` — Postgres on named volume `pgdata`
- `ASSUMPTIONS.md`, `AI_USAGE.md`
