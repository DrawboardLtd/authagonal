---
layout: default
title: Configuração
locale: pt
---

# Configuração

O Authagonal é configurado via `appsettings.json` ou variáveis de ambiente. As variáveis de ambiente usam `__` como separador de seção (por exemplo, `Storage__ConnectionString`).

## Definições Obrigatórias

O armazenamento pode ser configurado de uma de duas formas: forneça **ou** `Storage:ConnectionString` **ou** `Storage:TableServiceUri` (o caminho de identidade gerida, preferido em produção).

| Definição | Variável de Ambiente | Descrição |
|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | String de conexão do Azure Table Storage com uma chave de conta. Adequada para dev / Azurite. |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | Endpoint do Table Storage por identidade gerida, ex.: `https://{account}.table.core.windows.net/`. Alternativa a `Storage:ConnectionString` e **preferida em produção**: autentica-se via `DefaultAzureCredential`, portanto nenhuma chave de acesso é alguma vez colocada num segredo. O host deve conceder à identidade da carga de trabalho o papel **Storage Table Data Contributor**. |
| `Issuer` | `Issuer` | A URL base pública deste servidor (ex.: `https://auth.example.com`) |

## Armazenamento

| Definição | Variável de Ambiente | Padrão | Descrição |
|---|---|---|---|
| `Storage:ConnectionString` | `Storage__ConnectionString` | *(nenhum)* | String de conexão com chave de conta (consulte Definições Obrigatórias). |
| `Storage:TableServiceUri` | `Storage__TableServiceUri` | *(nenhum)* | URI do Table Storage por identidade gerida (consulte Definições Obrigatórias). Tem precedência sobre `Storage:ConnectionString` quando ambos estão definidos. |
| `Storage:NameIndexesEnabled` | `Storage__NameIndexesEnabled` | `true` | Se devem ser mantidas as tabelas de índice de pesquisa por prefixo `UserFirstNames` / `UserLastNames` que suportam a pesquisa por prefixo de nome no admin. Defina `false` em hosts que não exponham a pesquisa de nomes no admin para evitar essas gravações. **Nota de escalabilidade:** estes índices usam uma única partição quente e limitam o débito a cerca de 2.000 ops/seg em escala: desabilite-os se não precisar de pesquisa por nome. |
| `LoginAppUrl` | `LoginAppUrl` | `/login` | URL base para a qual o endpoint `/connect/authorize` redireciona para a SPA de login (telas de login, step-up e consentimento). Defina isto quando a interface de login for servida a partir de uma origem diferente da do servidor; por padrão usa o caminho relativo `/login` servido pela SPA incluída. |

## Autenticação

