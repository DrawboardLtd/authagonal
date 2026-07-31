---
layout: default
title: Extensibilidade
locale: pt
---

# Extensibilidade

O Authagonal pode ser hospedado como uma biblioteca no seu próprio projeto ASP.NET Core, com controlo total sobre as implementações de serviços.

## Métodos de Extensão

Três métodos compõem o Authagonal em qualquer aplicação ASP.NET Core:

```csharp
var builder = WebApplication.CreateBuilder(args);
builder.Services.AddAuthagonal(builder.Configuration);  // Services + auth + storage

var app = builder.Build();
app.UseAuthagonal();              // Middleware pipeline
app.MapAuthagonalEndpoints();     // All endpoints
app.MapFallbackToFile("index.html");
app.Run();
```

### Hospedagem Multi-Tenant

Para implantações multi-tenant, use `AddAuthagonalCore()` em vez disso. Ele regista endpoints, middleware e serviços principais, mas ignora o armazenamento e os serviços em segundo plano; você os fornece por tenant. A gestão de chaves de assinatura assume por padrão o singleton `ProtocolKeyManager` do `Authagonal.Protocol`, e um host que regista o seu próprio `IKeyManager` antes de `AddAuthagonalCore()` mantém-no:

```csharp
builder.Services.AddScoped<ITenantContext, MyTenantContext>();
builder.Services.AddScoped<IKeyManager, MyPerTenantKeyManager>();
builder.Services.AddAuthagonalCore(builder.Configuration);
```

`IKeyManager` e as interfaces de armazenamento (`IClientStore`, `IScimTokenStore`, etc.) são resolvidos a partir de `HttpContext.RequestServices` no momento do pedido, portanto os registos com escopo (scoped) funcionam corretamente para o isolamento por tenant.

## Substituição de Serviços

Registe as suas implementações personalizadas **antes** de chamar `AddAuthagonal()`. O Authagonal usa `TryAdd` internamente, portanto os seus registos têm precedência:

```csharp
// Custom implementations, registered first so they won't be overwritten
builder.Services.AddSingleton<IAuthHook, AuditAuthHook>();
builder.Services.AddSingleton<IEmailService, SmtpEmailService>();
builder.Services.AddSingleton<ISecretProvider, AwsSecretsProvider>();

// Authagonal setup skips services that are already registered
builder.Services.AddAuthagonal(builder.Configuration);
```

O `IAuthHook` é especial: é um pipeline de registo múltiplo. Registe tantos hooks quantos quiser (qualquer tempo de vida, incluindo `AddScoped`) e todos são executados na ordem de registo. O no-op `NullAuthHook` é adicionado apenas quando nenhum hook foi registado até ao momento em que `AddAuthagonal()` / `AddAuthagonalCore()` é executado, portanto registe sempre os seus hooks primeiro.

### Pontos de Extensibilidade

| Interface | Padrão | Finalidade |
|---|---|---|
| `IAuthHook` | `NullAuthHook` (no-op, adicionado apenas quando nenhum hook está registado) | Hooks de ciclo de vida para eventos de autenticação: registo de auditoria, validação personalizada, webhooks. Vários hooks podem ser registados; todos são executados por ordem |
| `IEmailService` | `NullEmailService` (no-op), ou o remetente Resend integrado quando `Email:ResendApiKey` está configurada | Entrega de e-mail para verificação, redefinição de senha e avisos de conta existente |
| `IProvisioningOrchestrator` | `TccProvisioningOrchestrator` (scoped) | Provisionamento de utilizadores em aplicações downstream |
| `ISecretProvider` | `PlaintextSecretProvider`, ou o `KeyVaultSecretProvider` integrado quando `SecretProvider:VaultUri` está configurado | Armazenamento reversível de segredos (Key Vault, AWS Secrets Manager, Vault Transit, etc.) |
| `ITenantContext` | `DefaultTenantContext` (lê de `IConfiguration`) | Resolução de tenant para implantações multi-tenant |
| `IKeyManager` | `ProtocolKeyManager` (singleton, do `Authagonal.Protocol`) | Gestão de chaves de assinatura; substitua para isolamento de chaves por tenant |
| `IProvisioningAppProvider` | `ConfigProvisioningAppProvider` (scoped) | Resolve as aplicações de provisionamento disponíveis; substitua para resolução dinâmica ou por tenant |
| `IAuditLogger` | `NullAuditLogger` (no-op) | Trilha de auditoria para alterações de configuração e eventos relevantes para a segurança |

