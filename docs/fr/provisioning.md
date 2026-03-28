---
layout: default
title: Provisionnement
locale: fr
---

# Provisionnement TCC

Authagonal provisionne les utilisateurs dans les applications en aval en utilisant le modele **Try-Confirm-Cancel (TCC)**. Cela garantit que toutes les applications sont d'accord avant qu'un utilisateur obtienne l'acces, avec un retour en arriere propre si une application refuse.

## Quand le provisionnement s'execute

Le provisionnement s'execute au niveau du **point d'acces d'autorisation** (`/connect/authorize`), apres l'authentification de l'utilisateur mais avant l'emission d'un code d'autorisation. Cela signifie :

- Il s'execute lors de la premiere connexion de l'utilisateur via un client qui necessite le provisionnement
- Les combinaisons application/utilisateur deja provisionnees sont ignorees (suivies dans la table `UserProvisions`)
- Si le provisionnement echoue, la requete d'autorisation renvoie `access_denied` -- aucun code n'est emis

## Configuration

### 1. Definir les applications de provisionnement

Dans `appsettings.json` :

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-bearer-token"
    }
  }
}
```

### 2. Assigner des applications aux clients

Chaque client declare dans quelles applications ses utilisateurs doivent etre provisionnes :

```json
{
  "Clients": [
    {
      "ClientId": "web-app",
      "ProvisioningApps": ["my-backend"],
      ...
    }
  ]
}
```

Lorsqu'un utilisateur s'autorise via `web-app`, il est provisionne dans `my-backend` s'il ne l'a pas deja ete.

## Protocole TCC

Authagonal effectue trois types d'appels HTTP vers votre point d'acces de provisionnement. Tous utilisent `POST` avec des corps JSON et `Authorization: Bearer {ApiKey}`.

### Phase 1 : Try

**Requete :** `POST {CallbackUrl}/try`

```json
{
  "transactionId": "a1b2c3d4...",
  "userId": "user-id",
  "email": "user@example.com",
  "firstName": "Jane",
  "lastName": "Doe",
  "organizationId": "org-id-or-null"
}
```

**Reponses attendues :**

| Statut | Corps | Signification |
|---|---|---|
| `200` | `{ "approved": true }` | L'utilisateur peut etre provisionne. L'application cree un enregistrement **en attente**. |
| `200` | `{ "approved": false, "reason": "..." }` | L'utilisateur est rejete. Aucun enregistrement cree. |
| Non-2xx | N'importe | Traite comme un echec. |

Le `transactionId` identifie cette tentative de provisionnement. Votre application doit le stocker a cote de l'enregistrement en attente.

### Phase 2 : Confirm

Appele uniquement si **toutes** les applications ont renvoye `approved: true` lors de la phase try.

**Requete :** `POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Reponse attendue :** `200` (n'importe quel corps). Votre application promeut l'enregistrement en attente en confirme.

### Phase 3 : Cancel

Appele si le try d'**une** application a ete rejete ou a echoue, pour nettoyer les applications qui ont reussi lors de la phase try.

**Requete :** `POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Reponse attendue :** `200` (n'importe quel corps). Votre application supprime l'enregistrement en attente.

L'annulation est effectuee au mieux -- si elle echoue, Authagonal enregistre l'erreur et continue. Votre application devrait **nettoyer les enregistrements non confirmes apres un TTL** (par exemple, 1 heure) comme filet de securite.

## Diagramme de flux

```
Authorize Endpoint
    |
    +- User authenticated ✓
    +- Client requires apps: [A, B]
    +- User already provisioned into: [A]
    +- Need to provision: [B]
    |
    +- TRY B ------------>App B: create pending record
    |   +- approved: true
    |
    +- CONFIRM B -------->App B: promote to confirmed
    |   +- 200 OK
    |
    +- Store provision record (userId, "B")
    +- Issue authorization code
    +- Redirect to client
```

### En cas d'echec

```
    +- TRY A ------------>App A: create pending record
    |   +- approved: true
    |
    +- TRY B ------------>App B: rejects
    |   +- approved: false, reason: "No license available"
    |
    +- CANCEL A --------->App A: delete pending record
    |
    +- Redirect with error=access_denied
```

### En cas d'echec partiel de confirmation

Si certaines confirmations reussissent mais qu'une echoue, les applications confirmees avec succes ont leurs enregistrements de provisionnement stockes (donc elles ne seront pas retentees). L'utilisateur voit une erreur et peut reessayer -- seule l'application echouee sera tentee la prochaine fois.

## Deprovisionnement

Lorsqu'un utilisateur est supprime via l'API d'administration (`DELETE /api/v1/profile/{userId}`), Authagonal appelle `DELETE {CallbackUrl}/users/{userId}` sur chaque application dans laquelle l'utilisateur a ete provisionne. C'est effectue au mieux -- les echecs sont enregistres mais ne bloquent pas la suppression.

## Implementation des points d'acces en amont

### Exemple minimal (Node.js/Express)

```javascript
const pending = new Map(); // transactionId → user data

app.post('/provisioning/try', (req, res) => {
  const { transactionId, userId, email } = req.body;

  // Your business logic: can this user be provisioned?
  if (!isAllowed(email)) {
    return res.json({ approved: false, reason: 'Domain not allowed' });
  }

  // Store pending record with TTL
  pending.set(transactionId, { userId, email, createdAt: Date.now() });

  res.json({ approved: true });
});

app.post('/provisioning/confirm', (req, res) => {
  const { transactionId } = req.body;
  const data = pending.get(transactionId);

  if (data) {
    createUser(data); // Promote to real record
    pending.delete(transactionId);
  }

  res.sendStatus(200);
});

app.post('/provisioning/cancel', (req, res) => {
  pending.delete(req.body.transactionId);
  res.sendStatus(200);
});

// Cleanup unconfirmed records older than 1 hour
setInterval(() => {
  const cutoff = Date.now() - 3600000;
  for (const [id, data] of pending) {
    if (data.createdAt < cutoff) pending.delete(id);
  }
}, 600000);
```
