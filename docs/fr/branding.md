---
layout: default
title: Personnalisation visuelle
locale: fr
---

# Personnalisation de l'interface de connexion

La SPA de connexion est configurable a l'execution via un fichier `branding.json` servi depuis la racine web. Aucune recompilation n'est necessaire -- montez simplement votre configuration et vos ressources.

## Comment ca fonctionne

Au demarrage, la SPA recupere `/branding.json`. Si le fichier n'existe pas ou est inaccessible, les valeurs par defaut sont utilisees. (Un serveur hote peut aussi integrer la configuration sous forme de payload de demarrage `<script type="application/json" id="authagonal-boot">` ; lorsqu'il est present, la SPA le lit au lieu de faire la requete.) La configuration controle :

- Le nom de l'application (affiche dans l'en-tete et le titre de la page)
- L'image du logo, avec une "pastille" d'arriere-plan optionnelle par mode
- La couleur principale (boutons, liens, indicateurs de focus), avec une variante optionnelle en mode sombre
- Les couleurs d'arriere-plan de la page et de la carte, par mode
- La visibilite des liens de mot de passe oublie et d'inscription
- Le mode sombre par defaut (clair / suivre l'OS / sombre)
- Les options du selecteur de langue
- Le pied de page "Powered by Authagonal"
- Le CSS personnalise pour une stylisation plus approfondie

## Configuration

Placez un fichier `branding.json` dans le repertoire `wwwroot/` (ou montez-le dans le conteneur Docker) :

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

| Propriete | Type | Defaut | Description |
|---|---|---|---|
| `appName` | `string` | `"Authagonal"` | Affiche dans l'en-tete et le titre de l'onglet du navigateur |
| `logoUrl` | `string \| null` | `null` | URL vers une image de logo. Lorsque definie, remplace l'en-tete texte. |
| `primaryColor` | `string` | `"#2563eb"` | Couleur hexadecimale pour les boutons, liens et indicateurs de focus |
| `supportEmail` | `string \| null` | `null` | Adresse email de support (reserve pour un usage futur) |
| `showForgotPassword` | `boolean` | `true` | Afficher/masquer le lien "Mot de passe oublie ?" sur la page de connexion |
| `showRegistration` | `boolean` | `false` | Afficher/masquer le lien d'inscription en libre-service |
| `customCssUrl` | `string \| null` | `null` | URL vers un fichier CSS personnalise charge apres les styles par defaut |
| `welcomeTitle` | `LocalizedString` | `null` | Remplacer le titre de la page de connexion (chaine simple ou `{ "en": "...", "de": "..." }`) |
| `welcomeSubtitle` | `LocalizedString` | `null` | Remplacer le sous-titre de la page de connexion |
| `languages` | `array \| null` | `null` | Options du selecteur de langue (`[{ "code": "en", "label": "English" }, ...]`). `null` affiche toutes les langues livrees sauf les locales fantaisie (voir [Localisation](localization)). |
| `poweredBy` | `boolean` | `true` | Afficher/masquer le pied de page "Powered by Authagonal" sur les pages d'authentification |
| `darkMode` | `"off" \| "auto" \| "force"` | `"auto"` | Theme par defaut lorsque le visiteur n'en a pas choisi : `"off"` (clair uniquement), `"auto"` (suivre la preference de l'OS), `"force"` (toujours sombre). Le basculeur de theme du visiteur garde toujours la priorite. |
| `lightBg` | `string \| null` | `null` | Couleur d'arriere-plan de la page en mode clair |
| `lightCardBg` | `string \| null` | `null` | Couleur d'arriere-plan de la carte/du formulaire en mode clair |
| `darkBg` | `string \| null` | `null` | Couleur d'arriere-plan de la page en mode sombre |
| `darkCardBg` | `string \| null` | `null` | Couleur d'arriere-plan de la carte/du formulaire en mode sombre |
| `darkPrimaryColor` | `string \| null` | `null` | Remplace `primaryColor` en mode sombre |
| `lightLogoBg` | `string \| null` | `null` | Arriere-plan de la pastille du logo en mode clair (voir ci-dessous) |
| `darkLogoBg` | `string \| null` | `null` | Arriere-plan de la pastille du logo en mode sombre (voir ci-dessous) |

Les valeurs de couleur doivent etre une couleur hexadecimale (`#rgb`, `#rrggbb`, `#rrggbbaa`) ou une expression `rgb()`/`rgba()`/`hsl()`/`hsla()` ; tout le reste est ignore. Les couleurs par mode sont injectees dans une regle `<style id="branding-theme-vars">` apres les styles integres (valeurs claires sur `:root`, valeurs sombres sur `.dark`), de sorte qu'une valeur sombre peut differer de son homologue claire.