Três outros seams vivem ao **nível do store** em vez de na DI: `IFieldCipher`, `IIndexTokenizer` e `IChangeWriter` (todos em `Authagonal.Core.Services`). Os provedores de armazenamento aceitam-nos como parâmetros de construtor opcionais; consulte as suas seções abaixo.

## IAuthHook

A interface `IAuthHook` fornece hooks no ciclo de vida da autenticação. Os métodos no caminho crítico (autenticação, criação de utilizador, emissão de token) podem lançar uma exceção para abortar a operação; os métodos mais recentes são notificações a posteriori. Várias implementações de `IAuthHook` podem ser registadas e todas são executadas na ordem de registo.

```csharp
public interface IAuthHook
{
    // Core lifecycle: implement these
    Task OnUserAuthenticatedAsync(string userId, string email, string method,
        string? clientId = null, CancellationToken ct = default);
    Task OnUserCreatedAsync(string userId, string email, string createdVia,
        CancellationToken ct = default);
    Task OnLoginFailedAsync(string email, string reason,
        CancellationToken ct = default);
    Task OnTokenIssuedAsync(string? subjectId, string clientId, string grantType,
        CancellationToken ct = default);
    Task<MfaPolicy> ResolveMfaPolicyAsync(string userId, string email,
        MfaPolicy clientPolicy, string clientId, CancellationToken ct = default);
    Task OnMfaVerifiedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default);
    Task OnUserUpdatedAsync(string userId, string email, string updatedVia,
        CancellationToken ct = default);
    Task OnUserDeletedAsync(string userId, string email, string deletedVia,
        CancellationToken ct = default);

    // Additive notifications: default no-op implementations, so existing
    // hooks keep compiling as the interface grows
    Task OnMfaVerifyFailedAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnEmailConfirmedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaEnrolledAsync(string userId, string email, string mfaMethod,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnMfaCredentialRemovedAsync(string userId, string email, string mfaMethod,
        bool mfaDisabled, CancellationToken ct = default) => Task.CompletedTask;
    Task OnRecoveryCodesRegeneratedAsync(string userId, string email,
        CancellationToken ct = default) => Task.CompletedTask;
    Task OnPasswordChangedAsync(string userId, string email, string changedVia,
        CancellationToken ct = default) => Task.CompletedTask;
}
```

### Parâmetros

| Método | Notas e valores de `method` / `via` |
|---|---|
| `OnUserAuthenticatedAsync` | `"password"`, `"passkey"`, `"saml"`, `"oidc"` |
| `OnUserCreatedAsync` | `"admin"`, `"saml"`, `"oidc"` |
| `OnUserUpdatedAsync` | `"admin"`, `"self"` (os hosts podem passar os seus próprios, ex.: uma origem SCIM) |
| `OnUserDeletedAsync` | `"admin"`; apenas notificação, o registo pode já não ser legível |
| `OnLoginFailedAsync` | `"user_not_found"`, `"invalid_password"`, etc. |
| `OnTokenIssuedAsync` | Tipos de grant: `"authorization_code"`, `"refresh_token"`, `"client_credentials"` |
| `ResolveMfaPolicyAsync` | Chamado após a verificação da senha; retorna a política de MFA efetiva para o utilizador. Padrão: retornar `clientPolicy` sem alteração. |
| `OnMfaVerifiedAsync` | `"totp"`, `"webauthn"`, `"recovery"` |
| `OnMfaVerifyFailedAsync` | Os mesmos métodos que `OnMfaVerifiedAsync`. Dispara apenas após credenciais de primeiro fator válidas, portanto rajadas são um forte sinal de tentativa de contornar o MFA (distinto de `OnLoginFailedAsync`, a fase da senha) |
| `OnEmailConfirmedAsync` | O utilizador confirmou o seu e-mail via o link de verificação; já persistido |
| `OnMfaEnrolledAsync` | `"totp"`, `"webauthn"`; a credencial já está ativa |
| `OnMfaCredentialRemovedAsync` | `"totp"`, `"webauthn"`, `"recoverycode"`; `mfaDisabled` é verdadeiro quando a remoção não deixou nenhum fator primário |
| `OnRecoveryCodesRegeneratedAsync` | O conjunto anterior de códigos de recuperação é invalidado |
| `OnPasswordChangedAsync` | ex.: `"reset"`; a alteração é persistida e as sessões existentes são invalidadas |

