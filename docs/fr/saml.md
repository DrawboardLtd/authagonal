---
layout: default
title: SAML
locale: fr
---

# SAML 2.0 SP

Authagonal inclut une implémentation maison de fournisseur de services (SP) SAML 2.0. Aucune bibliothèque SAML tierce : construit sur `System.Security.Cryptography.Xml.SignedXml` (composant de .NET).

## Portée

- **SSO initié par le SP** (l'utilisateur commence sur Authagonal, puis est redirigé vers l'IdP)
- **Binding HTTP-Redirect** pour l'AuthnRequest (signée en option, voir plus bas)
- **Binding HTTP-POST** pour la Response (ACS)
- **Assertions chiffrées** (`EncryptedAssertion`), déchiffrées avec une paire de clés SP propre à chaque connexion
- **Single Logout** (initié par le SP et par l'IdP, bindings Redirect et POST)
- Azure AD / Entra ID est la cible principale, mais tout IdP conforme fonctionne (Okta, OneLogin, Ping, Google Workspace, ADFS ; les noms d'attributs Shibboleth sont pris en charge)

### Non pris en charge

- Binding Artifact
- Chiffrement d'assertion AES-GCM (limitation de `EncryptedXml` de .NET ; configurez AES-CBC au niveau de l'IdP, voir plus bas)

Le SSO initié par l'IdP est pris en charge **par connexion, et désactivé par défaut** : définissez `allowUnsolicitedResponses: true` sur la connexion pour l'accepter. Sans cela, l'ACS refuse une Response sans `InResponseTo` et redirige avec `error=saml_unsolicited`. Désactivé par défaut parce qu'accepter les réponses non sollicitées permet à quiconque possède un compte chez l'IdP d'ouvrir une session depuis n'importe quel user-agent, et parce qu'exiger le cookie de requête sur le chemin initié par le SP ne vaut rien tant que la même assertion peut être rejouée sans `InResponseTo`. Lorsque l'option est active, la vérification de l'identifiant de requête est ignorée pour les réponses non sollicitées, mais l'usage unique de l'identifiant d'assertion reste appliqué (voir Sécurité).

## Configuration Azure AD

### 1. Créer un fournisseur SAML

**Option A : Configuration (recommandée pour les configurations statiques)**

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

Les fournisseurs sont injectés au démarrage. Les mappages de domaines SSO sont enregistrés automatiquement à partir de `AllowedDomains`. Les fournisseurs injectés par configuration exigent une URL `MetadataLocation` et n'obtiennent pas de paire de clés SP (donc pas d'AuthnRequests signées, d'assertions chiffrées ni de messages de déconnexion signés) ; utilisez l'API d'administration pour ces fonctionnalités.

`EntityId` est **l'entity ID de votre SP** (l'identifiant que vous enregistrez auprès de l'IdP), et non l'entity ID de l'IdP.

**Option B : API d'administration (pour la gestion à l'exécution)**

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

L'API génère le `connectionId` (un GUID) et le renvoie dans l'en-tête `Location` et le corps de la réponse. Champs optionnels supplémentaires : `metadataXml` (métadonnées collées, voir plus bas), `nameIdFormat` (voir plus bas), `signAuthnRequests` (forcer les AuthnRequests signées), `iconUrl` (icône du bouton de connexion), `disableJitProvisioning` (rejeter les utilisateurs inconnus au lieu de les créer automatiquement), `allowUnsolicitedResponses` (accepter la connexion initiée par l'IdP — désactivé par défaut, voir plus haut). Les connexions créées via l'API obtiennent aussi une paire de clés SP générée automatiquement (voir Paire de clés SP plus bas).

Les connexions se gèrent via `POST` / `GET` / `PUT` / `DELETE` sur `/api/v1/saml/connections[/{connectionId}]`. `PUT` est une mise à jour partielle : seuls les champs fournis sur le fil sont modifiés.

### 2. Configurer Azure AD

1. Dans Azure AD, Applications d'entreprise, Nouvelle application, Créer la vôtre
2. Configurez l'authentification unique, SAML
3. **Identifiant (Entity ID) :** `https://auth.example.com/saml/acme-azure`
4. **URL de réponse (ACS) :** `https://auth.example.com/saml/acme-azure/acs`
5. **URL de connexion :** `https://auth.example.com/saml/acme-azure/login`

### 3. Routage de domaine SSO

Lorsque `AllowedDomains` est spécifié (dans la configuration ou via l'API de création), les mappages de domaines SSO sont enregistrés automatiquement. Lorsqu'un utilisateur saisit `user@acme.com` sur la page de connexion, la SPA détecte que le SSO est requis et affiche "Continue with SSO". Un domaine ne peut être mappé qu'à une seule connexion ; l'API rejette un domaine déjà revendiqué par une autre connexion.

Vous pouvez également gérer les domaines à l'exécution via l'API d'administration ; voir [API d'administration](admin-api).

## Métadonnées XML collées

Certains IdP ne publient pas d'URL de métadonnées (Google Workspace), ou leur point d'accès de métadonnées est inaccessible depuis le SP (ADFS en réseau privé). Dans ces cas, collez plutôt le document de métadonnées : fournissez `metadataXml` à la création ou à la mise à jour. Un seul des deux champs `metadataLocation` ou `metadataXml` doit être fourni ; en fournir un lors d'une mise à jour efface l'autre.

Les métadonnées collées sont validées au moment de l'enregistrement et **condensées** (`SamlMetadataParser.Condense`) en un `EntityDescriptor` canonique minimal contenant exactement ce que le SP consomme : entityID, certificats de signature, le point d'accès SSO, le point d'accès SLO s'il est présent, et l'indicateur `WantAuthnRequestsSigned`. Les documents des éditeurs peuvent dépasser 100 Ko (le `FederationMetadata.xml` d'ADFS), au-delà de la limite de 64 Ko d'une propriété Azure Table, alors que les parties utilisées par le SP ne pèsent que quelques Ko. Les collages non analysables sont rejetés avec une erreur 400 ; le document doit contenir un `IDPSSODescriptor` avec un certificat de signature et un `SingleSignOnService`.

