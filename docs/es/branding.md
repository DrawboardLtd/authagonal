---
layout: default
title: Personalización visual
locale: es
---

# Personalización de la interfaz de inicio de sesión

La SPA de inicio de sesión es configurable en tiempo de ejecución mediante un archivo `branding.json` servido desde la raíz web. No se requiere recompilación: simplemente monte su configuración y recursos.

## Cómo funciona

Al iniciar, la SPA obtiene `/branding.json`. Si el archivo no existe o no es accesible, se usan los valores predeterminados. (Un servidor host también puede incrustar la configuración como una carga de arranque `<script type="application/json" id="authagonal-boot">`; cuando está presente, la SPA la lee en lugar de hacer la solicitud.) La configuración controla:

- El nombre de la aplicación (mostrado en el encabezado y título de la página)
- La imagen del logotipo, con un "chip" de fondo opcional por modo
- El color principal (botones, enlaces, indicadores de enfoque), con una variante opcional para modo oscuro
- Los colores de fondo de la página y de la tarjeta, por modo
- La visibilidad de los enlaces de contraseña olvidada y de registro
- El valor predeterminado del modo oscuro (claro / seguir el sistema operativo / oscuro)
- Las opciones del selector de idioma
- El pie de página "Powered by Authagonal"
- CSS personalizado para una estilización más profunda

## Configuración

Coloque un archivo `branding.json` en el directorio `wwwroot/` (o móntelo en el contenedor Docker):

```json
{
  "appName": "Acme Corp",
  "logoUrl": "/branding/logo.svg",
  "primaryColor": "#1a56db",
  "darkPrimaryColor": "#3b82f6",
  "darkMode": "auto",
  "supportEmail": "help@acme.com",
  "showForgotPassword": true,
  "customCssUrl": "/branding/custom.css"
}
```

### Opciones

| Propiedad | Tipo | Predeterminado | Descripción |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | Se muestra en el encabezado y título de la pestaña del navegador |
| `logoUrl` | `string \| null` | `null` | URL a una imagen de logotipo. Cuando se establece, reemplaza el encabezado de texto. |
| `primaryColor` | `string` | `"#2563eb"` | Color hexadecimal para botones, enlaces e indicadores de enfoque |
| `supportEmail` | `string \| null` | `null` | Correo electrónico de soporte (reservado para uso futuro) |
| `showForgotPassword` | `boolean` | `true` | Mostrar/ocultar el enlace "¿Contraseña olvidada?" en la página de inicio de sesión |
| `showRegistration` | `boolean` | `false` | Mostrar/ocultar el enlace de registro de autoservicio |
| `customCssUrl` | `string \| null` | `null` | URL a un archivo CSS personalizado cargado después de los estilos predeterminados |
| `welcomeTitle` | `LocalizedString` | `null` | Saludo opcional que se muestra bajo el encabezado en las páginas de autenticación (cadena simple o `{ "en": "...", "de": "..." }`). No se muestra nada si no se define. |
| `welcomeSubtitle` | `LocalizedString` | `null` | Línea opcional bajo `welcomeTitle`, con el mismo formato. No se muestra nada si no se define. |
| `languages` | `array \| null` | `null` | Opciones del selector de idioma (`[{ "code": "en", "label": "English" }, ...]`). `null` muestra todos los idiomas incluidos excepto los locales de novedad (ver [Localización](localization)). |
| `poweredBy` | `boolean` | `true` | Mostrar/ocultar el pie de página "Powered by Authagonal" en las páginas de autenticación |
| `darkMode` | `"off" \| "auto" \| "force"` | `"auto"` | Tema predeterminado cuando el visitante no ha elegido uno: `"off"` (solo claro), `"auto"` (seguir la preferencia del sistema operativo), `"force"` (siempre oscuro). El interruptor de tema del visitante sigue teniendo prioridad. |
| `lightBg` | `string \| null` | `null` | Color de fondo de la página en modo claro |
| `lightCardBg` | `string \| null` | `null` | Color de fondo de la tarjeta/formulario en modo claro |
| `darkBg` | `string \| null` | `null` | Color de fondo de la página en modo oscuro |
| `darkCardBg` | `string \| null` | `null` | Color de fondo de la tarjeta/formulario en modo oscuro |
| `darkPrimaryColor` | `string \| null` | `null` | Anula `primaryColor` en modo oscuro |
| `lightLogoBg` | `string \| null` | `null` | Fondo del chip del logotipo en modo claro (ver más abajo) |
| `darkLogoBg` | `string \| null` | `null` | Fondo del chip del logotipo en modo oscuro (ver más abajo) |

