---
layout: default
title: Configuração
locale: pt
---

# Configuração

O Authagonal é configurado via `appsettings.json` ou variáveis de ambiente. As variáveis de ambiente usam `__` como separador de seção (por exemplo, `Storage__ConnectionString`).

## Definições Obrigatórias

O armazenamento pode ser configurado de uma de duas formas — forneça **ou** `Storage:ConnectionString` **ou** `Storage:TableServiceUri` (o caminho de identidade gerida, preferido em produção).

| Definição | Variável de Ambiente | Descrição |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | String de conexão do Azure Table Storage com uma chave de conta. Adequada para dev / Azurite. |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | Endpoint do Table Storage por identidade gerida, ex.: `https://{account}.table.core.windows.net/`. Alternativa a `Storage:ConnectionString` e **preferida em produção** — autentica-se via `DefaultAzureCredential`, portanto nenhuma chave de acesso é alguma vez colocada num segredo. O host deve conceder à identidade da carga de trabalho o papel **Storage Table Data Contributor**. |
| `Issuer` | `Issuer` | A URL base pública deste servidor (ex.: `https://auth.example.com`) |

## Armazenamento

| Definição | Variável de Ambiente | Padrão | Descrição |
|---|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | *(nenhum)* | String de conexão com chave de conta (consulte Definições Obrigatórias). |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | *(nenhum)* | URI do Table Storage por identidade gerida (consulte Definições Obrigatórias). Tem precedência sobre `Storage:ConnectionString` quando ambos estão definidos. |
| `Storage:NameIndexesEnabled` | `Storage__NameIndexesEnabled` | `true` | Se devem ser mantidas as tabelas de índice de pesquisa por prefixo `UserFirstNames` / `UserLastNames` que suportam a pesquisa por prefixo de nome no admin. Defina `false` em hosts que não exponham a pesquisa de nomes no admin para evitar essas gravações. **Nota de escalabilidade:** estes índices usam uma única partição quente e limitam o débito a cerca de 2.000 ops/seg em escala — desabilite-os se não precisar de pesquisa por nome. |
| `LoginAppUrl` | `LoginAppUrl` | `/login` | URL base para a qual o endpoint `/connect/authorize` redireciona para a SPA de login (telas de login, step-up e consentimento). Defina isto quando a interface de login for servida a partir de uma origem diferente da do servidor; por padrão usa o caminho relativo `/login` servido pela SPA incluída. |

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
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | Janela de tolerância opcional (segundos) para reutilização concorrente de refresh token. `0` (padrão) mantém a postura estrita: qualquer reutilização de um refresh token consumido revoga todos os tokens para aquele utilizador+cliente. Defina `> 0` para tratar uma reutilização dentro da janela como uma repetição idempotente (reentrega os tokens sucessores) — útil para clientes móveis com falhas de conectividade. |
| `Auth:DynamicClientRegistrationEnabled` | `false` | Habilita o endpoint de registo dinâmico de clientes `POST /connect/register` (RFC 7591). Desabilitado por padrão porque o registo aberto pode ser abusado em implantações multi-tenant. Consulte [Registo Dinâmico de Clientes](client-registration). |
| `Auth:SigningKeyLifetimeDays` | `90` | Tempo de vida da chave de assinatura RSA antes da rotação automática |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Frequência de recarregamento das chaves de assinatura do armazenamento |
| `Auth:KeyRotationEnabled` | `false` | Habilita a rotação automática das chaves de assinatura |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | Frequência de verificação se a chave ativa precisa de rotação |
| `Auth:KeyRotationLeadTimeDays` | `14` | Rodar quando a chave ativa expirar dentro deste número de dias |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Intervalo entre verificações do carimbo de segurança do cookie |
| `DataProtection:BlobUri` | *(nenhum)* | URI de Blob do Azure para persistir chaves de Data Protection entre instâncias |

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
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
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
| `urn:ietf:params:oauth:grant-type:device_code` | Concessão de autorização de dispositivo (RFC 8628) para dispositivos com entrada limitada |

### Utilização do Refresh Token

| Valor | Comportamento |
|---|---|
| `OneTime` (padrão) | Cada refresh emite um novo refresh token e invalida o antigo. Por padrão (`Auth:RefreshTokenReuseGraceSeconds = 0`) qualquer reutilização de um token consumido revoga imediatamente todos os tokens para aquele utilizador+cliente — **não** há janela de tolerância ativa por padrão. Defina `Auth:RefreshTokenReuseGraceSeconds` para um valor positivo para optar por uma janela de tolerância a repetições. |
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

Os segredos de clientes OIDC upstream e as sementes TOTP / MFA podem ser armazenados no Azure Key Vault em vez de em texto simples:

| Definição | Descrição |
|---|---|
| `SecretProvider:VaultUri` | URI do Key Vault (ex.: `https://my-vault.vault.azure.net/`). Se não definido, é usado o provedor de **texto simples** e os segredos são armazenados como estão no Table Storage. |