## Format NameID

Le champ `nameIdFormat` contrôle le Format de `NameIDPolicy` demandé dans l'AuthnRequest :

| Valeur | Comportement |
|---|---|
| omis / null | `urn:oasis:names:tc:SAML:1.1:nameid-format:emailAddress` (la valeur par défaut historique) |
| `"none"` | Omet entièrement l'élément `NameIDPolicy`. Le réglage sûr pour ADFS : ADFS fait échouer toute la connexion (MSIS7070) lorsque ses règles de claims n'émettent pas le format demandé. |
| toute autre valeur | Envoyée telle quelle comme URN de Format (doit commencer par `urn:`) |

Lors d'une mise à jour, `""` rétablit la valeur par défaut emailAddress. Les métadonnées SP annoncent le format demandé par la connexion (et omettent `NameIDFormat` lorsqu'il vaut `"none"`).

## Points d'accès

| Point d'accès | Description |
|---|---|
| `GET /saml/{connectionId}/login?returnUrl=...&loginHint=...` | Initie le SSO initié par le SP. Construit une AuthnRequest (signée le cas échéant) et redirige vers l'IdP. `loginHint` est transmis comme `login_hint` pour les IdP qui l'honorent (Entra, Google). |
| `POST /saml/{connectionId}/acs` | Assertion Consumer Service. Reçoit la Response SAML, la valide, crée l'utilisateur ou le connecte. |
| `GET /saml/{connectionId}/metadata` | XML de métadonnées SP pour configurer l'IdP. |
| `GET /saml/{connectionId}/logout?returnUrl=...` | Single Logout initié par le SP. Met fin à la session locale, puis envoie une LogoutRequest à l'IdP lorsqu'il prend en charge le SLO. |
| `GET/POST /saml/{connectionId}/slo` | Point d'accès Single Logout. Reçoit les LogoutRequests initiées par l'IdP (binding Redirect ou POST) et le volet LogoutResponse du SLO initié par le SP. |

