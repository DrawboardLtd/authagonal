---
layout: default
title: Localización
locale: es
---

# Localización

La interfaz de inicio de sesión incluye diez idiomas de forma predeterminada: inglés, chino simplificado (`zh-Hans`), alemán (`de`), francés (`fr`), español (`es`), vietnamita (`vi`), portugués (`pt`), árabe (`ar`), afrikaans (`af`) e hindi (`hi`). Las respuestas de la API del servidor están localizadas en los primeros siete de estos. La localización abarca las respuestas de la API del servidor, la interfaz de inicio de sesión y este sitio de documentación.

## Idiomas admitidos

| Código | Idioma | Interfaz de inicio de sesión | API del servidor |
|---|---|---|---|
| `en` | Inglés (predeterminado) | ✓ | ✓ |
| `zh-Hans` | Chino simplificado | ✓ | ✓ |
| `de` | Alemán | ✓ | ✓ |
| `fr` | Francés | ✓ | ✓ |
| `es` | Español | ✓ | ✓ |
| `vi` | Vietnamita | ✓ | ✓ |
| `pt` | Portugués | ✓ | ✓ |
| `ar` | Árabe (de derecha a izquierda) | ✓ | — |
| `af` | Afrikaans | ✓ | — |
| `hi` | Hindi | ✓ | — |

## Servidor (respuestas de la API)

El servidor utiliza la localización integrada de ASP.NET Core con `IStringLocalizer<T>` y archivos de recursos `.resx`. El idioma se selecciona a partir del encabezado HTTP `Accept-Language`.

### Qué está localizado

- Mensajes de error de validación de contraseña
- Etiquetas de la política de contraseñas (`GET /api/auth/password-policy`)
- Mensajes del flujo de restablecimiento de contraseña (errores de token, expiración, éxito)
- Descripciones de errores genéricos del middleware de manejo de excepciones
- Mensajes de administración de usuarios (confirmación de correo electrónico, verificación, etc.)
- Mensaje de confirmación de cierre de sesión

### Qué NO está localizado

- Códigos `error` legibles por máquina (`"email_required"`, `"invalid_credentials"`, etc.), estos son contratos de API y permanecen constantes
- Códigos de error OAuth/OIDC y descripciones de errores orientadas a desarrolladores en los endpoints de token, autorización y revocación
- Mensajes de registro internos y mensajes de excepciones

### Probar la localización del servidor

Envíe un encabezado `Accept-Language` a cualquier endpoint localizado:

```bash
# English (default)
curl https://auth.example.com/api/auth/password-policy

# Simplified Chinese
curl -H "Accept-Language: zh-Hans" https://auth.example.com/api/auth/password-policy

# German
curl -H "Accept-Language: de" https://auth.example.com/api/auth/password-policy
```

### Archivos de recursos

Todas las cadenas de traducción del servidor se encuentran en archivos `.resx` bajo `src/Authagonal.Server/Resources/`:

```
Resources/
  SharedMessages.cs          # Marker class
  SharedMessages.resx        # English (default)
  SharedMessages.zh-Hans.resx
  SharedMessages.de.resx
  SharedMessages.fr.resx
  SharedMessages.es.resx
  SharedMessages.vi.resx
  SharedMessages.pt.resx
```

## Interfaz de inicio de sesión

