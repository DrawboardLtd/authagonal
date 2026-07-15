---
layout: default
title: Autenticação multifator
locale: pt
---

# Autenticação multifator (MFA)

O Authagonal suporta autenticação multifator. Três métodos estão disponíveis: TOTP (aplicativos de autenticação), WebAuthn/chaves de acesso (chaves de hardware e biometria) e códigos de recuperação de uso único. As chaves de acesso também podem ser usadas para [login sem senha](#login-sem-senha-com-chave-de-acesso).

Os logins federados (SAML/OIDC) também são cobertos: uma asserção SAML ou OIDC prova o primeiro fator, não o segundo. Um usuário federado com MFA registrada é encaminhado pelo mesmo desafio MFA local que um login por senha, e uma política `Required` força o registro antes de qualquer sessão ser emitida. Somente quando a MFA não está registrada nem é obrigatória é que a federação se sustenta sozinha.

## Métodos suportados

| Método | Descrição |
|---|---|
| **TOTP** | Senhas de uso único baseadas em tempo (RFC 6238): 6 dígitos, passo de 30 segundos, SHA-1, verificadas com uma janela de desvio de relógio de um passo. Funciona com qualquer aplicativo de autenticação (Google Authenticator, Authy, 1Password, etc.). Um código que já foi aceito não pode ser repetido dentro da sua janela de validade. |
| **WebAuthn / Chaves de acesso** | Chaves de segurança de hardware FIDO2, biometria de plataforma (Touch ID, Windows Hello) e chaves de acesso sincronizadas. Os usuários podem registrar várias chaves de acesso, e as chaves de acesso podem fazer login sem senha. |
| **Códigos de recuperação** | 10 códigos de backup de uso único (formato `XXXX-XXXX`) para recuperação de conta quando outros métodos não estão disponíveis. Armazenados com hash e encriptados em repouso. |

## Política de MFA

A aplicação de MFA é configurada **por cliente** por meio da propriedade `MfaPolicy` em `appsettings.json`:

| Valor | Comportamento |
|---|---|
| `Disabled` (padrão) | Não força o registro; a interface de configuração de autoatendimento oculta a MFA quando todos os clientes estão `Disabled` |
| `Enabled` | Oferecer o registro de MFA; não forçá-lo |
| `Required` | Forçar o registro para usuários sem MFA |

Um usuário que tem MFA registrada é **sempre desafiado no login, independentemente da política do cliente**. A MFA é uma propriedade do usuário e da sua sessão, não do cliente solicitante, portanto um pedido roteado através de um cliente `Disabled` não pode ser usado para pular o segundo fator de um usuário registrado.

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

A política resolvida governa o registro (se ele é oferecido ou forçado). Ela não isenta do desafio um usuário já registrado; usuários registrados são sempre desafiados.

Consulte [Extensibilidade](extensibility) para a documentação completa de hooks.

## Fluxo de login

O fluxo de login com MFA funciona da seguinte forma:

1. O usuário envia e-mail e senha para `POST /api/auth/login`
2. O servidor verifica a senha e resolve a política MFA efetiva
3. Com base na política e no status de registro do usuário:

| Política | Usuário tem MFA? | Resultado |
|---|---|---|
| Qualquer | Sim | Retorna `mfaRequired`: o usuário deve verificar |
| `Disabled` / `Enabled` | Não | Cookie definido, login concluído |
| `Required` | Não | Retorna `mfaSetupRequired`: o usuário deve se registrar |

### Desafio MFA

Quando `mfaRequired` é retornado, a resposta de login inclui um `challengeId`, os métodos disponíveis do usuário (`methods`) e (quando o usuário tem chaves de acesso) as opções de asserção `webAuthn`. O cliente redireciona para uma página de desafio MFA onde o usuário verifica com um de seus métodos registrados via `POST /api/auth/mfa/verify`:

```json
{
  "challengeId": "...",
  "method": "totp",
  "code": "123456"
}
```

`method` é `totp`, `recovery` ou `webauthn` (o WebAuthn envia uma `assertion` em vez de um `code`).

Os desafios expiram após 5 minutos (configurável via `Auth:MfaChallengeExpiryMinutes`) e são consumidos na verificação bem-sucedida.

#### Orçamento de tentativas

Um código incorreto não queima o desafio. O endpoint de verificação valida o código primeiro e consome o desafio apenas em caso de sucesso, portanto um dígito TOTP mal digitado pode simplesmente ser tentado novamente contra o mesmo `challengeId`. As tentativas falhadas retornam `invalid_code` (ou `assertion_failed` para WebAuthn) com um 401 e incrementam um contador limitado no desafio; a quinta tentativa incorreta consome o desafio e retorna `too_many_attempts`, forçando um novo login. Isto aplica-se aos três métodos e limita a força bruta de TOTP a 5 tentativas por desafio.

Um desafio inexistente, expirado ou já consumido retorna `invalid_challenge`.

### Logins federados

Após uma asserção SAML ou OIDC bem-sucedida, o servidor resolve a mesma política de MFA efetiva. Um usuário com MFA registrada é redirecionado para a página de desafio MFA hospedada (com um `challengeId`) em vez de receber uma sessão; um usuário sem MFA sob uma política `Required` é redirecionado para a página de configuração de MFA (com um `setupToken`). A sessão só é marcada como autenticada por MFA depois que a verificação é concluída.

### Registro forçado

Quando `mfaSetupRequired` é retornado, a resposta inclui um `setupToken`. Esse token autentica o usuário nos endpoints de configuração de MFA (via cabeçalho `X-MFA-Setup-Token`) para que ele possa registrar um método antes de obter uma sessão de cookie. Os tokens de configuração expiram após 15 minutos (configurável via `Auth:MfaSetupTokenExpiryMinutes`).

## Registrar MFA

Os usuários registram a MFA por meio dos endpoints de configuração de autoatendimento. Esses endpoints requerem uma sessão de cookie autenticada ou um token de configuração.

### Configuração do TOTP

1. Chamar `POST /api/auth/mfa/totp/setup`: retorna um código QR (`data:image/png;base64,...`), uma `manualKey` (Base32 para entrada manual) e um token de configuração
2. O usuário escaneia o código QR com seu aplicativo de autenticação
3. O usuário insere o código de 6 dígitos para confirmar: `POST /api/auth/mfa/totp/confirm`

### Configuração do WebAuthn / Chave de acesso

1. Chamar `POST /api/auth/mfa/webauthn/setup`: retorna um `setupToken` e `PublicKeyCredentialCreationOptions`
2. O cliente chama `navigator.credentials.create()` com as opções
3. Enviar a resposta de atestação para `POST /api/auth/mfa/webauthn/confirm`

O registro de chave de acesso requer primeiro uma credencial TOTP confirmada (`totp_required_first`). As chaves de acesso são uma conveniência por dispositivo sobreposta a um fator base portátil, portanto toda conta mantém um fator independente de dispositivo e uma política `Required` não pode ser satisfeita apenas por uma chave de acesso.

Os usuários podem registrar várias chaves de acesso (uma por dispositivo). Um ID de credencial já registrado para outro usuário é rejeitado (`credential_already_registered`), e usuários cujo domínio de e-mail é roteado para um IdP externo via SSO obrigatório não podem registrar uma chave de acesso local (`sso_managed`), uma vez que isso contornaria o IdP e o seu desprovisionamento.

### Códigos de recuperação

Chamar `POST /api/auth/mfa/recovery/generate` para gerar 10 códigos de uso único. Pelo menos um método principal (TOTP ou WebAuthn) deve ser registrado primeiro.

A regeneração de códigos substitui todos os códigos de recuperação existentes. Cada código só pode ser usado uma vez; um código resgatado é marcado como consumido e não é mais aceito.

Os códigos nunca são armazenados em texto simples: cada código recebe um hash, e o hash é adicionalmente encriptado em repouso com o provedor de segredos do tenant, portanto um dump de armazenamento produz texto cifrado em vez de um hash sujeito a força bruta offline.

## Login sem senha com chave de acesso

As chaves de acesso não são apenas um segundo fator: um usuário com uma chave de acesso registrada pode entrar sem senha.

1. `POST /api/auth/mfa/passwordless/begin` retorna um `challengeId` e `options` de asserção para credenciais detetáveis, para que o autenticador ofereça qualquer chave de acesso residente do site
2. O cliente chama `navigator.credentials.get()` com as opções
3. `POST /api/auth/mfa/passwordless/complete` com `{ challengeId, assertion }`: o servidor resolve o usuário a partir da própria chave de acesso e o autentica

A página de login hospedada liga isto ao campo de e-mail via mediação condicional (preenchimento automático de chave de acesso): quando o navegador o suporta, uma chave de acesso disponível é oferecida como uma sugestão de preenchimento automático sem qualquer interface extra.

Uma chave de acesso é autenticação forte resistente a phishing, portanto a sessão resultante carrega o marcador de MFA e não é desafiada novamente. Se o domínio de e-mail do usuário for roteado para um IdP externo via SSO obrigatório, o login sem senha é recusado com uma resposta 409 `sso_required` que inclui a URL de redirecionamento SSO, para que uma chave de acesso local não possa contornar o IdP.

## Gerenciar MFA

### Autoatendimento do usuário

- `GET /api/auth/mfa/status`: ver os métodos registrados (também informa se a MFA é oferecida por algum cliente)
- `DELETE /api/auth/mfa/credentials/{id}`: remover uma credencial específica

A remoção de uma credencial requer uma sessão autenticada real; um token de configuração só autoriza a adição de um primeiro fator e recebe `session_required` aqui, portanto um token de configuração vazado não pode rebaixar a MFA de um usuário.

Se o último método principal for removido, a MFA será desativada para o usuário.

### API de administração

Os administradores podem gerenciar a MFA para qualquer usuário por meio da [API de administração](admin-api):

- `GET /api/v1/profile/{userId}/mfa`: ver o status de MFA de um usuário
- `DELETE /api/v1/profile/{userId}/mfa`: redefinir toda a MFA (para usuários bloqueados)
- `DELETE /api/v1/profile/{userId}/mfa/{id}`: remover uma credencial específica

### Hooks de auditoria

Implemente `IAuthHook.OnMfaVerifiedAsync` para registrar eventos de MFA:

```csharp
public Task OnMfaVerifiedAsync(
    string userId, string email, string mfaMethod, CancellationToken ct)
{
    logger.LogInformation("MFA verified for {Email} via {Method}", email, mfaMethod);
    return Task.CompletedTask;
}
```

Todo o ciclo de vida da MFA pode ser interceptado por hooks: `OnMfaVerifyFailedAsync` (uma tentativa de verificação falhada), `OnMfaEnrolledAsync` (um método confirmado), `OnMfaCredentialRemovedAsync` (uma credencial removida, com um sinalizador para indicar se isso desativou a MFA) e `OnRecoveryCodesRegeneratedAsync`.

## Interface de login personalizada

Se você estiver criando uma interface de login personalizada, trate estas respostas de `POST /api/auth/login`:

1. **Login normal**: `{ userId, email, name }` com cookie definido. Redirecionar para `returnUrl`.
2. **MFA necessária**: `{ mfaRequired: true, challengeId, methods, webAuthn? }`. Exibir o formulário de desafio MFA.
3. **Configuração de MFA necessária**: `{ mfaSetupRequired: true, setupToken }`. Exibir o fluxo de registro de MFA.

Ao tratar erros de `POST /api/auth/mfa/verify`: `invalid_code` e `assertion_failed` podem ser tentados novamente contra o mesmo `challengeId` (até ao limite de tentativas); `too_many_attempts` e `invalid_challenge` são terminais, portanto envie o usuário de volta ao formulário de login.

Consulte a [API de autenticação](auth-api) para a referência completa de endpoints.