### Exemplo: Registo de Auditoria

```csharp
public sealed class AuditAuthHook(ILogger<AuditAuthHook> logger) : IAuthHook
{
    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Login: {Email} via {Method}", email, method);
        return Task.CompletedTask;
    }

    public Task OnUserCreatedAsync(string userId, string email,
        string createdVia, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] User created: {Email} via {Via}", email, createdVia);
        return Task.CompletedTask;
    }

    public Task OnLoginFailedAsync(string email, string reason, CancellationToken ct)
    {
        logger.LogWarning("[AUDIT] Login failed: {Email} ({Reason})", email, reason);
        return Task.CompletedTask;
    }

    public Task OnTokenIssuedAsync(string? subjectId, string clientId,
        string grantType, CancellationToken ct)
    {
        logger.LogInformation("[AUDIT] Token issued: {ClientId} ({GrantType})",
            clientId, grantType);
        return Task.CompletedTask;
    }

    // ... remaining required methods return Task.CompletedTask
}
```

### Exemplo: Restrição de Domínio

```csharp
public sealed class DomainRestrictionHook : IAuthHook
{
    private static readonly HashSet<string> BlockedDomains = ["competitor.com"];

    public Task OnUserAuthenticatedAsync(string userId, string email,
        string method, string? clientId, CancellationToken ct)
    {
        var domain = email.Split('@').Last();
        if (BlockedDomains.Contains(domain))
            throw new InvalidOperationException($"Domain {domain} is not allowed");

        return Task.CompletedTask;
    }

    // ... other methods return Task.CompletedTask
}
```

## ISecretProvider

O `ISecretProvider` (em `Authagonal.Core.Services`) é o seam de encriptação reversível para segredos armazenados, como segredos de clientes SSO, senhas SMTP e sementes TOTP. `ProtectAsync` transforma um texto simples numa referência que o store persiste; `ResolveAsync` transforma a referência de volta no texto simples. O `PlaintextSecretProvider` padrão armazena os valores como estão (a referência É o valor).

```csharp
public interface ISecretProvider
{
    Task<string> ResolveAsync(string secretReference, CancellationToken ct = default);
    Task<string> ProtectAsync(string name, string plaintext, CancellationToken ct = default);
}
```

Definir `SecretProvider:VaultUri` liga automaticamente o `KeyVaultSecretProvider` integrado (Azure Key Vault via `DefaultAzureCredential`). Para qualquer outra coisa, registe a sua própria implementação antes de `AddAuthagonal()`.

## Encriptação de Campos PII: IFieldCipher

O `IFieldCipher` encripta em repouso os valores de campos PII individuais do utilizador (telefone, empresa, atributos personalizados, e-mail e nomes na linha de perfil). É um seam ao nível do store: os provedores de armazenamento aceitam-no como um parâmetro de construtor opcional (ex.: `TableUserStore`) e, quando ausente, aplica-se o `NullFieldCipher` de passagem, portanto a encriptação é estritamente opcional e os hosts não configurados continuam a armazenar texto simples.

```csharp
public interface IFieldCipher
{
    Task<string> ProtectAsync(string plaintext, CancellationToken ct = default);
    Task<string> ResolveAsync(string stored, CancellationToken ct = default);

    // Batch variants have default loop implementations; override for backends
    // with a one-round-trip batch primitive (e.g. Vault Transit)
    Task<IReadOnlyList<string>> ProtectManyAsync(IReadOnlyList<string> plaintexts,
        CancellationToken ct = default);
    Task<IReadOnlyList<string>> ResolveManyAsync(IReadOnlyList<string> stored,
        CancellationToken ct = default);
}
```

Dois pontos do contrato importam. `ProtectAsync` deve retornar um token de texto cifrado autodescritivo (ex.: o `vault:v{n}:...` do Vault Transit), e `ResolveAsync` deve deixar passar sem alteração um valor que não reconheça como o seu próprio texto cifrado. A regra de passagem é o que permite implementar a encriptação de forma preguiçosa sobre as linhas existentes: a leitura de uma linha não migrada retorna o texto simples legado, e a gravação seguinte volta a protegê-lo.

## Pesquisa por Índice Cego: IIndexTokenizer

