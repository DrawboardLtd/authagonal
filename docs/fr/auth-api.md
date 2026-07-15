---
layout: default
title: API d'authentification
locale: fr
---

# API d'authentification

Ces points d'accès alimentent la SPA de connexion. Ils utilisent l'authentification par cookie (`SameSite=Lax`, `HttpOnly`).

Si vous construisez une interface de connexion personnalisée, ce sont les points d'accès que vous devez implémenter.

## Points d'accès

### Connexion

```
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**Succès (200) :** Définit un cookie d'authentification et renvoie :

```json
{
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe",
  "mfaAvailable": false
}
```

`mfaAvailable` vaut `true` lorsque la `MfaPolicy` du client est `Enabled` mais que l'utilisateur ne s'est pas encore inscrit (l'interface peut proposer la configuration) ; dans ce cas, un champ `clientId` est également inclus.

**MFA requis (200) :** Si l'utilisateur a inscrit le MFA, il est **toujours** mis au défi, quelle que soit la `MfaPolicy` du client demandeur (le MFA est une propriété de l'utilisateur/de la session, pas du client) :

```json
{
  "mfaRequired": true,
  "challengeId": "a1b2c3...",
  "methods": ["totp", "webauthn", "recoverycode"],
  "webAuthn": { /* PublicKeyCredentialRequestOptions */ }
}
```

Le client doit rediriger vers une page de vérification MFA et appeler `POST /api/auth/mfa/verify`.

**Configuration MFA requise (200) :** Si `MfaPolicy` est `Required` et que l'utilisateur n'a pas de MFA inscrit :

```json
{
  "mfaSetupRequired": true,
  "setupToken": "abc123..."
}
```

Le client doit rediriger vers une page de configuration MFA. Le jeton de configuration authentifie l'utilisateur auprès des points d'accès de configuration MFA via l'en-tête `X-MFA-Setup-Token`.

**Réponses d'erreur :**

| `error` | Statut | Description |
|---|---|---|
| `invalid_credentials` | 401 | Email ou mot de passe incorrect. Délibérément identique pour les emails inconnus (anti-énumération). |
| `locked_out` | 423 | Trop de tentatives échouées. `retryAfter` (secondes) est inclus. |
| `account_disabled` | 403 | Le compte est désactivé (révélé uniquement après un mot de passe correct) |
| `email_not_confirmed` | 403 | Email pas encore vérifié (révélé uniquement après un mot de passe correct) |
| `sso_required` | 409 | Le domaine requiert SSO. `redirectUrl` pointe vers la connexion SSO. |
| `captcha_failed` | 400 | La vérification Turnstile a échoué (uniquement lorsque Turnstile est configuré ; les requêtes doivent alors comporter un champ `turnstileToken`) |
| `email_required` | 400 | Le champ email est vide |
| `password_required` | 400 | Le champ mot de passe est vide |

### Inscription

```
POST /api/auth/register
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "SecurePass1!",
  "firstName": "Jane",
  "lastName": "Doe"
}
```

Crée un nouveau compte utilisateur et envoie un email de vérification. Renvoie `201 { "success": true, "userId": "..." }`. Champs facultatifs : `locale` (étiquette BCP-47 conservée sur l'utilisateur) et `customAttributes` (un dictionnaire de chaînes).

L'inscription est délibérément **neutre vis-à-vis de l'énumération** : si l'email est déjà enregistré, la réponse est le même `201` neutre (avec un `userId` jetable) et le véritable propriétaire reçoit à la place un email de notification de connexion/réinitialisation. L'inscription est également limitée en débit par IP, `429 rate_limited` en cas de dépassement (fenêtre et plafond configurables via `Auth:MaxRegistrationsPerIp` / `Auth:RegistrationWindowMinutes`).

### Confirmer l'email

```
GET  /api/auth/confirm-email?token={token}
POST /api/auth/confirm-email?token={token}
```

Confirme l'adresse email de l'utilisateur à l'aide du jeton de l'email de vérification. `GET` est le lien cliquable de l'email, il redirige vers `/login?email_confirmed=1` (plus un paramètre `continue_client` lorsque l'inscription provient d'un flux OAuth). `POST` est la voie programmatique et renvoie du JSON (le jeton peut aussi être fourni dans un corps JSON sous la forme `{ "token": "..." }`) ; la réponse inclut un `appLink` facultatif (cible « continuer vers l'application »).

### Fournisseurs

```
GET /api/auth/providers
```

Renvoie la liste des fournisseurs d'identité externes configurés (pour afficher les boutons SSO) :

```json
{
  "providers": [
    { "connectionId": "google", "name": "Google", "type": "oidc", "iconUrl": null, "loginUrl": "/oidc/google/login" }
  ],
  "turnstileSiteKey": null
}
```

Les connexions dont les `AllowedDomains` sont configurés sont **exclues** : celles-ci sont atteintes en priorité par email via `/api/auth/sso-check` plutôt que par un bouton. `turnstileSiteKey` est défini lorsque Cloudflare Turnstile est configuré (l'interface de connexion doit alors envoyer un `turnstileToken` avec les requêtes de connexion/inscription/mot de passe).

### Déconnexion

```
POST /api/auth/logout
```

Efface le cookie d'authentification. Renvoie `200 { success: true }`.

### Mot de passe oublié

```
POST /api/auth/forgot-password
Content-Type: application/json

