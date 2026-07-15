---
layout: default
title: Migración
locale: es
---

# Migración desde Duende IdentityServer

Authagonal incluye una herramienta de migración para pasar de Duende IdentityServer + SQL Server a Azure Table Storage.

## Ejecutar la migración

```bash
docker run authagonal-migration \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

(Sin separador `--` después del nombre de la imagen: todo lo que va después se pasa directamente a la herramienta, y un `--` aislado rompe el análisis de las opciones.)

O desde el código fuente:

```bash
dotnet run --project tools/Authagonal.Migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] [--MigrateRefreshTokens true]
```

## Qué se migra

| Origen (SQL Server) | Destino (Table Storage) | Notas |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | Users + UserEmails + índices de nombres | Consulta JOIN única. Claims: given_name, family_name, company, org_id (los tipos se pueden sobrescribir, ver más abajo). Los hashes de contraseñas se conservan tal cual; los hashes de ASP.NET Identity V3 y BCrypt se verifican sin cambios y se actualizan al formato nativo PBKDF2 de Authagonal en el siguiente inicio de sesión exitoso. |
| `AspNetUserLogins` | UserLogins (índice directo + inverso) | `409 Conflict` = omitir (idempotente) |
| Duende `SamlProviderConfigurations` | SamlProviders + SsoDomains | El CSV `AllowedDomains` se divide en registros de dominios SSO individuales |
| Duende `OidcProviderConfigurations` | OidcProviders + SsoDomains | Misma división de dominios |
| Duende `Clients` + tablas hijas | Clients | ClientSecrets, GrantTypes, RedirectUris, PostLogoutRedirectUris, Scopes, CorsOrigins se fusionan en una sola entidad |
| Duende `PersistedGrants` (tokens de actualización) | Grants + GrantsBySubject + GrantsByExpiry | Opt-in mediante `--MigrateRefreshTokens true`. Solo tokens no expirados. Si se omite, los usuarios simplemente vuelven a iniciar sesión. |

## Opciones

| Opción | Predeterminado | Descripción |
|---|---|---|
| `--DryRun` | `false` | Registrar lo que se migraría sin escribir en el almacenamiento |
| `--MigrateRefreshTokens` | `false` | Incluir tokens de actualización activos. Si es falso, los usuarios se re-autentican después del cambio. |
| `--Source:ClaimMap:{claim}` | el propio nombre del claim OIDC | Sobrescribe el ClaimType de `AspNetUserClaims` que se lee para un claim mapeado, por ejemplo `--Source:ClaimMap:given_name=FirstName`. Se usa para `given_name`, `family_name`, `company`, `org_id`. |

## Idempotencia

La migración es idempotente y es seguro ejecutarla múltiples veces. Los registros existentes se actualizan o se omiten, nunca se duplican. Esto le permite:

1. Ejecutar la migración días antes del cambio
2. Ejecutar una migración delta final cercana al cambio
3. Re-ejecutar si algo sale mal

## Qué NO se migra

Estas funcionalidades de Authagonal no tienen equivalente en Duende y comienzan vacías después de la migración:

- **Roles**: roles RBAC y asignaciones de roles a usuarios
- **Credenciales MFA**: inscripciones de TOTP, WebAuthn y códigos de recuperación
- **Tokens y grupos SCIM**: configuración de aprovisionamiento SCIM
- **Provisiones de usuarios**: estado de aprovisionamiento de aplicaciones posteriores TCC

Los usuarios deberán volver a inscribir MFA si la `MfaPolicy` de su cliente es `Enabled` o `Required`.

## Migración de la clave de firma

Aún no automatizada. Para mantener los tokens existentes válidos durante el cambio:

1. Exporte la clave de firma RSA desde Duende (típicamente en appsettings como Base64 PKCS8)
2. Impórtela en la tabla `SigningKeys`
3. Hágalo cercano al momento del cambio

## Estrategia de cambio

1. Ejecute la migración de usuarios + proveedores + clientes (puede hacerse días antes)
2. Inyecte las configuraciones de clientes en Authagonal
3. Importe la clave de firma (cercano al cambio)
4. Opcional: migre los tokens de actualización activos
5. Despliegue Authagonal en staging, pruebe
6. Modo de mantenimiento en el IdentityServer existente
7. Migración delta final
8. Cambio de DNS (establezca el TTL a 60s de antemano)
9. Monitoree durante 30 minutos
10. Si hay problemas: revierta el DNS (la clave de firma compartida significa que los tokens funcionan en ambos sistemas)
