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

Authagonal n'inclut pas de limitation de debit integree. La limitation du debit doit etre appliquee au niveau de l'infrastructure (equilibreur de charge, passerelle API ou proxy inverse) ou elle dispose d'une vue unifiee de tout le trafic entre les instances.

## Recommandations de mise a l'echelle

**Mise a l'echelle verticale** — augmentez le CPU et la memoire sur une seule instance. Utile pour gerer plus de requetes simultanees par instance.

**Mise a l'echelle horizontale** — executez plusieurs instances derriere un equilibreur de charge. Aucune session persistante ni cache partage requis. Chaque instance est entierement independante.

**Mise a l'echelle a zero** — Authagonal prend en charge les deploiements avec mise a l'echelle a zero (par exemple, Azure Container Apps avec `minReplicas: 0`). La premiere requete apres une periode d'inactivite aura un demarrage a froid de quelques secondes pendant que le runtime .NET s'initialise et que les cles de signature sont chargees depuis le stockage.
