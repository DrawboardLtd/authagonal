---
layout: default
title: Provisionnement
locale: fr
---

# Provisionnement TCC

Authagonal provisionne les utilisateurs dans les applications en aval en utilisant le modèle **Try-Confirm-Cancel (TCC)**. Cela garantit que toutes les applications sont d'accord avant qu'un utilisateur obtienne l'accès, avec un retour en arrière propre si une application refuse.

## Quand le provisionnement s'exécute

Le provisionnement s'exécute automatiquement chaque fois qu'un utilisateur est créé, quel que soit le chemin de création :

| Point d'accès | Déclencheur |
|---|---|
| `POST /api/v1/profile/` | Création d'utilisateur par l'administrateur |
| `POST /api/auth/register` | Inscription en libre-service |
| SAML ACS (`POST /saml/{id}/acs`) | Première connexion SSO (nouvel utilisateur) |
| OIDC callback (`GET /oidc/callback`) | Première connexion SSO (nouvel utilisateur) |
| SCIM (`POST /scim/v2/Users`) | Provisionnement du fournisseur d'identité |
| `GET /connect/authorize` | Première autorisation via un client avec `ProvisioningApps` |

Les combinaisons application/utilisateur déjà provisionnées sont ignorées (suivies dans la table `UserProvisions`).

Les chemins de création d'utilisateur provisionnent dans **chaque application configurée**. Le point d'accès d'autorisation provisionne uniquement dans la liste `ProvisioningApps` du client.

**En cas de rejet :** Si une application de provisionnement rejette l'utilisateur lors de la phase Try, l'utilisateur est supprimé et le point d'accès renvoie `422 Unprocessable Entity` avec le motif du rejet. Cela empêche la création d'utilisateurs à moitié créés.

## Configuration

### 1. Définir les applications de provisionnement

Dans `appsettings.json` :

```json
{
  "ProvisioningApps": {
    "my-backend": {
      "CallbackUrl": "https://api.example.com/provisioning",
      "ApiKey": "secret-bearer-token",
      "TryTimeoutSeconds": 60
    }
  }
}
```

`TryTimeoutSeconds` est facultatif (60 par défaut). Augmentez-le lorsque l'application en aval effectue un vrai travail pendant Try. Confirm et Cancel utilisent toujours un délai d'attente court et fixe (10 secondes) et ne sont pas configurables ; ils doivent toujours être peu coûteux.

### 2. Assigner des applications aux clients

Chaque client déclare dans quelles applications ses utilisateurs doivent être provisionnés, via le champ `provisioningApps` de l'enregistrement du client. Définissez-le via l'API d'administration des clients (la configuration d'amorçage `Clients` ne comporte pas ce champ) :

```
PUT /api/v1/clients/web-app
{
  "clientId": "web-app",
  "provisioningApps": ["my-backend"],
  ...
}
```

Lorsqu'un utilisateur s'autorise via `web-app`, il est provisionné dans `my-backend` s'il ne l'a pas déjà été.

## Protocole TCC

Authagonal effectue trois types d'appels HTTP vers votre point d'accès de provisionnement. Tous utilisent `POST` avec des corps JSON et `Authorization: Bearer {ApiKey}`.

### Phase 1 : Try

**Requête :** `POST {CallbackUrl}/try`

```json
{
  "transactionId": "a1b2c3d4...",
  "userId": "user-id",
  "email": "user@example.com",
  "firstName": "Jane",
  "lastName": "Doe",
  "organizationId": "org-id-or-null",
  "customAttributes": { "key": "value" }
}
```

Les champs nuls (y compris `customAttributes` lorsque l'utilisateur n'en a aucun) sont omis de la charge utile.

**Réponses attendues :**

| Statut | Corps | Signification |
|---|---|---|
| `200` | `{ "approved": true }` | L'utilisateur peut être provisionné. L'application crée un enregistrement **en attente**. |
| `200` | `{ "approved": false, "reason": "..." }` | L'utilisateur est rejeté. Aucun enregistrement créé. |
| Non-2xx | N'importe | Traité comme un échec. |

Le `transactionId` identifie cette tentative de provisionnement. Votre application doit le stocker à côté de l'enregistrement en attente.

