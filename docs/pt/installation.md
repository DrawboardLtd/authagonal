---
layout: default
title: Instalação
locale: pt
---

# Instalação

## Docker (recomendado)

Baixe e execute a imagem pré-construída:

```bash
docker run -p 8080:8080 \
  -e Storage__ConnectionString="your-connection-string" \
  -e Issuer="https://auth.example.com" \
  drawboardci/authagonal
```

## Docker Compose

Para desenvolvimento local com Azurite (emulador do Azure Storage):

```yaml
services:
  azurite:
    image: mcr.microsoft.com/azure-storage/azurite
    ports:
      - "10000:10000"
      - "10001:10001"
      - "10002:10002"

  authagonal:
    build: .
    ports:
      - "8080:8080"
    environment:
      - Storage__ConnectionString=DefaultEndpointsProtocol=http;AccountName=devstoreaccount1;AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;TableEndpoint=http://azurite:10002/devstoreaccount1;
      - Issuer=http://localhost:8080
    depends_on:
      - azurite
```

```bash
docker compose up
```

## Compilação a partir do Código-Fonte

### Pré-requisitos

- .NET 10 SDK
- Node.js 24+

### Compilar

```bash
# Build everything
dotnet build

# Build the login SPA
cd login-app
npm ci
npm run build

# Run the server
dotnet run --project src/Authagonal.Server
```

### Build Docker

```bash
# Server image (multi-stage: builds SPA + .NET in one image)
docker build -t authagonal .

# Migration tool
docker build -f Dockerfile.migration -t authagonal-migration .
```

## Como Biblioteca (NuGet)

Referencie os pacotes Authagonal no seu próprio projeto ASP.NET Core:

```xml
<PackageReference Include="Authagonal.Server" Version="x.y.z" />
<PackageReference Include="Authagonal.AzureProvider" Version="x.y.z" />
```