L'URL de retour après connexion est transportée côté serveur sur l'AuthnRequest stockée (indexée par l'identifiant de requête), et non dans RelayState : la spécification SAML limite RelayState à 80 octets et certains IdP le tronquent. RelayState n'est consulté que pour les flux initiés par l'IdP.

## Paire de clés SP et assertions chiffrées

Chaque connexion créée via l'API obtient une paire de clés SP générée automatiquement : un certificat RSA 2048 bits auto-signé (validité de 10 ans), stocké au format PKCS#12 et protégé au repos par le fournisseur de secrets de l'hôte. Elle reste côté serveur uniquement et n'est jamais renvoyée par l'API. La paire de clés permet :

- **Les AuthnRequests signées** (signature des paramètres `SigAlg`/`Signature` du binding redirect). La signature s'active automatiquement lorsque les métadonnées de l'IdP déclarent `WantAuthnRequestsSigned`, ou systématiquement lorsque la connexion définit `signAuthnRequests: true`.
- **Le déchiffrement des assertions chiffrées.** Lorsque les métadonnées SP annoncent un certificat de chiffrement, ADFS commence à chiffrer les assertions par défaut ; l'ACS les déchiffre avec la clé privée du SP et fait passer l'assertion déchiffrée par le même pipeline de signature et de conditions qu'une assertion en clair. Pris en charge : transport de clé RSA-OAEP (SHA-1/SHA-256) ; chiffrement des données AES-128/192/256-CBC et 3DES. **Le transport de clé RSA-1.5 est refusé** — le déballage PKCS#1 v1.5 est un oracle Bleichenbacher/ROBOT — et **AES-GCM n'est pas pris en charge** (limitation de `EncryptedXml` de .NET). Configurez l'IdP pour RSA-OAEP et AES-CBC. Les deux échecs renvoient le même message constant (« Could not decrypt the assertion. »), délibérément : nommer l'algorithme ou l'étape en échec est précisément ce qui construit l'oracle — diagnostiquez donc depuis la configuration de l'IdP, pas depuis l'erreur.
- **Les messages de déconnexion signés** (LogoutRequest/LogoutResponse sur le binding redirect).

Les métadonnées SP publient le certificat à la fois comme `KeyDescriptor` `signing` et `encryption`, et définissent `AuthnRequestsSigned="true"` lorsque la connexion force la signature.

## Single Logout

L'ACS enregistre la session SAML dans le cookie d'authentification (claims `saml_connection`, `saml_name_id`, `saml_name_id_format`, `saml_session_index`) afin que la déconnexion puisse être rattachée à la session de l'IdP.

- **Initié par le SP :** `GET /saml/{connectionId}/logout` met toujours fin d'abord à la session cookie locale (l'utilisateur a demandé à se déconnecter ; le SLO côté IdP est au mieux-effort). Si la session du navigateur provient de cette connexion et que les métadonnées de l'IdP annoncent un `SingleLogoutService`, une LogoutRequest (NameID + SessionIndex, signée lorsque le SP dispose d'une clé) est envoyée via le binding redirect ; la LogoutResponse de l'IdP revient sur `/slo`, qui ramène l'utilisateur vers le `returnUrl` stocké. Les IdP sans point d'accès SLO (Google) reçoivent uniquement la déconnexion locale.
- **Initié par l'IdP :** l'IdP envoie une LogoutRequest à `/saml/{connectionId}/slo` (binding Redirect GET ou POST). Les requêtes signées sont validées par rapport aux certificats des métadonnées de l'IdP. **Une LogoutRequest non signée ou non vérifiable est refusée avec un 400** avant même de consulter une session. Il n'existe pas de repli limité à la session : une page tierce qui fait naviguer le navigateur de la *victime* jusqu'ici fournit la session de la victime, pas celle de l'attaquant — restreindre à la session courante n'aurait donc pas limité qui peut être déconnecté. Profiles §4.4.3.1 exige de toute façon que l'IdP signe une LogoutRequest sur le binding Redirect ou POST, et les métadonnées de la connexion fournissent déjà les certificats : refuser une requête non signée ne coûte rien à un IdP conforme. Une LogoutResponse signée est renvoyée lorsque l'IdP dispose d'un point d'accès SLO. Uniquement en canal frontal : le message arrive dans le navigateur de l'utilisateur, donc mettre fin à la session cookie déconnecte exactement ce navigateur.

## Mise en cache des métadonnées et rotation des certificats

- Les métadonnées de l'IdP récupérées depuis `MetadataLocation` sont mises en cache en mémoire pendant 60 minutes (configurable via `Cache:SamlMetadataCacheMinutes`), indexées par l'URL des métadonnées (et non par l'identifiant de connexion, ce qui exclut toute confusion de cache entre tenants).
- Les métadonnées collées sont mises en cache par adressage de contenu (hash du XML) et ne sont jamais récupérées à nouveau.
- **Nouvelle récupération sur échec de signature :** un échec de validation de signature juste après une rotation du certificat de l'IdP signifie que les métadonnées en cache sont périmées. Sur cet échec précis, l'entrée de cache est évincée et les métadonnées récupérées une fois, puis la validation est retentée, avec un délai de refroidissement de 5 minutes par emplacement de métadonnées afin qu'une assertion frauduleuse ne puisse pas marteler le point d'accès de métadonnées de l'IdP. Sans cela, une rotation de certificat ferait échouer les connexions jusqu'à l'expiration du TTL du cache. (Uniquement pour les métadonnées récupérées par URL ; les métadonnées collées n'ont rien à récupérer.)

## Compatibilité Azure AD

| Comportement Azure AD | Traitement |
|---|---|
| Signe uniquement l'assertion (par défaut) | Valide la signature sur l'élément Assertion |
| Signe uniquement la réponse | Valide la signature sur l'élément Response |
| Signe les deux | Valide les deux signatures |
| SHA-256 (par défaut) | Prend en charge SHA-256 et SHA-1 |
| NameID : emailAddress | Extraction directe de l'email |
| NameID : persistent (opaque) | Se rabat sur le claim email issu des attributs |
| NameID : unspecified | Se rabat sur le claim email issu des attributs |
| NameID : transient | Change à chaque connexion, il n'est donc jamais utilisé comme clé de fédération. L'attribut object-id stable de l'IdP est utilisé à la place ; si aucun n'est fourni, la connexion est rejetée avec une erreur exploitable (configurez un NameID persistent ou emailAddress, ou fournissez un attribut object-id). |

