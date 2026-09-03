# Stage 1: Build SPA
FROM node:26-alpine@sha256:2d984a15c9b54fd0aeb608b8e0d0d83529eb34d2966db27a1fb4f1edc3d298a3 AS frontend
WORKDIR /app/login-app
COPY login-app/package*.json ./
RUN npm ci
COPY login-app/ ./
RUN npm run build:spa

# Stage 2: Build .NET
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1ffd2a92ae84c1291bc1b6887501f8af98e6331e7af6d4c8d37168c5e87a64c AS backend
WORKDIR /src
# Directory.Build.props, NuGet.Config and the lock files are part of the restore input, not
# incidental repo furniture. Without them this stage restored a DIFFERENT graph from every other
# build of the same commit: no lock file, so nothing for --locked-mode to check; no package-source
# mapping; and an empty $(IdentityModelVersion) — so the Microsoft.IdentityModel family the token
# pipeline runs on lost its single-version pin and fell back to whatever a transitive reference
# happened to ask for. The image people actually pull was the one build in the repository not covered
# by any of it. Copied with the csprojs so the restore layer still caches on them alone.
COPY *.slnx Directory.Build.props NuGet.Config ./
COPY src/Authagonal.Core/*.csproj src/Authagonal.Core/packages.lock.json src/Authagonal.Core/
COPY src/Authagonal.Protocol/*.csproj src/Authagonal.Protocol/packages.lock.json src/Authagonal.Protocol/
COPY src/Authagonal.AzureProvider/*.csproj src/Authagonal.AzureProvider/packages.lock.json src/Authagonal.AzureProvider/
COPY src/Authagonal.Server/*.csproj src/Authagonal.Server/packages.lock.json src/Authagonal.Server/
# --locked-mode: this image is the identity provider people run. A plain restore re-resolves every
# floating dependency (10.*, 12.*) at build time and rewrites the lock file to match, so the image
# published under a release tag could contain a package graph that exists in no commit. Locked mode
# makes a drifted graph a build failure instead of a silent substitution. Passed explicitly because a
# docker build does not inherit the runner's CI=true, so the Directory.Build.props condition that
# covers restores on the runner does not reach inside this stage.
RUN dotnet restore src/Authagonal.Server/ --locked-mode
COPY src/ src/
RUN dotnet publish src/Authagonal.Server/ -f net10.0 -c Release -o /app/publish --no-restore

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94
WORKDIR /app
COPY --from=backend /app/publish .
COPY --from=frontend /app/login-app/dist-spa ./wwwroot/
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
# Runs as a non-root user. The image ran as root, so a remote-code-execution bug anywhere in the
# identity provider began with uid 0 inside the container — and with it the ability to write the
# application binaries, read every mounted secret, and use any capability the runtime retained.
# The aspnet base image ships this uid; it owns nothing, which is the point.
USER $APP_UID

ENTRYPOINT ["dotnet", "Authagonal.Server.dll"]
