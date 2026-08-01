# Stage 1: Build SPA
FROM node:24-alpine@sha256:f70403e87646dc51b45295f4b8b70cdad0b63d2297c4c9899119b03f7af7a6b3 AS frontend
WORKDIR /app/login-app
COPY login-app/package*.json ./
RUN npm ci
COPY login-app/ ./
RUN npm run build:spa

# Stage 2: Build .NET
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS backend
WORKDIR /src
# Directory.Build.props, NuGet.Config and the lock files are part of the restore input, not
# incidental repo furniture. Without them this stage restored a DIFFERENT graph from every other
# build of the same commit: no lock file, no package-source mapping, and an empty
# $(IdentityModelVersion) — so the Microsoft.IdentityModel family the token pipeline runs on lost its
# single-version pin and fell back to whatever a transitive reference happened to ask for. The image
# people actually pull was the one build in the repository not covered by any of it.
COPY *.slnx Directory.Build.props NuGet.Config ./
COPY src/Authagonal.Core/*.csproj src/Authagonal.Core/packages.lock.json src/Authagonal.Core/
COPY src/Authagonal.Protocol/*.csproj src/Authagonal.Protocol/packages.lock.json src/Authagonal.Protocol/
COPY src/Authagonal.AzureProvider/*.csproj src/Authagonal.AzureProvider/packages.lock.json src/Authagonal.AzureProvider/
COPY src/Authagonal.Server/*.csproj src/Authagonal.Server/packages.lock.json src/Authagonal.Server/
RUN dotnet restore src/Authagonal.Server/
COPY src/ src/
RUN dotnet publish src/Authagonal.Server/ -f net10.0 -c Release -o /app/publish --no-restore

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:1fa23fc4872d95fd71c2833ebe65d7e84a43b2d51a31d119516852f13d9505a7
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