## Mappage des attributs

Les attributs sont indexés sans distinction de casse sous leur `Name` comme sous leur `FriendlyName` (Okta et Shibboleth émettent des Names OID accompagnés de FriendlyNames lisibles ; c'est le fait de correspondre à l'un ou à l'autre qui fait fonctionner le mappage des éditeurs). Chaque champ essaie une liste d'alias dans l'ordre ; le premier alias est l'URI de claim Microsoft, de sorte que le comportement Entra/ADFS reste inchangé, et les suivants couvrent les noms friendly et OID qu'Okta, OneLogin, Ping, Google et Shibboleth émettent par défaut :

| Champ | Noms d'attributs acceptés |
|---|---|
| email | `.../claims/emailaddress`, `email`, `mail`, `emailaddress`, `urn:oid:0.9.2342.19200300.100.1.3` |
| firstName | `.../claims/givenname`, `givenName`, `given_name`, `firstName`, `first_name`, `urn:oid:2.5.4.42` |
| lastName | `.../claims/surname`, `sn`, `surname`, `lastName`, `last_name`, `familyName`, `family_name`, `urn:oid:2.5.4.4` |
| displayName | `http://schemas.microsoft.com/identity/claims/displayname`, `displayName`, `urn:oid:2.16.840.1.113730.3.1.241`, `cn`, `urn:oid:2.5.4.3` |
| objectId | `http://schemas.microsoft.com/identity/claims/objectidentifier`, `objectGUID`, `user.objectid` |
| groups | `.../claims/groups`, `groups`, `memberOf`, `.../claims/role`, `urn:oid:1.3.6.1.4.1.5923.1.5.1.1` |

(`.../claims/...` abrège l'URI complète `http://schemas.xmlsoap.org/ws/2005/05/identity/claims/...` ou `http://schemas.microsoft.com/ws/2008/06/identity/claims/...`.)

Priorité de résolution de l'email : attribut email explicite (n'importe quel alias), puis NameID lorsque son format est emailAddress, puis le claim `name` s'il contient `@`, sinon rejet (un email est requis).

**Les groupes sont multivalués :** chaque élément `AttributeValue` est capturé (un par appartenance à un groupe), pas seulement le premier.

## Provisionnement JIT

Les utilisateurs inconnus sont créés automatiquement à la première connexion (email, prénom/nom depuis l'assertion, email marqué comme confirmé) et liés à la connexion par leur identité fédérée stable (`saml:{connectionId}` + NameID, ou l'object-id pour les NameID transient). Définissez `disableJitProvisioning: true` pour rejeter plutôt les utilisateurs inconnus. Les utilisateurs récurrents sont d'abord identifiés par le lien fédéré, jamais par l'email seul ; un compte local existant n'est rattaché par email que lorsque les `AllowedDomains` de la connexion couvrent le domaine de cet email (l'affirmation explicite de l'administrateur que cet IdP possède le domaine), ce qui empêche la prise de contrôle de compte via un IdP malveillant.

## Sécurité

- **Prévention du rejeu :** pour les flux initiés par le SP, `InResponseTo` est validé par rapport à un identifiant de requête stocké (à usage unique). Indépendamment, l'identifiant de chaque assertion acceptée est stocké et son usage unique est appliqué, ce qui couvre aussi les réponses initiées par l'IdP et les réponses dont l'`InResponseTo` a été supprimé (l'identifiant d'assertion réside à l'intérieur de l'assertion signée, il ne peut donc pas être modifié sans casser la signature).
- **Décalage d'horloge :** tolérance de 5 minutes sur NotBefore/NotOnOrAfter
- **Prévention des attaques par encapsulation :** l'URI de Reference de la signature doit correspondre à l'ID de l'élément signé
- **Prévention de la redirection ouverte :** l'URL de retour après connexion doit être un chemin racine-relatif (commençant par `/`, sans `//`, sans barres obliques inverses, car les navigateurs traitent `\` comme `/`)
- **Attestation de domaine :** lorsque `AllowedDomains` est configuré, les assertions pour des emails hors de ces domaines sont rejetées, de sorte qu'une connexion ne peut pas revendiquer le domaine d'une autre ni l'email d'un utilisateur local
- **MFA :** la fédération ne prouve que le premier facteur. Si la politique effective de l'utilisateur exige la MFA, la connexion passe par le challenge/la configuration MFA locale au lieu d'émettre une session pleinement authentifiée.
