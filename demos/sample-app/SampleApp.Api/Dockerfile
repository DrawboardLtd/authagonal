# Stage 1: Build .NET API
FROM mcr.microsoft.com/dotnet/sdk:10.0@sha256:72dd743782f2ae7e5476fd64f6a460045e3998dc862218b80e6944cba79a01b0 AS backend
WORKDIR /src
COPY demos/sample-app/SampleApp.Api/*.csproj ./
RUN dotnet restore
COPY demos/sample-app/SampleApp.Api/ ./
RUN dotnet publish -c Release -o /app/publish --no-restore

# Stage 2: Runtime
FROM mcr.microsoft.com/dotnet/aspnet:10.0@sha256:207cc51496778557731c81ff670333d8ade4a4fec22768fd1be8e78474a84ecf
WORKDIR /app
COPY --from=backend /app/publish .
EXPOSE 8080
ENV ASPNETCORE_URLS=http://+:8080
# Runs as a non-root user, for the same reason the server image does: without this, code execution
# in the demo API begins with uid 0 inside the container, able to overwrite the application binaries
# and read every mounted secret. The aspnet base image ships this uid; it owns nothing.
USER $APP_UID

ENTRYPOINT ["dotnet", "SampleApp.Api.dll"]
