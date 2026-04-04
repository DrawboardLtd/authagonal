---
layout: default
title: Personalizacion visual
locale: es
---

# Personalizacion de la interfaz de inicio de sesion

La SPA de inicio de sesion es configurable en tiempo de ejecucion mediante un archivo `branding.json` servido desde la raiz web. No se requiere recompilacion -- simplemente monte su configuracion y recursos.

## Como funciona

Al iniciar, la SPA obtiene `/branding.json`. Si el archivo no existe o no es accesible, se usan los valores predeterminados. La configuracion controla:

- El nombre de la aplicacion (mostrado en el encabezado y titulo de la pagina)
- La imagen del logotipo
- El color principal (botones, enlaces, indicadores de enfoque)
- La visibilidad del enlace de contrasena olvidada
- CSS personalizado para una estilizacion mas profunda

## Configuracion

Coloque un archivo `branding.json` en el directorio `wwwroot/` (o montelo en el contenedor Docker):

```json
{
  "appName": "Acme Corp",
  "logoUrl": "/branding/logo.svg",
  "primaryColor": "#1a56db",
  "supportEmail": "help@acme.com",
  "showForgotPassword": true,
  "customCssUrl": "/branding/custom.css"
}
```

### Opciones

| Propiedad | Tipo | Predeterminado | Descripcion |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | Se muestra en el encabezado y titulo de la pestana del navegador |
| `logoUrl` | `string \| null` | `null` | URL a una imagen de logotipo. Cuando se establece, reemplaza el encabezado de texto. |
| `primaryColor` | `string` | `"#2563eb"` | Color hexadecimal para botones, enlaces e indicadores de enfoque |
| `supportEmail` | `string \| null` | `null` | Correo electronico de soporte (reservado para uso futuro) |
| `showForgotPassword` | `boolean` | `true` | Mostrar/ocultar el enlace "Contrasena olvidada?" en la pagina de inicio de sesion |
| `customCssUrl` | `string \| null` | `null` | URL a un archivo CSS personalizado cargado despues de los estilos predeterminados |

## Ejemplo Docker

Monte sus archivos de personalizacion en el contenedor:

```bash
docker run -p 8080:8080 \
  -v ./my-branding/branding.json:/app/wwwroot/branding.json \
  -v ./my-branding/logo.svg:/app/wwwroot/branding/logo.svg \
  -v ./my-branding/custom.css:/app/wwwroot/branding/custom.css \
  -e Storage__ConnectionString="..." \
  -e Issuer="https://auth.example.com" \
  authagonal
```

O con docker-compose:

```yaml
services:
  authagonal:
    build: .
    ports:
      - "8080:8080"
    volumes:
      - ./my-branding/branding.json:/app/wwwroot/branding.json
      - ./my-branding/assets:/app/wwwroot/branding
    environment:
      - Storage__ConnectionString=...
      - Issuer=https://auth.example.com
```

## CSS personalizado

La opcion `customCssUrl` carga una hoja de estilos adicional despues de los estilos predeterminados, por lo que sus reglas tienen prioridad. Util para cambiar fuentes, ajustar espaciado o reestilizar elementos especificos.

### Clases CSS disponibles

| Clase | Elemento |
|---|---|
| `.auth-container` | Contenedor de pagina completa (flex center) |
| `.auth-card` | La tarjeta de inicio de sesion (caja blanca con sombra) |
| `.auth-logo` | Area de logo/titulo |
| `.auth-logo h1` | Encabezado de texto (cuando no hay imagen de logo) |
| `.auth-logo-img` | Imagen del logo (cuando `logoUrl` esta establecido) |
| `.auth-title` | Titulos de pagina ("Iniciar sesion", "Restablecer su contrasena") |
| `.auth-subtitle` | Texto secundario debajo de los titulos |
| `.form-group` | Contenedor de campo de formulario |
| `.form-group label` | Etiquetas de campos |
| `input` | Campos de entrada de texto |
| `.btn-primary` | Boton de accion principal |
| `.btn-secondary` | Boton secundario (por ejemplo, "Continuar con SSO") |
| `.alert-error` | Mensajes de error |
| `.alert-success` | Mensajes de exito |
| `.link` | Enlaces de texto |
| `.sso-notice` | Aviso de deteccion SSO |
| `.password-requirements` | Lista de requisitos de robustez de contrasena |

### Propiedades CSS personalizadas

El color principal se expone como una propiedad CSS personalizada. Puede sobreescribirlo en su CSS personalizado en lugar de usar `branding.json`:

```css
:root {
  --color-primary: #059669;
}
```

### Ejemplo: Fondo y fuente personalizados

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}

.auth-card {
  border-radius: 16px;
  box-shadow: 0 4px 24px rgba(0, 0, 0, 0.2);
}

.auth-logo h1 {
  font-family: 'Inter', sans-serif;
  font-weight: 800;
}
```

## Niveles de personalizacion

| Nivel | Lo que hace | Ruta de actualizacion |
|---|---|---|
| **Solo configuracion** | Monte `branding.json` + logotipo | Transparente -- actualice la imagen Docker, mantenga sus montajes |
| **Configuracion + CSS** | Agregue `customCssUrl` con sustituciones de estilo | Igual -- las clases CSS son estables |
| **Paquete npm** | `npm install @drawboard/authagonal-login`, personalice `branding.json`, compile en `wwwroot/` | Actualizable -- `npm update` obtiene nuevas versiones |
| **Bifurcar la SPA** | Clone `login-app/`, modifique el codigo fuente, compile su propia version | Usted es dueno de la interfaz -- las actualizaciones del servidor son independientes |
| **Escribir la suya** | Construya un frontend completamente personalizado contra la API de autenticacion | Control total -- ver [API de autenticacion](auth-api) para el contrato |

Consulte `demos/custom-server/` para un ejemplo funcional con personalizacion visual (tema verde, "Acme Corp").
