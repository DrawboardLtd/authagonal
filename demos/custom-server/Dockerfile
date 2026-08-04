# Stage 1: Build the custom login SPA (packages come from npm)
FROM node:24-alpine@sha256:f70403e87646dc51b45295f4b8b70cdad0b63d2297c4c9899119b03f7af7a6b3 AS frontend
WORKDIR /app
COPY demos/custom-server/login-app/package*.json ./
# ci, not install: install ignores the committed lockfile and is free to resolve a different tree
# than the one that was reviewed — in a build that produces a login page, that is a supply-chain
# decision made by whatever was published most recently.
RUN npm ci
COPY demos/custom-server/login-app/ ./
RUN npm run build

# Stage 2: Build .NET (packages come from nuget.org, with the just-released
# Authagonal.* nupkgs preferred from ./nupkgs when present — see NuGet.Config.
# This avoids the 4–6 min wait for nuget.org indexing during a release build.)
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS backend
WORKDIR /src
COPY NuGet.Config ./
COPY nupkgs/ ./nupkgs/
COPY demos/custom-server/CustomAuthServer.csproj demos/custom-server/
# The one place the repo's signatureValidationMode=require has to be lifted. ./nupkgs holds the
# Authagonal.* packages `dotnet pack` produced minutes ago in the same workflow run, and dotnet pack
# does not sign, so a require-mode restore refuses them with NU3004. The exception is scoped to this
# build stage rather than weakened in NuGet.Config, so every other restore in the repository — the
# server image, the release pack, a developer's machine — still requires a signature.
ENV DOTNET_NUGET_SIGNATURE_VERIFICATION=false
RUN dotnet restore demos/custom-server/
COPY demos/custom-server/*.cs demos/custom-server/
COPY demos/custom-server/Services/ demos/custom-server/Services/
COPY demos/custom-server/appsettings.json demos/custom-server/
# Pre-populate wwwroot so the MSBuild target skips npm (already built above)
COPY --from=frontend /app/dist/ demos/custom-server/wwwroot/
RUN dotnet publish demos/custom-server/ -c Release -o /app/publish --no-restore

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:f1126d438ccc359f51cc6d4701a8deae513856cf10f5fe645d29ea6403dcac6b
WORKDIR /app
COPY --from=backend /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
# Runs as a non-root user, for the same reason the server image does. This one is an auth server
# people are invited to point a browser at, so it is the most exposed image published here: without
# this, a remote-code-execution bug in it begins with uid 0 inside the container, able to overwrite
# the application binaries and read every mounted secret. The aspnet base image ships this uid; it
# owns nothing, which is the point.
USER $APP_UID

ENTRYPOINT ["dotnet", "CustomAuthServer.dll"]
