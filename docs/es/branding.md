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
| `showRegistration` | `boolean` | `false` | Mostrar/ocultar el enlace de registro de autoservicio |
| `customCssUrl` | `string \| null` | `null` | URL a un archivo CSS personalizado cargado despues de los estilos predeterminados |
| `welcomeTitle` | `LocalizedString` | `null` | Anular el titulo de la pagina de inicio de sesion (cadena simple o `{ "en": "...", "de": "..." }`) |
| `welcomeSubtitle` | `LocalizedString` | `null` | Anular el subtitulo de la pagina de inicio de sesion |
| `languages` | `array \| null` | `null` | Opciones del selector de idioma (`[{ "code": "en", "label": "English" }, ...]`) |

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

### Propiedades CSS personalizadas

El color principal se establece mediante la propiedad CSS personalizada `--brand-primary` (que alimenta el tema de Tailwind). Puede sobreescribirlo en su CSS personalizado en lugar de usar `branding.json`:

```css
:root {
  --brand-primary: #059669;
}
```

La interfaz de inicio de sesion utiliza Tailwind CSS. El CSS personalizado puede apuntar a elementos HTML estandar y clases de utilidad de Tailwind. Los componentes de UI exportados (`Button`, `Input`, `Card`, `Alert`, etc.) usan Tailwind internamente.

### Ejemplo: Fondo y fuente personalizados

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}
```

## Niveles de personalizacion

| Nivel | Lo que hace | Ruta de actualizacion |
|---|---|---|
| **Solo configuracion** | Monte `branding.json` + logotipo | Transparente -- actualice la imagen Docker, mantenga sus montajes |
| **Configuracion + CSS** | Agregue `customCssUrl` con sustituciones de estilo | Igual -- las clases CSS son estables |
| **Paquete npm** | `npm install @authagonal/login`, personalice `branding.json`, compile en `wwwroot/` | Actualizable -- `npm update` obtiene nuevas versiones |
| **Bifurcar la SPA** | Clone `login-app/`, modifique el codigo fuente, compile su propia version | Usted es dueno de la interfaz -- las actualizaciones del servidor son independientes |
| **Escribir la suya** | Construya un frontend completamente personalizado contra la API de autenticacion | Control total -- ver [API de autenticacion](auth-api) para el contrato |

Consulte `demos/custom-server/` para un ejemplo funcional con personalizacion visual (tema verde, "Acme Corp").