### Pastille d'arriere-plan du logo

Si votre logo a un dessin blanc ou transparent, il peut disparaitre sur la carte claire. Definissez `lightLogoBg` et/ou `darkLogoBg` pour afficher le logo dans une "pastille" arrondie et avec marge interieure de cette couleur d'arriere-plan :

```json
{
  "logoUrl": "/branding/logo.svg",
  "lightLogoBg": "#1c1e22",
  "darkLogoBg": "#1c1e22"
}
```

La pastille (un conteneur `data-auth="logo-chip"` pilote par la variable CSS `--auth-logo-bg`) ne recoit sa marge interieure et son arriere-plan que lorsqu'un arriere-plan de logo est configure, de sorte que les tenants qui n'en definissent pas voient le logo directement sur la carte, exactement comme avant. Les deux champs sont independants : definissez uniquement `lightLogoBg` pour encadrer le logo en mode clair et le laisser nu en mode sombre.

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

## CSS personnalise

L'option `customCssUrl` charge une feuille de style supplementaire apres les styles par defaut, de sorte que vos regles ont la priorite. Utile pour changer les polices, ajuster l'espacement ou restyler des elements specifiques. L'URL doit etre de meme origine (les URLs relatives comme `/branding/custom.css` conviennent) ; les feuilles de style d'origine differente sont ignorees silencieusement.

### Proprietes CSS personnalisees

L'interface de connexion expose plusieurs proprietes CSS personnalisees pour un controle fin :

| Propriete | Defaut | Description |
|---|---|---|
| `--brand-primary` | `#2563eb` | Couleur principale pour les boutons, liens et indicateurs de focus |
| `--auth-bg` | `#f3f4f6` | Couleur d'arriere-plan de la page |
| `--auth-card-bg` | `#ffffff` | Couleur d'arriere-plan de la carte/du formulaire |
| `--auth-logo-bg` | `transparent` | Arriere-plan de la pastille du logo (la marge interieure de la pastille n'apparait que lorsqu'un arriere-plan de logo est configure) |
| `--auth-radius` | `0.5rem` | Rayon de bordure de la carte d'authentification |
| `--auth-font` | *(inherit; system font stack)* | Famille de polices de la carte d'authentification |
| `--auth-heading` | `#111827` | Couleur du texte des titres |

Les variables de couleur ci-dessus correspondent directement aux champs de configuration (`primaryColor`, `lightBg`/`darkBg`, `lightCardBg`/`darkCardBg`, `lightLogoBg`/`darkLogoBg`) ; preferez donc la configuration pour les changements de couleur simples et reservez le CSS personnalise a tout le reste.

Remplacez-les dans votre CSS personnalise :

```css
:root {
  --brand-primary: #059669;
  --auth-bg: #0f172a;
  --auth-card-bg: #1e293b;
  --auth-heading: #f8fafc;
}
```

L'interface de connexion utilise Tailwind CSS. Le CSS personnalise peut cibler les elements HTML standard et les classes utilitaires Tailwind. Les composants d'interface exportes (`Button`, `Input`, `Card`, `Alert`, etc.) utilisent Tailwind en interne.

### Exemple : Arriere-plan et police personnalises

```css
/* custom.css */
body {
  font-family: 'Inter', sans-serif;
  background-color: #0f172a;
}
```

## Niveaux de personnalisation

| Niveau | Ce que vous faites | Chemin de mise a jour |
|---|---|---|
| **Configuration seule** | Montez `branding.json` + logo | Transparent -- mettez a jour l'image Docker, gardez vos montages |
| **Configuration + CSS** | Ajoutez `customCssUrl` avec des substitutions de style | Idem -- les classes CSS sont stables |
| **Package npm** | `npm install @authagonal/login`, personnalisez `branding.json`, compilez dans `wwwroot/` | Mise a jour possible -- `npm update` recupere les nouvelles versions |
| **Forker la SPA** | Clonez `login-app/`, modifiez les sources, compilez votre propre version | Vous possedez l'interface -- les mises a jour du serveur sont independantes |
| **Ecrire la votre** | Construisez un frontend entierement personnalise contre l'API d'authentification | Controle total -- voir [API d'authentification](auth-api) pour le contrat |

Consultez `demos/custom-server/` pour un exemple fonctionnel avec personnalisation visuelle (theme vert, "Acme Corp").
