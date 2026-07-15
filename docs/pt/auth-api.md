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
  "name": "Jane Doe",
  "mfaAvailable": false
}
```

`mfaAvailable` é `true` quando a `MfaPolicy` do cliente é `Enabled` mas o utilizador ainda não se inscreveu (a interface pode oferecer a configuração); nesse caso, um campo `clientId` também é incluído.

**MFA obrigatório (200):** Se o utilizador tem MFA inscrito, é **sempre** desafiado, independentemente da `MfaPolicy` do cliente solicitante (o MFA é uma propriedade do utilizador/sessão, não do cliente):

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
| `invalid_credentials` | 401 | E-mail ou senha incorretos. Deliberadamente idêntico para e-mails desconhecidos (anti-enumeração). |
| `locked_out` | 423 | Demasiadas tentativas falhadas. `retryAfter` (segundos) é incluído. |
| `account_disabled` | 403 | A conta está desativada (apenas revelado após uma senha correta) |
| `email_not_confirmed` | 403 | E-mail ainda não verificado (apenas revelado após uma senha correta) |
| `sso_required` | 409 | O domínio requer SSO. `redirectUrl` aponta para o login SSO. |
| `captcha_failed` | 400 | Verificação do Turnstile falhou (apenas quando o Turnstile está configurado; os pedidos precisam então de um campo `turnstileToken`) |
| `email_required` | 400 | Campo de e-mail vazio |
| `password_required` | 400 | Campo de senha vazio |

### Registar

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

Cria uma nova conta de utilizador e envia um e-mail de verificação. Retorna `201 { "success": true, "userId": "..." }`. Campos opcionais: `locale` (tag BCP-47 persistida no utilizador) e `customAttributes` (um mapa de strings).

O registo é deliberadamente **neutro quanto a enumeração**: se o e-mail já estiver registado, a resposta é o mesmo `201` neutro (com um `userId` descartável) e, em vez disso, o proprietário real recebe por e-mail um aviso de início de sessão/redefinição. O registo também tem limite de taxa por IP: `429 rate_limited` quando excedido (janela e limite configuráveis via `Auth:MaxRegistrationsPerIp` / `Auth:RegistrationWindowMinutes`).

### Confirmar E-mail

```
GET  /api/auth/confirm-email?token={token}
POST /api/auth/confirm-email?token={token}
```

Confirma o endereço de e-mail do utilizador usando o token do e-mail de verificação. `GET` é o link clicável do e-mail: redireciona para `/login?email_confirmed=1` (mais um parâmetro `continue_client` quando o registo teve origem num fluxo OAuth). `POST` é o caminho programático e retorna JSON (o token também pode ser fornecido num corpo JSON como `{ "token": "..." }`); a resposta inclui um `appLink` opcional (destino de "continuar para a aplicação").

### Provedores

```
GET /api/auth/providers
```

Retorna a lista de provedores de identidade externos configurados (para renderizar botões SSO):

```json
{
  "providers": [
    { "connectionId": "google", "name": "Google", "type": "oidc", "iconUrl": null, "loginUrl": "/oidc/google/login" }
  ],
  "turnstileSiteKey": null
}
```

As conexões com `AllowedDomains` configurados são **excluídas**: essas são alcançadas primeiro por e-mail via `/api/auth/sso-check` em vez de um botão. `turnstileSiteKey` é definido quando o Cloudflare Turnstile está configurado (a interface de login deve então enviar um `turnstileToken` nos pedidos de login/registo/senha).

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
| `token_expired` | O token expirou (validade padrão de 60 minutos, configurável via `Auth:PasswordResetExpiryMinutes`) |

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

### Aplicações

```
GET /api/auth/apps
```

Retorna os links de aplicação do tenant para o lançador "voltar à aplicação" da página de conta: clientes ativos que têm um URI inicial (`initiateLoginUri` preferido em relação a `clientUri`). Cada entrada é `{ clientId, clientName, homeUri, logoUri, isDefault }`; exatamente uma aplicação é marcada como padrão (o cliente sinalizado, ou o único cliente com um URI inicial). Requer autenticação por cookie.

### Perfil (autoatendimento)

```
GET   /api/auth/profile
PATCH /api/auth/profile
```

O utilizador autenticado lê/atualiza os seus próprios campos de perfil não sensíveis: `firstName`, `lastName`, `companyName`, `phone`, `locale`. Os campos nulos permanecem inalterados; e-mail, senha, funções, estado ativo e organização **não** são editáveis aqui. Ambos retornam o perfil `{ email, emailConfirmed, firstName, lastName, companyName, phone, locale }`.

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

Estes podem ser personalizados via a seção de configuração `PasswordPolicy`, consulte [Configuração](configuration).

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

**Semântica de repetição:** um código incorreto **não** queima o desafio: o código é validado primeiro e o desafio só é consumido em caso de sucesso, portanto o utilizador pode tentar novamente com o mesmo `challengeId` após um dígito mal digitado (`401 invalid_code` / `assertion_failed`). Cada desafio tolera **5 tentativas falhadas**; a 5ª falha consome-o e retorna `401 too_many_attempts`, forçando um novo login (isto limita a força bruta de TOTP a 5 tentativas por desafio). Os desafios também expiram (por padrão 5 minutos, `Auth:MfaChallengeExpiryMinutes`); um `challengeId` expirado, desconhecido ou já consumido retorna `invalid_challenge`. Os códigos TOTP têm ainda proteção contra repetição: um código de um passo de tempo já utilizado é rejeitado.

### Estado do MFA

```
GET /api/auth/mfa/status
```

Retorna os métodos MFA inscritos do utilizador. Requer autenticação por cookie ou cabeçalho `X-MFA-Setup-Token`.

```json
{
  "enabled": true,
  "offered": true,
  "methods": [
    { "id": "cred-id", "type": "totp", "name": "Authenticator app", "createdAt": "...", "lastUsedAt": "..." }
  ]
}
```

`offered` é `false` quando a `MfaPolicy` de todos os clientes é `Disabled`: o tenant tem o MFA desligado, portanto a interface de configuração pode ocultar-se. As entradas de código de recuperação têm ainda `isConsumed`.

### Configuração TOTP

```
POST /api/auth/mfa/totp/setup
→ { "setupToken": "...", "qrCodeDataUri": "data:image/png;base64,...", "manualKey": "BASE32..." }

