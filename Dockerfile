# Stage 1: Build SPA
FROM node:24-alpine AS frontend
WORKDIR /app/login-app
COPY login-app/package*.json ./
RUN npm ci
COPY login-app/ ./
RUN npm run build

# Stage 2: Build .NET
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY *.slnx ./
COPY src/Authagonal.Core/*.csproj src/Authagonal.Core/
COPY src/Authagonal.Storage/*.csproj src/Authagonal.Storage/
COPY src/Authagonal.Server/*.csproj src/Authagonal.Server/
RUN dotnet restore src/Authagonal.Server/
COPY src/ src/
RUN dotnet publish src/Authagonal.Server/ -c Release -o /app/publish --no-restore

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=backend /app/publish .
COPY --from=frontend /app/login-app/dist ./wwwroot/
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "Authagonal.Server.dll"]