Une réponse approuvée peut également renvoyer `organizationId` et/ou `customAttributes`. Authagonal les fusionne dans l'utilisateur : `organizationId` n'est appliqué que si l'utilisateur n'en possède pas déjà un (les applications suivantes de la même transaction voient l'affectation antérieure), et les entrées de `customAttributes` sont fusionnées clé par clé. Les deux se propagent dans les tokens (revendication `org_id` ; attributs personnalisés via la configuration du scope `UserClaims`).

### Phase 2 : Confirm

Appelé uniquement si **toutes** les applications ont renvoyé `approved: true` lors de la phase try.

**Requête :** `POST {CallbackUrl}/confirm`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Réponse attendue :** `200` (n'importe quel corps). Votre application promeut l'enregistrement en attente en confirmé.

### Phase 3 : Cancel

Appelé si le try d'**une** application a été rejeté ou a échoué, pour nettoyer les applications qui ont réussi lors de la phase try.

**Requête :** `POST {CallbackUrl}/cancel`

```json
{
  "transactionId": "a1b2c3d4..."
}
```

**Réponse attendue :** `200` (n'importe quel corps). Votre application supprime l'enregistrement en attente.

L'annulation est effectuée au mieux : si elle échoue, Authagonal enregistre l'erreur et continue. Votre application devrait **nettoyer les enregistrements non confirmés après un TTL** (par exemple, 1 heure) comme filet de sécurité.

## Diagramme de flux

```
Authorize Endpoint
    │
    ├─ User authenticated ✓
    ├─ Client requires apps: [A, B]
    ├─ User already provisioned into: [A]
    ├─ Need to provision: [B]
    │
    ├─ TRY B ──────────► App B: create pending record
    │   └─ approved: true
    │
    ├─ CONFIRM B ──────► App B: promote to confirmed
    │   └─ 200 OK
    │
    ├─ Store provision record (userId, "B")
    ├─ Issue authorization code
    └─ Redirect to client
```

### En cas d'échec

```
    ├─ TRY A ──────────► App A: create pending record
    │   └─ approved: true
    │
    ├─ TRY B ──────────► App B: rejects
    │   └─ approved: false, reason: "No license available"
    │
    ├─ CANCEL A ───────► App A: delete pending record
    │
    └─ Redirect with error=access_denied
```

### En cas d'échec partiel de confirmation

Si certaines confirmations réussissent mais qu'une échoue, les applications confirmées avec succès ont leurs enregistrements de provisionnement stockés (donc elles ne seront pas retentées). L'utilisateur voit une erreur et peut réessayer ; seule l'application échouée sera tentée la prochaine fois.

## Résolution d'applications personnalisée

Par défaut, les applications de provisionnement sont lues depuis la section de configuration `ProvisioningApps` via `ConfigProvisioningAppProvider`. Remplacez `IProvisioningAppProvider` pour résoudre les applications dynamiquement, par exemple depuis une base de données ou par tenant :

```csharp
builder.Services.AddSingleton<IProvisioningAppProvider, MyAppProvider>();
builder.Services.AddAuthagonal(builder.Configuration);
```

Le fournisseur renvoie une liste d'applications et leurs URLs de callback. Le `TccProvisioningOrchestrator` appelle Try/Confirm/Cancel sur chacune.

Pour des opérations CRUD à l'exécution sans fournisseur personnalisé, la bibliothèque fournit `StoreProvisioningAppProvider`, adossé à `IProvisioningAppStore`. Enregistrez-le explicitement (même modèle que ci-dessus) et gérez les applications via l'API d'administration à `/api/v1/provisioning/apps` (list/create/update/delete, plus `POST /{appId}/test` pour sonder le point d'accès Try d'une application).

## Déprovisionnement

Lorsqu'un utilisateur est supprimé via l'API d'administration (`DELETE /api/v1/profile/{userId}`), Authagonal appelle `DELETE {CallbackUrl}/users/{userId}` sur chaque application dans laquelle l'utilisateur a été provisionné. C'est effectué au mieux : les échecs sont enregistrés mais ne bloquent pas la suppression.

## Implémentation des points d'accès en amont

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
