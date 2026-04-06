---
layout: default
title: Mise a l'echelle
locale: fr
---

# Mise a l'echelle

Authagonal est concu pour etre mis a l'echelle verticalement et horizontalement sans configuration speciale.

## Sans etat par conception

Tous les etats persistants sont stockes dans Azure Table Storage. Il n'y a pas d'etat en cours de processus necessitant des sessions persistantes ou une coordination entre les instances :

- **Cles de signature** — chargees depuis Table Storage, actualisees toutes les heures
- **Codes d'autorisation et jetons de rafraichissement** — stockes dans Table Storage avec application a usage unique
- **Prevention de la relecture SAML** — les identifiants de requete sont suivis dans Table Storage avec suppression atomique
- **OIDC state et verificateurs PKCE** — stockes dans Table Storage
- **Configuration des clients et des fournisseurs** — recuperee par requete depuis Table Storage

## Chiffrement des cookies (Data Protection)

Les cles Data Protection d'ASP.NET Core sont automatiquement persistees dans Azure Blob Storage lors de l'utilisation d'une veritable chaine de connexion Azure Storage. Cela signifie que les cookies signes par une instance peuvent etre dechiffres par n'importe quelle autre instance — aucune session persistante requise.

Pour le developpement local avec Azurite, les cles Data Protection reviennent au stockage par defaut base sur les fichiers.

Vous pouvez egalement specifier une URI blob explicite via la configuration :

```json
{
  "DataProtection": {
    "BlobUri": "https://youraccount.blob.core.windows.net/dataprotection/keys.xml"
  }
}
```

## Caches par instance

Un petit nombre de valeurs frequemment lues et changeant lentement sont mises en cache en memoire par instance pour reduire les allers-retours vers Table Storage :

| Donnees | Duree du cache | Impact de l'obsolescence |
|---|---|---|
| Documents de decouverte OIDC | 60 minutes | Prise de conscience retardee de la rotation des cles IdP |
| Metadonnees SAML IdP | 60 minutes | Idem |
| Origines CORS autorisees | 60 minutes | Les nouvelles origines mettent jusqu'a une heure a se propager |

Ces caches sont acceptables pour une utilisation en production. Si vous avez besoin d'une propagation immediate, redemarrez les instances concernees.

## Limitation du debit

Les points de terminaison d'inscription sont proteges par un limiteur de debit distribue integre (5 inscriptions par IP par heure). Lors de l'execution de plusieurs instances, les compteurs de limitation du debit sont automatiquement partages entre toutes les instances via un protocole gossip — aucune coordination externe requise.

### Fonctionnement

Chaque instance maintient ses propres compteurs en memoire en utilisant un CRDT G-Counter. Les instances se decouvrent mutuellement via UDP multicast et echangent leur etat par HTTP toutes les quelques secondes. Le compteur consolide de toutes les instances est utilise pour prendre les decisions de limitation du debit.

Cela signifie que les limites de debit sont appliquees globalement : si un client atteint 3 instances differentes, les 3 savent que le total est de 3, et non 1 chacune.

### Identite des noeuds

Chaque instance genere un identifiant de noeud hexadecimal aleatoire au demarrage (par exemple, `a3f1b2`). Cet identifiant identifie l'instance dans les messages gossip et l'etat de limitation du debit. Il n'est pas persiste -- un nouvel identifiant est genere a chaque redemarrage.

Un `ClusterLeaderService` s'execute sur chaque instance, elisant un leader unique parmi les pairs decouverts (l'identifiant de noeud le plus bas l'emporte). Le leadership est transfere automatiquement lorsque le leader tombe en panne. L'election du leader est disponible pour les taches de coordination a l'echelle du cluster qui ne doivent s'executer que sur un seul noeud.

### Configuration du cluster

Le clustering est **active par defaut** sans aucune configuration. Les instances sur le meme reseau se decouvrent automatiquement via UDP multicast (`239.42.42.42:19847`).

Pour les environnements ou le multicast n'est pas disponible (certains VPC cloud), configurez une URL interne avec equilibrage de charge comme solution de repli :

```json
{
  "Cluster": {
    "InternalUrl": "http://authagonal-auth.svc.cluster.local:8080",
    "Secret": "shared-secret-here"
  }
}
```

Pour desactiver entierement le clustering (limitation du debit locale uniquement) :

```json
{
  "Cluster": {
    "Enabled": false
  }
}
```

Consultez la page [Configuration](configuration) pour tous les parametres du cluster.

### Degradation gracieuse

- **Aucun pair trouve** — fonctionne comme un limiteur de debit local uniquement (chaque instance applique sa propre limite)
- **Pair injoignable** — le dernier etat connu de ce pair est toujours utilise ; les pairs obsoletes sont supprimes apres 30 secondes
- **Multicast indisponible** — la decouverte echoue silencieusement ; le gossip se replie sur `InternalUrl` si configure

## Recommandations de mise a l'echelle

**Mise a l'echelle verticale** — augmentez le CPU et la memoire sur une seule instance. Utile pour gerer plus de requetes simultanees par instance.

**Mise a l'echelle horizontale** — executez plusieurs instances derriere un equilibreur de charge. Aucune session persistante ni cache partage requis. Chaque instance est entierement independante.

**Mise a l'echelle a zero** — Authagonal prend en charge les deploiements avec mise a l'echelle a zero (par exemple, Azure Container Apps avec `minReplicas: 0`). La premiere requete apres une periode d'inactivite aura un demarrage a froid de quelques secondes pendant que le runtime .NET s'initialise et que les cles de signature sont chargees depuis le stockage.
