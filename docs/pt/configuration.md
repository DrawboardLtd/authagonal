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
| `Auth:MaxFailedAttempts` | `5` | Tentativas de login falhadas antes do bloqueio da conta |
| `Auth:LockoutDurationMinutes` | `10` | Duração do bloqueio da conta após o máximo de tentativas falhadas |
| `Auth:MaxRegistrationsPerIp` | `5` | Máximo de registos por endereço IP dentro da janela |
| `Auth:RegistrationWindowMinutes` | `60` | Janela de limitação de taxa de registo |
| `Auth:EmailVerificationExpiryHours` | `24` | Tempo de vida do link de verificação de e-mail |
| `Auth:PasswordResetExpiryMinutes` | `60` | Tempo de vida do link de redefinição de senha |
| `Auth:MfaChallengeExpiryMinutes` | `5` | Tempo de vida do token de verificação MFA |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | Tempo de vida do token de configuração MFA (para inscrição forçada) |
| `Auth:Pbkdf2Iterations` | `100000` | Contagem de iterações PBKDF2 para hashing de senhas |
| `Auth:RefreshTokenReuseGraceSeconds` | `60` | Janela de tolerância para reutilização concorrente de refresh token |
| `Auth:SigningKeyLifetimeDays` | `90` | Tempo de vida da chave de assinatura RSA antes da rotação automática |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Frequência de recarregamento das chaves de assinatura do armazenamento |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Intervalo entre verificações do carimbo de segurança do cookie |

## Cache e Tempos Limite

| Definição | Padrão | Descrição |
|---|---|---|
| `Cache:CorsCacheMinutes` | `60` | Tempo de cache das origens CORS permitidas |
| `Cache:OidcDiscoveryCacheMinutes` | `60` | Duração do cache do documento de descoberta OIDC |
| `Cache:SamlMetadataCacheMinutes` | `60` | Duração do cache dos metadados SAML do IdP |
| `Cache:OidcStateLifetimeMinutes` | `10` | Tempo de vida do parâmetro state de autorização OIDC |
| `Cache:SamlReplayLifetimeMinutes` | `10` | Tempo de vida do ID AuthnRequest SAML (prevenção de replay) |
| `Cache:HealthCheckTimeoutSeconds` | `5` | Tempo limite da verificação de saúde do Table Storage |

## Serviços em Segundo Plano

| Definição | Padrão | Descrição |
|---|---|---|
| `BackgroundServices:TokenCleanupDelayMinutes` | `5` | Atraso inicial antes da primeira limpeza de tokens expirados |
| `BackgroundServices:TokenCleanupIntervalMinutes` | `60` | Intervalo de limpeza de tokens expirados |
| `BackgroundServices:GrantReconciliationDelayMinutes` | `10` | Atraso inicial antes da primeira reconciliação de concessões |
| `BackgroundServices:GrantReconciliationIntervalMinutes` | `30` | Intervalo de reconciliação de concessões |

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
      "MfaPolicy": "Enabled",
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

## Política de MFA

A autenticação multifator é aplicada por cliente através da propriedade `MfaPolicy`:

| Valor | Comportamento |
|---|---|
| `Disabled` (padrão) | Sem desafio MFA, mesmo que o utilizador tenha MFA inscrito |
| `Enabled` | Desafia utilizadores que têm MFA inscrito; não força a inscrição |
| `Required` | Desafia utilizadores inscritos; força a inscrição para utilizadores sem MFA |

```json
{
  "Clients": [
    {
      "ClientId": "secure-app",
      "MfaPolicy": "Required"
    }
  ]
}
```

Quando `MfaPolicy` é `Required` e o utilizador não tem MFA inscrito, o login retorna `{ mfaSetupRequired: true, setupToken: "..." }`. O token de configuração autentica o utilizador nos endpoints de configuração de MFA (via cabeçalho `X-MFA-Setup-Token`) para que possam inscrever-se antes de obter uma sessão de cookie.

