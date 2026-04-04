---
layout: default
title: SAML
locale: fr
---

# SAML 2.0 SP

Authagonal inclut une implementation maison de fournisseur de services SAML 2.0. Aucune bibliotheque SAML tierce -- construit sur `System.Security.Cryptography.Xml.SignedXml` (partie de .NET).

## Portee

- **SSO initie par le SP** (l'utilisateur commence sur Authagonal, redirige vers l'IdP)
- **Binding HTTP-Redirect** pour AuthnRequest
- **Binding HTTP-POST** pour la reponse (ACS)
- Azure AD est la cible principale, mais tout IdP conforme fonctionne

### Non pris en charge

- SSO initie par l'IdP
- Deconnexion SAML (utiliser l'expiration de session)
- Chiffrement d'assertion (ne pas publier de certificat de chiffrement)
- Binding Artifact

## Configuration Azure AD

### 1. Creer un fournisseur SAML

**Option A -- Configuration (recommande pour les configurations statiques) :**

Ajoutez dans `appsettings.json` :

```json
{
  "SamlProviders": [
    {
      "ConnectionId": "acme-azure",
      "ConnectionName": "Acme Corp Azure AD",
      "EntityId": "https://auth.example.com/saml/acme-azure",
      "MetadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
      "AllowedDomains": ["acme.com"]
    }
  ]
}
```

Les fournisseurs sont injectes au demarrage. Les mappages de domaines SSO sont enregistres automatiquement a partir de `AllowedDomains`.

**Option B -- API d'administration (pour la gestion a l'execution) :**

```bash
curl -X POST https://auth.example.com/api/v1/saml/connections \
  -H "Authorization: Bearer {admin-token}" \
  -H "Content-Type: application/json" \
  -d '{
    "connectionName": "Acme Corp Azure AD",
    "entityId": "https://auth.example.com/saml/acme-azure",
    "metadataLocation": "https://login.microsoftonline.com/{tenant-id}/federationmetadata/2007-06/federationmetadata.xml?appid={app-id}",
    "allowedDomains": ["acme.com"]
  }'
```

### 2. Configurer Azure AD

1. Dans Azure AD, allez dans Applications d'entreprise, Nouvelle application, Creer la votre
2. Configurez l'authentification unique, SAML
3. **Identifiant (Entity ID) :** `https://auth.example.com/saml/acme-azure`
4. **URL de reponse (ACS) :** `https://auth.example.com/saml/acme-azure/acs`
5. **URL de connexion :** `https://auth.example.com/saml/acme-azure/login`

### 3. Routage de domaine SSO

Lorsque `AllowedDomains` est specifie (dans la configuration ou via l'API de creation), les mappages de domaines SSO sont enregistres automatiquement. Lorsqu'un utilisateur entre `user@acme.com` sur la page de connexion, la SPA detecte que le SSO est requis et affiche "Continuer avec SSO".

Vous pouvez egalement gerer les domaines a l'execution via l'API d'administration -- voir [API d'administration](admin-api).

## Points d'acces

| Point d'acces | Description |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...` | Initie le SSO initie par le SP. Construit une AuthnRequest et redirige vers l'IdP. |
| `POST /saml/{connectionId}/acs` | Service consommateur d'assertions. Recoit la reponse SAML, la valide, cree/connecte l'utilisateur. |
| `GET /saml/{connectionId}/metadata` | XML de metadonnees SP pour configurer l'IdP. |

## Compatibilite Azure AD

| Comportement Azure AD | Traitement |
|---|---|
| Signe uniquement l'assertion (par defaut) | Valide la signature sur l'element Assertion |
| Signe uniquement la reponse | Valide la signature sur l'element Response |
| Signe les deux | Valide les deux signatures |
| SHA-256 (par defaut) | Prend en charge SHA-256 et SHA-1 |
| NameID : emailAddress | Extraction directe de l'email |
| NameID : persistent (opaque) | Se rabat sur le claim email depuis les attributs |
| NameID : transient, unspecified | Se rabat sur le claim email depuis les attributs |

## Mappage des claims

Les claims Azure AD (format URI complet) sont mappes vers des noms simples :

| URI de claim Azure AD | Mappe vers |
|---|---|
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/emailaddress` | `email` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/givenname` | `firstName` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/surname` | `lastName` |
| `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/name` | `name` (UPN) |
| `http://schemas.microsoft.com/identity/claims/objectidentifier` | `oid` |
| `http://schemas.microsoft.com/identity/claims/tenantid` | `tenantId` |
| `http://schemas.microsoft.com/identity/claims/displayname` | `displayName` |

## Securite

- **Prevention de la reutilisation :** InResponseTo est valide par rapport a un identifiant de requete stocke. Chaque identifiant est a usage unique.
- **Tolerance d'horloge :** Tolerance de 5 minutes sur NotBefore/NotOnOrAfter
- **Prevention des attaques par encapsulation :** La validation de signature utilise la resolution de reference correcte
- **Prevention de redirection ouverte :** RelayState (returnUrl) doit etre un chemin racine-relatif (commencant par `/`, sans scheme ni hote)
