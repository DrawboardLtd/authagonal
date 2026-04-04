# Stage 1: Build the custom login SPA (packages come from npm)
FROM node:24-alpine AS frontend
WORKDIR /app
COPY demos/custom-server/login-app/package.json ./
RUN npm install
COPY demos/custom-server/login-app/ ./
RUN npm run build

# Stage 2: Build .NET (packages come from nuget.org)
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY demos/custom-server/CustomAuthServer.csproj demos/custom-server/
RUN dotnet restore demos/custom-server/
COPY demos/custom-server/*.cs demos/custom-server/
COPY demos/custom-server/Services/ demos/custom-server/Services/
COPY demos/custom-server/appsettings.json demos/custom-server/
# Pre-populate wwwroot so the MSBuild target skips npm (already built above)
COPY --from=frontend /app/dist/ demos/custom-server/wwwroot/
RUN dotnet publish demos/custom-server/ -c Release -o /app/publish --no-restore

# Stage 3: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=backend /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "CustomAuthServer.dll"]
