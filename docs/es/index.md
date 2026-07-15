---
layout: default
title: Inicio
locale: es
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

Servidor de autenticacion OAuth 2.0 / OpenID Connect / SAML 2.0 para .NET, respaldado por almacenamiento en la nube intercambiable -- Azure Table Storage o AWS (DynamoDB / S3 / Secrets Manager).

Un unico despliegue autonomo. El servidor y la interfaz de inicio de sesion se entregan como una sola imagen Docker -- la SPA se sirve desde el mismo origen que la API, por lo que la autenticacion por cookies, las redirecciones y la CSP funcionan sin complejidad de origen cruzado.

> **¿Prefieres un servicio gestionado?** [Authagonal Cloud](https://authagonal.io) lo ejecuta todo por ti -- multiinquilino, todas las funciones en todos los planes, sin tarifas de SSO por conexion. → [authagonal.io](https://authagonal.io)

## Caracteristicas principales

- **Proveedor OIDC** -- concesiones authorization_code + PKCE, client_credentials, refresh_token, device_code con rotacion de uso unico
- **SAML 2.0 SP** -- implementacion propia con soporte completo de Azure AD (respuesta firmada, asercion, o ambas), un par de claves de SP por conexion para AuthnRequests firmados + descifrado de `EncryptedAssertion`, y cierre de sesion unico (Single Logout, iniciado por SP e IdP)
- **Federacion OIDC dinamica** -- conexion con Google, Apple, Azure AD o cualquier IdP compatible con OIDC
- **Autenticacion multifactor** -- TOTP, WebAuthn/passkeys, codigos de recuperacion; politica por cliente (`Disabled` / `Enabled` / `Required`) con anulacion por usuario mediante `IAuthHook`, aplicada tambien a los inicios de sesion federados
- **Aprovisionamiento SCIM 2.0** -- aprovisionamiento entrante de usuarios/grupos desde Entra ID, Okta, OneLogin; listado paginado por cursor y filtros `eq` respaldados por indice ciego (blind-index)
- **Pantalla de consentimiento OAuth** -- consentimiento por cliente con reaviso segun el alcance y gestion de concesiones
- **Concesion de autorizacion de dispositivo** -- flujo RFC 8628 para dispositivos con entrada limitada (televisores inteligentes, CLIs, IoT)
- **Introspeccion de tokens** -- RFC 7662 para que los servidores de recursos verifiquen la validez de los tokens
- **Cierre de sesion por canal trasero (Back-Channel Logout)** -- notificaciones OIDC Back-Channel Logout 1.0 a las partes confiantes
- **Autoservicio RGPD** -- exportacion de datos y eliminacion programada de la cuenta desde la pagina de cuenta alojada
- **Aprovisionamiento TCC** -- aprovisionamiento Try-Confirm-Cancel en aplicaciones posteriores en el momento de la autorizacion
- **Interfaz de inicio de sesion personalizable** -- configurable en tiempo de ejecucion mediante un archivo JSON -- logotipo, colores, CSS personalizado -- sin necesidad de recompilacion; traducida a 10 idiomas
- **Hooks de autenticacion** -- extensibilidad `IAuthHook` para registro de auditoria, validacion personalizada, webhooks
- **Puntos de cifrado de PII** -- puntos de extension `IFieldCipher` / `IIndexTokenizer` para cifrado a nivel de campo en reposo con busqueda por indice ciego con clave (HMAC); codigos de recuperacion cifrados mediante `ISecretProvider`
- **HashiCorp Vault Transit** -- firma remota de JWT sin acceso a la clave privada local
- **Biblioteca composable** -- `AddAuthagonal()` / `UseAuthagonal()` para alojar en su propio proyecto con sustituciones de servicios personalizadas
- **Compatible con Native AOT** -- recorte de IL y serializacion JSON generada por codigo fuente para un arranque rapido
- **Almacenamiento en la nube intercambiable** -- Azure Table Storage o AWS (DynamoDB / S3 / Secrets Manager); backends de bajo costo y compatibles con serverless
- **Copia de seguridad y restauracion** -- copias de seguridad incrementales (basadas en registro de cambios con respaldo de escaneo completo), verificacion de integridad, seguimiento de eliminaciones basado en tombstones
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