{
  "email": "user@example.com"
}
```

Renvoie toujours `200` (anti-énumération). Si l'utilisateur existe, un email de réinitialisation est envoyé.

### Réinitialisation du mot de passe

```
POST /api/auth/reset-password
Content-Type: application/json

{
  "token": "base64-encoded-token",
  "newPassword": "NewSecurePass1!"
}
```

| `error` | Description |
|---|---|
| `weak_password` | Ne répond pas aux exigences de robustesse |
| `invalid_token` | Le jeton est mal formé |
| `token_expired` | Le jeton a expiré (validité par défaut de 60 minutes, configurable via `Auth:PasswordResetExpiryMinutes`) |

### Session

```
GET /api/auth/session
```

Renvoie les informations de session en cours si authentifié :

```json
{
  "authenticated": true,
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

Renvoie `401` si non authentifié.

### Applications

```
GET /api/auth/apps
```

Renvoie les liens d'application du tenant pour le lanceur « retour vers l'application » de la page de compte : les clients activés qui ont un URI d'accueil (`initiateLoginUri` prioritaire sur `clientUri`). Chaque entrée est `{ clientId, clientName, homeUri, logoUri, isDefault }` ; exactement une application est marquée par défaut (le client signalé, ou le seul client possédant un URI d'accueil). Nécessite l'authentification par cookie.

### Profil (libre-service)

```
GET   /api/auth/profile
PATCH /api/auth/profile
```

L'utilisateur authentifié lit/met à jour ses propres champs de profil non sensibles : `firstName`, `lastName`, `companyName`, `phone`, `locale`. Les champs nuls restent inchangés ; l'email, le mot de passe, les rôles, l'état actif et l'organisation ne sont **pas** modifiables ici. Les deux renvoient le profil `{ email, emailConfirmed, firstName, lastName, companyName, phone, locale }`.

### Vérification SSO

```
GET /api/auth/sso-check?email=user@acme.com
```

Vérifie si le domaine de l'email requiert SSO :

```json
{
  "ssoRequired": true,
  "providerType": "saml",
  "connectionId": "acme-azure",
  "redirectUrl": "/saml/acme-azure/login"
}
```

Si le SSO n'est pas requis :

```json
{
  "ssoRequired": false
}
```

### Politique de mot de passe

```
GET /api/auth/password-policy
```

Renvoie les exigences de mot de passe du serveur (configurées via `PasswordPolicy` dans les paramètres) :

```json
{
  "rules": [
    { "rule": "minLength", "value": 8, "label": "At least 8 characters" },
    { "rule": "uppercase", "value": null, "label": "Uppercase letter" },
    { "rule": "lowercase", "value": null, "label": "Lowercase letter" },
    { "rule": "digit", "value": null, "label": "Number" },
    { "rule": "specialChar", "value": null, "label": "Special character" }
  ]
}
```

L'interface de connexion par défaut récupère ce point d'accès sur la page de réinitialisation du mot de passe pour afficher les exigences dynamiquement.

## Exigences de mot de passe par défaut

Avec la configuration par défaut, les mots de passe doivent satisfaire toutes ces conditions :

- Au moins 8 caractères
- Au moins une lettre majuscule
- Au moins une lettre minuscule
- Au moins un chiffre
- Au moins un caractère non alphanumérique
- Au moins 2 caractères distincts

Celles-ci peuvent être personnalisées via la section de configuration `PasswordPolicy`, voir [Configuration](configuration).

## Points d'accès MFA

### Vérification MFA

```
POST /api/auth/mfa/verify
Content-Type: application/json

{
  "challengeId": "a1b2c3...",
  "method": "totp",
  "code": "123456"
}
```

Vérifie un défi MFA. En cas de succès, définit le cookie d'authentification et renvoie les informations de l'utilisateur.

**Méthodes :**

