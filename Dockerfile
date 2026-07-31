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
COPY src/Authagonal.Server/*.csproj src/Authagonal.Server/
RUN dotnet restore src/Authagonal.Server/
COPY src/ src/
RUN dotnet publish src/Authagonal.Server/ -f net10.0 -c Release -o /app/publish --no-restore

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
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
