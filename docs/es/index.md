---
layout: default
title: Inicio
locale: es
---

<p align="center">
  <img src="{{ 'assets/logo.svg' | relative_url }}" width="120" alt="Authagonal logo">
</p>

# Authagonal

Servidor de autenticación OAuth 2.0 / OpenID Connect / SAML 2.0 para .NET, respaldado por almacenamiento en la nube intercambiable: Azure Table Storage o AWS (DynamoDB / S3 / Secrets Manager).

Un único despliegue autónomo. El servidor y la interfaz de inicio de sesión se entregan como una sola imagen Docker: la SPA se sirve desde el mismo origen que la API, por lo que la autenticación por cookies, las redirecciones y la CSP funcionan sin complejidad de origen cruzado.

> **¿Prefieres un servicio gestionado?** [Authagonal Cloud](https://authagonal.io) lo ejecuta todo por ti: multiinquilino, todas las funciones en todos los planes, sin tarifas de SSO por conexión. → [authagonal.io](https://authagonal.io)

## Características principales

- **Proveedor OIDC**: concesiones authorization_code + PKCE, client_credentials, refresh_token, device_code con rotación de uso único
- **SAML 2.0 SP**: implementación propia con soporte completo de Azure AD (respuesta firmada, aserción, o ambas), un par de claves de SP por conexión para AuthnRequests firmados + descifrado de `EncryptedAssertion`, y cierre de sesión único (Single Logout, iniciado por SP e IdP)
- **Federación OIDC dinámica**: conexión con Google, Apple, Azure AD o cualquier IdP compatible con OIDC
- **Autenticación multifactor**: TOTP, WebAuthn/passkeys, códigos de recuperación; política por cliente (`Disabled` / `Enabled` / `Required`) con anulación por usuario mediante `IAuthHook`, aplicada también a los inicios de sesión federados
- **Aprovisionamiento SCIM 2.0**: aprovisionamiento entrante de usuarios/grupos desde Entra ID, Okta, OneLogin; listado paginado por cursor y filtros `eq` respaldados por índice ciego (blind-index)
- **Pantalla de consentimiento OAuth**: consentimiento por cliente con reaviso según el alcance y gestión de concesiones
- **Concesión de autorización de dispositivo**: flujo RFC 8628 para dispositivos con entrada limitada (televisores inteligentes, CLIs, IoT)
- **Introspección de tokens**: RFC 7662 para que los servidores de recursos verifiquen la validez de los tokens
- **Cierre de sesión por canal trasero (Back-Channel Logout)**: notificaciones OIDC Back-Channel Logout 1.0 a las partes confiantes
- **Autoservicio RGPD**: exportación de datos y eliminación programada de la cuenta desde la página de cuenta alojada
- **Aprovisionamiento TCC**: aprovisionamiento Try-Confirm-Cancel en aplicaciones posteriores en el momento de la autorización
- **Interfaz de inicio de sesión personalizable**: configurable en tiempo de ejecución mediante un archivo JSON (logotipo, colores, CSS personalizado), sin necesidad de recompilación; traducida a 10 idiomas
- **Hooks de autenticación**: extensibilidad `IAuthHook` para registro de auditoría, validación personalizada, webhooks
- **Puntos de cifrado de PII**: puntos de extensión `IFieldCipher` / `IIndexTokenizer` para cifrado a nivel de campo en reposo con búsqueda por índice ciego con clave (HMAC); códigos de recuperación cifrados mediante `ISecretProvider`
- **HashiCorp Vault Transit**: firma remota de JWT sin acceso a la clave privada local
- **Biblioteca composable**: `AddAuthagonal()` / `UseAuthagonal()` para alojar en su propio proyecto con sustituciones de servicios personalizadas
- **Compatible con Native AOT**: recorte de IL y serialización JSON generada por código fuente para un arranque rápido
- **Almacenamiento en la nube intercambiable**: Azure Table Storage o AWS (DynamoDB / S3 / Secrets Manager); backends de bajo costo y compatibles con serverless
- **Copia de seguridad y restauración**: copias de seguridad incrementales (basadas en registro de cambios con respaldo de escaneo completo), verificación de integridad, seguimiento de eliminaciones basado en tombstones
- **APIs de administración**: CRUD de usuarios, gestión de proveedores SAML/OIDC, enrutamiento de dominios SSO, suplantación de tokens

## Arquitectura

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

Comience con la guía de [Instalación](installation) o vaya directamente al [Inicio rápido](quickstart). Para alojar Authagonal en su propio proyecto, consulte [Extensibilidad](extensibility).
