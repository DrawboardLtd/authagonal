FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine AS build
WORKDIR /src
COPY tools/Authagonal.Backup/Authagonal.Backup.csproj tools/Authagonal.Backup/
RUN dotnet restore tools/Authagonal.Backup/Authagonal.Backup.csproj
COPY tools/Authagonal.Backup/ tools/Authagonal.Backup/
RUN dotnet publish tools/Authagonal.Backup/Authagonal.Backup.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["./authagonal-backup"]
