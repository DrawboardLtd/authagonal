FROM mcr.microsoft.com/dotnet/sdk:10.0-alpine@sha256:d8ee39817ca03a3757288e83c37ed73cc969a286c603b827c7cbe33add1c2d1c AS build
WORKDIR /src
COPY tools/Authagonal.Backup/Authagonal.Backup.csproj tools/Authagonal.Backup/
RUN dotnet restore tools/Authagonal.Backup/Authagonal.Backup.csproj
COPY tools/Authagonal.Backup/ tools/Authagonal.Backup/
RUN dotnet publish tools/Authagonal.Backup/Authagonal.Backup.csproj -c Release -o /app

FROM mcr.microsoft.com/dotnet/runtime:10.0-alpine@sha256:9297ae3050f7e80428142f784a6d016f80bc8b5cd54e7fc1b596935911b1e58e
WORKDIR /app
COPY --from=build /app .

# Runs as a non-root user. This image is handed a storage connection string that can read every
# table in the identity provider and it writes the result to disk in the clear, so it is the last
# image that should start as uid 0: a write primitive here otherwise rewrites the tool's own binary
# in place, and the next scheduled backup runs the attacker's code against those credentials.
#
# The default --output is ./backups, which uid 1654 cannot create under a root-owned /app, so the
# directory is made for it here. A mounted output volume has to be writable by 1654 too.
RUN mkdir -p /app/backups && chown $APP_UID /app/backups
USER $APP_UID

ENTRYPOINT ["./authagonal-backup"]
