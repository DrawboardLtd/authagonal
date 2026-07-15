---
layout: default
title: Mise à l'échelle
locale: fr
---

# Mise à l'échelle

Authagonal est conçu pour être mis à l'échelle à la fois verticalement et horizontalement, sans configuration particulière.

## Sans état par conception

Tout l'état persistant est stocké dans le magasin de tables sous-jacent, Azure Table Storage, ou DynamoDB sur le backend AWS. Il n'y a aucun état en cours de processus qui nécessite des sessions persistantes ou une coordination entre les instances :

- **Clés de signature** : chargées depuis Table Storage, actualisées toutes les heures
- **Codes d'autorisation et refresh tokens** : stockés dans Table Storage avec application de l'usage unique
- **Prévention du rejeu SAML** : les identifiants de requête sont suivis dans Table Storage avec suppression atomique
- **OIDC state et vérificateurs PKCE** : stockés dans Table Storage
- **Configuration des clients et des fournisseurs** : récupérée à chaque requête depuis Table Storage

## Chiffrement des cookies (Data Protection)

Les clés Data Protection d'ASP.NET Core sont automatiquement persistées dans Azure Blob Storage lorsqu'une véritable chaîne de connexion Azure Storage est utilisée. Cela signifie que les cookies signés par une instance peuvent être déchiffrés par n'importe quelle autre instance : aucune session persistante n'est requise.

Pour le développement local avec Azurite, les clés Data Protection se rabattent sur le magasin par défaut basé sur les fichiers.

Vous pouvez également pointer vers une URI blob explicite via la configuration (la voie par identité managée, préférée en production) :

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

