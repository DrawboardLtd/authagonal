---
layout: default
title: Localizacion
locale: es
---

# Localizacion

Authagonal admite seis idiomas de forma predeterminada: ingles, chino simplificado (`zh-Hans`), aleman (`de`), frances (`fr`), espanol (`es`) y vietnamita (`vi`). La localizacion abarca las respuestas de la API del servidor, la interfaz de inicio de sesion y este sitio de documentacion.

## Idiomas admitidos

| Codigo | Idioma |
|---|---|
| `en` | Ingles (predeterminado) |
| `zh-Hans` | Chino simplificado |
| `de` | Aleman |
| `fr` | Frances |
| `es` | Espanol |
| `vi` | Vietnamita |

## Servidor (respuestas de la API)

El servidor utiliza la localizacion integrada de ASP.NET Core con `IStringLocalizer<T>` y archivos de recursos `.resx`. El idioma se selecciona a partir del encabezado HTTP `Accept-Language`.

### Que esta localizado

- Mensajes de error de validacion de contrasena
- Etiquetas de la politica de contrasenas (`GET /api/auth/password-policy`)
- Mensajes del flujo de restablecimiento de contrasena (errores de token, expiracion, exito)
- Descripciones de errores genericos del middleware de manejo de excepciones
- Mensajes de administracion de usuarios (confirmacion de correo electronico, verificacion, etc.)
- Mensaje de confirmacion de cierre de sesion

### Que NO esta localizado

- Codigos `error` legibles por maquina (`"email_required"`, `"invalid_credentials"`, etc.) — estos son contratos de API y permanecen constantes
- Codigos de error OAuth/OIDC y descripciones de errores orientadas a desarrolladores en los endpoints de token, autorizacion y revocacion
- Mensajes de registro internos y mensajes de excepciones

### Probar la localizacion del servidor

Envie un encabezado `Accept-Language` a cualquier endpoint localizado:

```bash
# English (default)
curl https://auth.example.com/api/auth/password-policy

# Simplified Chinese
curl -H "Accept-Language: zh-Hans" https://auth.example.com/api/auth/password-policy

# German
curl -H "Accept-Language: de" https://auth.example.com/api/auth/password-policy
```

### Archivos de recursos

Todas las cadenas de traduccion del servidor se encuentran en archivos `.resx` bajo `src/Authagonal.Server/Resources/`:

```
Resources/
  SharedMessages.cs          # Marker class
  SharedMessages.resx        # English (default)
  SharedMessages.zh-Hans.resx
  SharedMessages.de.resx
  SharedMessages.fr.resx
  SharedMessages.es.resx
```

## Interfaz de inicio de sesion

La SPA de inicio de sesion utiliza [react-i18next](https://react.i18next.com/) para la localizacion del lado del cliente. El idioma se detecta automaticamente a partir de la configuracion `navigator.language` del navegador.

### Deteccion de idioma

El orden de deteccion es:

1. **Parametro de consulta** — `?lng=de` tiene prioridad sobre todo
2. **Idioma del navegador** — `navigator.language` (automatico)
3. **Respaldo** — Ingles (`en`)

### Archivos de traduccion

Los archivos JSON de traduccion se empaquetan con la aplicacion en `login-app/src/i18n/`:

```
i18n/
  index.ts        # i18n initialization
  en.json         # English
  zh-Hans.json    # Simplified Chinese
  de.json         # German
  fr.json         # French
  es.json         # Spanish
```

### Etiquetas de la politica de contrasenas

La interfaz de inicio de sesion traduce las etiquetas de requisitos de contrasena del lado del cliente basandose en la clave `rule` devuelta por `GET /api/auth/password-policy`, en lugar de usar el campo `label` proporcionado por el servidor. Esto garantiza que los requisitos de contrasena siempre se muestren en el idioma del navegador del usuario, incluso si el encabezado `Accept-Language` del servidor difiere.

### Consumidores del paquete npm

Si consume la aplicacion de inicio de sesion a traves de `@drawboard/authagonal-login`, la instancia de i18n esta exportada:

```typescript
import { i18n } from '@drawboard/authagonal-login';

// Change language programmatically
i18n.changeLanguage('de');
```

## Documentacion

El sitio de documentacion utiliza un enfoque basado en directorios. Las paginas en ingles estan en la raiz y las traducciones en subdirectorios de idioma (`/zh-Hans/`, `/de/`, `/fr/`, `/es/`). Un selector desplegable de idioma en la barra lateral permite cambiar entre idiomas.

## Agregar un nuevo idioma

Para agregar soporte para un nuevo idioma (por ejemplo, japones `ja`):

### 1. Servidor

Cree un nuevo archivo `.resx` copiando el de ingles y traduciendo los valores:

```
src/Authagonal.Server/Resources/SharedMessages.ja.resx
```

Agregue `"ja"` al arreglo de culturas admitidas en `AuthagonalExtensions.cs`:

```csharp
var supportedCultures = new[] { "en", "zh-Hans", "de", "fr", "es", "ja" };
```

### 2. Interfaz de inicio de sesion

Cree un nuevo archivo JSON de traduccion copiando `en.json` y traduciendo los valores:

```
login-app/src/i18n/ja.json
```

Registrelo en `login-app/src/i18n/index.ts`:

```typescript
import ja from './ja.json';

// In the resources object:
ja: { translation: ja },
```

### 3. Documentacion

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

Agregue la opcion de idioma al selector en `docs/_layouts/default.html`.

## Agregar nuevas cadenas

### Servidor

1. Agregue la clave y el valor en ingles a `SharedMessages.resx`
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

### Interfaz de inicio de sesion

1. Agregue la clave y el valor en ingles a `en.json`
2. Agregue los valores traducidos al archivo JSON de cada idioma
3. Use la funcion `t()` en los componentes:

```tsx
const { t } = useTranslation();

// Simple string
<p>{t('myNewKey')}</p>

// With interpolation
<p>{t('myNewKey', { name: 'value' })}</p>
```
