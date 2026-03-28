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