| Definição | Padrão | Descrição |
|---|---|---|
| `Authentication:CookieLifetimeHours` | `48` | Tempo de vida da sessão do cookie (deslizante) |
| `Authentication:AlwaysSecureCookie` | `false` | Força incondicionalmente a flag `Secure` do cookie de sessão. O padrão (`SameAsRequest`) já produz um cookie Secure atrás de um proxy com terminação TLS que encaminha `X-Forwarded-Proto: https`. |
| `Auth:AllowInsecureHttp` | `false` | Deixa os endpoints OAuth (`/connect/*`) responder a pedidos http em claro. **Apenas para desenvolvimento.** A RFC 6749 §3.1/§3.2 exige TLS nos endpoints de autorização e de token, portanto por omissão um pedido não-https a qualquer um deles é recusado com `invalid_request`. O esquema é avaliado *depois* do processamento dos cabeçalhos encaminhados, pelo que um proxy que termina TLS e encaminha `X-Forwarded-Proto: https` passa a barreira com esta opção desligada — desde que esse proxy esteja declarado em `ForwardedHeaders:KnownNetworks` / `KnownProxies`; sem essa declaração o cabeçalho é ignorado. Só uma implantação genuinamente em texto simples (o `docker-compose.yml` fornecido, a demo de servidor personalizado) precisa dela, e o servidor regista um aviso no arranque sempre que estiver ligada. É propagada para `AuthagonalProtocolOptions.AllowInsecureHttp`, pelo que governa também os endpoints pertencentes a `Authagonal.Protocol` (consulte [Extensibilidade](extensibility#embedding-authagonalprotocol-alone)). |
| `Auth:MaxFailedAttempts` | `5` | Tentativas de login falhadas antes do bloqueio da conta |
| `Auth:LockoutDurationMinutes` | `10` | Duração do bloqueio da conta após o máximo de tentativas falhadas |
| `Auth:MaxRegistrationsPerIp` | `5` | Máximo de registos por endereço IP dentro da janela |
| `Auth:RegistrationWindowMinutes` | `60` | Janela de limitação de taxa de registo |
| `Auth:MaxPasswordResetsPerEmail` | `3` | Máximo de e-mails de redefinição de senha por endereço de destino dentro da janela (indexado pelo e-mail, não pelo IP do chamador, para que um endereço não possa ser bombardeado com e-mails) |
| `Auth:PasswordResetWindowMinutes` | `60` | Janela de limitação de taxa de redefinição de senha |
| `Auth:AutoConfirmEmailDomains` | *(vazio)* | Domínios de e-mail (array de strings) cujos registos self-service são confirmados automaticamente: eles ignoram o e-mail de verificação. Vazio (o padrão) significa que cada registo deve ser verificado. Destinado apenas a dev/test; nunca liste um domínio que possa receber e-mail real. |
| `Auth:EmailVerificationExpiryHours` | `24` | Tempo de vida do link de verificação de e-mail |
| `Auth:PasswordResetExpiryMinutes` | `60` | Tempo de vida do link de redefinição de senha |
| `Auth:MfaChallengeExpiryMinutes` | `5` | Tempo de vida do token de verificação MFA |
| `Auth:MfaSetupTokenExpiryMinutes` | `15` | Tempo de vida do token de configuração MFA (para inscrição forçada) |
| `Auth:Pbkdf2Iterations` | `100000` | Contagem de iterações PBKDF2 para hashing de senhas |
| `Auth:FailedLoginMinimumMilliseconds` | `250` | Piso de tempo de relógio ao qual um login falho é mantido antes de devolver `invalid_credentials`, medido a partir do início da requisição. Fecha o oráculo temporal de enumeração de utilizadores: uma conta inexistente é verificada contra um hash fictício no formato PBKDF2 nativo, mas uma conta real pode ainda ter um hash bcrypt ou ASP.NET Identity V3 importado com um custo diferente, portanto igualar o trabalho é impossível e o que se impõe é igualar o tempo decorrido. Eleve-o acima do hash mais lento que a implantação detém, por exemplo se importou bcrypt acima do custo 11 ou aumentou `Pbkdf2Iterations` muito além do padrão: um único aviso é registado na primeira vez que um login falho ultrapassa o piso. `0` desativa o preenchimento e reabre o oráculo. |
| `Auth:RefreshTokenReuseGraceSeconds` | `0` | Janela de tolerância opcional (segundos) para reutilização concorrente de refresh token. `0` (padrão) mantém a postura estrita: qualquer reutilização de um refresh token consumido revoga todos os tokens para aquele utilizador+cliente. Defina `> 0` para tratar uma reutilização dentro da janela como uma repetição idempotente (reentrega os tokens sucessores), útil para clientes móveis com falhas de conectividade. |
| `Auth:DynamicClientRegistrationEnabled` | `false` | Habilita o endpoint de registo dinâmico de clientes `POST /connect/register` (RFC 7591). Desabilitado por padrão porque o registo aberto pode ser abusado em implantações multi-tenant. Consulte [Registo Dinâmico de Clientes](client-registration). |
| `Auth:SigningKeyLifetimeDays` | `90` | Tempo de vida da chave de assinatura RSA antes da rotação automática |
| `Auth:SigningKeyCacheRefreshMinutes` | `60` | Frequência de recarregamento das chaves de assinatura do armazenamento |
| `Auth:KeyRotationEnabled` | `false` | Habilita a rotação automática das chaves de assinatura |
| `Auth:KeyRotationCheckIntervalMinutes` | `360` | Frequência de verificação se a chave ativa precisa de rotação |
| `Auth:KeyRotationLeadTimeDays` | `14` | Rodar quando a chave ativa expirar dentro deste número de dias |
| `Auth:SecurityStampRevalidationMinutes` | `30` | Intervalo entre verificações do carimbo de segurança do cookie |

## Data Protection

As chaves de Data Protection do ASP.NET Core (que cifram o cookie de sessão) devem ser partilhadas entre instâncias, consulte [Escalabilidade](scaling#cookie-encryption-data-protection). Opções de persistência, por ordem de precedência:

| Definição | Padrão | Descrição |
|---|---|---|
| `DataProtection:BlobUri` | *(nenhum)* | URI de Blob do Azure explícito para o conjunto de chaves (ex.: `https://{account}.blob.core.windows.net/dataprotection/keys.xml`). Autentica-se via `DefaultAzureCredential`, o caminho de produção preferido a par de `Storage:TableServiceUri`. |
| *(fallback)* | — | Quando `DataProtection:BlobUri` não está definido e `Storage:ConnectionString` aponta para uma conta de armazenamento real (não Azurite), as chaves são persistidas automaticamente num contentor `dataprotection` nessa conta. Com o Azurite, as chaves recorrem ao armazenamento padrão baseado em ficheiros. |

No backend AWS, passe um cliente S3 + bucket a `AddAuthagonalAwsStorage` para persistir o conjunto de chaves no S3, consulte [Instalação → backend AWS](installation#aws-backend).

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

## Papéis

Os papéis são definidos no array `Roles` e semeados na inicialização, junto com clientes, scopes e
provedores. Semeá-los importa sobretudo quando um scope é restringido com
[`AllowedRoles`](scopes#role-gated-scopes): um scope restringido a um papel que nada cria fica
restringido para toda a gente, incluindo o operador que o configurou, e falha em silêncio -- o scope
simplesmente nunca é concedido.

```json
{
  "Roles": [
    {
      "Name": "staff-admin",
      "Description": "Internal staff console",
      "Members": [ "ada@example.com", "grace@example.com" ]
    }
  ]
}
```

| Campo | Descrição |
|---|---|
| `Name` | O nome do papel, tal como usado em `Scope.AllowedRoles` e no claim `roles` do token |
| `Description` | Legível por humanos; atualizada em arranques posteriores quando a semente indica uma |
| `Members` | Emails colocados no papel a cada arranque. Um endereço ainda sem utilizador é ignorado com um aviso e tentado de novo no arranque seguinte -- o arranque nunca depende de uma conta que ninguém criou |

A semeadura é **aditiva e idempotente**. Nunca remove um papel nem revoga uma pertença: a
configuração não é a fonte da verdade sobre quem detém o quê, portanto um papel concedido através da
API de administração sobrevive ao reinício seguinte.

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
| `OneTime` (padrão) | Cada refresh emite um novo refresh token e invalida o antigo. Por padrão (`Auth:RefreshTokenReuseGraceSeconds = 0`) qualquer reutilização de um token consumido revoga imediatamente todos os tokens para aquele utilizador+cliente: **não** há janela de tolerância ativa por padrão. Defina `Auth:RefreshTokenReuseGraceSeconds` para um valor positivo para optar por uma janela de tolerância a repetições. |
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

Logins federados (SAML/OIDC) também respeitam a política de MFA: um utilizador com MFA inscrito é encaminhado pelo desafio MFA após o IdP externo o autenticar, e `Required` força a inscrição para utilizadores federados sem MFA.

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
| `EntityId` | Sim | O ID de entidade do SP **deste servidor**: o identificador que regista no IdP, não o próprio ID de entidade do IdP |
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

| `SecretProvider:RequireVaultReferences` | `false` por omissão. Quando `true`, uma referência armazenada sem prefixo de vault (`kv:` para Key Vault, `sm:` para AWS Secrets Manager) é um **erro** em vez de ser honrada como um valor em texto simples. Ative-o assim que a migração para o vault estiver concluída. |

Quando configurado, os valores de segredo que se assemelham a referências do Key Vault são resolvidos em tempo de execução. Usa `DefaultAzureCredential` para autenticação.

### Migrar para um vault, e fechar a porta depois

Ambos os provedores apoiados em vault devolvem uma referência sem prefixo tal como está, tratando-a como um valor em texto simples escrito antes de a implantação ter um vault. É isso que permite migrar um sistema em funcionamento segredo a segredo em vez de tudo de uma vez -- mas deixada aberta, é uma via de degradação permanente: tudo o que consiga escrever uma única coluna de configuração (uma migração a meio, um caminho de administração que guarda um valor bruto onde devia estar uma referência, um atacante com acesso ao armazenamento mas não ao vault) substitui um segredo protegido pelo vault por um valor à sua escolha, e verifica na perfeição, porque para uma referência sem prefixo a referência *é* o valor.

Ative `SecretProvider:RequireVaultReferences` quando a migração terminar. Resolver uma referência sem prefixo passa então a lançar uma exceção em vez de devolver texto em claro silenciosamente. Ativá-lo enquanto o provedor resolvido é o de texto simples é recusado no arranque, já que essa combinação não tem qualquer estado funcional -- todas as referências que o provedor de texto simples escreve são sem prefixo.

O servidor regista ainda um aviso no arranque sempre que um host fora de desenvolvimento acaba com o provedor de texto simples.

> ⚠️ **Produção: defina `SecretProvider:VaultUri`.** O provedor de segredos padrão é **texto simples**. Quando `SecretProvider:VaultUri` não está definido, os segredos de clientes OIDC upstream e as sementes TOTP / MFA são escritos no Azure Table Storage em texto claro e, portanto, aparecem em texto claro em qualquer [backup](backup-restore). Para qualquer implantação em produção, configure `SecretProvider:VaultUri` para que esses segredos sejam armazenados no Key Vault.

## API de Administração

| Definição | Padrão | Descrição |
|---|---|---|
| `AdminApi:Enabled` | `true` | **Habilitada por padrão.** Defina como `false` para desabilitar todos os endpoints de administração (não serão registados). |
| `AdminApi:Scope` | `authagonal-admin` | Scope JWT necessário para aceder aos endpoints de administração. Altere isto para corresponder ao seu nome de scope existente (ex.: `projects-identity-admin` para migrações do IdentityServer). |

> ⚠️ **A API de administração está habilitada por padrão e é altamente privilegiada.** O scope de administração concede gestão total e impersonação de utilizadores: qualquer pessoa que possua um token com `AdminApi:Scope` pode emitir tokens para qualquer utilizador, gerir clientes e ler/escrever toda a configuração. Restrinja a nível de rede os endpoints de administração (as rotas de administração `/api/v1/*`) e controle rigorosamente quem pode receber o scope de administração. Como medida de defesa em profundidade, o scope é *reservado*: nunca pode ser concedido a um cliente OAuth (consulte [API de Administração](admin-api)) e não pode ser emitido através do endpoint de impersonação. Defina `AdminApi:Enabled = false` por completo se a API de administração não for usada.

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

O remetente de e-mail integrado usa o [Resend](https://resend.com) e **ativa-se automaticamente** quando `Email:ResendApiKey` está configurada: não é necessário registar nenhum serviço. Para usar um provedor diferente, registe a sua própria implementação de `IEmailService` antes de chamar `AddAuthagonal()` (tem precedência independentemente das chaves `Email:*`).

| Definição | Descrição |
|---|---|
| `Email:ResendApiKey` | Chave de API do Resend. Quando definida, é usado o remetente Resend integrado. |
| `Email:SenderEmail` | Endereço de e-mail do remetente |
| `Email:SenderName` | Nome de exibição do remetente (padrão: `"Authagonal"`) |

> ⚠️ **Sem nenhum remetente de e-mail, o auto-registo fica quebrado.** Quando `Email:ResendApiKey` não está definida e nenhum `IEmailService` personalizado está registado, um serviço no-op descarta silenciosamente todo o e-mail: os e-mails de verificação e de redefinição de senha nunca chegam e, como o login exige um e-mail confirmado por padrão, os utilizadores auto-registados nunca conseguem entrar. O `UseAuthagonal` regista um aviso na inicialização neste estado. Válvula de escape para dev/test: `Auth:AutoConfirmEmailDomains` confirma automaticamente os registos para os domínios listados.

E-mails para endereços `@example.com` são silenciosamente ignorados (útil para testes).

## Cluster

A camada de clustering fornece **eleição de líder** (para que trabalhos restritos ao líder, como a rotação da chave de assinatura, sejam executados em exatamente um nó) e um **barramento de eventos entre nós**, por trás de backends plugáveis. O padrão é em processo: um único nó é sempre o seu próprio líder, a definição certa para nó único e desenvolvimento local, sem qualquer configuração.

| Definição | Variável de Ambiente | Padrão | Descrição |
|---|---|---|---|
| `Cluster:Enabled` | `Cluster__Enabled` | `true` | Interruptor principal. Quando `false`, o nó é executado de forma autónoma (sempre líder, barramento de eventos em processo). |
| `Cluster:Secret` | `Cluster__Secret` | *(nenhum)* | Segredo partilhado exigido no endpoint exclusivamente interno `/_internal/backchannel-logout`. Quando definido, os chamadores devem apresentá-lo no cabeçalho `X-Cluster-Secret` (comparado em tempo constante). Quando **não definido**, o endpoint só é acessível a partir de IPs de origem de loopback / privados (RFC 1918 / link-local / ULA): um pedido externo com um IP público é rejeitado. |
| `Cluster:LeaseTtlSeconds` | `Cluster__LeaseTtlSeconds` | `30` | Duração da concessão de liderança. Renovada aproximadamente a metade deste intervalo. |
| `Cluster:PollIntervalSeconds` | `Cluster__PollIntervalSeconds` | `3` | Frequência com que o backend do barramento de eventos sonda mensagens publicadas por outros nós. |

**Implantações multi-nó** substituem por um backend real através do callback `configureClustering` em `AddAuthagonal` / `AddAuthagonalCore`:

```csharp
// Azure: leadership via a blob lease, event bus via a table log (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS equivalent (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));

// Self-hosted PostgreSQL (Authagonal.SqlProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseSql(sqlDataSource));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` / `UseSqlBus` registam apenas o barramento de eventos, mantendo a concessão em processo, para nós que devem receber eventos do cluster mas nunca devem disputar a liderança.

Consulte [Escalabilidade](scaling) para saber como a liderança e o barramento de eventos se comportam entre instâncias.

## Cabeçalhos Encaminhados (proxy confiável)

O Authagonal indexa a limitação de taxa e o bloqueio de conta pelo IP do cliente, e só emite HSTS em pedidos HTTPS. Atrás de um proxy reverso / ingress, o IP real do cliente e o esquema chegam nos cabeçalhos `X-Forwarded-For` / `X-Forwarded-Proto`. Estas definições controlam **quais saltos de proxy são confiáveis** para definir esses valores, de modo que um chamador não possa falsificar `X-Forwarded-For` para forjar o IP do cliente.

| Definição | Variável de Ambiente | Padrão | Descrição |
|---|---|---|---|
| `ForwardedHeaders:ForwardLimit` | `ForwardedHeaders__ForwardLimit` | `1` | Número de saltos de proxy a honrar a partir da direita da cadeia `X-Forwarded-For`. O padrão de `1` confia apenas no único salto que o seu ingress acrescenta e ignora qualquer coisa mais à esquerda na cadeia. |
| `ForwardedHeaders:KnownNetworks` | `ForwardedHeaders__KnownNetworks__0` (array) | *(vazio)* | Faixas CIDR (array de strings, ex.: `"10.0.0.0/8"`) autorizadas a definir cabeçalhos encaminhados. Defina isto para o CIDR do seu proxy / ingress / pod. É esta declaração que permite que `X-Forwarded-Proto` seja sequer considerado — ver abaixo. |
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

### Os dois cabeçalhos não merecem confiança nos mesmos termos

`X-Forwarded-For` ajusta o **IP do cliente**: a chave de que dependem a limitação de taxa, o bloqueio de contas e a guarda de `/_internal`. Sem nada declarado, o Authagonal aceita-o a partir do loopback e das faixas RFC1918, e regista um aviso. É um valor por omissão de melhor esforço, e é melhor do que o comportamento da framework com um conjunto de confiança vazio, que consiste em aceitar o cabeçalho de *qualquer* chamador.

`X-Forwarded-Proto` altera o **esquema**, e o esquema decide se `/connect/*` sequer responde (RFC 6749 §3.1/§3.2), se os cookies são marcados como `Secure` e se os URLs absolutos gerados são https. Só é aceite **a partir de** um proxy que tenha declarado em `KnownNetworks` / `KnownProxies`. Um endereço privado não é uma declaração: o Authagonal é distribuído como biblioteca e não consegue ver a rede onde foi implantado, pelo que «o par tem um endereço privado» é um palpite sobre a topologia. Numa LAN plana, numa VPC partilhada ou numa bridge de contentores partilhada, todas as cargas de trabalho vizinhas estão dentro dessas faixas e poderiam afirmar `https` para um pedido que chegou em texto simples.

**Se o seu proxy não tiver endereço fixo** — um ingress do Kubernetes, um balanceador rotativo, uma plataforma que não lhe dirá o CIDR do salto — declare todos os pares como proxy:

```json
{
  "ForwardedHeaders": {
    "KnownNetworks": ["0.0.0.0/0", "::/0"]
  }
}
```

Isto é seguro exatamente quando nada além do proxy consegue alcançar o processo, que é o pressuposto em que uma implantação dessas já se apoia. Escrevê-lo coloca-o onde pode ser revisto, em vez de deixar a biblioteca inferi-lo. Se outras cargas de trabalho *conseguirem* alcançar o Kestrel diretamente, com esta definição poderão falsificar o esquema e o IP do cliente — fixe então o CIDR real.

> ⚠️ **Proxy com terminação TLS obrigatório, e tem de ser declarado.** O Authagonal deve ser executado atrás de um proxy reverso com terminação TLS (ou terminar o TLS ele próprio). O HSTS (`Strict-Transport-Security`) só é emitido em pedidos HTTPS, e os endpoints OAuth recusam liminarmente pedidos em texto simples a menos que `Auth:AllowInsecureHttp` esteja ligado — portanto o proxy deve encaminhar `X-Forwarded-Proto: https` **e** estar nomeado em `ForwardedHeaders:KnownNetworks` / `ForwardedHeaders:KnownProxies` para que o HSTS seja enviado e `/connect/*` sequer responda. Não declarar nada é a falha de atualização mais comum: o cabeçalho chega, nada está habilitado a aplicá-lo, e todos os pedidos a `/connect/*` respondem 400 numa implantação que está genuinamente em TLS. O registo de arranque di-lo, e o corpo da recusa também.

## Limitação de Taxa

Limites de taxa integrados protegem os endpoints propensos a abuso:

| Endpoint | Limite | Janela | Indexado por |
|---|---|---|---|
| `POST /api/auth/register` | 5 (`Auth:MaxRegistrationsPerIp`) | 1 hora (`Auth:RegistrationWindowMinutes`) | IP do cliente |
| `POST /api/auth/forgot-password` | 3 (`Auth:MaxPasswordResetsPerEmail`) | 1 hora (`Auth:PasswordResetWindowMinutes`) | E-mail de destino |
| `POST /connect/register` (quando habilitado) | 10 | 1 hora | IP do cliente |
| Endpoints SCIM | 200 | 1 minuto | Cliente SCIM |

Os limites são aplicados **em processo por nó** (por trás do seam `IRateLimiter`), portanto com N instâncias o teto efetivo é N× o valor configurado. Trate-os como uma rede de segurança e aplique o limite global autoritativo na borda (WAF / ingress / CDN). Consulte [Escalabilidade](scaling#rate-limiting).

## CORS

O CORS é configurado dinamicamente. As origens de todos os `AllowedCorsOrigins` dos clientes registados são automaticamente permitidas, com um cache de 60 minutos.

## HashiCorp Vault Transit

O Authagonal pode assinar JWTs usando o motor de segredos Transit do HashiCorp Vault. As chaves privadas nunca saem do Vault: apenas a operação de assinatura é delegada remotamente. As chaves públicas são armazenadas em cache localmente para verificação.

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