| `method` | Champs requis | Description |
|---|---|---|
| `totp` | `code` (6 chiffres) | Mot de passe à usage unique basé sur le temps depuis une application d'authentification |
| `webauthn` | `assertion` (chaîne JSON) | Réponse d'assertion WebAuthn depuis `navigator.credentials.get()` |
| `recovery` | `code` (`XXXX-XXXX`) | Code de récupération à usage unique (consommé lors de l'utilisation) |

**Sémantique de nouvelle tentative :** un code erroné ne **consomme pas** le défi, le code est validé d'abord et le défi n'est consommé qu'en cas de succès, de sorte que l'utilisateur peut réessayer avec le même `challengeId` après une faute de frappe (`401 invalid_code` / `assertion_failed`). Chaque défi tolère **5 tentatives échouées** ; le 5e échec le consomme et renvoie `401 too_many_attempts`, forçant une nouvelle connexion (cela borne la force brute TOTP à 5 essais par défi). Les défis expirent également (par défaut 5 minutes, `Auth:MfaChallengeExpiryMinutes`) ; un `challengeId` expiré, inconnu ou déjà consommé renvoie `invalid_challenge`. Les codes TOTP sont en outre protégés contre le rejeu, un code provenant d'un pas de temps déjà utilisé est rejeté.

### Statut MFA

```
GET /api/auth/mfa/status
```

Renvoie les méthodes MFA inscrites de l'utilisateur. Nécessite l'authentification par cookie ou l'en-tête `X-MFA-Setup-Token`.

```json
{
  "enabled": true,
  "offered": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

`offered` vaut `false` lorsque la `MfaPolicy` de chaque client est `Disabled` : le tenant a désactivé le MFA, l'interface de configuration peut donc se masquer. Les entrées de code de récupération portent en plus `isConsumed`.

### Configuration TOTP

```
POST /api/auth/mfa/totp/setup
→ { "setupToken": "...", "qrCodeDataUri": "data:image/png;base64,...", "manualKey": "BASE32..." }

POST /api/auth/mfa/totp/confirm
{ "setupToken": "...", "code": "123456" }
→ { "success": true }
```

### Configuration WebAuthn / Passkey

```
POST /api/auth/mfa/webauthn/setup
→ { "setupToken": "...", "options": { /* PublicKeyCredentialCreationOptions */ } }

POST /api/auth/mfa/webauthn/confirm
{ "setupToken": "...", "attestationResponse": "..." }
→ { "success": true, "credentialId": "..." }
```

L'inscription d'un passkey requiert **d'abord un identifiant TOTP confirmé** (`400 totp_required_first`) : les passkeys sont un confort par appareil superposé à un facteur de base portable, de sorte qu'un compte ne peut jamais se retrouver uniquement en passkey et verrouillé à un appareil. Les utilisateurs dont le domaine d'email est routé vers le SSO ne peuvent pas inscrire de passkey local (`400 sso_managed`), cela contournerait l'IdP du tenant. Un identifiant déjà enregistré pour un autre utilisateur est rejeté avec `409 credential_already_registered`.

### Codes de récupération

```
POST /api/auth/mfa/recovery/generate
→ { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

Génère 10 codes de récupération à usage unique. Nécessite qu'au moins une méthode principale (TOTP ou WebAuthn) soit inscrite. La régénération remplace tous les codes de récupération existants.

### Supprimer un identifiant MFA

```
DELETE /api/auth/mfa/credentials/{credentialId}
→ { "success": true }
```

Supprime un identifiant MFA spécifique. Si la dernière méthode principale est supprimée, le MFA est désactivé pour l'utilisateur. Nécessite une véritable session par cookie, un jeton de configuration est rejeté avec `403 session_required` (les jetons de configuration n'existent que pour ajouter un premier facteur, jamais pour rétrograder le MFA).

### Connexion Passkey sans mot de passe

```
POST /api/auth/mfa/passwordless/begin
→ { "challengeId": "...", "options": { /* PublicKeyCredentialRequestOptions */ } }

POST /api/auth/mfa/passwordless/complete
{ "challengeId": "...", "assertion": "..." }
→ { "userId": "...", "email": "...", "name": "..." }
```

Connexion par identifiant découvrable (passkey résident) sans contexte utilisateur préalable : `begin` émet un défi d'assertion avec une liste `allowCredentials` vide, et `complete` résout l'utilisateur **à partir** du passkey choisi, vérifie l'assertion et le connecte (la session porte le marqueur MFA, un passkey est une authentification forte résistante à l'hameçonnage). Si le domaine d'email de l'utilisateur résolu est routé vers le SSO, la connexion est refusée avec `409 sso_required` + `redirectUrl` afin qu'un passkey local ne puisse pas contourner un IdP imposé.

## Autorisation d'appareil (RFC 8628)

### Demander un code d'appareil

```
POST /connect/deviceauthorization
Content-Type: application/x-www-form-urlencoded

client_id=my-cli&scope=openid+profile
```

Renvoie un code d'appareil, un code utilisateur et un URI de vérification :

