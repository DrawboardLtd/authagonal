---
layout: default
title: Personnalisation visuelle
locale: fr
---

# Personnalisation de l'interface de connexion

La SPA de connexion est configurable à l'exécution via un fichier `branding.json` servi depuis la racine web. Aucune recompilation n'est nécessaire : montez simplement votre configuration et vos ressources.

## Comment ça fonctionne

Au démarrage, la SPA récupère `/branding.json`. Si le fichier n'existe pas ou est inaccessible, les valeurs par défaut sont utilisées. (Un serveur hôte peut aussi intégrer la configuration sous forme de payload de démarrage `<script type="application/json" id="authagonal-boot">` ; lorsqu'il est présent, la SPA le lit au lieu de faire la requête.) La configuration contrôle :

- Le nom de l'application (affiché dans l'en-tête et le titre de la page)
- L'image du logo, avec une "pastille" d'arrière-plan optionnelle par mode
- La couleur principale (boutons, liens, indicateurs de focus), avec une variante optionnelle en mode sombre
- Les couleurs d'arrière-plan de la page et de la carte, par mode
- La visibilité des liens de mot de passe oublié et d'inscription
- Le mode sombre par défaut (clair / suivre l'OS / sombre)
- Les options du sélecteur de langue
- Le pied de page "Powered by Authagonal"
- Le CSS personnalisé pour une stylisation plus approfondie

## Configuration

Placez un fichier `branding.json` dans le répertoire `wwwroot/` (ou montez-le dans le conteneur Docker) :

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

### Options

| Propriété | Type | Défaut | Description |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | Affiché dans l'en-tête et le titre de l'onglet du navigateur |
| `logoUrl` | `string \| null` | `null` | URL vers une image de logo. Lorsque définie, remplace l'en-tête texte. |
| `primaryColor` | `string` | `"#2563eb"` | Couleur hexadécimale pour les boutons, liens et indicateurs de focus |
| `supportEmail` | `string \| null` | `null` | Adresse email de support (réservé pour un usage futur) |
| `showForgotPassword` | `boolean` | `true` | Afficher/masquer le lien "Mot de passe oublié ?" sur la page de connexion |
| `showRegistration` | `boolean` | `false` | Afficher/masquer le lien d'inscription en libre-service |
| `customCssUrl` | `string \| null` | `null` | URL vers un fichier CSS personnalisé chargé après les styles par défaut |
| `welcomeTitle` | `LocalizedString` | `null` | Message d'accueil optionnel affiché sous l'en-tête des pages d'authentification (chaîne simple ou `{ "en": "...", "de": "..." }`). Rien n'est affiché s'il est absent. |
| `welcomeSubtitle` | `LocalizedString` | `null` | Ligne optionnelle sous `welcomeTitle`, même forme. Rien n'est affiché si elle est absente. |
| `languages` | `array \| null` | `null` | Options du sélecteur de langue (`[{ "code": "en", "label": "English" }, ...]`). `null` affiche toutes les langues livrées sauf les locales fantaisie (voir [Localisation](localization)). |
| `poweredBy` | `boolean` | `true` | Afficher/masquer le pied de page "Powered by Authagonal" sur les pages d'authentification |
| `darkMode` | `"off" \| "auto" \| "force"` | `"auto"` | Thème par défaut lorsque le visiteur n'en a pas choisi : `"off"` (clair uniquement), `"auto"` (suivre la préférence de l'OS), `"force"` (toujours sombre). Le basculeur de thème du visiteur garde toujours la priorité. |
| `lightBg` | `string \| null` | `null` | Couleur d'arrière-plan de la page en mode clair |
| `lightCardBg` | `string \| null` | `null` | Couleur d'arrière-plan de la carte/du formulaire en mode clair |
| `darkBg` | `string \| null` | `null` | Couleur d'arrière-plan de la page en mode sombre |
| `darkCardBg` | `string \| null` | `null` | Couleur d'arrière-plan de la carte/du formulaire en mode sombre |
| `darkPrimaryColor` | `string \| null` | `null` | Remplace `primaryColor` en mode sombre |
| `lightLogoBg` | `string \| null` | `null` | Arrière-plan de la pastille du logo en mode clair (voir ci-dessous) |
| `darkLogoBg` | `string \| null` | `null` | Arrière-plan de la pastille du logo en mode sombre (voir ci-dessous) |

