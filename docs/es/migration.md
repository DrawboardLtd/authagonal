---
layout: default
title: Migracion
locale: es
---

# Migracion desde Duende IdentityServer

Authagonal incluye una herramienta de migracion para pasar de Duende IdentityServer + SQL Server a Azure Table Storage.

## Ejecutar la migracion

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

O desde el codigo fuente:

```bash
dotnet run --project tools/Authagonal.Migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] [--MigrateRefreshTokens true]
```

## Que se migra

| Origen (SQL Server) | Destino (Table Storage) | Notas |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | Users + UserEmails | Consulta JOIN unica. Claims: given_name, family_name, company, org_id. Los hashes de contrasenas se conservan tal cual (BCrypt se actualiza automaticamente al iniciar sesion). |
| `AspNetUserLogins` | UserLogins (indice directo + inverso) | `409 Conflict` = omitir (idempotente) |
| Duende `SamlProviderConfigurations` | SamlProviders + SsoDomains | El CSV `AllowedDomains` se divide en registros de dominios SSO individuales |
| Duende `OidcProviderConfigurations` | OidcProviders + SsoDomains | Misma division de dominios |
| Duende `Clients` + tablas hijas | Clients | ClientSecrets, GrantTypes, RedirectUris, PostLogoutRedirectUris, Scopes, CorsOrigins se fusionan en una sola entidad |
| Duende `PersistedGrants` (tokens de actualizacion) | Grants + GrantsBySubject | Opt-in mediante `--MigrateRefreshTokens true`. Solo tokens no expirados. Si se omite, los usuarios simplemente vuelven a iniciar sesion. |

## Opciones

| Opcion | Predeterminado | Descripcion |
|---|---|---|
| `--DryRun` | `false` | Registrar lo que se migraria sin escribir en el almacenamiento |
| `--MigrateRefreshTokens` | `false` | Incluir tokens de actualizacion activos. Si es falso, los usuarios se re-autentican despues del cambio. |

## Idempotencia

La migracion es idempotente -- es seguro ejecutarla multiples veces. Los registros existentes se actualizan (upsert) y no se duplican. Esto le permite:

1. Ejecutar la migracion dias antes del cambio
2. Ejecutar una migracion delta final cercana al cambio
3. Re-ejecutar si algo sale mal

## Migracion de la clave de firma

Aun no automatizada. Para mantener los tokens existentes validos durante el cambio:

1. Exporte la clave de firma RSA desde Duende (tipicamente en appsettings como Base64 PKCS8)
2. Importela en la tabla `SigningKeys`
3. Hagalo cercano al momento del cambio

## Estrategia de cambio

1. Ejecute la migracion de usuarios + proveedores + clientes (puede hacerse dias antes)
2. Inyecte las configuraciones de clientes en Authagonal
3. Importe la clave de firma (cercano al cambio)
4. Opcional: migre los tokens de actualizacion activos
5. Despliegue Authagonal en staging, pruebe
6. Modo de mantenimiento en el IdentityServer existente
7. Migracion delta final
8. Cambio de DNS (establezca el TTL a 60s de antemano)
9. Monitoree durante 30 minutos
10. Si hay problemas: revierta el DNS (la clave de firma compartida significa que los tokens funcionan en ambos sistemas)
