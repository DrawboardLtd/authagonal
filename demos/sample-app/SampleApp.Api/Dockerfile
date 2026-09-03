# Stage 1: Build .NET API
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:e1fc6e423f543119c406d24e2e687d67c569f18f04a37a8b0005d80ad0dcee80 AS backend
WORKDIR /src
COPY demos/sample-app/SampleApp.Api/*.csproj ./
RUN dotnet restore
COPY demos/sample-app/SampleApp.Api/ ./
RUN dotnet publish -c Release -o /app/publish --no-restore

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:a4556ed033fa96f984bb7a8d348851cb2d36b1281dd2420070045f664fbb5f94
WORKDIR /app
COPY --from=backend /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
# Runs as a non-root user, for the same reason the server image does: without this, code execution
# in the demo API begins with uid 0 inside the container, able to overwrite the application binaries
# and read every mounted secret. The aspnet base image ships this uid; it owns nothing.
USER $APP_UID

ENTRYPOINT ["dotnet", "SampleApp.Api.dll"]
