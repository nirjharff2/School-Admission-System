# AI Usage

## Tools used

- Claude Code (Anthropic) — scaffolding, implementation and tests, driven feature by feature.

## Where AI was used

- Repository skeleton: backend/frontend scaffolds, docker-compose, docs skeletons.

## Where the AI was wrong, and how it was caught

- Skeleton: `dotnet new sln` on the .NET 10 SDK produced a `.slnx` file by default; caught while planning Docker builds on the .NET 8 SDK and replaced with a classic `.sln`.
