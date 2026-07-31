FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:d8ee39817ca03a3757288e83c37ed73cc969a286c603b827c7cbe33add1c2d1c AS build
WORKDIR /src
COPY tools/Authagonal.Backup/Authagonal.Backup.csproj tools/Authagonal.Backup/
RUN dotnet restore tools/Authagonal.Backup/Authagonal.Backup.csproj
COPY tools/Authagonal.Backup/ tools/Authagonal.Backup/
RUN dotnet publish tools/Authagonal.Backup/Authagonal.Backup.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine@sha256:9297ae3050f7e80428142f784a6d016f80bc8b5cd54e7fc1b596935911b1e58e
WORKDIR /app
COPY --from=build /app .
ENTRYPOINT ["./authagonal-backup"]
