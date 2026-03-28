# Stage 1: Build the base login-app (needed as local npm dependency)
FROM node:22-alpine AS login-lib
WORKDIR /app/login-app
COPY login-app/package*.json ./
RUN npm ci
COPY login-app/ ./
RUN npm run build

# Stage 2: Build the custom login SPA
FROM node:22-alpine AS frontend
WORKDIR /app
COPY --from=login-lib /app/login-app /app/login-app
WORKDIR /app/demos/custom-server/login-app
COPY demos/custom-server/login-app/package*.json ./
RUN npm ci
COPY demos/custom-server/login-app/ ./
RUN npm run build

# Stage 3: Build .NET
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY *.slnx ./
COPY src/Authagonal.Core/*.csproj src/Authagonal.Core/
COPY src/Authagonal.Storage/*.csproj src/Authagonal.Storage/
COPY src/Authagonal.Server/*.csproj src/Authagonal.Server/
COPY demos/custom-server/CustomAuthServer.csproj demos/custom-server/
RUN dotnet restore demos/custom-server/
COPY src/ src/
COPY demos/custom-server/*.cs demos/custom-server/
COPY demos/custom-server/Services/ demos/custom-server/Services/
COPY demos/custom-server/appsettings.json demos/custom-server/
# Pre-populate wwwroot so the MSBuild target skips npm (already built above)
COPY --from=frontend /app/demos/custom-server/login-app/dist/ demos/custom-server/wwwroot/
RUN dotnet publish demos/custom-server/ -c Release -o /app/publish --no-restore

# Stage 4: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=backend /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "CustomAuthServer.dll"]