Los valores de color deben ser un color hexadecimal (`#rgb`, `#rrggbb`, `#rrggbbaa`) o una expresión `rgb()`/`rgba()`/`hsl()`/`hsla()`; cualquier otra cosa se ignora. Los colores por modo se inyectan como una regla `<style id="branding-theme-vars">` después de los estilos incluidos (los valores claros en `:root`, los oscuros en `.dark`), por lo que un valor oscuro puede diferir de su contraparte clara.

### Chip de fondo del logotipo

Si su logotipo tiene arte blanco o transparente, puede desaparecer sobre la tarjeta clara. Establezca `lightLogoBg` y/o `darkLogoBg` para renderizar el logotipo dentro de un "chip" acolchado y redondeado con ese color de fondo:

```json
{
  "logoUrl": "/branding/logo.svg",
  "lightLogoBg": "#1c1e22",
  "darkLogoBg": "#1c1e22"
}
```

El chip (un contenedor `data-auth="logo-chip"` controlado por la variable CSS `--auth-logo-bg`) solo obtiene su relleno y fondo cuando se configura un fondo de logotipo, por lo que los inquilinos que no establecen uno ven el logotipo a ras de la tarjeta exactamente como antes. Los dos campos son independientes: establezca solo `lightLogoBg` para poner el logotipo en un chip en modo claro y dejarlo desnudo en modo oscuro.

## Ejemplo Docker

Monte sus archivos de personalización en el contenedor:

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

La opción `customCssUrl` carga una hoja de estilos adicional después de los estilos predeterminados, por lo que sus reglas tienen prioridad. Útil para cambiar fuentes, ajustar espaciado o reestilizar elementos específicos. La URL debe ser del mismo origen (las URLs relativas como `/branding/custom.css` están bien); las hojas de estilo de origen cruzado se omiten silenciosamente.

### Propiedades CSS personalizadas

La interfaz de inicio de sesión expone varias propiedades CSS personalizadas para un control detallado:

| Propiedad | Predeterminado | Descripción |
|---|---|---|
| `--brand-primary` | `#2563eb` | Color principal para botones, enlaces, indicadores de enfoque |
| `--auth-bg` | `#f3f4f6` | Color de fondo de la página |
| `--auth-card-bg` | `#ffffff` | Color de fondo de la tarjeta/formulario |
| `--auth-logo-bg` | `transparent` | Fondo del chip del logotipo (el relleno del chip solo aparece cuando se configura un fondo de logotipo) |
| `--auth-radius` | `0.5rem` | Radio de borde de la tarjeta de autenticación |
| `--auth-font` | *(heredado; pila de fuentes del sistema)* | Familia de fuentes de la tarjeta de autenticación |
| `--auth-heading` | `#111827` | Color del texto de los encabezados |

Las variables de color aquí se corresponden directamente con campos de configuración (`primaryColor`, `lightBg`/`darkBg`, `lightCardBg`/`darkCardBg`, `lightLogoBg`/`darkLogoBg`), así que prefiera la configuración para cambios de color simples y reserve el CSS personalizado para todo lo demás.

Anúlelas en su CSS personalizado:

