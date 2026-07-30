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
RUN dotnet restore src/Authagonal.Host/
COPY src/ src/
RUN dotnet publish src/Authagonal.Host/ -c Release -o /app/publish --no-restore

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=backend /app/publish .
COPY --from=frontend /app/login-app/dist-spa ./wwwroot/
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Authagonal.Host.dll"]