O `IIndexTokenizer` mantém os campos encriptados pesquisáveis. Transforma um valor de texto simples normalizado num token de índice cego determinístico e seguro como chave de tabela, tipicamente um HMAC com chave em que a chave vive fora da base de dados. O determinismo significa que uma pesquisa por igualdade ainda funciona ("email = x" torna-se "token = HMAC(x)"), enquanto um dump da base de dados não consegue recalcular nem reverter um token. A pesquisa por prefixo é sobreposta ao tokenizar cada prefixo de um valor separadamente, uma vez que um HMAC com chave destrói a ordenação e as varreduras de intervalo.

> **O que um dump ainda revela.** "Nem recalcular nem reverter" é verdade para um token isolado, não
> para o índice no seu conjunto. Sobrevivem três resíduos, e vale a pena conhecê-los antes de confiar
> nisto:
>
>   *(Corrigido.)* ~~**Estrutura.** O índice de prefixos escreve uma linha por prefixo, pelo que a
>   contagem de linhas de um registo equivale ao comprimento do campo indexado.~~ Cada valor indexado
>   escreve agora um número fixo de linhas, preenchido com engodos que nenhuma consulta consegue
>   produzir e que um dump não consegue distinguir de prefixos reais.
> - **Igualdade e frequência.** Os tokens são determinísticos por construção, que é o que faz a
>   pesquisa funcionar, portanto um dump mostra que registos partilham um valor e quão comum é cada
>   valor. O índice de domínios agrupa a sua população por empregador, o que muitas vezes identifica
>   pessoas sem recuperar um endereço.
> - **Texto simples escolhido.** Quem consiga ler o armazenamento *e* provocar a indexação de valores
>   (registar uma conta, ser aprovisionado por SCIM) pode submeter um candidato e procurar o seu
>   token. Isso recupera qualquer valor adivinhável -- domínios comuns, nomes próprios comuns --
>   independentemente de onde a chave viva, porque o oráculo é o caminho de escrita e não a cifra.
>
> A tokenização defende o caso para o qual foi construída: alguém que tem um dump e mais nada, a
> tentar ler endereços. Os dois resíduos que restam são exatamente aquilo que um oráculo de registo
> entrega de qualquer forma. Se forem inaceitáveis, deixe as tabelas de índice de prefixo e de
> domínio por configurar -- a pesquisa por correspondência exata não acarreta nenhum deles -- em vez
> de assumir que o HMAC as cobre.

```csharp
public interface IIndexTokenizer
{
    Task<string> TokenizeAsync(string value, CancellationToken ct = default);
    Task<IReadOnlyList<string>> TokenizeBatchAsync(IReadOnlyList<string> values,
        CancellationToken ct = default);
}
```

Tal como o `IFieldCipher`, é um parâmetro de construtor de store opcional com um padrão de passagem (`NullIndexTokenizer`), portanto as linhas de índice permanecem indexadas em texto simples até que você opte por participar. Os tokens retornados devem ser seguros como valores de PartitionKey/RowKey do Azure Table (nenhum de `/ \ # ?` nem caracteres de controlo).

## Captura de Registo de Alterações: IChangeWriter

O `IChangeWriter` (renomeado de `ITombstoneWriter` na 0.6.0) regista a chave de cada linha alterada numa tabela de registo de alterações dedicada, para que os backups incrementais possam encontrar o que mudou sem varrer a coluna `Timestamp` não indexada das tabelas ativas. As exclusões são capturadas para cada tabela (uma varredura de linhas ativas não consegue ver uma linha que já não existe); os upserts são capturados para as tabelas que o backup lê a partir do registo em vez de varrer. Implementações integradas: `TableChangeWriter` (Azure Table Storage), `DynamoChangeWriter` (DynamoDB) e `SqlChangeWriter` (PostgreSQL / SQLite).

```csharp
public interface IChangeWriter
{
    // Deletes
    Task WriteAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);

    // Upserts
    Task WriteUpsertAsync(string tableName, string partitionKey, string rowKey,
        CancellationToken ct = default);
    Task WriteUpsertBatchAsync(string tableName,
        IEnumerable<(string PartitionKey, string RowKey)> keys, CancellationToken ct = default);
}
```

Contrato de ordenação para implementadores e chamadores: escreva a tombstone de exclusão ANTES de excluir a linha de dados. Uma falha na ordem inversa perde a exclusão de todos os backups futuros, uma vez que as exclusões são a única classe de mutação que uma nova varredura não consegue autocorrigir. A falha inversa é segura: uma gravação posterior na chave volta a carimbar um timestamp mais recente, e o merge/restauro mantêm as linhas escritas após a tombstone.

