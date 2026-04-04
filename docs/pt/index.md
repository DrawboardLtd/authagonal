---
layout: default
title: Início
locale: pt
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

Servidor de autenticação OAuth 2.0 / OpenID Connect / SAML 2.0 com armazenamento em Azure Table Storage.

Uma implantação única e autossuficiente. O servidor e a interface de login são entregues como uma única imagem Docker — o SPA é servido a partir da mesma origem que a API, portanto autenticação por cookie, redirecionamentos e CSP funcionam sem complexidade de origens cruzadas.

## Funcionalidades Principais

- **Provedor OIDC** — grants authorization_code + PKCE, client_credentials, refresh_token com rotação de uso único
- **SAML 2.0 SP** — implementação própria com suporte completo ao Azure AD (resposta assinada, asserção ou ambos)
- **Federação OIDC Dinâmica** — conecte-se ao Google, Apple, Azure AD ou qualquer IdP compatível com OIDC
- **Provisionamento TCC** — provisionamento Try-Confirm-Cancel em aplicações downstream no momento da autorização
- **Interface de Login Personalizável** — configurável em tempo de execução via arquivo JSON — logotipo, cores, CSS personalizado — sem necessidade de rebuild
- **Auth Hooks** — extensibilidade via `IAuthHook` para registro de auditoria, validação personalizada, webhooks
- **Biblioteca Composável** — `AddAuthagonal()` / `UseAuthagonal()` para hospedar no seu próprio projeto com substituições de serviço personalizadas
- **Azure Table Storage** — backend de armazenamento de baixo custo e compatível com serverless
- **APIs de Administração** — CRUD de utilizadores, gestão de provedores SAML/OIDC, roteamento de domínios SSO, impersonação de tokens

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
