# Stage 1: Build .NET API
FROM mcr.microsoft.com/dotnet/sdk:10.0 AS backend
WORKDIR /src
COPY demos/sample-app/SampleApp.Api/*.csproj ./
RUN dotnet restore
COPY demos/sample-app/SampleApp.Api/ ./
RUN dotnet publish -c Release -o /app/publish --no-restore

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0
WORKDIR /app
COPY --from=backend /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
ENTRYPOINT ["dotnet", "SampleApp.Api.dll"]