## Endpoints Personalizados

Adicione os seus próprios endpoints ao lado dos do Authagonal:

```csharp
app.UseAuthagonal();
app.MapAuthagonalEndpoints();

// Your custom endpoints
app.MapGet("/api/custom", () => "custom endpoint");
app.MapGet("/custom/health", () => new { status = "healthy" });

app.MapFallbackToFile("index.html");
```

## Integração com HashiCorp Vault Transit

O Authagonal pode delegar a assinatura de JWTs ao motor de segredos Transit do HashiCorp Vault. As chaves privadas nunca saem do Vault; apenas a operação de assinatura é remota. As chaves públicas são armazenadas em cache localmente para verificação.

```csharp
var builder = WebApplication.CreateBuilder(args);

// Configure Vault Transit HTTP client
builder.Services.AddHttpClient("Vault", client =>
{
    client.BaseAddress = new Uri("https://vault.example.com");
    client.DefaultRequestHeaders.Add("X-Vault-Token", "hvs.xxx");
});

// Register Vault Transit services
builder.Services.AddSingleton<VaultTransitClient>();
builder.Services.AddSingleton<VaultTransitCryptoProvider>();

builder.Services.AddAuthagonal(builder.Configuration);
```

O `VaultTransitClient` fornece estas operações:

| Método | Descrição |
|---|---|
| `SignAsync(keyName, data)` | Assina dados usando uma chave Vault Transit |
| `VerifyAsync(keyName, data, signature)` | Verifica uma assinatura serializada em JWS via o endpoint de verificação do Transit |
| `EncryptAsync` / `DecryptAsync` (+ `EncryptBatchAsync` / `DecryptBatchAsync`) | Encriptação simétrica sob uma chave `aes256-gcm96`; retorna tokens `vault:v{n}:...` para armazenar literalmente |
| `HmacAsync` / `HmacBatchAsync` | HMAC com chave sob uma chave `hmac` (tokens de índice cego) |
| `CreateKeyAsync(keyName, type)` | Cria uma nova chave Transit (padrão: `ecdsa-p256`) |
| `EnsureKeyTypeAsync(keyName, type)` | Garante idempotentemente que uma chave existe com o tipo desejado (recria em caso de incompatibilidade de tipo; as chaves Transit não podem ser redefinidas para outro tipo no lugar) |
| `RotateKeyAsync(keyName)` | Roda uma chave para uma nova versão |
| `DeleteKeyAsync(keyName)` | Elimina uma chave (habilita `deletion_allowed` primeiro) |
| `ReadKeyAsync(keyName)` | Lê os metadados, versões e chaves públicas de uma chave |
| `KeyExistsAsync(keyName)` | Verifica se uma chave existe |

O `VaultTransitCryptoProvider` integra-se com o `JsonWebTokenHandler` do .NET para que a assinatura de JWT use o Vault de forma transparente. O `VaultTransitSecurityKey` e o `VaultTransitSignatureProvider` tratam da integração de baixo nível.

## Email

O remetente Resend integrado ativa-se automaticamente quando `Email:ResendApiKey` está configurada (defina também `Email:SenderEmail`). Sem nenhum `IEmailService`, o e-mail é descartado via `NullEmailService` e, como a barreira de login por e-mail confirmado está ativa por padrão, os utilizadores auto-registados nunca conseguiriam entrar; o `UseAuthagonal()` regista um aviso de inicialização ruidoso nesse estado.

Para usar outro provedor, registe o seu próprio `IEmailService` antes de `AddAuthagonal()`:

```csharp
public sealed class SmtpEmailService(SmtpClient smtp) : IEmailService
{
    public async Task SendVerificationEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Verify your email", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }

    public async Task SendPasswordResetEmailAsync(string email, string callbackUrl,
        CancellationToken ct = default)
    {
        var message = new MailMessage("noreply@example.com", email,
            "Reset your password", $"Click here: {callbackUrl}");
        await smtp.SendMailAsync(message, ct);
    }
}
```

O `IEmailService` também declara `SendAccountExistsEmailAsync` (enviado quando alguém tenta registar um e-mail já registado, mantendo a resposta de registo neutra contra a enumeração de contas). Tem uma implementação no-op padrão, portanto as implementações existentes continuam a compilar.

## Veja Também

- [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server): exemplo completo funcional
- [demos/sample-app/](https://github.com/authagonal/authagonal/tree/master/demos/sample-app): exemplo de aplicação cliente
