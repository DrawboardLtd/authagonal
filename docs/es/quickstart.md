---
layout: default
title: Inicio rápido
locale: es
---

# Inicio rápido

Ponga Authagonal en funcionamiento localmente en 5 minutos.

## 1. Iniciar el servidor

```bash
docker compose up
```

Esto inicia Authagonal en `http://localhost:8080` con Azurite para el almacenamiento.

## 2. Verificar que está funcionando

```bash
# Health check
curl http://localhost:8080/health

# OIDC discovery
curl http://localhost:8080/.well-known/openid-configuration

# Login page (returns the SPA)
curl http://localhost:8080/login
```

## 3. Registrar un cliente

Agregue un cliente a su `appsettings.json` (o páselo mediante variables de entorno):

```json
{
  "Clients": [
    {
      "ClientId": "my-web-app",
      "ClientName": "My Web App",
      "AllowedGrantTypes": ["authorization_code"],
      "RedirectUris": ["http://localhost:3000/callback"],
      "PostLogoutRedirectUris": ["http://localhost:3000"],
      "AllowedScopes": ["openid", "profile", "email"],
      "AllowedCorsOrigins": ["http://localhost:3000"],
      "RequirePkce": true,
      "RequireClientSecret": false
    }
  ]
}
```

Los clientes se inyectan al inicio, seguro en cada despliegue.

## 4. Iniciar un inicio de sesión

Redirija a sus usuarios a:

```
http://localhost:8080/connect/authorize
  ?client_id=my-web-app
  &redirect_uri=http://localhost:3000/callback
  &response_type=code
  &scope=openid profile email
  &state=random-state
  &code_challenge=...
  &code_challenge_method=S256
```

El usuario ve la página de inicio de sesión, se autentica y es redirigido con un código de autorización.

> **Primer usuario:** registre uno en `http://localhost:8080/login/register`, o cree uno mediante la [API de administración](admin-api). El autorregistro envía un correo de verificación y, sin un remitente de correo configurado (el valor predeterminado local), ese correo se descarta, así que para pruebas locales establezca `Auth__AutoConfirmEmailDomains__0=example.dev` (cualquier dominio con el que se registre) para omitir la verificación, o configure `Email:ResendApiKey` + `Email:SenderEmail`. Consulte [Configuración → Email](configuration#email).

## 5. Intercambiar el código

```bash
curl -X POST http://localhost:8080/connect/token \
  -d grant_type=authorization_code \
  -d code=THE_CODE \
  -d redirect_uri=http://localhost:3000/callback \
  -d client_id=my-web-app \
  -d code_verifier=THE_VERIFIER
```

Respuesta:

```json
{
  "access_token": "eyJ...",
  "id_token": "eyJ...",
  "token_type": "Bearer",
  "expires_in": 1800
}
```

## Demo funcional

El directorio `demos/sample-app/` contiene una SPA React completa + API que implementa el flujo OIDC completo descrito anteriormente. Consulte el [README de demos](https://github.com/authagonal/authagonal/tree/master/demos) para las instrucciones.

## Próximos pasos

- [Configuración](configuration): referencia completa de todos los ajustes
- [Extensibilidad](extensibility): alojar como biblioteca, agregar hooks personalizados
- [Personalización visual](branding): personalizar la interfaz de inicio de sesión
- [SAML](saml): agregar proveedores SSO SAML
- [Aprovisionamiento](provisioning): aprovisionar usuarios en aplicaciones posteriores
