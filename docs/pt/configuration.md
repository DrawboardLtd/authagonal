---
layout: default
title: Configuração
locale: pt
---

# Configuração

O Authagonal é configurado via `appsettings.json` ou variáveis de ambiente. As variáveis de ambiente usam `__` como separador de seção (por exemplo, `Storage__ConnectionString`).

## Definições Obrigatórias

| Definição | Variável de Ambiente | Descrição |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | String de conexão do Azure Table Storage |
| `Issuer` | `Issuer` | A URL base pública deste servidor (ex.: `https://auth.example.com`) |

## Autenticação

| Definição | Padrão | Descrição |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Tempo de vida da sessão do cookie (deslizante) |

## Clientes

Os clientes são definidos no array `Clients` e semeados na inicialização. Cada cliente pode ter:

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "ClientName": "My Application",
      "ClientSecretHashes": ["sha256-hash-here"],
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email", "custom-scope"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "AlwaysIncludeUserClaimsInIdToken": false,
      "AccessTokenLifetimeSeconds": 1800,
      "IdentityTokenLifetimeSeconds": 300,
      "AuthorizationCodeLifetimeSeconds": 300,
      "AbsoluteRefreshTokenLifetimeSeconds": 2592000,
      "SlidingRefreshTokenLifetimeSeconds": 1296000,
      "RefreshTokenUsage": "OneTime",
      "ProvisioningApps": ["my-backend"]
    }
  ]
}
```

### Tipos de Grant

| Tipo de Grant | Caso de Uso |
|---|---|
| `authorization_code` | Login interativo de utilizador (aplicações web, SPAs, mobile) |
| `client_credentials` | Comunicação serviço-a-serviço |
| `refresh_token` | Renovação de token (requer `AllowOfflineAccess: true`) |

### Utilização do Refresh Token

| Valor | Comportamento |
|---|---|
| `OneTime` (padrão) | Cada refresh emite um novo refresh token. O antigo é invalidado com uma janela de tolerância de 60 segundos para pedidos concorrentes. Reutilização após a janela de tolerância revoga todos os tokens para aquele utilizador+cliente. |
| `ReUse` | O mesmo refresh token é reutilizado até expirar. |

### Aplicações de Provisionamento

O array `ProvisioningApps` referencia IDs de aplicações definidos na seção de configuração `ProvisioningApps`. Quando um utilizador autoriza através deste cliente, é provisionado nessas aplicações via TCC. Consulte [Provisionamento](provisioning) para detalhes.

## Aplicações de Provisionamento

Defina as aplicações downstream nas quais os utilizadores devem ser provisionados:

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-api-key"
    },
    "analytics": {
      "CallbackUrl": "https://analytics.example.com/provisioning",
      "ApiKey": "another-key"
    }
  }
}
```

Consulte [Provisionamento](provisioning) para a especificação completa do protocolo TCC.

## Política de Senhas

Personalize os requisitos de complexidade de senha:

```json
{
  "PasswordPolicy": {
    "MinLength": 10,
    "MinUniqueChars": 3,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": false
  }
}
```

| Propriedade | Padrão | Descrição |
|---|---|---|
| `MinLength` | `8` | Comprimento mínimo da senha |
| `MinUniqueChars` | `2` | Número mínimo de caracteres distintos |
| `RequireUppercase` | `true` | Exigir pelo menos uma letra maiúscula |
| `RequireLowercase` | `true` | Exigir pelo menos uma letra minúscula |
| `RequireDigit` | `true` | Exigir pelo menos um dígito |
| `RequireSpecialChar` | `true` | Exigir pelo menos um caractere não alfanumérico |

A política é aplicada na redefinição de senha e no registo de utilizadores pelo administrador. A interface de login obtém a política ativa de `GET /api/auth/password-policy` para exibir os requisitos dinamicamente.

## Provedores SAML