La SPA de inicio de sesión utiliza [react-i18next](https://react.i18next.com/) para la localización del lado del cliente. El idioma se detecta automáticamente a partir de la configuración `navigator.language` del navegador.

Los idiomas registrados se encuentran en un único registro `LANGUAGES` en `login-app/src/i18n/index.ts`, que gobierna tanto el registro de recursos de i18next como cada selector de idioma, de modo que ambos no pueden desincronizarse. Actualmente todos los idiomas registrados aparecen en el selector predeterminado. `DEFAULT_LANGUAGES` se exporta por separado de `LANGUAGES` para que un futuro idioma restringido pueda excluirse de los selectores sin tocar los puntos de llamada, pero hoy no se excluye ninguno. Los tenants también pueden acotar el selector de la misma manera: un arreglo `languages` en `branding.json` reemplaza por completo la lista predeterminada (ver [Personalización visual](branding)).

El idioma activo se refleja en `<html lang>` y `<html dir>`, de modo que los idiomas de derecha a izquierda (`ar`) invierten la tarjeta de autenticación automáticamente, incluso cuando el idioma se cambia en el momento mediante el selector.

### Detección de idioma

El orden de detección es:

1. **localStorage**: preferencia persistida de una visita anterior
2. **Parámetro de consulta**: `?lng=de` anula la detección del navegador
3. **Idioma del navegador**: `navigator.language` (automático)
4. **Respaldo**: inglés (`en`)

### Archivos de traducción

Los archivos JSON de traducción se empaquetan con la aplicación en `login-app/src/i18n/`:

```
i18n/
  index.ts        # i18n initialization + the LANGUAGES registry
  en.json         # English
  zh-Hans.json    # Simplified Chinese
  de.json         # German
  fr.json         # French
  es.json         # Spanish
  vi.json         # Vietnamese
  pt.json         # Portuguese
  ar.json         # Arabic
  af.json         # Afrikaans
  hi.json         # Hindi
```

### Etiquetas de la política de contraseñas

La página de restablecimiento de contraseña traduce su lista de verificación de requisitos de contraseña del lado del cliente basándose en la clave `rule` devuelta por `GET /api/auth/password-policy` (recurriendo al campo `label` proporcionado por el servidor para reglas no reconocidas). Esto garantiza que los requisitos sigan el idioma seleccionado en la interfaz, incluso si el encabezado `Accept-Language` del navegador difiere. La página de registro muestra los valores `label` proporcionados por el servidor, que se localizan a partir de `Accept-Language`.

### Consumidores del paquete npm

Si consume la aplicación de inicio de sesión a través de `@authagonal/login`, la instancia de i18n está exportada:

```typescript
import { i18n } from '@authagonal/login';

// Change language programmatically
i18n.changeLanguage('de');
```

## Documentación

El sitio de documentación utiliza un enfoque basado en directorios. Las páginas en inglés están en la raíz y las traducciones en subdirectorios de idioma (`/zh-Hans/`, `/de/`, `/fr/`, `/es/`, `/vi/`, `/pt/`). Un selector desplegable de idioma en la barra lateral permite cambiar entre idiomas.

## Agregar un nuevo idioma

Para agregar soporte para un nuevo idioma (por ejemplo, japonés `ja`):

### 1. Servidor

Cree un nuevo archivo `.resx` copiando el de inglés y traduciendo los valores:

```
src/Authagonal.Server/Resources/SharedMessages.ja.resx
```

Agregue `"ja"` al arreglo de culturas admitidas en `AuthagonalExtensions.cs`:

```csharp
var supportedCultures = new[] { "en", "zh-Hans", "de", "fr", "es", "vi", "pt", "ja" };
```

### 2. Interfaz de inicio de sesión

Cree un nuevo archivo JSON de traducción copiando `en.json` y traduciendo los valores:

```
login-app/src/i18n/ja.json
```

Regístrelo en el arreglo `LANGUAGES` de `login-app/src/i18n/index.ts`. Esa única entrada registra el recurso de i18next y agrega el idioma a cada selector:

```typescript
import ja from './ja.json';

// In the LANGUAGES array:
{ code: 'ja', label: '日本語', resource: ja },
```

### 3. Documentación

Cree un nuevo directorio con archivos markdown traducidos:

```
docs/ja/
  index.md
  installation.md
  quickstart.md
  ...
```

Agregue un valor predeterminado de idioma en `docs/_config.yml`:

```yaml
defaults:
  - scope:
      path: "ja"
    values:
      locale: "ja"
```

Agregue la opción de idioma al selector en `docs/_layouts/default.html`.

## Agregar nuevas cadenas

### Servidor

1. Agregue la clave y el valor en inglés a `SharedMessages.resx`
2. Agregue los valores traducidos al archivo `.resx` de cada idioma
3. Use `IStringLocalizer<SharedMessages>` para acceder a la cadena:

```csharp
// Inject via parameter
IStringLocalizer<SharedMessages> localizer

// Use with key
localizer["MyNewKey"].Value

// With format parameters
string.Format(localizer["MyNewKey"].Value, param1)
```

### Interfaz de inicio de sesión

1. Agregue la clave y el valor en inglés a `en.json`
2. Agregue los valores traducidos al archivo JSON de cada idioma
3. Use la función `t()` en los componentes:

```tsx
const { t } = useTranslation();

// Simple string
<p>{t('myNewKey')}</p>

// With interpolation
<p>{t('myNewKey', { name: 'value' })}</p>
```