Logins federados (SAML/OIDC) ignoram o MFA — o provedor de identidade externo trata disso.

### Substituição via IAuthHook

O método `IAuthHook.ResolveMfaPolicyAsync` pode substituir a política do cliente por utilizador:

```csharp
public Task<MfaPolicy> ResolveMfaPolicyAsync(
    string userId, string email, MfaPolicy clientPolicy,
    string clientId, CancellationToken ct)
{
    // Forçar MFA para utilizadores admin independentemente da definição do cliente
    if (email.EndsWith("@admin.example.com"))
        return Task.FromResult(MfaPolicy.Required);

    return Task.FromResult(clientPolicy);
}
```

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

## Cluster

As instâncias do Authagonal formam automaticamente um cluster para partilhar o estado de limitação de taxa. O clustering é habilitado por padrão sem necessidade de configuração.

| Definição | Variável de Ambiente | Padrão | Descrição |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Interruptor principal do clustering. Defina como `false` para limitação de taxa apenas local. |
| `Cluster:MulticastGroup` | `Cluster__MulticastGroup` | `239.42.42.42` | Grupo multicast UDP para descoberta de peers |
| `Cluster:MulticastPort` | `Cluster__MulticastPort` | `19847` | Porta multicast UDP para descoberta de peers |
| `Cluster:InternalUrl` | `Cluster__InternalUrl` | *(nenhum)* | URL com balanceamento de carga como fallback para gossip quando o multicast não está disponível |
| `Cluster:Secret` | `Cluster__Secret` | *(nenhum)* | Segredo partilhado para autenticação do endpoint de gossip (recomendado quando `InternalUrl` está definido) |
| `Cluster:GossipIntervalSeconds` | `Cluster__GossipIntervalSeconds` | `5` | Frequência com que as instâncias trocam estado de limitação de taxa |
| `Cluster:DiscoveryIntervalSeconds` | `Cluster__DiscoveryIntervalSeconds` | `10` | Frequência com que as instâncias se anunciam via multicast |
| `Cluster:PeerStaleAfterSeconds` | `Cluster__PeerStaleAfterSeconds` | `30` | Descartar peers sem comunicação após este número de segundos |

**Zero-config (padrão):** As instâncias descobrem-se mutuamente via multicast UDP. Funciona em Kubernetes, Docker Compose ou qualquer rede partilhada.

**Multicast desabilitado (ex.: algumas VPCs na cloud):**

```json
{
  "Cluster": {
    "InternalUrl": "http://authagonal-auth.svc.cluster.local:8080",
    "Secret": "shared-secret-here"
  }
}
```

**Clustering totalmente desabilitado:**

```json
{
  "Cluster": {
    "Enabled": false
  }
}
```

Consulte [Escalabilidade](scaling) para mais detalhes sobre como funciona a limitação de taxa distribuída.

## Limitação de Taxa

Limites de taxa integrados por IP são aplicados em todas as instâncias através do protocolo de gossip do cluster:

| Endpoint | Limite | Janela |
|---|---|---|
| `POST /api/auth/register` | 5 registos | 1 hora |

Quando o clustering está habilitado, estes limites são consolidados entre todas as instâncias. Quando desabilitado, cada instância aplica o seu próprio limite de forma independente.

## CORS

O CORS é configurado dinamicamente. As origens de todos os `AllowedCorsOrigins` dos clientes registados são automaticamente permitidas, com um cache de 60 minutos.

## Exemplo Completo

```json
{
  "Storage": {
    "ConnectionString": "DefaultEndpointsProtocol=https;AccountName=...;AccountKey=...;TableEndpoint=https://..."
  },
  "Issuer": "https://auth.example.com",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "SigningKeyLifetimeDays": 90
  },
  "Cluster": {
    "Enabled": true
  },
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
      "MfaPolicy": "Enabled",
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
