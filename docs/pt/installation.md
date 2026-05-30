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
  authagonal
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
- Node.js 22+

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
<PackageReference Include="Authagonal.Storage" Version="x.y.z" />
```

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

## SPA de Login (npm)

A interface de login é publicada como um pacote npm para personalização:

```bash
npm install @authagonal/login
```

O pacote inclui JS e CSS compilados — importe componentes e estilos diretamente na sua própria aplicação React. Consulte [Servidor Personalizado](custom-server) para um guia completo.

## Checklist de segurança para produção

Antes de expor o Authagonal a tráfego real, confirme o seguinte. Cada item é detalhado na página de [Configuração](configuration).

- **Execute atrás de um proxy com terminação TLS.** O Authagonal deve ficar atrás de um proxy reverso / ingress que termina o TLS. O cookie de sessão usa `SecurePolicy = SameAsRequest` e o HSTS só é emitido em HTTPS, portanto o proxy deve encaminhar `X-Forwarded-Proto: https`. Defina `ForwardedHeaders:KnownNetworks` (ou `KnownProxies`) para o CIDR do seu ingress / pod para que o IP do cliente e o esquema não possam ser falsificados; `ForwardedHeaders:ForwardLimit` tem por padrão `1` (confiar apenas no último salto).
- **Defina `SecretProvider:VaultUri`.** O provedor de segredos padrão é **texto simples** — sem o Key Vault, os segredos de clientes OIDC upstream e as sementes TOTP / MFA são armazenados em texto claro no Table Storage (e nos backups). Configure o Key Vault para qualquer implantação em produção.
- **Bloqueie a API de administração.** `AdminApi:Enabled` tem por padrão **true**. O scope de administração (`AdminApi:Scope`, padrão `authagonal-admin`) concede gestão total e impersonação de utilizadores. Restrinja a nível de rede as rotas de administração `/api/v1/*` e controle rigorosamente quem recebe o scope de administração, ou defina `AdminApi:Enabled = false` se não for usado.
- **Proteja os endpoints internos.** Defina `Cluster:Secret` para que `/_internal/cluster/gossip` e `/_internal/backchannel-logout` exijam o cabeçalho `X-Cluster-Secret` — especialmente quando o gossip é roteado através de um balanceador de carga via `Cluster:InternalUrl`.
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