Quando configurado, os valores de segredo que se assemelham a referências do Key Vault são resolvidos em tempo de execução. Usa `DefaultAzureCredential` para autenticação.

> ⚠️ **Produção: defina `SecretProvider:VaultUri`.** O provedor de segredos padrão é **texto simples**. Quando `SecretProvider:VaultUri` não está definido, os segredos de clientes OIDC upstream e as sementes TOTP / MFA são escritos no Azure Table Storage em texto claro — e, portanto, aparecem em texto claro em qualquer [backup](backup-restore). Para qualquer implantação em produção, configure `SecretProvider:VaultUri` para que esses segredos sejam armazenados no Key Vault.

## API de Administração

| Definição | Padrão | Descrição |
|---|---|---|
| `AdminApi:Enabled` | `true` | **Habilitada por padrão.** Defina como `false` para desabilitar todos os endpoints de administração (não serão registados). |
| `AdminApi:Scope` | `authagonal-admin` | Scope JWT necessário para aceder aos endpoints de administração. Altere isto para corresponder ao seu nome de scope existente (ex.: `projects-identity-admin` para migrações do IdentityServer). |

> ⚠️ **A API de administração está habilitada por padrão e é altamente privilegiada.** O scope de administração concede gestão total e impersonação de utilizadores — qualquer pessoa que possua um token com `AdminApi:Scope` pode emitir tokens para qualquer utilizador, gerir clientes e ler/escrever toda a configuração. Restrinja a nível de rede os endpoints de administração (as rotas de administração `/api/v1/*`) e controle rigorosamente quem pode receber o scope de administração. Como medida de defesa em profundidade, o scope é *reservado*: nunca pode ser concedido a um cliente OAuth (consulte [API de Administração](admin-api)) e não pode ser emitido através do endpoint de impersonação. Defina `AdminApi:Enabled = false` por completo se a API de administração não for usada.

## Consentimento

O consentimento por cliente pode ser habilitado com a propriedade `RequireConsent`:

| Valor | Comportamento |
|---|---|
| `false` (padrão) | A autorização prossegue imediatamente após a autenticação |
| `true` | É exibida ao utilizador uma tela de consentimento listando os scopes solicitados. O consentimento é persistido por 5 anos e só é solicitado novamente quando novos scopes são pedidos. |

Os utilizadores podem ver e revogar as suas concessões de consentimento em `GET /consent/grants` e `DELETE /consent/grants/{clientId}`.

## Logout via Back-Channel

Registe um `BackChannelLogoutUri` num cliente para receber notificações de OIDC Back-Channel Logout 1.0. Quando um utilizador faz logout, o Authagonal envia um token de logout assinado (JWT) para o URI registado de cada cliente.

```json
{
  "Clients": [
    {
      "ClientId": "my-app",
      "BackChannelLogoutUri": "https://app.example.com/logout-callback"
    }
  ]
}
```

## E-mail

Por padrão, o Authagonal usa um serviço de e-mail no-op que descarta silenciosamente todos os e-mails. Para habilitar o envio de e-mails, registre uma implementação de `IEmailService` antes de chamar `AddAuthagonal()`.

