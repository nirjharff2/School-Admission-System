# Build context: ./backend
FROM mcr.microsoft.com/dotnet/sdk:8.0 AS build
WORKDIR /src

# restore first so the package layer is cached until the project file changes
COPY AdmissionSystem.Api/AdmissionSystem.Api.csproj AdmissionSystem.Api/
RUN dotnet restore AdmissionSystem.Api/AdmissionSystem.Api.csproj

COPY AdmissionSystem.Api/ AdmissionSystem.Api/
RUN dotnet publish AdmissionSystem.Api/AdmissionSystem.Api.csproj -c Release -o /app --no-restore

FROM mcr.microsoft.com/dotnet/aspnet:8.0 AS runtime
WORKDIR /app
COPY --from=build /app .

# The API listens on 8080 (the .NET 8 images already default to it). The connection string
# (ConnectionStrings__Default) and HOLD_DURATION_SECONDS come from the environment; EF Core migrations run at startup.
ENV ASPNETCORE_HTTP_PORTS=8080
EXPOSE 8080
USER $APP_UID
ENTRYPOINT ["dotnet", "AdmissionSystem.Api.dll"]
