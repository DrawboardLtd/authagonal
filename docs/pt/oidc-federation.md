---
layout: default
title: Federação OIDC
locale: pt
---

# Federação OIDC

O Authagonal pode federar a autenticação para provedores de identidade OIDC externos (Google, Apple, Azure AD, etc.). Isto permite fluxos do tipo "Entrar com o Google" enquanto o Authagonal permanece como o servidor de autenticação central.

## Como Funciona

1. O utilizador introduz o seu e-mail na página de login
2. O SPA chama `/api/auth/sso-check` — se o domínio do e-mail estiver vinculado a um provedor OIDC, o SSO é obrigatório
3. O utilizador clica em "Continuar com SSO" e é redirecionado para o IdP externo
4. Após a autenticação, o IdP redireciona de volta para `/oidc/callback`
5. O Authagonal valida o id_token, cria/vincula o utilizador e define um cookie de sessão

## Configuração

### 1. Criar um Provedor OIDC

**Opção A — Configuração (recomendado para configurações estáticas):**

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

Os provedores são semeados na inicialização. O `ClientSecret` é protegido via `ISecretProvider` (Key Vault quando configurado, texto simples caso contrário). Os mapeamentos de domínio SSO são registados automaticamente a partir de `AllowedDomains`.

**Opção B — API de Administração (para gestão em tempo de execução):**

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
| `GET /oidc/{connectionId}/login?returnUrl=...` | Inicia o login OIDC. Gera PKCE + state + nonce, redireciona para o endpoint de autorização do IdP. |
| `GET /oidc/callback` | Trata o callback do IdP. Troca o código por tokens, valida o id_token, cria/autentica o utilizador. |

## Funcionalidades de Segurança

- **PKCE** — code_challenge com S256 em cada pedido de autorização
- **Validação de nonce** — nonce armazenado no state, verificado no id_token
- **Validação de state** — uso único, armazenado no Azure Table Storage com expiração
- **Validação de assinatura do id_token** — chaves obtidas do endpoint JWKS do IdP
- **Fallback para userinfo** — se o id_token não contiver um e-mail, o endpoint userinfo é tentado

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