O `EmailService` integrado usa o [Resend](https://resend.com). Para usá-lo, registre-o explicitamente:

```csharp
services.AddSingleton<IEmailService, EmailService>();
services.AddAuthagonal(configuration);
```

| Definição | Descrição |
|---|---|
| `Email:ResendApiKey` | Chave de API do Resend para envio de e-mails |
| `Email:SenderEmail` | Endereço de e-mail do remetente |
| `Email:SenderName` | Nome de exibição do remetente (padrão: `"Authagonal"`) |

E-mails para endereços `@example.com` são silenciosamente ignorados (útil para testes).

## Cluster

As instâncias do Authagonal formam automaticamente um cluster para partilhar o estado de limitação de taxa. O clustering é habilitado por padrão sem necessidade de configuração.

| Definição | Variável de Ambiente | Padrão | Descrição |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Interruptor principal do clustering. Defina como `false` para limitação de taxa apenas local. |
| `Cluster:MulticastGroup` | `Cluster__MulticastGroup` | `239.42.42.42` | Grupo multicast UDP para descoberta de peers |
| `Cluster:MulticastPort` | `Cluster__MulticastPort` | `19847` | Porta multicast UDP para descoberta de peers |
| `Cluster:InternalUrl` | `Cluster__InternalUrl` | *(nenhum)* | URL com balanceamento de carga como fallback para gossip quando o multicast não está disponível |
| `Cluster:Secret` | `Cluster__Secret` | *(nenhum)* | Segredo partilhado exigido nos endpoints exclusivamente internos (`/_internal/cluster/gossip` e `/_internal/backchannel-logout`). Quando definido, os chamadores devem apresentá-lo no cabeçalho `X-Cluster-Secret` (comparado em tempo constante). Quando **não definido**, esses endpoints só são acessíveis a partir de IPs de origem de loopback / privados (RFC 1918 / link-local / ULA) — um pedido externo com um IP público é rejeitado. Recomendado sempre que `InternalUrl` rotear o gossip através de um balanceador de carga. |
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

## Cabeçalhos Encaminhados (proxy confiável)

O Authagonal indexa a limitação de taxa e o bloqueio de conta pelo IP do cliente, e só emite HSTS em pedidos HTTPS. Atrás de um proxy reverso / ingress, o IP real do cliente e o esquema chegam nos cabeçalhos `X-Forwarded-For` / `X-Forwarded-Proto`. Estas definições controlam **quais saltos de proxy são confiáveis** para definir esses valores, de modo que um chamador não possa falsificar `X-Forwarded-For` para forjar o IP do cliente.

| Definição | Variável de Ambiente | Padrão | Descrição |
|---|---|---|---|
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | `1` | Número de saltos de proxy a honrar a partir da direita da cadeia `X-Forwarded-For`. O padrão de `1` confia apenas no único salto que o seu ingress acrescenta e ignora qualquer coisa mais à esquerda na cadeia. |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0` (array) | *(vazio)* | Faixas CIDR (array de strings, ex.: `"10.0.0.0/8"`) autorizadas a definir cabeçalhos encaminhados. **Garantia mais forte:** defina isto para o CIDR do seu ingress / pod para que apenas essa rede possa definir o IP do cliente. |
| `ForwardedHeaders:KnownProxies` | `ForwardedHeaders__KnownProxies__0` (array) | *(vazio)* | Endereços IP de proxy individuais (array de strings) autorizados a definir cabeçalhos encaminhados. Use em conjunto com ou em vez de `KnownNetworks`. |

```json
{
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"],
    "KnownProxies": []
  }
}
```

> ⚠️ **Proxy com terminação TLS obrigatório.** O Authagonal deve ser executado atrás de um proxy reverso com terminação TLS. O cookie de sessão usa `SecurePolicy = SameAsRequest` e o HSTS (`Strict-Transport-Security`) só é emitido em pedidos HTTPS, portanto o proxy deve encaminhar `X-Forwarded-Proto: https` para que os cookies sejam marcados como `Secure` e o HSTS seja enviado. Configure `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` para o seu proxy confiável de modo que o esquema e o IP do cliente não possam ser falsificados.

## Limitação de Taxa

Limites de taxa integrados por IP são aplicados em todas as instâncias através do protocolo de gossip do cluster:

| Endpoint | Limite | Janela |
|---|---|---|
| `POST /api/auth/register` | 5 registos | 1 hora |

Quando o clustering está habilitado, estes limites são consolidados entre todas as instâncias. Quando desabilitado, cada instância aplica o seu próprio limite de forma independente.

## CORS

O CORS é configurado dinamicamente. As origens de todos os `AllowedCorsOrigins` dos clientes registados são automaticamente permitidas, com um cache de 60 minutos.

## HashiCorp Vault Transit

O Authagonal pode assinar JWTs usando o motor de segredos Transit do HashiCorp Vault. As chaves privadas nunca saem do Vault — apenas a operação de assinatura é delegada remotamente. As chaves públicas são armazenadas em cache localmente para verificação.

Isto é configurado programaticamente ao hospedar como biblioteca. Consulte [Extensibilidade](extensibility) para detalhes.

## Exemplo Completo

```json
{
  "Storage": {
    "TableServiceUri": "https://myaccount.table.core.windows.net/",
    "NameIndexesEnabled": true
  },
  "Issuer": "https://auth.example.com",
  "LoginAppUrl": "/login",
  "Auth": {
    "MaxFailedAttempts": 5,
    "LockoutDurationMinutes": 10,
    "MaxRegistrationsPerIp": 5,
    "RegistrationWindowMinutes": 60,
    "EmailVerificationExpiryHours": 24,
    "PasswordResetExpiryMinutes": 60,
    "Pbkdf2Iterations": 100000,
    "RefreshTokenReuseGraceSeconds": 0,
    "DynamicClientRegistrationEnabled": false,
    "SigningKeyLifetimeDays": 90
  },
  "SecretProvider": {
    "VaultUri": "https://my-vault.vault.azure.net/"
  },
  "ForwardedHeaders": {
    "ForwardLimit": 1,
    "KnownNetworks": ["10.244.0.0/16"]
  },
  "Cluster": {
    "Enabled": true,
    "Secret": "shared-secret-here"
  },
  "AdminApi": {
    "Enabled": true,
    "Scope": "authagonal-admin"
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
    "ResendApiKey": "re_xxx",
    "SenderEmail": "noreply@example.com",
    "SenderName": "Example Auth"
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
      "RequireConsent": false,
      "BackChannelLogoutUri": "https://app.example.com/logout-callback",
      "ProvisioningApps": ["backend"]
    }
  ]
}
```
