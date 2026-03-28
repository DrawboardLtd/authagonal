---
layout: default
title: Inicio
locale: es
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

Servidor de autenticacion OAuth 2.0 / OpenID Connect / SAML 2.0 respaldado por Azure Table Storage.

Un unico despliegue autonomo. El servidor y la interfaz de inicio de sesion se entregan como una sola imagen Docker -- la SPA se sirve desde el mismo origen que la API, por lo que la autenticacion por cookies, las redirecciones y la CSP funcionan sin complejidad de origen cruzado.

## Caracteristicas principales

- **Proveedor OIDC** -- authorization_code + PKCE, client_credentials, refresh_token con rotacion de uso unico
- **SAML 2.0 SP** -- implementacion propia con soporte completo de Azure AD (respuesta firmada, asercion, o ambas)
- **Federacion OIDC dinamica** -- conexion con Google, Apple, Azure AD o cualquier IdP compatible con OIDC
- **Aprovisionamiento TCC** -- aprovisionamiento Try-Confirm-Cancel en aplicaciones posteriores en el momento de la autorizacion
- **Interfaz de inicio de sesion personalizable** -- configurable en tiempo de ejecucion mediante un archivo JSON -- logotipo, colores, CSS personalizado -- sin necesidad de recompilacion
- **Hooks de autenticacion** -- extensibilidad `IAuthHook` para registro de auditoria, validacion personalizada, webhooks
- **Biblioteca composable** -- `AddAuthagonal()` / `UseAuthagonal()` para alojar en su propio proyecto con sustituciones de servicios personalizadas
- **Azure Table Storage** -- almacenamiento backend de bajo costo, compatible con serverless
- **APIs de administracion** -- CRUD de usuarios, gestion de proveedores SAML/OIDC, enrutamiento de dominios SSO, suplantacion de tokens

## Arquitectura

```
Client App                    Authagonal                         IdP (Azure AD, etc.)
    |                             |                                    |
    +- GET /connect/authorize --> |                                    |
    |                             +- 302 -> /login (SPA)               |
    |                             |   +- SSO check                     |
    |                             |   +- SAML/OIDC redirect ---------->|
    |                             |                                    |
    |                             | <-- SAML Response / OIDC callback -|
    |                             |   +- Create user + cookie          |
    |                             |                                    |
    |                             +- TCC provisioning (try/confirm)    |
    |                             +- Issue authorization code          |
    | <-- 302 ?code=...&state=...|                                    |
    |                             |                                    |
    +- POST /connect/token -----> |                                    |
    | <-- { access_token, ... } --|                                    |
```

Comience con la guia de [Instalacion](installation) o vaya directamente al [Inicio rapido](quickstart). Para alojar Authagonal en su propio proyecto, consulte [Extensibilidad](extensibility).
