---
layout: default
title: Localisation
locale: fr
---

# Localisation

L'interface de connexion est livrée avec onze locales par défaut : anglais, chinois simplifié (`zh-Hans`), allemand (`de`), français (`fr`), espagnol (`es`), vietnamien (`vi`), portugais (`pt`), arabe (`ar`), afrikaans (`af`), hindi (`hi`) et une locale fantaisie klingon (`tlh`). Les réponses de l'API serveur sont localisées dans les sept premières. La localisation couvre les réponses de l'API serveur, l'interface de connexion et ce site de documentation.

## Langues prises en charge

| Code | Langue | Interface de connexion | API serveur |
|---|---|---|---|
| `en` | Anglais (par défaut) | ✓ | ✓ |
| `zh-Hans` | Chinois simplifié | ✓ | ✓ |
| `de` | Allemand | ✓ | ✓ |
| `fr` | Français | ✓ | ✓ |
| `es` | Espagnol | ✓ | ✓ |
| `vi` | Vietnamien | ✓ | ✓ |
| `pt` | Portugais | ✓ | ✓ |
| `ar` | Arabe (droite à gauche) | ✓ | — |
| `af` | Afrikaans | ✓ | — |
| `hi` | Hindi | ✓ | — |
| `tlh` | Klingon (fantaisie) | ✓ | — |

## Serveur (réponses API)

Le serveur utilise la localisation intégrée d'ASP.NET Core avec `IStringLocalizer<T>` et des fichiers de ressources `.resx`. La langue est sélectionnée à partir de l'en-tête HTTP `Accept-Language`.

### Ce qui est localisé

- Messages d'erreur de validation du mot de passe
- Labels de la politique de mot de passe (`GET /api/auth/password-policy`)
- Messages du flux de réinitialisation du mot de passe (erreurs de jeton, expiration, succès)
- Descriptions d'erreurs génériques du middleware de gestion des exceptions
- Messages de gestion des utilisateurs administrateurs (confirmation par e-mail, vérification, etc.)
- Message de confirmation de fin de session

### Ce qui N'EST PAS localisé

- Codes `error` lisibles par machine (`"email_required"`, `"invalid_credentials"`, etc.), ce sont des contrats d'API et restent constants
- Codes d'erreur OAuth/OIDC et descriptions d'erreurs destinées aux développeurs sur les points de terminaison de jeton, d'autorisation et de révocation
- Messages de journaux internes et messages d'exceptions

### Tester la localisation du serveur

Envoyez un en-tête `Accept-Language` à n'importe quel point de terminaison localisé :

```bash
# English (default)
curl https://auth.example.com/api/auth/password-policy

# Simplified Chinese
curl -H "Accept-Language: zh-Hans" https://auth.example.com/api/auth/password-policy

# German
curl -H "Accept-Language: de" https://auth.example.com/api/auth/password-policy
```

### Fichiers de ressources

Toutes les chaînes de traduction du serveur se trouvent dans les fichiers `.resx` sous `src/Authagonal.Server/Resources/` :

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

