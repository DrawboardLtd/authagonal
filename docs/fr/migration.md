---
layout: default
title: Migration
locale: fr
---

# Migration depuis Duende IdentityServer

Authagonal inclut un outil de migration pour passer de Duende IdentityServer + SQL Server a Azure Table Storage.

## Executer la migration

```bash
docker run authagonal-migration \
  --Source:ConnectionString "Server=sql.example.com;Database=Identity;User Id=...;Password=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

(Pas de separateur `--` apres le nom de l'image : tout ce qui suit est transmis directement a l'outil, et un `--` isole casse l'analyse des options.)

Ou depuis les sources :

```bash
dotnet run --project tools/Authagonal.Migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] [--MigrateRefreshTokens true]
```

## Ce qui est migre

| Source (SQL Server) | Cible (Table Storage) | Notes |
|---|---|---|
| `AspNetUsers` + `AspNetUserClaims` | Users + UserEmails + index de noms | Requete JOIN unique. Claims : given_name, family_name, company, org_id (types surchargeables, voir ci-dessous). Les hashes de mots de passe sont conserves tels quels ; les hashes ASP.NET Identity V3 et BCrypt sont verifies sans modification et migres vers le format PBKDF2 natif d'Authagonal lors de la prochaine connexion reussie. |
| `AspNetUserLogins` | UserLogins (index direct + inverse) | `409 Conflict` = ignorer (idempotent) |
| Duende `SamlProviderConfigurations` | SamlProviders + SsoDomains | Le CSV `AllowedDomains` est divise en enregistrements de domaines SSO individuels |
| Duende `OidcProviderConfigurations` | OidcProviders + SsoDomains | Meme division des domaines |
| Duende `Clients` + tables enfants | Clients | ClientSecrets, GrantTypes, RedirectUris, PostLogoutRedirectUris, Scopes, CorsOrigins sont tous fusionnes dans une seule entite |
| Duende `PersistedGrants` (jetons de rafraichissement) | Grants + GrantsBySubject + GrantsByExpiry | Opt-in via `--MigrateRefreshTokens true`. Uniquement les jetons non expires. Si ignore, les utilisateurs se reconnectent simplement. |

## Options

| Option | Defaut | Description |
|---|---|---|
| `--DryRun` | `false` | Journaliser ce qui serait migre sans ecrire dans le stockage |
| `--MigrateRefreshTokens` | `false` | Inclure les jetons de rafraichissement actifs. Si faux, les utilisateurs se re-authentifient apres le basculement. |
| `--Source:ClaimMap:{claim}` | le nom du claim OIDC lui-meme | Remplace le ClaimType `AspNetUserClaims` lu pour un claim mappe, par exemple `--Source:ClaimMap:given_name=FirstName`. Utilise pour `given_name`, `family_name`, `company`, `org_id`. |

## Idempotence

La migration est idempotente et peut etre executee plusieurs fois en toute securite. Les enregistrements existants sont mis a jour ou ignores, jamais dupliques. Cela vous permet de :

1. Executer la migration des jours avant le basculement
2. Executer une migration delta finale proche du basculement
3. Re-executer en cas de probleme

## Ce qui N'EST PAS migre

Ces fonctionnalites Authagonal n'ont pas d'equivalent Duende et sont vides apres la migration :

- **Roles** : roles RBAC et assignations role-utilisateur
- **Identifiants MFA** : inscriptions TOTP, WebAuthn et codes de recuperation
- **Jetons et groupes SCIM** : configuration du provisionnement SCIM
- **Provisions utilisateur** : etat de provisionnement des applications en aval TCC

Les utilisateurs devront se reinscrire a la MFA si la `MfaPolicy` de votre client est `Enabled` ou `Required`.

## Migration de la cle de signature

Pas encore automatisee. Pour garder les jetons existants valides lors du basculement :

1. Exportez la cle de signature RSA depuis Duende (typiquement dans appsettings en Base64 PKCS8)
2. Importez-la dans la table `SigningKeys`
3. Faites-le proche du moment du basculement

## Strategie de basculement

1. Executez la migration des utilisateurs + fournisseurs + clients (peut etre fait des jours a l'avance)
2. Injectez les configurations clients dans Authagonal
3. Importez la cle de signature (proche du basculement)
4. Optionnel : migrez les jetons de rafraichissement actifs
5. Deployez Authagonal en pre-production, testez
6. Mode maintenance sur l'IdentityServer existant
7. Migration delta finale
8. Bascule DNS (definissez le TTL a 60s au prealable)
9. Surveillez pendant 30 minutes
10. En cas de probleme : rebasculez le DNS (la cle de signature partagee signifie que les jetons fonctionnent sur les deux systemes)