Les valeurs de couleur doivent être une couleur hexadécimale (`#rgb`, `#rrggbb`, `#rrggbbaa`) ou une expression `rgb()`/`rgba()`/`hsl()`/`hsla()` ; tout le reste est ignoré. Les couleurs par mode sont injectées dans une règle `<style id="branding-theme-vars">` après les styles intégrés (valeurs claires sur `:root`, valeurs sombres sur `.dark`), de sorte qu'une valeur sombre peut différer de son homologue claire.

### Pastille d'arrière-plan du logo

Si votre logo a un dessin blanc ou transparent, il peut disparaître sur la carte claire. Définissez `lightLogoBg` et/ou `darkLogoBg` pour afficher le logo dans une "pastille" arrondie et avec marge intérieure de cette couleur d'arrière-plan :

```json
{
  "logoUrl": "/branding/logo.svg",
  "lightLogoBg": "#1c1e22",
  "darkLogoBg": "#1c1e22"
}
```

La pastille (un conteneur `data-auth="logo-chip"` piloté par la variable CSS `--auth-logo-bg`) ne reçoit sa marge intérieure et son arrière-plan que lorsqu'un arrière-plan de logo est configuré, de sorte que les tenants qui n'en définissent pas voient le logo directement sur la carte, exactement comme avant. Les deux champs sont indépendants : définissez uniquement `lightLogoBg` pour encadrer le logo en mode clair et le laisser nu en mode sombre.

## Exemple Docker

Montez vos fichiers de personnalisation dans le conteneur :

```bash
docker run -p 8080:8080 \
  -v ./my-branding/branding.json:/app/wwwroot/branding.json \
  -v ./my-branding/logo.svg:/app/wwwroot/branding/logo.svg \
  -v ./my-branding/custom.css:/app/wwwroot/branding/custom.css \
  -e Storage__ConnectionString="..." \
  -e Issuer="https://auth.example.com" \
  authagonal
```

Ou avec docker-compose :

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

## CSS personnalisé

L'option `customCssUrl` charge une feuille de style supplémentaire après les styles par défaut, de sorte que vos règles ont la priorité. Utile pour changer les polices, ajuster l'espacement ou restyler des éléments spécifiques. L'URL doit être de même origine (les URLs relatives comme `/branding/custom.css` conviennent) ; les feuilles de style d'origine différente sont ignorées silencieusement.

### Propriétés CSS personnalisées

L'interface de connexion expose plusieurs propriétés CSS personnalisées pour un contrôle fin :

| Propriété | Défaut | Description |
|---|---|---|
| `--brand-primary` | `#2563eb` | Couleur principale pour les boutons, liens et indicateurs de focus |
| `--auth-bg` | `#f3f4f6` | Couleur d'arrière-plan de la page |
| `--auth-card-bg` | `#ffffff` | Couleur d'arrière-plan de la carte/du formulaire |
| `--auth-logo-bg` | `transparent` | Arrière-plan de la pastille du logo (la marge intérieure de la pastille n'apparaît que lorsqu'un arrière-plan de logo est configuré) |
| `--auth-radius` | `0.5rem` | Rayon de bordure de la carte d'authentification |
| `--auth-font` | *(inherit; system font stack)* | Famille de polices de la carte d'authentification |
| `--auth-heading` | `#111827` | Couleur du texte des titres |

Les variables de couleur ci-dessus correspondent directement aux champs de configuration (`primaryColor`, `lightBg`/`darkBg`, `lightCardBg`/`darkCardBg`, `lightLogoBg`/`darkLogoBg`) ; préférez donc la configuration pour les changements de couleur simples et réservez le CSS personnalisé à tout le reste.

Remplacez-les dans votre CSS personnalisé :