```css
:root {
  --brand-primary: #059669;
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

La interfaz de inicio de sesión utiliza Tailwind CSS. El CSS personalizado puede apuntar a elementos HTML estándar y clases de utilidad de Tailwind. Los componentes de UI exportados (`Button`, `Input`, `Card`, `Alert`, etc.) usan Tailwind internamente.

## Modo oscuro

La SPA de inicio de sesión incluye temas claro, oscuro y **de sistema**. El interruptor de tema siempre está visible en el diseño. La selección del usuario se persiste en `localStorage` bajo la clave `auth-theme`.

### Cómo funciona

- **Predeterminado**: hasta que el visitante elige un tema, la opción de personalización `darkMode` establece el valor predeterminado: `"off"` (claro), `"auto"` (sistema, el predeterminado) o `"force"` (oscuro). Una vez que el visitante usa el interruptor, su elección siempre tiene prioridad.
- **Detección**: cuando el tema es "system", la SPA observa `window.matchMedia('(prefers-color-scheme: dark)')` y vuelve a aplicar el tema automáticamente a medida que cambia la preferencia del sistema operativo.
- **Aplicación**: la SPA alterna una clase `.dark` en `<html>`. La variante oscura de Tailwind (`&:where(.dark, .dark *)`) activa los estilos oscuros compilados en cada componente.
- **Persistencia**: las elecciones explícitas "light" / "dark" / "system" se almacenan en `localStorage`.

### Variables CSS

Los valores claros se declaran en `:root`; las anulaciones de modo oscuro tienen alcance en `.dark`, por lo que la personalización del inquilino en `customCssUrl` siempre tiene prioridad cuando se proporciona.

| Variable | Claro | Oscuro |
|---|---|---|
| `--auth-bg` | `#f3f4f6` (o `lightBg`) | `#030712` (o `darkBg`) |
| `--auth-card-bg` | `#ffffff` (o `lightCardBg`) | `#111827` (o `darkCardBg`) |
| `--auth-heading` | `#111827` | `#f9fafb` |
| `--auth-logo-bg` | `transparent` (o `lightLogoBg`) | `transparent` (o `darkLogoBg`) |
| `--brand-primary` | `#2563eb` (o `primaryColor`) | el valor claro (o `darkPrimaryColor`) |

### Deshabilitar o anular

La personalización del inquilino siempre tiene prioridad. Para forzar un único tema, establezca sus propios valores en `customCssUrl`:

```css
/* Force dark palette regardless of user choice */
:root {
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
.dark {
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

Para eliminar por completo el interruptor de tema, use la ruta del paquete npm: importe `AuthLayout` y renderice sin el interruptor, o bifurque la SPA.

### Atributos de datos

Todos los elementos del formulario de inicio de sesión tienen atributos `data-auth` para segmentación CSS y automatización de pruebas:

| Atributo | Elemento |
|---|---|
| `data-auth="page"` | Contenedor principal de la página |
| `data-auth="header"` | Sección del encabezado |
| `data-auth="logo-chip"` | Contenedor alrededor de la imagen del logotipo (con relleno solo cuando se establece un fondo de logotipo) |
| `data-auth="logo"` | Imagen del logotipo |
| `data-auth="app-name"` | Encabezado del nombre de la aplicación |
| `data-auth="content"` | Área de contenido principal |
| `data-auth="languages"` | Selector de idioma |
| `data-auth="language-trigger"` | Botón disparador del selector de idioma |
| `data-auth="theme-toggle"` | Interruptor de tema claro/sistema/oscuro |
| `data-auth="powered-by"` | Pie de página "Powered by Authagonal" |

Segméntelos en su CSS personalizado:

```css
[data-auth="header"] {
  background: linear-gradient(135deg, #667eea, #764ba2);
}
```

### Ejemplo: Fondo y fuente personalizados

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}
```

## Niveles de personalización

| Nivel | Lo que hace | Ruta de actualización |
|---|---|---|
| **Solo configuración** | Monte `branding.json` + logotipo | Transparente: actualice la imagen Docker, mantenga sus montajes |
| **Configuración + CSS** | Agregue `customCssUrl` con sustituciones de estilo | Igual: las clases CSS son estables |
| **Paquete npm** | `npm install @authagonal/login`, personalice `branding.json`, compile en `wwwroot/` | Actualizable: `npm update` obtiene nuevas versiones |
| **Bifurcar la SPA** | Clone `login-app/`, modifique el código fuente, compile su propia versión | Usted es dueño de la interfaz: las actualizaciones del servidor son independientes |
| **Escribir la suya** | Construya un frontend completamente personalizado contra la API de autenticación | Control total: ver [API de autenticación](auth-api) para el contrato |

Consulte `demos/custom-server/` para un ejemplo funcional con personalización visual (tema verde, "Acme Corp").