Sur le backend AWS, passez un client S3 et un bucket à `AddAuthagonalAwsStorage` pour persister le trousseau de clés dans S3 : sans cela, le trousseau reste en mémoire et les cookies sont invalidés au redémarrage et d'un nœud à l'autre. Voir [Installation → AWS backend](installation#aws-backend).

## Caches par instance

Un petit nombre de valeurs très lues et changeant lentement sont mises en cache en mémoire, par instance, pour réduire les allers-retours vers Table Storage :

| Données | Durée du cache | Impact de l'obsolescence |
|---|---|---|
| Documents de découverte OIDC | 60 minutes (configurable) | Prise de conscience retardée de la rotation des clés de l'IdP |
| Métadonnées SAML de l'IdP | 60 minutes (configurable) | Idem |
| Origines CORS autorisées | 60 minutes (configurable) | Les nouvelles origines mettent jusqu'à une heure à se propager |

Ces caches conviennent à une utilisation en production. Toutes les durées sont configurables via la section de configuration `Cache` : voir [Configuration](configuration). Si vous avez besoin d'une propagation immédiate, redémarrez les instances concernées.

## Limitation du débit

Les points d'accès exposés aux abus (inscription par IP, réinitialisation de mot de passe par email cible, SCIM par client, enregistrement dynamique de client par IP ; voir [Configuration → Rate Limiting](configuration#rate-limiting)) sont protégés par un limiteur de débit intégré.

Les limites sont appliquées **en cours de processus, par nœud**, derrière le point d'extension `IRateLimiter` ; ainsi, avec N instances, le plafond effectif vaut N fois la valeur configurée. C'est délibéré : le limiteur est un filet de sécurité contre l'abus incontrôlé d'un nœud unique, et la limite globale de référence a sa place à la périphérie (WAF / ingress / CDN), qui voit tout le trafic avant sa répartition de charge.

## Clustering

Plusieurs instances se coordonnent via une **élection de leader** et un **bus d'événements inter-nœuds**, tous deux derrière des backends interchangeables :

- **Élection de leader** : une élection basée sur un bail (`Cluster:LeaseTtlSeconds`, 30s par défaut, renouvelé à environ la moitié de cet intervalle). Exactement un nœud détient le bail ; le leadership est transféré automatiquement lorsque le leader tombe en panne. Les travaux réservés au leader (actuellement la rotation des clés de signature, lorsqu'elle est activée) ne s'exécutent que sur le leader afin d'éviter la génération simultanée de clés.
- **Bus d'événements** : notifications inter-nœuds (par exemple l'invalidation de cache dans les hôtes multi-tenants), interrogé toutes les `Cluster:PollIntervalSeconds` (3s par défaut).

Chaque instance génère au démarrage un identifiant de nœud aléatoire de 12 caractères hexadécimaux pour s'identifier ; il n'est pas persisté.

### Backends

La **valeur par défaut est en cours de processus** : un nœud unique est toujours son propre leader, et les événements restent purement locaux, ce qui convient à une instance unique sans aucune configuration. Les déploiements multi-nœuds y substituent un backend réel via le callback `configureClustering` sur `AddAuthagonal` :

```csharp
// Azure : leadership via un bail de blob, bus d'événements via un journal de table (Authagonal.AzureProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAzureStorage(blobServiceClient, tableServiceClient));

// AWS : leadership + bus d'événements via DynamoDB (Authagonal.AwsProvider)
builder.Services.AddAuthagonal(builder.Configuration,
    cluster => cluster.UseAwsDynamo(dynamoDb));
```

`UseAzureStorageBus` / `UseAwsDynamoBus` n'enregistrent que le bus d'événements, en conservant le bail en cours de processus (toujours leader) : utilisez-les sur les nœuds qui doivent recevoir les événements du cluster mais ne doivent jamais entrer en concurrence pour le leadership.

> **Note :** avec la valeur par défaut en cours de processus sur plusieurs nœuds, *chaque* nœud se croit leader. C'est sans conséquence pour la plupart des charges de travail, mais activez un backend de bail réel avant d'activer `Auth:KeyRotationEnabled` sur plusieurs instances.

Consultez la page [Configuration](configuration#cluster) pour tous les paramètres du cluster.

### Déploiements multi-tenant

En mode multi-tenant (`AddAuthagonalCore()`), aucun service d'arrière-plan n'est enregistré : `TokenCleanupService`, `GrantReconciliationService`, `SigningKeyRotationService` et les services d'injection de configuration font tous partie de la composition mono-tenant `AddAuthagonal()`. L'hôte les gère par tenant.

## Partition chaude de l'index de noms

La recherche par préfixe de nom dans l'administration s'appuie sur les tables d'index `UserFirstNames` / `UserLastNames`, qui utilisent une **partition chaude unique**. À grande échelle, cela plafonne le débit d'écriture de l'index à environ 2 000 ops/sec, ce qui peut devenir un goulot d'étranglement lors de la création/mise à jour d'utilisateurs sous forte charge. Si vous n'exposez pas la recherche de noms dans l'administration, définissez `Storage:NameIndexesEnabled = false` pour ignorer entièrement ces écritures. Voir [Configuration](configuration).

## Proxy de confiance et points d'accès internes

Lorsque vous exécutez plusieurs instances derrière un équilibreur de charge :

- **En-têtes transférés** : la limitation de débit et le verrouillage se basent sur l'IP du client, résolue depuis `X-Forwarded-For`. Définissez `ForwardedHeaders:KnownNetworks` sur le CIDR de votre ingress / de vos pods afin que l'IP du client ne puisse pas être usurpée entre les instances. `ForwardedHeaders:ForwardLimit` vaut `1` par défaut. Voir [Configuration](configuration#forwarded-headers-trusted-proxy).
- **Points d'accès internes** : `/_internal/backchannel-logout` est protégé par l'IP source (boucle locale / privée uniquement) sauf si `Cluster:Secret` est défini, auquel cas les appelants doivent présenter le secret dans l'en-tête `X-Cluster-Secret` (comparé en temps constant). Définissez le secret dès que le trafic interne transite par un élément qui réécrit l'IP source.

## Recommandations de mise à l'échelle

**Mise à l'échelle verticale** : augmentez le CPU et la mémoire d'une seule instance. Utile pour gérer davantage de requêtes simultanées par instance.

**Mise à l'échelle horizontale** : exécutez plusieurs instances derrière un équilibreur de charge. Aucune session persistante ni cache partagé requis. Chaque instance est entièrement indépendante.

**Mise à l'échelle à zéro** : Authagonal prend en charge les déploiements avec mise à l'échelle à zéro (par exemple Azure Container Apps avec `minReplicas: 0`). La première requête après une période d'inactivité subira un démarrage à froid de quelques secondes, le temps que le runtime .NET s'initialise et que les clés de signature soient chargées depuis le stockage.