POST /api/auth/mfa/totp/confirm
{ "setupToken": "...", "code": "123456" }
→ { "success": true }
```

### Configuração WebAuthn / Passkey

```
POST /api/auth/mfa/webauthn/setup
→ { "setupToken": "...", "options": { /* PublicKeyCredentialCreationOptions */ } }

POST /api/auth/mfa/webauthn/confirm
{ "setupToken": "...", "attestationResponse": "..." }
→ { "success": true, "credentialId": "..." }
```

A inscrição de passkey requer primeiro uma **credencial TOTP confirmada** (`400 totp_required_first`): as passkeys são uma conveniência por dispositivo assente num fator base portátil, portanto uma conta nunca pode acabar apenas com passkey e presa a um dispositivo. Os utilizadores cujo domínio de e-mail é encaminhado por SSO não podem inscrever uma passkey local (`400 sso_managed`): isso contornaria o IdP do tenant. Um ID de credencial já registado para outro utilizador é rejeitado com `409 credential_already_registered`.

### Códigos de Recuperação

```
POST /api/auth/mfa/recovery/generate
→ { "codes": ["ABCD-1234", "EFGH-5678", ...] }
```

Gera 10 códigos de recuperação de uso único. Requer que pelo menos um método primário (TOTP ou WebAuthn) esteja inscrito. Regenerar substitui todos os códigos de recuperação existentes.

### Remover Credencial MFA

```
DELETE /api/auth/mfa/credentials/{credentialId}
→ { "success": true }
```

Remove uma credencial MFA específica. Se o último método primário for removido, o MFA é desativado para o utilizador. Requer uma sessão de cookie real: um token de configuração é rejeitado com `403 session_required` (os tokens de configuração existem apenas para adicionar um primeiro fator, nunca para reduzir o MFA).

### Login Passwordless com Passkey

```
POST /api/auth/mfa/passwordless/begin
→ { "challengeId": "...", "options": { /* PublicKeyCredentialRequestOptions */ } }

