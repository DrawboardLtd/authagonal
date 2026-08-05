---
layout: default
title: Federação OIDC
locale: pt
---

# Federação OIDC

O Authagonal pode federar a autenticação para provedores de identidade OIDC externos (Google, Apple, Azure AD, etc.). Isto permite fluxos do tipo "Entrar com o Google" enquanto o Authagonal permanece como o servidor de autenticação central.

## Como Funciona

Existem dois caminhos de entrada na federação:

**Baseado em domínio (login interativo):**

1. O utilizador introduz o seu e-mail na página de login
2. O SPA chama `/api/auth/sso-check`: se o domínio do e-mail estiver vinculado a um provedor OIDC, o SSO é obrigatório
3. O utilizador clica em "Continuar com SSO" e é redirecionado para o IdP externo
4. Após a autenticação, o IdP redireciona de volta para `/oidc/callback`
5. O Authagonal valida o id_token, cria/vincula o utilizador e define um cookie de sessão

**Indicado pelo RP (`idp_hint`):**

A relying party a jusante pode encaminhar diretamente para um IdP upstream específico sem passar pelo passo de e-mail/domínio SSO. Acrescente `idp_hint={connectionId}` a `/connect/authorize`:

```
/connect/authorize?client_id=my-rp&scope=openid+email&...&idp_hint=google
```

Quando o pedido não está autenticado, o Authagonal redireciona para `/oidc/{connectionId}/login` com o URL original de `/authorize` preservado como `returnUrl`. Depois de a federação terminar, o utilizador regressa a `/authorize` com um cookie de sessão e o fluxo prossegue normalmente.

## Configuração

### 1. Criar um Provedor OIDC

**Opção A: Configuração (recomendado para configurações estáticas):**

Adicione ao `appsettings.json`:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-google-client-id",
      "ClientSecret": "your-google-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

Os provedores são semeados na inicialização. Os campos semeáveis são exatamente os apresentados, menos `RedirectUrl`: `ConnectionId`, `ConnectionName`, `MetadataLocation`, `ClientId`, `ClientSecret`, `AllowedDomains`. `RedirectUrl` é aceite por compatibilidade e ignorado — o URI de redirecionamento é derivado por pedido como `{Issuer}/oidc/callback`, pois tem de estar na origem em que o browser se encontra, e é esse o URI a registar no IdP. O `ClientSecret` é protegido via `ISecretProvider` (Key Vault quando configurado, texto simples caso contrário). Os mapeamentos de domínio SSO são registados automaticamente a partir de `AllowedDomains`.

