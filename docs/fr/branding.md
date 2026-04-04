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
| `customCssUrl` | `string \| null` | `null` | URL vers un fichier CSS personnalise charge apres les styles par defaut |

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

### Classes CSS disponibles

| Classe | Element |
|---|---|
| `.auth-container` | Conteneur pleine page (flex center) |
| `.auth-card` | La carte de connexion (boite blanche avec ombre) |
| `.auth-logo` | Zone logo/titre |
| `.auth-logo h1` | En-tete texte (quand aucune image de logo) |
| `.auth-logo-img` | Image du logo (quand `logoUrl` est defini) |
| `.auth-title` | Titres des pages ("Connexion", "Reinitialiser votre mot de passe") |
| `.auth-subtitle` | Texte secondaire sous les titres |
| `.form-group` | Conteneur de champ de formulaire |
| `.form-group label` | Libelles des champs |
| `input` | Champs de saisie texte |
| `.btn-primary` | Bouton d'action principal |
| `.btn-secondary` | Bouton secondaire (par exemple, "Continuer avec SSO") |
| `.alert-error` | Messages d'erreur |
| `.alert-success` | Messages de succes |
| `.link` | Liens texte |
| `.sso-notice` | Notification de detection SSO |
| `.password-requirements` | Liste des exigences de robustesse du mot de passe |

### Proprietes CSS personnalisees

La couleur principale est exposee en tant que propriete CSS personnalisee. Vous pouvez la remplacer dans votre CSS personnalise au lieu d'utiliser `branding.json` :

```css
:root {
  --color-primary: #059669;
}
```

### Exemple : Arriere-plan et police personnalises

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

## Niveaux de personnalisation

| Niveau | Ce que vous faites | Chemin de mise a jour |
|---|---|---|
| **Configuration seule** | Montez `branding.json` + logo | Transparent -- mettez a jour l'image Docker, gardez vos montages |
| **Configuration + CSS** | Ajoutez `customCssUrl` avec des substitutions de style | Idem -- les classes CSS sont stables |
| **Package npm** | `npm install @drawboard/authagonal-login`, personnalisez `branding.json`, compilez dans `wwwroot/` | Mise a jour possible -- `npm update` recupere les nouvelles versions |
| **Forker la SPA** | Clonez `login-app/`, modifiez les sources, compilez votre propre version | Vous possedez l'interface -- les mises a jour du serveur sont independantes |
| **Ecrire la votre** | Construisez un frontend entierement personnalise contre l'API d'authentification | Controle total -- voir [API d'authentification](auth-api) pour le contrat |

Consultez `demos/custom-server/` pour un exemple fonctionnel avec personnalisation visuelle (theme vert, "Acme Corp").
