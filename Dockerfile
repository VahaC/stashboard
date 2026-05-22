ARG DOTNET_VERSION=10.0
ARG NODE_VERSION=22

# ---------- Stage 1: build the React frontend ----------
FROM node:${NODE_VERSION}-alpine AS frontend-build
WORKDIR /web

COPY frontend/package*.json ./
RUN npm ci --include=optional

COPY frontend/ ./
RUN npm run build

# ---------- Stage 2: build the .NET API ----------
FROM mcr.microsoft.com/dotnet/sdk:${DOTNET_VERSION} AS api-build
WORKDIR /src

COPY Stashboard.slnx ./
COPY src/Stashboard.Core/*.csproj src/Stashboard.Core/
COPY src/Stashboard.Infrastructure/*.csproj src/Stashboard.Infrastructure/
COPY src/Stashboard.Api/*.csproj src/Stashboard.Api/
COPY src/Stashboard.Migrations/*.csproj src/Stashboard.Migrations/
COPY tests/Stashboard.Tests/*.csproj tests/Stashboard.Tests/
RUN dotnet restore Stashboard.slnx

COPY . .
RUN dotnet publish src/Stashboard.Api/Stashboard.Api.csproj \
    -c Release \
    -o /app/publish \
    --no-restore \
    /p:UseAppHost=false

# ---------- Stage 3: runtime ----------
FROM mcr.microsoft.com/dotnet/aspnet:${DOTNET_VERSION} AS runtime
WORKDIR /app

RUN apt-get update \
    && apt-get install -y --no-install-recommends curl \
    && rm -rf /var/lib/apt/lists/*

# Note: aspnet:10.0 already ships with a non-root `app` user occupying UID 1000,
# so we don't pin our user to a specific UID here — let useradd pick the next free
# system UID. The named `stashboard` user is what matters for ops.
RUN groupadd -r stashboard && useradd -r -g stashboard stashboard \
    && mkdir -p /app/Data /app/wwwroot/uploads/logos \
    && chown -R stashboard:stashboard /app

COPY --from=api-build --chown=stashboard:stashboard /app/publish .
COPY --from=frontend-build --chown=stashboard:stashboard /web/dist /app/wwwroot

USER stashboard

ENV ASPNETCORE_URLS=http://+:8080
ENV ASPNETCORE_ENVIRONMENT=Production
EXPOSE 8080

HEALTHCHECK --interval=30s --timeout=5s --start-period=20s --retries=3 \
    CMD curl -fsS http://localhost:8080/ || exit 1

ENTRYPOINT ["dotnet", "Stashboard.Api.dll"]