```css
:root {
  --brand-primary: #059669;
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

L'interface de connexion utilise Tailwind CSS. Le CSS personnalisé peut cibler les éléments HTML standard et les classes utilitaires Tailwind. Les composants d'interface exportés (`Button`, `Input`, `Card`, `Alert`, etc.) utilisent Tailwind en interne.

## Mode sombre

La SPA de connexion est livrée avec des thèmes clair, sombre et **système**. Le basculeur de thème est toujours visible dans la mise en page. La sélection de l'utilisateur est persistée dans `localStorage` sous la clé `auth-theme`.

### Comment ça fonctionne

- **Par défaut** : jusqu'à ce que le visiteur choisisse un thème, l'option de personnalisation `darkMode` définit la valeur par défaut : `"off"` (clair), `"auto"` (système, la valeur par défaut) ou `"force"` (sombre). Une fois que le visiteur utilise le basculeur, son choix garde toujours la priorité.
- **Détection** : lorsque le thème est "system", la SPA observe `window.matchMedia('(prefers-color-scheme: dark)')` et réapplique le thème automatiquement à mesure que la préférence de l'OS change.
- **Application** : la SPA bascule une classe `.dark` sur `<html>`. La variante sombre de Tailwind (`&:where(.dark, .dark *)`) active les styles sombres compilés dans chaque composant.
- **Persistance** : les choix explicites "light" / "dark" / "system" sont stockés dans `localStorage`.

### Variables CSS

Les valeurs claires sont déclarées sur `:root` ; les substitutions du mode sombre sont limitées à `.dark`, de sorte que la personnalisation du tenant dans `customCssUrl` a toujours la priorité lorsqu'elle est fournie.

| Variable | Clair | Sombre |
|---|---|---|
| `--auth-bg` | `#f3f4f6` (ou `lightBg`) | `#030712` (ou `darkBg`) |
| `--auth-card-bg` | `#ffffff` (ou `lightCardBg`) | `#111827` (ou `darkCardBg`) |
| `--auth-heading` | `#111827` | `#f9fafb` |
| `--auth-logo-bg` | `transparent` (ou `lightLogoBg`) | `transparent` (ou `darkLogoBg`) |
| `--brand-primary` | `#2563eb` (ou `primaryColor`) | la valeur claire (ou `darkPrimaryColor`) |

### Désactiver ou remplacer

La personnalisation du tenant l'emporte toujours. Pour forcer un thème unique, définissez vos propres valeurs dans `customCssUrl` :

```css
/* Forcer la palette sombre quel que soit le choix de l'utilisateur */
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

Pour supprimer entièrement le basculeur de thème, utilisez la voie du package npm : importez `AuthLayout` et affichez sans le basculeur, ou forkez la SPA.

### Attributs de données

Tous les éléments du formulaire de connexion possèdent des attributs `data-auth` pour le ciblage CSS et l'automatisation des tests :

| Attribut | Élément |
|---|---|
| `data-auth="page"` | Conteneur principal de la page |
| `data-auth="header"` | Section d'en-tête |
| `data-auth="logo-chip"` | Conteneur autour de l'image du logo (avec marge intérieure uniquement lorsqu'un arrière-plan de logo est défini) |
| `data-auth="logo"` | Image du logo |
| `data-auth="app-name"` | Titre du nom de l'application |
| `data-auth="content"` | Zone de contenu principale |
| `data-auth="languages"` | Sélecteur de langue |
| `data-auth="language-trigger"` | Bouton de déclenchement du sélecteur de langue |
| `data-auth="theme-toggle"` | Basculeur de thème clair/système/sombre |
| `data-auth="powered-by"` | Pied de page "Powered by Authagonal" |

Ciblez-les dans votre CSS personnalisé :

```css
[data-auth="header"] {
  background: linear-gradient(135deg, #667eea, #764ba2);
}
```

### Exemple : Arrière-plan et police personnalisés

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}
```

## Niveaux de personnalisation

| Niveau | Ce que vous faites | Chemin de mise à jour |
|---|---|---|
| **Configuration seule** | Montez `branding.json` + logo | Transparent : mettez à jour l'image Docker, gardez vos montages |
| **Configuration + CSS** | Ajoutez `customCssUrl` avec des substitutions de style | Idem : les classes CSS sont stables |
| **Package npm** | `npm install @authagonal/login`, personnalisez `branding.json`, compilez dans `wwwroot/` | Mise à jour possible : `npm update` récupère les nouvelles versions |
| **Forker la SPA** | Clonez `login-app/`, modifiez les sources, compilez votre propre version | Vous possédez l'interface : les mises à jour du serveur sont indépendantes |
| **Écrire la vôtre** | Construisez un frontend entièrement personnalisé contre l'API d'authentification | Contrôle total : voir [API d'authentification](auth-api) pour le contrat |

Consultez `demos/custom-server/` pour un exemple fonctionnel avec personnalisation visuelle (thème vert, "Acme Corp").
