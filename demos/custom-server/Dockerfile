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
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:ed034a8bf0b24ded0cbbac07e17825d8e9ebfe21e308191d0f7421eaf5ad4664 AS backend
WORKDIR /src
COPY NuGet.Config ./
COPY nupkgs/ ./nupkgs/
COPY demos/custom-server/CustomAuthServer.csproj demos/custom-server/
RUN dotnet restore demos/custom-server/
COPY demos/custom-server/*.cs demos/custom-server/
COPY demos/custom-server/Services/ demos/custom-server/Services/
COPY demos/custom-server/appsettings.json demos/custom-server/
# Pre-populate wwwroot so the MSBuild target skips npm (already built above)
COPY --from=frontend /app/dist/ demos/custom-server/wwwroot/
RUN dotnet publish demos/custom-server/ -c Release -o /app/publish --no-restore

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:1fa23fc4872d95fd71c2833ebe65d7e84a43b2d51a31d119516852f13d9505a7
WORKDIR /app
COPY --from=backend /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "CustomAuthServer.dll"]
