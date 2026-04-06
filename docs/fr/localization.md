---
layout: default
title: Localisation
locale: fr
---

# Localisation

Authagonal prend en charge huit langues par defaut : anglais, chinois simplifie (`zh-Hans`), allemand (`de`), francais (`fr`), espagnol (`es`), vietnamien (`vi`), portugais (`pt`) et klingon (`tlh`). La localisation couvre les reponses de l'API serveur, l'interface de connexion et ce site de documentation.

## Langues prises en charge

| Code | Langue |
|---|---|
| `en` | Anglais (par defaut) |
| `zh-Hans` | Chinois simplifie |
| `de` | Allemand |
| `fr` | Francais |
| `es` | Espagnol |
| `vi` | Vietnamien |
| `pt` | Portugais |

## Serveur (reponses API)

Le serveur utilise la localisation integree d'ASP.NET Core avec `IStringLocalizer<T>` et des fichiers de ressources `.resx`. La langue est selectionnee a partir de l'en-tete HTTP `Accept-Language`.

### Ce qui est localise

- Messages d'erreur de validation du mot de passe
- Labels de la politique de mot de passe (`GET /api/auth/password-policy`)
- Messages du flux de reinitialisation du mot de passe (erreurs de jeton, expiration, succes)
- Descriptions d'erreurs generiques du middleware de gestion des exceptions
- Messages de gestion des utilisateurs administrateurs (confirmation par e-mail, verification, etc.)
- Message de confirmation de fin de session

### Ce qui N'EST PAS localise

- Codes `error` lisibles par machine (`"email_required"`, `"invalid_credentials"`, etc.) — ce sont des contrats d'API et restent constants
- Codes d'erreur OAuth/OIDC et descriptions d'erreurs destinees aux developpeurs sur les points de terminaison de jeton, d'autorisation et de revocation
- Messages de journaux internes et messages d'exceptions

### Tester la localisation du serveur

Envoyez un en-tete `Accept-Language` a n'importe quel point de terminaison localise :

```bash
# English (default)
curl https://auth.example.com/api/auth/password-policy

# Simplified Chinese
curl -H "Accept-Language: zh-Hans" https://auth.example.com/api/auth/password-policy

# German
curl -H "Accept-Language: de" https://auth.example.com/api/auth/password-policy
```

### Fichiers de ressources

Toutes les chaines de traduction du serveur se trouvent dans les fichiers `.resx` sous `src/Authagonal.Server/Resources/` :

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

## Interface de connexion

La SPA de connexion utilise [react-i18next](https://react.i18next.com/) pour la localisation cote client. La langue est detectee automatiquement a partir du parametre `navigator.language` du navigateur.

### Detection de la langue

L'ordre de detection est :

1. **localStorage** — preference persistee d'une visite precedente
2. **Parametre de requete** — `?lng=de` remplace la detection du navigateur
3. **Langue du navigateur** — `navigator.language` (automatique)
4. **Repli** — Anglais (`en`)

### Fichiers de traduction

Les fichiers JSON de traduction sont integres a l'application dans `login-app/src/i18n/` :

```
i18n/
  index.ts        # i18n initialization
  en.json         # English
  zh-Hans.json    # Simplified Chinese
  de.json         # German
  fr.json         # French
  es.json         # Spanish
  vi.json         # Vietnamese
  pt.json         # Portuguese
  tlh.json        # Klingon
```

### Labels de la politique de mot de passe

L'interface de connexion traduit les labels des exigences de mot de passe cote client en fonction de la cle `rule` renvoyee par `GET /api/auth/password-policy`, plutot que d'utiliser le champ `label` fourni par le serveur. Cela garantit que les exigences de mot de passe sont toujours affichees dans la langue du navigateur de l'utilisateur, meme si l'en-tete `Accept-Language` du serveur differe.

### Consommateurs du paquet npm

Si vous utilisez l'application de connexion via `@drawboard/authagonal-login`, l'instance i18n est exportee :

```typescript
import { i18n } from '@drawboard/authagonal-login';

// Change language programmatically
i18n.changeLanguage('de');
```

## Documentation

Le site de documentation utilise une approche basee sur les repertoires. Les pages en anglais se trouvent a la racine et les traductions dans des sous-repertoires de langue (`/zh-Hans/`, `/de/`, `/fr/`, `/es/`). Un menu deroulant de changement de langue dans la barre laterale permet de basculer entre les langues.

## Ajouter une nouvelle langue

Pour ajouter la prise en charge d'une nouvelle langue (par ex. japonais `ja`) :

### 1. Serveur

Creez un nouveau fichier `.resx` en copiant celui en anglais et en traduisant les valeurs :

```
src/Authagonal.Server/Resources/SharedMessages.ja.resx
```

Ajoutez `"ja"` au tableau des cultures prises en charge dans `AuthagonalExtensions.cs` :

```csharp
var supportedCultures = new[] { "en", "zh-Hans", "de", "fr", "es", "vi", "pt", "ja" };
```

### 2. Interface de connexion

Creez un nouveau fichier JSON de traduction en copiant `en.json` et en traduisant les valeurs :

```
login-app/src/i18n/ja.json
```

Enregistrez-le dans `login-app/src/i18n/index.ts` :

```typescript
import ja from './ja.json';

// In the resources object:
ja: { translation: ja },
```

### 3. Documentation

Creez un nouveau repertoire avec des fichiers markdown traduits :

```
docs/ja/
  index.md
  installation.md
  quickstart.md
  ...
```

Ajoutez une valeur par defaut de langue dans `docs/_config.yml` :

```yaml
defaults:
  - scope:
      path: "ja"
    values:
      locale: "ja"
```

Ajoutez l'option de langue au selecteur dans `docs/_layouts/default.html`.

## Ajouter de nouvelles chaines

### Serveur

1. Ajoutez la cle et la valeur en anglais a `SharedMessages.resx`
2. Ajoutez les valeurs traduites au fichier `.resx` de chaque langue
3. Utilisez `IStringLocalizer<SharedMessages>` pour acceder a la chaine :

```csharp
// Inject via parameter
IStringLocalizer<SharedMessages> localizer

// Use with key
localizer["MyNewKey"].Value

// With format parameters
string.Format(localizer["MyNewKey"].Value, param1)
```

### Interface de connexion

1. Ajoutez la cle et la valeur en anglais a `en.json`
2. Ajoutez les valeurs traduites au fichier JSON de chaque langue
3. Utilisez la fonction `t()` dans les composants :

```tsx
const { t } = useTranslation();

// Simple string
<p>{t('myNewKey')}</p>

// With interpolation
<p>{t('myNewKey', { name: 'value' })}</p>
```