POST /api/auth/mfa/passwordless/complete
{ "challengeId": "...", "assertion": "..." }
→ { "userId": "...", "email": "...", "name": "..." }
```

Login com credencial detetável (passkey residente) sem contexto de utilizador prévio: `begin` emite um desafio de asserção com uma lista `allowCredentials` vazia, e `complete` resolve o utilizador **a partir** da passkey escolhida, verifica a asserção e inicia a sua sessão (a sessão carrega o marcador MFA: uma passkey é autenticação forte resistente a phishing). Se o domínio de e-mail do utilizador resolvido for encaminhado por SSO, o login é recusado com `409 sso_required` + `redirectUrl`, para que uma passkey local não possa contornar um IdP obrigatório.

## Autorização de Dispositivo (RFC 8628)

### Solicitar Código de Dispositivo

```
POST /connect/deviceauthorization
Content-Type: application/x-www-form-urlencoded

client_id=my-cli&scope=openid+profile
```

Retorna um código de dispositivo, um código de utilizador e um URI de verificação:

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

`expires_in` provém do `DeviceCodeLifetimeSeconds` do cliente (padrão 300). O dispositivo exibe o `verification_uri` e o `user_code` ao utilizador, depois consulta o endpoint de token com o `device_code`, não mais rápido do que a cada `interval` segundos, ou o endpoint de token responde `slow_down` (RFC 8628 §3.5). Enquanto o utilizador ainda não aprovou, o endpoint de token retorna `authorization_pending`. O utilizador visita o URI de verificação, faz login e insere o código de utilizador para aprovar.

### Aprovar Dispositivo

```
POST /api/auth/device/approve
Content-Type: application/json

{
  "userCode": "ABCD-EFGH"
}
```

Requer autenticação por cookie. Aprova o código de dispositivo para o utilizador atual. O dispositivo pode então trocar o código de dispositivo por tokens via o endpoint de token usando o tipo de concessão `urn:ietf:params:oauth:grant-type:device_code`.

## Introspeção de Token (RFC 7662)

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded
Authorization: Basic base64(client_id:client_secret)

token=eyJhbGci...
```

Ou com credenciais codificadas no formulário:

```
POST /connect/introspect
Content-Type: application/x-www-form-urlencoded

token=eyJhbGci...&client_id=my-app&client_secret=secret
```

Retorna os metadados do token:

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

Tokens inativos ou inválidos retornam `{ "active": false }`. Suporta tanto tokens de acesso JWT como tokens de atualização opacos.

## Endpoints de Consentimento

### Informações de Consentimento

```
GET /consent/info?client_id=my-app&scope=openid%20profile%20email
```

Retorna os detalhes do cliente e os scopes solicitados para a página de consentimento (`scope` assume `openid` por padrão quando omitido):

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

Retorna `404 client_not_found` para um cliente desconhecido.

### Enviar Consentimento

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

Regista a decisão de consentimento do utilizador (requer autenticação por cookie) e retorna `{ "redirect": "..." }` para o SPA navegar. Ao permitir, os scopes concedidos são persistidos (filtrados para os `AllowedScopes` do cliente: um corpo adulterado não pode registar scopes que o cliente não poderia solicitar) e o redirecionamento aponta de volta para o fluxo de autorização. Em `"decision": "deny"`, o redirecionamento aponta para o `redirect_uri` do cliente com um erro `access_denied`.

### Listar Concessões

```
GET /consent/grants
```

Retorna todas as aplicações que o utilizador autorizou:

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

### Revogar Concessão

```
DELETE /consent/grants/{clientId}
```

Revoga o consentimento para uma aplicação específica. O utilizador será solicitado a reconsentir no seu próximo login.

## Construir uma Interface de Login Personalizada

O SPA padrão (`login-app/`) é uma implementação desta API. Para construir a sua própria:

1. Sirva a sua interface nos caminhos `/login`, `/forgot-password`, `/reset-password`
2. O endpoint de autorização redireciona utilizadores não autenticados para `/login?returnUrl={encoded-authorize-url}`
3. Após login bem-sucedido (cookie definido), redirecione o utilizador para o `returnUrl`
4. Os links de redefinição de senha usam `{Issuer}/login/reset-password?p={token}` (o SPA de login está montado em `/login`)

A sua interface deve ser servida a partir da **mesma origem** que a API porque:
- A autenticação por cookie usa `SameSite=Lax` + `HttpOnly`
- O endpoint de autorização redireciona para `/login` (relativo)
- Os links de redefinição usam `{Issuer}/login/reset-password`