O pacote do provedor de armazenamento é plugável: `Authagonal.AzureProvider` para Azure Table Storage (a configuração padrão do `AddAuthagonal()`), `Authagonal.SqlProvider` para PostgreSQL ou SQLite auto-hospedados (consulte [Backend SQL](#backend-sql)), ou `Authagonal.AwsProvider` para DynamoDB / S3 / Secrets Manager (consulte [Backend AWS](#backend-aws)).

Em seguida, componha-o no seu `Program.cs`:

```csharp
builder.Services.AddSingleton<IAuthHook, MyAuditHook>();   // Custom hook
builder.Services.AddSingleton<IEmailService, MyEmailService>(); // Custom email
builder.Services.AddAuthagonal(builder.Configuration);

var app = builder.Build();
app.UseAuthagonal();
app.MapAuthagonalEndpoints();
app.MapFallbackToFile("index.html");
app.Run();
```

Consulte [Extensibilidade](extensibility) para todos os pontos de extensão e [demos/custom-server/](https://github.com/authagonal/authagonal/tree/master/demos/custom-server) para um exemplo completo.

### Email

O remetente [Resend](https://resend.com) integrado é ativado automaticamente quando `Email:ResendApiKey` e `Email:SenderEmail` estão configurados, sem necessidade de registrar um serviço. Sem nenhum `IEmailService`, os emails de verificação e de redefinição de senha são **descartados silenciosamente** e, como o login exige um email confirmado por padrão, os usuários auto-registrados nunca conseguem entrar (`UseAuthagonal` registra um aviso na inicialização). Defina as chaves `Email:*`, registre seu próprio `IEmailService` antes de `AddAuthagonal()`, ou liste seus domínios em `Auth:AutoConfirmEmailDomains` para pular a verificação (apenas dev/teste). Consulte [Configuração → Email](configuration#email).

## Backend SQL

Para executar no seu próprio banco de dados em vez de um serviço de nuvem, referencie `Authagonal.SqlProvider` e registre-o **antes** de `AddAuthagonal()`: são esses registros que fazem o `AddAuthagonal()` pular sua configuração do Azure Table Storage:

```csharp
using Authagonal.SqlProvider;

// PostgreSQL — the production self-hosted backend
builder.Services.AddAuthagonalPostgres(
    "Host=db;Database=authagonal;Username=auth;Password=…;SSL Mode=VerifyFull;Root Certificate=/etc/ssl/certs/db-ca.pem");

// or SQLite — one file, no server. Suits embedded hosts, CI and small single-node deployments
builder.Services.AddAuthagonalSqlite("Data Source=authagonal.db");

builder.Services.AddAuthagonal(builder.Configuration);
```

As tabelas espelham um a um os layouts do Azure e do DynamoDB e são criadas na inicialização se não existirem (toda instrução é um `IF NOT EXISTS`, então é seguro que vários pods disputem a criação e nada acontece contra um esquema que você mesmo provisionou). Nenhuma configuração `Storage:*` é necessária. O conjunto de chaves do Data Protection é persistido no mesmo banco, de modo que cookies e tokens antiforgery sobrevivem a reinicializações e funcionam entre pods sem nenhum serviço adicional.

O SQLite serializa as escritas, portanto é um backend de nó único: o lease em processo e o barramento de eventos de cluster registrados por padrão são a combinação correta ali. Uma implantação PostgreSQL com vários pods vai querer `clustering.UseSql(dataSource)` para a eleição de líder.

> **Collation.** No PostgreSQL as colunas de chave são fixadas em `COLLATE "C"`. O esquema de chaves é ordinal por bytes de ponta a ponta (limites de prefixo, faixas de partição por ambiente, a varredura de expiração de concessões, a paginação por keyset), e um banco criado com uma collation linguística -- `en_US.UTF-8` e locales ICU são os padrões comuns -- ordenaria pontuação e maiúsculas de outra forma e retornaria silenciosamente as linhas erradas. A fixação torna o layout independente de como o banco foi criado; você não precisa criá-lo de nenhuma maneira específica.

Consulte o [README do pacote](https://github.com/authagonal/authagonal/tree/master/src/Authagonal.SqlProvider) para o layout das tabelas, as primitivas de concorrência por trás de cada garantia de uso único, e como adicionar um dialeto para outro motor.

## Backend AWS

Para executar na AWS em vez do Azure, referencie `Authagonal.AwsProvider` e registre o pacote AWS **antes** de `AddAuthagonal()`: são esses registros que fazem o `AddAuthagonal()` pular sua configuração do Azure Table Storage:

```csharp
using Authagonal.AwsProvider;

builder.Services.AddAuthagonalAwsStorage(
    dynamoDb,                // IAmazonDynamoDB — required
    secretsManager,          // IAmazonSecretsManager — optional; replaces the plaintext ISecretProvider
    s3,                      // IAmazonS3 — optional; used for DataProtection keys
    "my-auth-keys-bucket");  // S3 bucket for the DataProtection key ring
builder.Services.AddAuthagonal(builder.Configuration);
```

As tabelas do DynamoDB espelham o layout do Azure um a um e são garantidas na inicialização (idempotente: uma no-op quando já estão provisionadas pelo Terraform). As credenciais são resolvidas via a cadeia padrão da AWS (env / função de instância EC2 / IRSA), portanto não há divisão entre connection-string e managed-identity: nenhuma configuração `Storage:*` é necessária.

> ⚠️ **Chaves de DataProtection no S3.** Sem um cliente S3 + bucket, o key ring do Data Protection do ASP.NET Core é mantido em memória: adequado para um único nó em dev, mas cookies e tokens antiforgery quebram ao reiniciar e entre nós em produção. Sempre passe o cliente S3 e o bucket em uma implantação AWS de produção.

## SPA de Login (npm)

A interface de login é publicada como um pacote npm para personalização:

```bash
npm install @authagonal/login
```

O pacote inclui JS e CSS compilados: importe componentes e estilos diretamente na sua própria aplicação React. Consulte [Servidor Personalizado](custom-server) para um guia completo.

## Checklist de segurança para produção

Antes de expor o Authagonal a tráfego real, confirme o seguinte. Cada item é detalhado na página de [Configuração](configuration).

- **Execute atrás de um proxy com terminação TLS, e declare-o.** O Authagonal deve ficar atrás de um proxy reverso / ingress que termina o TLS (ou terminar o TLS ele próprio). O HSTS só é emitido em HTTPS e `/connect/*` recusa texto simples, portanto o proxy deve encaminhar `X-Forwarded-Proto: https` — e esse cabeçalho é ignorado enquanto não definir `ForwardedHeaders:KnownNetworks` (ou `KnownProxies`) para o CIDR ou o endereço do seu proxy. Use `["0.0.0.0/0", "::/0"]` se o proxy não tiver endereço fixo e nada mais conseguir alcançar o processo. `ForwardedHeaders:ForwardLimit` tem por padrão `1` (confiar apenas no último salto).
- **Defina `SecretProvider:VaultUri`.** O provedor de segredos padrão é **texto simples**: sem o Key Vault, os segredos de clientes OIDC upstream e as sementes TOTP / MFA são armazenados em texto claro no Table Storage (e nos backups). Configure o Key Vault para qualquer implantação em produção.
- **Bloqueie a API de administração.** `AdminApi:Enabled` tem por padrão **true**. O scope de administração (`AdminApi:Scope`, padrão `authagonal-admin`) concede gestão total e impersonação de utilizadores. Restrinja a nível de rede as rotas de administração `/api/v1/*` e controle rigorosamente quem recebe o scope de administração, ou defina `AdminApi:Enabled = false` se não for usado.
- **Proteja os endpoints internos.** Defina `Cluster:Secret` para que o endpoint interno `/_internal/backchannel-logout` exija o cabeçalho `X-Cluster-Secret` (comparado em tempo constante). Sem segredo o endpoint não autoriza **ninguém** e responde 404: um endereço de origem não é uma credencial, e loopback é o que um proxy inverso no mesmo host apresenta para cada pedido que reencaminha. `Cluster:AllowLoopbackWithoutSecret` readmite um par loopback antes do reencaminhamento, apenas para desenvolvimento local. Nada no produto entregue chama este endpoint, portanto falhar fechado não quebra nenhum fluxo próprio: defina o segredo se construir sobre ele a sua própria difusão pod-a-pod.
- **Criptografe os backups.** Com o provedor de segredos de texto simples, os backups contêm segredos. A tabela `SigningKeys` é excluída dos backups por padrão; se optar por incluí-la via `Backup:IncludeSigningKeys`, o alvo do backup deve estar criptografado em repouso. Consulte [Backup e Restauração](backup-restore).

## Ferramenta de Migração

Para migrar do Duende IdentityServer + SQL Server:

```bash
docker run authagonal-migration -- \
  --Source:ConnectionString "Server=...;Database=...;" \
  --Target:ConnectionString "DefaultEndpointsProtocol=https;..." \
  [--DryRun true] \
  [--MigrateRefreshTokens true]
```

Consulte [Migração](migration) para detalhes.
