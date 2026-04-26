---
layout: default
title: Personnalisation visuelle
locale: fr
---

# Personnalisation de l'interface de connexion

La SPA de connexion est configurable a l'execution via un fichier `branding.json` servi depuis la racine web. Aucune recompilation n'est necessaire -- montez simplement votre configuration et vos ressources.

## Comment ca fonctionne

Au demarrage, la SPA recupere `/branding.json`. Si le fichier n'existe pas ou est inaccessible, les valeurs par defaut sont utilisees. La configuration controle :

- Le nom de l'application (affiche dans l'en-tete et le titre de la page)
- L'image du logo
- La couleur principale (boutons, liens, indicateurs de focus)
- La visibilite du lien de mot de passe oublie
- Le CSS personnalise pour une stylisation plus approfondie

## Configuration

Placez un fichier `branding.json` dans le repertoire `wwwroot/` (ou montez-le dans le conteneur Docker) :

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
| `languages` | `array \| null` | `null` | Options du selecteur de langue (`[{ "code": "en", "label": "English" }, ...]`) |

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

L'option `customCssUrl` charge une feuille de style supplementaire apres les styles par defaut, de sorte que vos regles ont la priorite. Utile pour changer les polices, ajuster l'espacement ou restyler des elements specifiques.

### Proprietes CSS personnalisees

La couleur principale est definie via la propriete CSS personnalisee `--brand-primary` (qui alimente le theme Tailwind). Remplacez-la dans votre CSS personnalise au lieu d'utiliser `branding.json` :

```css
:root {
  --brand-primary: #059669;
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
