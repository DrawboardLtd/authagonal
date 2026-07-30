# Stage 1: Build SPA
FROM node:24-alpine AS frontend
WORKDIR /app/login-app
COPY login-app/package*.json ./
RUN npm ci
COPY login-app/ ./
RUN npm run build:spa

# Stage 2: Build .NET
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY *.slnx ./
COPY src/Authagonal.Core/*.csproj src/Authagonal.Core/
COPY src/Authagonal.Protocol/*.csproj src/Authagonal.Protocol/
COPY src/Authagonal.AzureProvider/*.csproj src/Authagonal.AzureProvider/
COPY src/Authagonal.SqlProvider/*.csproj src/Authagonal.SqlProvider/
COPY src/Authagonal.Server/*.csproj src/Authagonal.Server/
COPY src/Authagonal.Host/*.csproj src/Authagonal.Host/
# Authagonal.Host is the deployable: a thin entrypoint over the Authagonal.Server library that also
# references the storage providers, so the image can pick one via Storage:Provider at runtime while the
# published Authagonal.Server package stays free of their drivers.
#
# Publish for a specific RID. SQLitePCLRaw carries a native e_sqlite3 build for ~30 platforms, and a
# RID-agnostic publish keeps every one of them under runtimes/ — 61 MB of iOS-simulator, Mac Catalyst and
# ppc64le binaries that can never execute here. Naming the RID prunes that to the single .so this image
# runs on, and costs the SQL backends about 4 MB in total. TARGETARCH comes from buildx (amd64/arm64) and
# needs mapping to the .NET RID spelling (x64/arm64); it is written to a file so the restore and publish
# steps, which are separate layers, agree on it.
ARG TARGETARCH
RUN case "${TARGETARCH:-amd64}" in \
        amd64) echo linux-x64 ;; \
        arm64) echo linux-arm64 ;; \
        *) echo "unsupported TARGETARCH '${TARGETARCH}'" >&2; exit 1 ;; \
    esac > /tmp/rid \
 && dotnet restore src/Authagonal.Host/ -r "$(cat /tmp/rid)"
COPY src/ src/
# --self-contained false: still framework-dependent, so the aspnet base image supplies the runtime.
RUN dotnet publish src/Authagonal.Host/ -c Release -r "$(cat /tmp/rid)" --self-contained false \
        -o /app/publish --no-restore

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=backend /app/publish .
COPY --from=frontend /app/login-app/dist-spa ./wwwroot/
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Authagonal.Host.dll"]