Defina provedores de identidade SAML na configuração. Estes são semeados na inicialização:

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com", "example.org"]
    }
  ]
}
```

| Propriedade | Obrigatório | Descrição |
|---|---|---|
| `ConnectionId` | Sim | Identificador estável (usado em URLs como `/saml/{connectionId}/login`) |
| `ConnectionName` | Não | Nome de exibição (padrão: ConnectionId) |
| `EntityId` | Sim | ID da entidade do Service Provider SAML |
| `MetadataLocation` | Sim | URL para o XML de metadados SAML do IdP |
| `AllowedDomains` | Não | Domínios de e-mail roteados para este provedor via SSO |

## Provedores OIDC

Defina provedores de identidade OIDC na configuração. Estes são semeados na inicialização:

```json
{
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "your-client-id",
      "ClientSecret": "your-client-secret",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["example.com"]
    }
  ]
}
```

| Propriedade | Obrigatório | Descrição |
|---|---|---|
| `ConnectionId` | Sim | Identificador estável (usado em URLs como `/oidc/{connectionId}/login`) |
| `ConnectionName` | Não | Nome de exibição (padrão: ConnectionId) |
| `MetadataLocation` | Sim | URL para o documento de descoberta OpenID Connect do IdP |
| `ClientId` | Sim | ID de cliente OAuth2 registado no IdP |
| `ClientSecret` | Sim | Segredo de cliente OAuth2 (protegido via `ISecretProvider` na inicialização) |
| `RedirectUrl` | Sim | URI de redirecionamento OAuth2 registado no IdP |
| `AllowedDomains` | Não | Domínios de e-mail roteados para este provedor via SSO |

> **Nota:** Os provedores também podem ser geridos em tempo de execução via a [API de Administração](admin-api). Os provedores semeados pela configuração são inseridos/atualizados em cada inicialização, portanto as alterações de configuração entram em vigor ao reiniciar.

## Provedor de Segredos

Os segredos de clientes e provedores OIDC podem ser opcionalmente armazenados no Azure Key Vault:

| Definição | Descrição |
|---|---|
| `SecretProvider:VaultUri` | URI do Key Vault (ex.: `https://my-vault.vault.azure.net/`). Se não definido, os segredos são tratados como texto simples. |

Quando configurado, os valores de segredo que se assemelham a referências do Key Vault são resolvidos em tempo de execução. Usa `DefaultAzureCredential` para autenticação.

## E-mail

Por padrão, o Authagonal usa um serviço de e-mail no-op que descarta silenciosamente todos os e-mails. Para habilitar o envio de e-mails, registre uma implementação de `IEmailService` antes de chamar `AddAuthagonal()`. O serviço integrado `EmailService` usa o SendGrid.

| Definição | Descrição |
|---|---|
| `Email:SendGridApiKey` | Chave de API do SendGrid para envio de e-mails |
| `Email:FromAddress` | Endereço de e-mail do remetente |
| `Email:FromName` | Nome de exibição do remetente |
| `Email:VerificationTemplateId` | ID do template dinâmico do SendGrid para verificação de e-mail |
| `Email:PasswordResetTemplateId` | ID do template dinâmico do SendGrid para redefinição de senha |

E-mails para endereços `@example.com` são silenciosamente ignorados (útil para testes).

## Limitação de Taxa

Limites de taxa integrados por IP:

| Grupo de Endpoints | Limite | Janela |
|---|---|---|
| Endpoints de autenticação (login, SSO) | 20 pedidos | 1 minuto |
| Endpoint de token | 30 pedidos | 1 minuto |

## CORS

O CORS é configurado dinamicamente. As origens de todos os `AllowedCorsOrigins` dos clientes registados são automaticamente permitidas, com um cache de 60 minutos.

## Exemplo Completo

```json
{
  "Storage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..."
  },
  "Issuer": "https://auth.example.com",
  "Authentication": {
    "CookieLifetimeHours": 48
  },
  "PasswordPolicy": {
    "MinLength": 8,
    "RequireUppercase": true,
    "RequireLowercase": true,
    "RequireDigit": true,
    "RequireSpecialChar": true
  },
  "Email": {
    "SendGridApiKey": "SG.xxx",
    "FromAddress": "noreply@example.com",
    "FromName": "Example Auth",
    "VerificationTemplateId": "d-xxx",
    "PasswordResetTemplateId": "d-yyy"
  },
  "SamlProviders": [
    {
      "ConnectionId": "azure-ad",
      "ConnectionName": "Azure AD",
      "EntityId": "https://auth.example.com",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant}/FederationMetadata/2007-06/FederationMetadata.xml",
      "AllowedDomains": ["example.com"]
    }
  ],
  "OidcProviders": [
    {
      "ConnectionId": "google",
      "ConnectionName": "Google",
      "MetadataLocation": "https://accounts.google.com/.well-known/openid-configuration",
      "ClientId": "...",
      "ClientSecret": "...",
      "RedirectUrl": "https://auth.example.com/oidc/callback",
      "AllowedDomains": ["gmail.com"]
    }
  ],
  "ProvisioningApps": {
    "backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret"
    }
  },
  "Clients": [
    {
      "ClientId": "web",
      "ClientName": "Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["https://app.example.com/callback"],
      "PostLogoutRedirectUris": ["https://app.example.com"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["https://app.example.com"],
      "RequirePkce": true,
      "RequireClientSecret": false,
      "AllowOfflineAccess": true,
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
