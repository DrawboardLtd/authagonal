---
layout: default
title: Autenticação multifator
locale: pt
---

# Autenticação multifator (MFA)

O Authagonal suporta autenticação multifator para logins baseados em senha. Três métodos estão disponíveis: TOTP (aplicativos de autenticação), WebAuthn/chaves de acesso (chaves de hardware e biometria) e códigos de recuperação de uso único.

Logins federados (SAML/OIDC) ignoram a MFA — o provedor de identidade externo lida com a autenticação de segundo fator.

## Métodos suportados

| Método | Descrição |
|---|---|
| **TOTP** | Senhas de uso único baseadas em tempo (RFC 6238). Funciona com qualquer aplicativo de autenticação — Google Authenticator, Authy, 1Password, etc. |
| **WebAuthn / Chaves de acesso** | Chaves de segurança de hardware FIDO2, biometria de plataforma (Touch ID, Windows Hello) e chaves de acesso sincronizadas. |
| **Códigos de recuperação** | 10 códigos de backup de uso único (formato `XXXX-XXXX`) para recuperação de conta quando outros métodos não estão disponíveis. |

## Política de MFA

A aplicação de MFA é configurada **por cliente** por meio da propriedade `MfaPolicy` em `appsettings.json`:

| Valor | Comportamento |
|---|---|
| `Disabled` (padrão) | Nenhum desafio MFA, mesmo que o usuário tenha MFA registrado |
| `Enabled` | Desafiar usuários que têm MFA registrado; não forçar o registro |
| `Required` | Desafiar usuários registrados; forçar o registro para usuários sem MFA |

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "MfaPolicy": "Enabled"
    },
    {
      "ClientId": "admin-portal",
      "MfaPolicy": "Required"
    }
  ]
}
```

O padrão é `Disabled`, portanto os clientes existentes não são afetados até que você opte por participar.

### Substituição por usuário

Implemente `IAuthHook.ResolveMfaPolicyAsync` para substituir a política do cliente para usuários específicos:

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Force MFA for admin users regardless of client setting
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    // Exempt service accounts
    if (email.EndsWith("@service.internal"))
        return Task.FromResult(MfaPolicy.Disabled);

    return Task.FromResult(clientPolicy);
}
```

Consulte [Extensibilidade](extensibility) para a documentação completa de hooks.

## Fluxo de login

O fluxo de login com MFA funciona da seguinte forma:

1. O usuário envia e-mail e senha para `POST /api/auth/login`
2. O servidor verifica a senha e resolve a política MFA efetiva
3. Com base na política e no status de registro do usuário:

| Política | Usuário tem MFA? | Resultado |
|---|---|---|
| `Disabled` | — | Cookie definido, login concluído |
| `Enabled` | Não | Cookie definido, login concluído |
| `Enabled` | Sim | Retorna `mfaRequired` — o usuário deve verificar |
| `Required` | Não | Retorna `mfaSetupRequired` — o usuário deve se registrar |
| `Required` | Sim | Retorna `mfaRequired` — o usuário deve verificar |

### Desafio MFA

Quando `mfaRequired` é retornado, a resposta de login inclui um `challengeId` e os métodos disponíveis do usuário. O cliente redireciona para uma página de desafio MFA onde o usuário verifica com um de seus métodos registrados via `POST /api/auth/mfa/verify`.

Os desafios expiram após 5 minutos e são de uso único.

### Registro forçado

Quando `mfaSetupRequired` é retornado, a resposta inclui um `setupToken`. Esse token autentica o usuário nos endpoints de configuração de MFA (via cabeçalho `X-MFA-Setup-Token`) para que ele possa registrar um método antes de obter uma sessão de cookie.

## Registrar MFA

Os usuários registram a MFA por meio dos endpoints de configuração de autoatendimento. Esses endpoints requerem uma sessão de cookie autenticada ou um token de configuração.

### Configuração do TOTP

1. Chamar `POST /api/auth/mfa/totp/setup` — retorna um código QR (`data:image/svg+xml;base64,...`) e um token de configuração
2. O usuário escaneia o código QR com seu aplicativo de autenticação
3. O usuário insere o código de 6 dígitos para confirmar: `POST /api/auth/mfa/totp/confirm`

### Configuração do WebAuthn / Chave de acesso

1. Chamar `POST /api/auth/mfa/webauthn/setup` — retorna `PublicKeyCredentialCreationOptions`
2. O cliente chama `navigator.credentials.create()` com as opções
3. Enviar a resposta de atestação para `POST /api/auth/mfa/webauthn/confirm`

### Códigos de recuperação

Chamar `POST /api/auth/mfa/recovery/generate` para gerar 10 códigos de uso único. Pelo menos um método principal (TOTP ou WebAuthn) deve ser registrado primeiro.

A regeneração de códigos substitui todos os códigos de recuperação existentes. Cada código só pode ser usado uma vez.

## Gerenciar MFA

### Autoatendimento do usuário

- `GET /api/auth/mfa/status` — ver os métodos registrados
- `DELETE /api/auth/mfa/credentials/{id}` — remover uma credencial específica

Se o último método principal for removido, a MFA será desativada para o usuário.

### API de administração

Os administradores podem gerenciar a MFA para qualquer usuário por meio da [API de administração](admin-api):

- `GET /api/v1/profile/{userId}/mfa` — ver o status de MFA de um usuário
- `DELETE /api/v1/profile/{userId}/mfa` — redefinir toda a MFA (para usuários bloqueados)
- `DELETE /api/v1/profile/{userId}/mfa/{id}` — remover uma credencial específica

### Hook de auditoria

Implemente `IAuthHook.OnMfaVerifiedAsync` para registrar eventos de MFA:

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

## Interface de login personalizada

Se você estiver criando uma interface de login personalizada, trate estas respostas de `POST /api/auth/login`:

1. **Login normal** — `{ userId, email, name }` com cookie definido. Redirecionar para `returnUrl`.
2. **MFA necessária** — `{ mfaRequired: true, challengeId, methods, webAuthn? }`. Exibir o formulário de desafio MFA.
3. **Configuração de MFA necessária** — `{ mfaSetupRequired: true, setupToken }`. Exibir o fluxo de registro de MFA.

Consulte a [API de autenticação](auth-api) para a referência completa de endpoints.
