---
layout: default
title: API d'authentification
locale: fr
---

# API d'authentification

Ces points d'acces alimentent la SPA de connexion. Ils utilisent l'authentification par cookie (`SameSite=Lax`, `HttpOnly`).

Si vous construisez une interface de connexion personnalisee, ce sont les points d'acces que vous devez implementer.

## Points d'acces

### Connexion

```
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**Succes (200) :** Definit un cookie d'authentification et renvoie :

```json
{
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

**MFA requis (200) :** Si l'utilisateur a inscrit le MFA et que la `MfaPolicy` du client est `Enabled` ou `Required` :

```json
{
  "mfaRequired": true,
  "challengeId": "a1b2c3...",
  "methods": ["totp", "webauthn", "recoverycode"],
  "webAuthn": { /* PublicKeyCredentialRequestOptions */ }
}
```

Le client doit rediriger vers une page de verification MFA et appeler `POST /api/auth/mfa/verify`.

**Configuration MFA requise (200) :** Si `MfaPolicy` est `Required` et que l'utilisateur n'a pas de MFA inscrit :

```json
{
  "mfaSetupRequired": true,
  "setupToken": "abc123..."
}
```

Le client doit rediriger vers une page de configuration MFA. Le jeton de configuration authentifie l'utilisateur aupres des points d'acces de configuration MFA via l'en-tete `X-MFA-Setup-Token`.

**Reponses d'erreur :**

| `error` | Statut | Description |
|---|---|---|
| `invalid_credentials` | 401 | Email ou mot de passe incorrect |
| `locked_out` | 423 | Trop de tentatives echouees. `retryAfter` (secondes) est inclus. |
| `email_not_confirmed` | 403 | Email pas encore verifie |
| `sso_required` | 403 | Le domaine requiert SSO. `redirectUrl` pointe vers la connexion SSO. |
| `email_required` | 400 | Le champ email est vide |
| `password_required` | 400 | Le champ mot de passe est vide |

### Deconnexion

```
POST /api/auth/logout
```

Efface le cookie d'authentification. Renvoie `200 { success: true }`.

### Mot de passe oublie

```
POST /api/auth/forgot-password
Content-Type: application/json

{
  "email": "user@example.com"
}
```

Renvoie toujours `200` (anti-enumeration). Si l'utilisateur existe, un email de reinitialisation est envoye.

### Reinitialisation du mot de passe

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
| `weak_password` | Ne repond pas aux exigences de robustesse |
| `invalid_token` | Le jeton est mal forme |
| `token_expired` | Le jeton a expire (validite de 24 heures) |

### Session

```
GET /api/auth/session
```

Renvoie les informations de session en cours si authentifie :

```json
{
  "authenticated": true,
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

Renvoie `401` si non authentifie.

### Verification SSO

```
GET /api/auth/sso-check?email=user@acme.com
```

Verifie si le domaine de l'email requiert SSO :

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

Renvoie les exigences de mot de passe du serveur (configurees via `PasswordPolicy` dans les parametres) :

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

L'interface de connexion par defaut recupere ce point d'acces sur la page de reinitialisation du mot de passe pour afficher les exigences dynamiquement.

## Exigences de mot de passe par defaut

Avec la configuration par defaut, les mots de passe doivent satisfaire toutes ces conditions :

- Au moins 8 caracteres
- Au moins une lettre majuscule
- Au moins une lettre minuscule
- Au moins un chiffre
- Au moins un caractere non alphanumerique
- Au moins 2 caracteres distincts

Celles-ci peuvent etre personnalisees via la section de configuration `PasswordPolicy` -- voir [Configuration](configuration).

## Points d'acces MFA

### Verification MFA

```
POST /api/auth/mfa/verify
Content-Type: application/json

{
  "challengeId": "a1b2c3...",
  "method": "totp",
  "code": "123456"
}
```

Verifie un defi MFA. En cas de succes, definit le cookie d'authentification et renvoie les informations de l'utilisateur.

**Methodes :**

| `method` | Champs requis | Description |
|---|---|---|
| `totp` | `code` (6 chiffres) | Mot de passe a usage unique base sur le temps depuis une application d'authentification |
| `webauthn` | `assertion` (chaine JSON) | Reponse d'assertion WebAuthn depuis `navigator.credentials.get()` |
| `recovery` | `code` (`XXXX-XXXX`) | Code de recuperation a usage unique (consomme lors de l'utilisation) |

### Statut MFA

```
GET /api/auth/mfa/status
```

Renvoie les methodes MFA inscrites de l'utilisateur. Necessite l'authentification par cookie ou l'en-tete `X-MFA-Setup-Token`.

```json
{
  "enabled": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

### Configuration TOTP

```
POST /api/auth/mfa/totp/setup
→ { "setupToken": "...", "qrCodeDataUri": "data:image/svg+xml;base64,..." }

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

### Codes de recuperation

```
POST /api/auth/mfa/recovery/generate
→ { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

Genere 10 codes de recuperation a usage unique. Necessite qu'au moins une methode principale (TOTP ou WebAuthn) soit inscrite. La regeneration remplace tous les codes de recuperation existants.

### Supprimer un identifiant MFA

```
DELETE /api/auth/mfa/credentials/{credentialId}
→ { "success": true }
```

Supprime un identifiant MFA specifique. Si la derniere methode principale est supprimee, le MFA est desactive pour l'utilisateur.

## Construire une interface de connexion personnalisee

La SPA par defaut (`login-app/`) est une implementation de cette API. Pour construire la votre :

1. Servez votre interface aux chemins `/login`, `/forgot-password`, `/reset-password`
2. Le point d'acces d'autorisation redirige les utilisateurs non authentifies vers `/login?returnUrl={encoded-authorize-url}`
3. Apres une connexion reussie (cookie defini), redirigez l'utilisateur vers le `returnUrl`
4. Les liens de reinitialisation de mot de passe utilisent `{Issuer}/reset-password?p={token}`

Votre interface doit etre servie depuis la **meme origine** que l'API parce que :
- L'authentification par cookie utilise `SameSite=Lax` + `HttpOnly`
- Le point d'acces d'autorisation redirige vers `/login` (relatif)
- Les liens de reinitialisation utilisent `{Issuer}/reset-password`
