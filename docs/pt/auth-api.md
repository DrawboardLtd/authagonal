---
layout: default
title: API de Autenticação
locale: pt
---

# API de Autenticação

Estes endpoints alimentam o SPA de login. Utilizam autenticação por cookie (`SameSite=Lax`, `HttpOnly`).

Se estiver a construir uma interface de login personalizada, estes são os endpoints que precisa de implementar.

## Endpoints

### Login

```
POST /api/auth/login
Content-Type: application/json

{
  "email": "user@example.com",
  "password": "password123"
}
```

**Sucesso (200):** Define um cookie de autenticação e retorna:

```json
{
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

**MFA obrigatório (200):** Se o utilizador tem MFA inscrito e a `MfaPolicy` do cliente é `Enabled` ou `Required`:

```json
{
  "mfaRequired": true,
  "challengeId": "a1b2c3...",
  "methods": ["totp", "webauthn", "recoverycode"],
  "webAuthn": { /* PublicKeyCredentialRequestOptions */ }
}
```

O cliente deve redirecionar para uma página de desafio MFA e chamar `POST /api/auth/mfa/verify`.

**Configuração de MFA obrigatória (200):** Se `MfaPolicy` é `Required` e o utilizador não tem MFA inscrito:

```json
{
  "mfaSetupRequired": true,
  "setupToken": "abc123..."
}
```

O cliente deve redirecionar para uma página de configuração de MFA. O token de configuração autentica o utilizador nos endpoints de configuração de MFA via o cabeçalho `X-MFA-Setup-Token`.

**Respostas de erro:**

| `error` | Estado | Descrição |
|---|---|---|
| `invalid_credentials` | 401 | E-mail ou senha incorretos |
| `locked_out` | 423 | Demasiadas tentativas falhadas. `retryAfter` (segundos) é incluído. |
| `email_not_confirmed` | 403 | E-mail ainda não verificado |
| `sso_required` | 403 | O domínio requer SSO. `redirectUrl` aponta para o login SSO. |
| `email_required` | 400 | Campo de e-mail vazio |
| `password_required` | 400 | Campo de senha vazio |

### Logout

```
POST /api/auth/logout
```

Limpa o cookie de autenticação. Retorna `200 { success: true }`.

### Esqueceu a Senha

```
POST /api/auth/forgot-password
Content-Type: application/json

{
  "email": "user@example.com"
}
```

Retorna sempre `200` (anti-enumeração). Se o utilizador existir, envia um e-mail de redefinição.

### Redefinir Senha

```
POST /api/auth/reset-password
Content-Type: application/json

{
  "token": "base64-encoded-token",
  "newPassword": "NewSecurePass1!"
}
```

| `error` | Descrição |
|---|---|
| `weak_password` | Não cumpre os requisitos de complexidade |
| `invalid_token` | O token é malformado |
| `token_expired` | O token expirou (validade de 24 horas) |

### Sessão

```
GET /api/auth/session
```

Retorna informações da sessão atual se autenticado:

```json
{
  "authenticated": true,
  "userId": "abc123",
  "email": "user@example.com",
  "name": "Jane Doe"
}
```

Retorna `401` se não autenticado.

### Verificação SSO

```
GET /api/auth/sso-check?email=user@acme.com
```

Verifica se o domínio do e-mail requer SSO:

```json
{
  "ssoRequired": true,
  "providerType": "saml",
  "connectionId": "acme-azure",
  "redirectUrl": "/saml/acme-azure/login"
}
```

Se o SSO não for obrigatório:

```json
{
  "ssoRequired": false
}
```

### Política de Senhas

```
GET /api/auth/password-policy
```

Retorna os requisitos de senha do servidor (configurados via `PasswordPolicy` nas definições):

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

A interface de login padrão obtém este endpoint na página de redefinição de senha para exibir os requisitos dinamicamente.

## Requisitos de Senha Padrão

Com a configuração padrão, as senhas devem cumprir todos os seguintes requisitos:

- Pelo menos 8 caracteres
- Pelo menos uma letra maiúscula
- Pelo menos uma letra minúscula
- Pelo menos um dígito
- Pelo menos um caractere não alfanumérico
- Pelo menos 2 caracteres únicos

Estes podem ser personalizados via a seção de configuração `PasswordPolicy` — consulte [Configuração](configuration).

## Endpoints de MFA

### Verificação de MFA

```
POST /api/auth/mfa/verify
Content-Type: application/json

{
  "challengeId": "a1b2c3...",
  "method": "totp",
  "code": "123456"
}
```

Verifica um desafio MFA. Em caso de sucesso, define o cookie de autenticação e retorna as informações do utilizador.

**Métodos:**

| `method` | Campos obrigatórios | Descrição |
|---|---|---|
| `totp` | `code` (6 dígitos) | Senha única baseada em tempo de uma aplicação autenticadora |
| `webauthn` | `assertion` (string JSON) | Resposta de asserção WebAuthn de `navigator.credentials.get()` |
| `recovery` | `code` (`XXXX-XXXX`) | Código de recuperação de uso único (consumido ao usar) |

### Estado do MFA

```
GET /api/auth/mfa/status
```

Retorna os métodos MFA inscritos do utilizador. Requer autenticação por cookie ou cabeçalho `X-MFA-Setup-Token`.

```json
{
  "enabled": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

### Configuração TOTP

```
POST /api/auth/mfa/totp/setup
-> { "setupToken": "...", "qrCodeDataUri": "data:image/svg+xml;base64,..." }

POST /api/auth/mfa/totp/confirm
{ "setupToken": "...", "code": "123456" }
-> { "success": true }
```

### Configuração WebAuthn / Passkey

```
POST /api/auth/mfa/webauthn/setup
-> { "setupToken": "...", "options": { /* PublicKeyCredentialCreationOptions */ } }

POST /api/auth/mfa/webauthn/confirm
{ "setupToken": "...", "attestationResponse": "..." }
-> { "success": true, "credentialId": "..." }
```

### Códigos de Recuperação

```
POST /api/auth/mfa/recovery/generate
-> { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

Gera 10 códigos de recuperação de uso único. Requer que pelo menos um método primário (TOTP ou WebAuthn) esteja inscrito. Regenerar substitui todos os códigos de recuperação existentes.

### Remover Credencial MFA

```
DELETE /api/auth/mfa/credentials/{credentialId}
-> { "success": true }
```

Remove uma credencial MFA específica. Se o último método primário for removido, o MFA é desativado para o utilizador.

## Construir uma Interface de Login Personalizada

O SPA padrão (`login-app/`) é uma implementação desta API. Para construir a sua própria:

1. Sirva a sua interface nos caminhos `/login`, `/forgot-password`, `/reset-password`
2. O endpoint de autorização redireciona utilizadores não autenticados para `/login?returnUrl={encoded-authorize-url}`
3. Após login bem-sucedido (cookie definido), redirecione o utilizador para o `returnUrl`
4. Os links de redefinição de senha usam `{Issuer}/reset-password?p={token}`

A sua interface deve ser servida a partir da **mesma origem** que a API porque:
- A autenticação por cookie usa `SameSite=Lax` + `HttpOnly`
- O endpoint de autorização redireciona para `/login` (relativo)
- Os links de redefinição usam `{Issuer}/reset-password`