```json
{
  "device_code": "abc123...",
  "user_code": "ABCD-EFGH",
  "verification_uri": "https://auth.example.com/device",
  "verification_uri_complete": "https://auth.example.com/device?user_code=ABCD-EFGH",
  "expires_in": 300,
  "interval": 5
}
```

`expires_in` provient du `DeviceCodeLifetimeSeconds` du client (par défaut 300). L'appareil affiche le `verification_uri` et le `user_code` à l'utilisateur, puis interroge le point d'accès de token avec le `device_code`, à un intervalle jamais inférieur à `interval` secondes, sans quoi le point d'accès de token répond `slow_down` (RFC 8628 §3.5). Tant que l'utilisateur n'a pas encore approuvé, le point d'accès de token renvoie `authorization_pending`. L'utilisateur visite l'URI de vérification, se connecte et saisit le code utilisateur pour approuver.

### Approuver l'appareil

```
POST /api/auth/device/approve
Content-Type: application/json

{
  "userCode": "ABCD-EFGH"
}
```

Nécessite l'authentification par cookie. Approuve le code d'appareil pour l'utilisateur actuel. L'appareil peut ensuite échanger le code d'appareil contre des tokens via le point d'accès de token en utilisant le type de Grant `urn:ietf:params:oauth:grant-type:device_code`.

## Introspection de Token (RFC 7662)

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(client_id:client_secret)

token=eyJhbGci...
```

Ou avec des identifiants encodés dans le formulaire :

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded

token=eyJhbGci...&client_id=my-app&client_secret=secret
```

Renvoie les métadonnées du token :

```json
{
  "active": true,
  "sub": "user-id",
  "client_id": "my-app",
  "scope": "openid profile",
  "iss": "https://auth.example.com",
  "exp": 1234567890,
  "iat": 1234567890,
  "token_type": "Bearer"
}
```

Les tokens inactifs ou invalides renvoient `{ "active": false }`. Prend en charge à la fois les access tokens JWT et les refresh tokens opaques.

## Points d'accès de consentement

### Informations de consentement

```
GET /consent/info?client_id=my-app&scope=openid%20profile%20email
```

Renvoie les détails du client et les Scopes demandés pour la page de consentement (`scope` vaut `openid` par défaut lorsqu'il est omis) :

```json
{
  "clientId": "my-app",
  "clientName": "My Application",
  "description": null,
  "clientUri": null,
  "logoUri": null,
  "scopes": ["openid", "profile", "email"]
}
```

Renvoie `404 client_not_found` pour un client inconnu.

### Soumettre le consentement

```
POST /consent
Content-Type: application/json

{
  "clientId": "my-app",
  "decision": "allow",
  "scopes": ["openid", "profile", "email"],
  "returnUrl": "/connect/authorize?..."
}
```

Enregistre la décision de consentement de l'utilisateur (nécessite l'authentification par cookie) et renvoie `{ "redirect": "..." }` vers lequel la SPA doit naviguer. En cas d'autorisation, les Scopes accordés sont conservés (filtrés selon les `AllowedScopes` du client, un corps falsifié ne peut pas enregistrer des Scopes que le client ne pouvait pas demander) et la redirection ramène vers le flux d'autorisation. Sur `"decision": "deny"`, la redirection pointe vers le `redirect_uri` du client avec une erreur `access_denied`.

### Lister les Grants

```
GET /consent/grants
```

Renvoie toutes les applications que l'utilisateur a autorisées :

```json
[
  {
    "clientId": "my-app",
    "clientName": "My Application",
    "scopes": ["openid", "profile", "email"],
    "consentedAt": "2026-04-09T12:00:00Z"
  }
]
```

### Révoquer un Grant

```
DELETE /consent/grants/{clientId}
```

Révoque le consentement pour une application spécifique. L'utilisateur sera invité à consentir de nouveau lors de sa prochaine connexion.

## Construire une interface de connexion personnalisée

La SPA par défaut (`login-app/`) est une implémentation de cette API. Pour construire la vôtre :

1. Servez votre interface aux chemins `/login`, `/forgot-password`, `/reset-password`, `/consent`, `/device`
2. Le point d'accès d'autorisation redirige les utilisateurs non authentifiés vers `/login?returnUrl={encoded-authorize-url}`
3. Après une connexion réussie (cookie défini), redirigez l'utilisateur vers le `returnUrl`
4. Les liens de réinitialisation de mot de passe utilisent `{Issuer}/login/reset-password?p={token}` (la SPA de connexion est montée sous `/login`)

Votre interface doit être servie depuis la **même origine** que l'API parce que :
- L'authentification par cookie utilise `SameSite=Lax` + `HttpOnly`
- Le point d'accès d'autorisation redirige vers `/login` (relatif)
- Les liens de réinitialisation utilisent `{Issuer}/login/reset-password`