O modelo de conexão transporta comportamento opcional adicional: `PassthroughParams` (definível via a criação na API de administração), mais `SessionExpClaim` e `DisableJitProvisioning` (campos ao nível do store, definidos via `IOidcProviderStore` a partir do código de hospedagem). Consulte [Repasse de scopes e claims](#repasse-de-scopes-e-claims) e [Limite de vida da sessão](#limite-de-vida-da-sessão) abaixo.

**Opção B: API de Administração (para gestão em tempo de execução):**

```bash
curl -X POST https://auth.example.com/api/v1/oidc/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Google",
    "metadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
    "clientId": "your-google-client-id",
    "clientSecret": "your-google-client-secret",
    "redirectUrl": "https://auth.example.com/oidc/callback",
    "allowedDomains": ["example.com"]
  }'
```

### 2. Roteamento de Domínio SSO

Quando `AllowedDomains` é especificado (na configuração ou via a API de criação), os mapeamentos de domínio SSO são registados automaticamente. Sem roteamento de domínio, os utilizadores ainda podem ser direcionados para o login OIDC via `/oidc/{connectionId}/login`.

## Endpoints

| Endpoint | Descrição |
|---|---|
| `GET /oidc/{connectionId}/login?returnUrl=...` | Inicia o login OIDC. Gera PKCE + state + nonce, deriva o scope upstream e os parâmetros de passthrough a partir de `returnUrl`, redireciona para o endpoint de autorização do IdP. |
| `GET /oidc/callback` | Trata o callback do IdP. Troca o código por tokens, valida o id_token, captura todas as claims não-protocolares no cookie como `federated:*`, cria/autentica o utilizador. |

## Repasse de scopes e claims

O conjunto de scopes pedido pela RP a jusante em `/connect/authorize` é encaminhado para o IdP upstream, **filtrado para o conjunto OIDC padrão** (`openid`, `profile`, `email`, `address`, `phone`), com `openid` sempre incluído. Tudo o resto que a RP pediu (scopes de API personalizados, `offline_access`, etc.) é descartado antes da chamada upstream: um IdP estrito como o Google devolve `invalid_scope` perante valores desconhecidos, e o upstream só precisa de identificar o utilizador; os scopes próprios da RP são honrados nos tokens emitidos pelo Authagonal, não nos do upstream. As claims que o IdP upstream coloca no id_token (condicionadas pelos scopes) regressam ao Authagonal, são guardadas no ticket do cookie como claims `federated:<name>` e seguem para `OidcSubject.FederationClaims` na próxima passagem por `/connect/authorize`. A partir daí o `ProtocolTokenService` reemite-as nos tokens emitidos pelo Authagonal, sujeitas à mesma whitelist `Scope.UserClaims` que condiciona `CustomAttributes`. Em caso de colisão de chaves, os valores de federação prevalecem.

Efeito líquido: não há allowlist por conexão de claims a preservar. Todas as claims não-protocolares que o upstream coloca no id_token são capturadas; quais delas chegam aos tokens a jusante é controlado pelas `UserClaims` do scope a jusante: declare a claim aí e o valor flui.

`FederationClaims` sobrevive às rotações de refresh de forma distinta de `CustomAttributes`, pelo que o contexto de federação por sessão (por exemplo, um token de share-link capturado no authorize original) permanece intacto enquanto os atributos por utilizador continuam a ser relidos do user store.

## Parâmetros de query de passthrough

`OidcProviderConfig.PassthroughParams` é uma whitelist por conexão de chaves de query que fluem do pedido `/authorize` original para o URL de autorização do IdP upstream. O conjunto padrão (`scope`, `state`, `nonce`, PKCE) é sempre encaminhado; isto destina-se a valores adicionais especificados pela RP, como uma credencial de uso único de que o upstream precisa para autenticar (por exemplo, `link_token` para IdPs de share-link).

Quando uma chave está na whitelist, o Authagonal extrai o seu valor da query do `/authorize` original (transportada via `returnUrl`) e acrescenta-o ao URL upstream. Tudo o que não estiver na whitelist é descartado silenciosamente.

## Limite de vida da sessão

`OidcProviderConfig.SessionExpClaim` é o nome opcional de uma claim do id_token (segundos Unix) cujo valor limita o tempo de vida da sessão local. Quando presente, o valor upstream segue como `session_max_exp` no ticket do cookie e no auth code emitido; os tokens de acesso / id / refresh são limitados de modo que nenhum token, incluindo os cunhados a partir de rotações, sobreviva à sessão upstream. Útil quando o IdP upstream impõe limites de sessão mais curtos do que o Authagonal imporia por padrão.

## Funcionalidades de Segurança

- **PKCE**: code_challenge com S256 em cada pedido de autorização
- **Validação de nonce**: nonce armazenado com o state, tem de estar presente no id_token e corresponder
- **Validação de state**: uso único (consumido atomicamente via `IOidcStateStore`, persistido com expiração) **e vinculado ao navegador**: um cookie `SameSite=Lax` com âmbito `/oidc` é definido no login e tem de corresponder ao `state` no callback, de modo que um atacante não possa concluir um fluxo de federação que iniciou e entregar o URL de callback a uma vítima (login CSRF)
- **Validação de assinatura do id_token**: chaves obtidas do endpoint JWKS do IdP; issuer, audience e tempo de vida validados
- **Fallback para userinfo**: se o id_token não contiver um e-mail, o endpoint userinfo é tentado. O `sub` do userinfo tem de corresponder ao `sub` do id_token (OIDC Core 5.3.2), caso contrário a resposta é ignorada
- **Vinculação de identidade estável**: um utilizador que regressa é resolvido por provedor + `sub`, nunca apenas por e-mail. Anexar uma identidade federada a uma conta local **pré-existente** por e-mail exige que os `AllowedDomains` da conexão abranjam o domínio desse e-mail, a garantia explícita do administrador de que o IdP o possui. Um `email_verified` afirmado pelo upstream *não* é suficiente para tomar posse de uma conta existente
- **Imposição de domínio**: quando `AllowedDomains` está definido, a conexão só pode afirmar identidades dentro desses domínios (`access_denied` caso contrário)
- **Exclusão de JIT**: `DisableJitProvisioning` rejeita utilizadores desconhecidos em vez de os criar automaticamente
- **Proteção contra open-redirect**: `returnUrl` tem de ser um caminho relativo do mesmo site; formas relativas ao protocolo (`//`) e com barra invertida são rejeitadas
- **O MFA local continua a aplicar-se**: a federação prova apenas o primeiro fator. Um utilizador inscrito em MFA (ou cujo cliente exige MFA por política) é encaminhado pelas páginas locais de desafio/configuração de MFA após o callback, em vez de ser autenticado diretamente; só então a sessão passa a conter o marcador de MFA

## Especificidades do Azure AD

O Azure AD por vezes retorna e-mails como um array JSON na claim `emails` (especialmente para B2C). O Authagonal trata isto verificando tanto a claim `email` como o array `emails`.

## Provedores Suportados

Qualquer provedor compatível com OIDC que suporte:
- Fluxo Authorization Code
- PKCE (S256)
- Documento de descoberta (`.well-known/openid-configuration`)

Testado com:
- Google
- Apple
- Azure AD / Entra ID
- Azure AD B2C
