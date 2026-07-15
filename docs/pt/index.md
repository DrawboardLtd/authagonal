---
layout: default
title: Início
locale: pt
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

Servidor de autenticação OAuth 2.0 / OpenID Connect / SAML 2.0 para .NET, com armazenamento em nuvem plugável: Azure Table Storage ou AWS (DynamoDB / S3 / Secrets Manager).

Uma implantação única e autossuficiente. O servidor e a interface de login são entregues como uma única imagem Docker: o SPA é servido a partir da mesma origem que a API, portanto autenticação por cookie, redirecionamentos e CSP funcionam sem complexidade de origens cruzadas.

> **Prefere um serviço gerenciado?** O [Authagonal Cloud](https://authagonal.io) executa tudo isso para você: multi-inquilino, todos os recursos em todos os planos, sem taxas de SSO por conexão. → [authagonal.io](https://authagonal.io)

## Funcionalidades Principais

- **Provedor OIDC**: grants authorization_code + PKCE, client_credentials, refresh_token, device_code com rotação de uso único
- **SAML 2.0 SP**: implementação própria com suporte completo ao Azure AD (resposta assinada, asserção ou ambos), um par de chaves SP por conexão para AuthnRequests assinados + desencriptação de `EncryptedAssertion`, e Single Logout (iniciado pelo SP e pelo IdP)
- **Federação OIDC Dinâmica**: conecte-se ao Google, Apple, Azure AD ou qualquer IdP compatível com OIDC
- **Autenticação Multifator**: TOTP, WebAuthn/passkeys, códigos de recuperação; política por cliente (`Disabled` / `Enabled` / `Required`) com substituição por utilizador via `IAuthHook`, aplicada também aos logins federados
- **Provisionamento SCIM 2.0**: provisionamento de entrada de utilizadores/grupos a partir do Entra ID, Okta, OneLogin; listagem paginada por cursor e filtros `eq` suportados por índice cego (blind-index)
- **Ecrã de Consentimento OAuth**: consentimento por cliente com re-prompt sensível a scopes e gestão de concessões
- **Device Authorization Grant**: fluxo RFC 8628 para dispositivos com entrada limitada (smart TVs, CLIs, IoT)
- **Introspeção de Token**: RFC 7662 para servidores de recursos verificarem a validade dos tokens
- **Back-Channel Logout**: notificações OIDC Back-Channel Logout 1.0 para as relying parties
- **Autosserviço RGPD**: exportação de dados e eliminação agendada de conta a partir da página de conta alojada
- **Provisionamento TCC**: provisionamento Try-Confirm-Cancel em aplicações downstream no momento da autorização
- **Interface de Login Personalizável**: configurável em tempo de execução via arquivo JSON (logotipo, cores, CSS personalizado) sem necessidade de rebuild; localizada em 10 idiomas
- **Auth Hooks**: extensibilidade via `IAuthHook` para registro de auditoria, validação personalizada, webhooks
- **Pontos de Extensão para Encriptação de PII**: pontos de extensão `IFieldCipher` / `IIndexTokenizer` para encriptação ao nível do campo em repouso com pesquisa por índice cego com chave (HMAC); códigos de recuperação encriptados via `ISecretProvider`
- **HashiCorp Vault Transit**: assinatura remota de JWT sem acesso local à chave privada
- **Biblioteca Composável**: `AddAuthagonal()` / `UseAuthagonal()` para hospedar no seu próprio projeto com substituições de serviço personalizadas
- **Pronto para Native AOT**: IL trimming e serialização JSON gerada na compilação para arranque rápido
- **Armazenamento em nuvem plugável**: Azure Table Storage ou AWS (DynamoDB / S3 / Secrets Manager); backends de baixo custo e compatíveis com serverless
- **Cópias de Segurança e Restauro**: cópias de segurança incrementais (baseadas em registo de alterações com um backstop de varrimento completo), verificação de integridade, rastreio de eliminações baseado em tombstones
- **APIs de Administração**: CRUD de utilizadores, gestão de provedores SAML/OIDC, roteamento de domínios SSO, impersonação de tokens

## Arquitetura

```
Client App                    Authagonal                         IdP (Azure AD, etc.)
    │                             │                                    │
    ├─ GET /connect/authorize ──► │                                    │
    │                             ├─ 302 → /login (SPA)                │
    │                             │   ├─ SSO check                     │
    │                             │   └─ SAML/OIDC redirect ─────────► │
    │                             │                                    │
    │                             │ ◄── SAML Response / OIDC callback ─┤
    │                             │   └─ Create user + cookie          │
    │                             │                                    │
    │                             ├─ TCC provisioning (try/confirm)    │
    │                             ├─ Issue authorization code          │
    │ ◄─ 302 ?code=...&state=... ┤                                    │
    │                             │                                    │
    ├─ POST /connect/token ─────► │                                    │
    │ ◄─ { access_token, ... } ──┤                                    │
```

Comece com o guia de [Instalação](installation) ou vá diretamente para o [Início Rápido](quickstart). Para hospedar o Authagonal no seu próprio projeto, consulte [Extensibilidade](extensibility).
