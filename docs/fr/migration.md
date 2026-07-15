---
layout: default
title: Migration
locale: fr
---

# Migration depuis Duende IdentityServer

Authagonal inclut un outil de migration pour passer de Duende IdentityServer + SQL Server à Azure Table Storage.

## Exécuter la migration

```bash
docker run authagonal-migration \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

(Pas de séparateur `--` après le nom de l'image : tout ce qui suit est transmis directement à l'outil, et un `--` isolé casse l'analyse des options.)

Ou depuis les sources :

```bash
dotnet run --project tools/Authagonal.Migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] [--MigrateRefreshTokens true]
```

## Ce qui est migré

| Source (SQL Server) | Cible (Table Storage) | Notes |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | Users + UserEmails + index de noms | Requête JOIN unique. Claims : given_name, family_name, company, org_id (types surchargeables, voir ci-dessous). Les hashes de mots de passe sont conservés tels quels ; les hashes ASP.NET Identity V3 et BCrypt sont vérifiés sans modification et migrent vers le format PBKDF2 natif d'Authagonal lors de la prochaine connexion réussie. |
| `AspNetUserLogins` | UserLogins (index direct + inverse) | `409 Conflict` = ignorer (idempotent) |
| Duende `SamlProviderConfigurations` | SamlProviders + SsoDomains | Le CSV `AllowedDomains` est divisé en enregistrements de domaines SSO individuels |
| Duende `OidcProviderConfigurations` | OidcProviders + SsoDomains | Même division des domaines |
| Duende `Clients` + tables enfants | Clients | ClientSecrets, GrantTypes, RedirectUris, PostLogoutRedirectUris, Scopes, CorsOrigins sont tous fusionnés dans une seule entité |
| Duende `PersistedGrants` (jetons de rafraîchissement) | Grants + GrantsBySubject + GrantsByExpiry | Opt-in via `--MigrateRefreshTokens true`. Uniquement les jetons non expirés. Si ignoré, les utilisateurs se reconnectent simplement. |

## Options

| Option | Défaut | Description |
|---|---|---|
| `--DryRun` | `false` | Journaliser ce qui serait migré sans écrire dans le stockage |
| `--MigrateRefreshTokens` | `false` | Inclure les jetons de rafraîchissement actifs. Si faux, les utilisateurs se ré-authentifient après le basculement. |
| `--Source:ClaimMap:{claim}` | le nom du claim OIDC lui-même | Remplace le ClaimType `AspNetUserClaims` lu pour un claim mappé, par exemple `--Source:ClaimMap:given_name=FirstName`. Utilisé pour `given_name`, `family_name`, `company`, `org_id`. |

## Idempotence

La migration est idempotente et peut être exécutée plusieurs fois en toute sécurité. Les enregistrements existants sont mis à jour ou ignorés, jamais dupliqués. Cela vous permet de :

1. Exécuter la migration des jours avant le basculement
2. Exécuter une migration delta finale proche du basculement
3. Ré-exécuter en cas de problème

## Ce qui N'EST PAS migré

Ces fonctionnalités Authagonal n'ont pas d'équivalent Duende et démarrent vides après la migration :

- **Rôles** : rôles RBAC et affectations rôle-utilisateur
- **Identifiants MFA** : inscriptions TOTP, WebAuthn et codes de récupération
- **Jetons et groupes SCIM** : configuration du provisionnement SCIM
- **Provisions utilisateur** : état de provisionnement des applications en aval TCC

Les utilisateurs devront se réinscrire à la MFA si la `MfaPolicy` de votre client est `Enabled` ou `Required`.

## Migration de la clé de signature

Pas encore automatisée. Pour conserver la validité des jetons existants lors du basculement :

1. Exportez la clé de signature RSA depuis Duende (typiquement dans appsettings en Base64 PKCS8)
2. Importez-la dans la table `SigningKeys`
3. Faites-le proche du moment du basculement

## Stratégie de basculement

1. Exécutez la migration des utilisateurs + fournisseurs + clients (peut être fait des jours à l'avance)
2. Injectez les configurations clients dans Authagonal
3. Importez la clé de signature (proche du basculement)
4. Optionnel : migrez les jetons de rafraîchissement actifs
5. Déployez Authagonal en pré-production, testez
6. Mode maintenance sur l'IdentityServer existant
7. Migration delta finale
8. Bascule DNS (définissez le TTL à 60s au préalable)
9. Surveillez pendant 30 minutes
10. En cas de problème : rebasculez le DNS (la clé de signature partagée signifie que les jetons fonctionnent sur les deux systèmes)