La SPA de connexion utilise [react-i18next](https://react.i18next.com/) pour la localisation côté client. La langue est détectée automatiquement à partir du paramètre `navigator.language` du navigateur.

Les locales enregistrées vivent dans un registre `LANGUAGES` unique dans `login-app/src/i18n/index.ts`, qui pilote à la fois l'enregistrement des ressources i18next et chaque sélecteur de langue, de sorte que les deux ne peuvent pas diverger. Les locales marquées `novelty` (actuellement `tlh`) restent pleinement fonctionnelles (`?lng=tlh` fonctionne) mais sont exclues du sélecteur par défaut ; elles n'apparaissent dans un menu déroulant que lorsque le `BrandingConfig.languages` d'un tenant les liste explicitement. Les tenants peuvent aussi restreindre le sélecteur de la même façon : un tableau `languages` dans `branding.json` remplace entièrement la liste par défaut (voir [Marque](branding)).

La langue active est reflétée sur `<html lang>` et `<html dir>`, de sorte que les langues de droite à gauche (`ar`) retournent automatiquement la carte d'authentification, y compris lorsque la langue est changée sur place via le sélecteur.

### Détection de la langue

L'ordre de détection est :

1. **localStorage** : préférence persistée d'une visite précédente
2. **Paramètre de requête** : `?lng=de` remplace la détection du navigateur
3. **Langue du navigateur** : `navigator.language` (automatique)
4. **Repli** : Anglais (`en`)

### Fichiers de traduction

Les fichiers JSON de traduction sont intégrés à l'application dans `login-app/src/i18n/` :

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
  tlh.json        # Klingon (novelty)
```

### Labels de la politique de mot de passe

La page de réinitialisation de mot de passe traduit sa liste d'exigences de mot de passe côté client en fonction de la clé `rule` renvoyée par `GET /api/auth/password-policy` (avec repli sur le champ `label` fourni par le serveur pour les règles non reconnues). Cela garantit que les exigences suivent la langue sélectionnée dans l'interface, même si l'en-tête `Accept-Language` du navigateur diffère. La page d'inscription affiche les valeurs `label` fournies par le serveur, qui sont localisées à partir de `Accept-Language`.

### Consommateurs du paquet npm

Si vous utilisez l'application de connexion via `@authagonal/login`, l'instance i18n est exportée :

```typescript
import { i18n } from '@authagonal/login';

// Change language programmatically
i18n.changeLanguage('de');
```

## Documentation

Le site de documentation utilise une approche basée sur les répertoires. Les pages en anglais se trouvent à la racine et les traductions dans des sous-répertoires de langue (`/zh-Hans/`, `/de/`, `/fr/`, `/es/`, `/vi/`, `/pt/`). Un menu déroulant de changement de langue dans la barre latérale permet de basculer entre les langues.

## Ajouter une nouvelle langue

Pour ajouter la prise en charge d'une nouvelle langue (par ex. japonais `ja`) :

### 1. Serveur

Créez un nouveau fichier `.resx` en copiant celui en anglais et en traduisant les valeurs :

```
src/Authagonal.Server/Resources/SharedMessages.ja.resx
```

Ajoutez `"ja"` au tableau des cultures prises en charge dans `AuthagonalExtensions.cs` :

```csharp
var supportedCultures = new[] { "en", "zh-Hans", "de", "fr", "es", "vi", "pt", "ja" };
```

### 2. Interface de connexion

Créez un nouveau fichier JSON de traduction en copiant `en.json` et en traduisant les valeurs :

```
login-app/src/i18n/ja.json
```

Enregistrez-le dans le tableau `LANGUAGES` de `login-app/src/i18n/index.ts`. Cette seule entrée enregistre la ressource i18next et ajoute la langue à chaque sélecteur :

```typescript
import ja from './ja.json';

// In the LANGUAGES array:
{ code: 'ja', label: '日本語', resource: ja },
```

### 3. Documentation

Créez un nouveau répertoire avec des fichiers markdown traduits :

```
docs/ja/
  index.md
  installation.md
  quickstart.md
  ...
```

Ajoutez une valeur par défaut de langue dans `docs/_config.yml` :

```yaml
defaults:
  - scope:
      path: "ja"
    values:
      locale: "ja"
```

Ajoutez l'option de langue au sélecteur dans `docs/_layouts/default.html`.

## Ajouter de nouvelles chaînes

### Serveur

1. Ajoutez la clé et la valeur en anglais à `SharedMessages.resx`
2. Ajoutez les valeurs traduites au fichier `.resx` de chaque langue
3. Utilisez `IStringLocalizer<SharedMessages>` pour accéder à la chaîne :

```csharp
// Inject via parameter
IStringLocalizer<SharedMessages> localizer

// Use with key
localizer["MyNewKey"].Value

// With format parameters
string.Format(localizer["MyNewKey"].Value, param1)
```

### Interface de connexion

1. Ajoutez la clé et la valeur en anglais à `en.json`
2. Ajoutez les valeurs traduites au fichier JSON de chaque langue
3. Utilisez la fonction `t()` dans les composants :

```tsx
const { t } = useTranslation();

// Simple string
<p>{t('myNewKey')}</p>

// With interpolation
<p>{t('myNewKey', { name: 'value' })}</p>
```
