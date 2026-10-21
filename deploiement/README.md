# Mise en ligne sur Azure

Le Parc informatique s'installe **dans son propre groupe de ressources** `rg-parc-informatique`, dans l'abonnement `VITAL-PILOT`.
Les ressources du projet Vital Pilot ne sont **ni créées, ni modifiées, ni supprimées** :

| Ressource Vital Pilot | Ce que le Parc informatique en fait | Droit utilisé |
|---|---|---|
| Environnement Container Apps `cae-vital-pilot` | Notre application s'y rattache et y tourne (conteneur séparé, adresse séparée) | rôle personnalisé « rattachement environnement » : rattacher, sans pouvoir modifier l'environnement |
| Serveur PostgreSQL `pg-vital-pilot-94eed4` | Une base `parcinfo` + un compte `parcinfo` qui n'a accès qu'à elle | rôle personnalisé « création de base PostgreSQL » : créer une base, sans pouvoir modifier ni supprimer |
| Coffre `kv-vital-pilot-94eed4` | Lecture du seul secret `pg-admin-password`, le temps de l'installation | accès temporaire, retiré à la fin |

Tout le reste est dans `rg-parc-informatique` et porte l'étiquette `projet=parc-informatique` :
application `ca-parc-informatique`, registre d'images `acrparcinfo94eed4`, identités `id-parc-informatique` (l'application)
et `id-parc-github` (le déploiement automatique). S'y ajoute l'inscription d'application Entra ID « Parc informatique Vitalrest ».

Adresse de l'application : `https://ca-parc-informatique.salmonmushroom-95a18e16.francecentral.azurecontainerapps.io`

> Prévenez votre collègue responsable de Vital Pilot avant de commencer.

## Prérequis

- Pouvoir **attribuer des rôles Azure** sur l'abonnement : par exemple un administrateur général qui a activé
  *Microsoft Entra ID > Propriétés > Gestion de l'accès pour les ressources Azure* (à remettre sur « Non » à la fin).
- Pour l'étape 2 : un rôle **Administrateur d'application** (ou Administrateur général) dans Entra ID.
- Pour l'étape 4 : un rôle **Administrateur Exchange**.

Toutes les commandes se lancent dans **Azure Cloud Shell**, en **Bash** (portail Azure, icône `>_`, « No storage account required »).

## Étape 0 – Espace et droits (une seule fois, ≈ 5 min)

```bash
cd ~ && rm -rf GitGLPI && git clone https://github.com/Vitalinfo7878/GitGLPI.git
cd GitGLPI/deploiement/azure
bash 0-preparer-acces.sh
```

Le script affiche vos rôles actuels, puis :

1. crée le groupe `rg-parc-informatique` et vous en donne la propriété. Si vous n'êtes pas déjà Contributeur de l'abonnement,
   ce droit vous est donné **temporairement**, uniquement pour créer le groupe, puis retiré aussitôt ;
2. crée les deux rôles personnalisés limités décrits ci-dessus et vous les attribue sur l'environnement et sur le serveur PostgreSQL.

Attendez ensuite 2 à 3 minutes que les droits s'appliquent.

## Étape 1 – Ressources (≈ 10 min)

Le mot de passe administrateur PostgreSQL est dans le coffre de Vital Pilot (secret `pg-admin-password`).
Autorisez-vous à lire **ce seul secret**, attendez 2 à 3 minutes, puis lancez le script :

```bash
MOI=$(az ad signed-in-user show --query id -o tsv)
COFFRE=$(az keyvault show -n kv-vital-pilot-94eed4 --query id -o tsv)
az role assignment create --role "Key Vault Secrets User" --assignee-object-id $MOI --assignee-principal-type User \
  --scope "$COFFRE/secrets/pg-admin-password" -o none && echo "Accès accordé"
```

```bash
MDP_ADMIN_PG=$(az keyvault secret show --vault-name kv-vital-pilot-94eed4 -n pg-admin-password --query value -o tsv) bash 1-creer-ressources.sh
```

Le mot de passe passe du coffre au script sans jamais s'afficher. Le script crée le registre, les identités, la base `parcinfo`
et son compte (via une tâche temporaire exécutée dans le réseau privé, puis supprimée), l'application et l'identité de déploiement.
À la fin, il affiche **trois valeurs à copier dans GitHub** (étape 3).

Ensuite, **refermez l'accès au coffre** (et le droit de lecture des noms de secrets, s'il a été ajouté) :

```bash
MOI=$(az ad signed-in-user show --query id -o tsv)
COFFRE=$(az keyvault show -n kv-vital-pilot-94eed4 --query id -o tsv)
az role assignment delete --assignee $MOI --role "Key Vault Secrets User" --scope "$COFFRE/secrets/pg-admin-password"
az role assignment delete --assignee $MOI --role "Key Vault Reader" --scope "$COFFRE"
```

Ne réinitialisez jamais le mot de passe administrateur PostgreSQL : si les applications Vital Pilot utilisent ce compte,
elles cesseraient de fonctionner.

## Étape 2 – Connexion Microsoft / SSO (≈ 5 min)

1. Ouvrir `parametres.sh` (`code parametres.sh` dans Cloud Shell) et compléter `GESTIONNAIRES` avec les adresses de l'équipe informatique.
   `VALIDEURS` contient déjà `thomas.verchere@groupe-vitalrest.com`.
2. Lancer `bash 2-configurer-sso.sh`.

Le script crée l'inscription d'application (adresse de retour, rôles *Gestionnaire* et *Valideur*, adresse mail dans le jeton),
donne le **consentement administrateur** (aucun écran d'accord pour les salariés), enregistre le secret dans l'application
et attribue les rôles.

- **Tous les comptes de l'organisation** se connectent avec leur session Microsoft 365 (pas de nouveau mot de passe) et accèdent à
  « Mon matériel », « Faire une demande », « Mes demandes », « Créer un ticket » et « Mes tickets ».
- **Gestionnaire** : toute la gestion du parc. **Valideur** : validation des demandes.
  Gestion ensuite dans *Entra ID > Applications d'entreprise > Parc informatique Vitalrest > Utilisateurs et groupes*.
- Le mode démo est désactivé.

## Étape 3 – Déploiement automatique depuis GitHub (≈ 5 min)

Dans GitHub, dépôt **Vitalinfo7878/GitGLPI** :

1. *Settings > Environments > New environment* : créer `production`.
2. *Settings > Secrets and variables > Actions > onglet **Variables*** : créer `AZURE_CLIENT_ID`, `AZURE_TENANT_ID`, `AZURE_SUBSCRIPTION_ID`
   avec les valeurs de fin d'étape 1 (ce ne sont pas des secrets : la connexion à Azure se fait sans mot de passe).
3. *Actions > Tests et déploiement Azure > Run workflow*.

Le workflow teste, construit l'image, la publie dans **notre** registre, met à jour **notre** application et vérifie `/sante`.
Au premier démarrage, l'application crée ses tables dans la base `parcinfo`.

## Étape 3 bis – Adresse personnalisée (facultatif, ≈ 30 min dont l'attente DNS)

Pour ouvrir l'application sur `https://informatique.vitalrest.com` (réglage `DOMAINE_PERSONNALISE` de `parametres.sh`) :

1. `bash 3-domaine-personnalise.sh` : affiche les deux enregistrements à créer chez l'hébergeur DNS de vitalrest.com
   (un **CNAME** vers l'adresse de l'application et un **TXT** `asuid.…` qui prouve la propriété du domaine), puis s'arrête.
2. Créer ces deux enregistrements, attendre leur propagation (quelques minutes à 1 h).
3. Relancer `bash 3-domaine-personnalise.sh` : certificat HTTPS gratuit (géré et renouvelé par Azure), adresse de retour
   de la connexion Microsoft et liens des mails mis à jour. L'ancienne adresse continue de fonctionner.

Seul ajout côté Vital Pilot : le certificat, rangé dans l'environnement Container Apps (Azure l'impose), sans effet
sur les autres applications.

## Étape 4 – Envoi des mails (≈ 5 min)

Tant que cette étape n'est pas faite, les mails sont **simulés** (*Administration > Courriels envoyés*).
La fin de l'étape 2 affiche les commandes exactes : Exchange Online PowerShell pour autoriser l'envoi **uniquement** depuis
`informatique@vitalrest.com`, puis passage de `Messagerie__Mode` à `Graph`.

## Étape 4 bis – Clé de chiffrement des mots de passe (≈ 2 min)

Les mots de passe de session des PC sont chiffrés en base (AES-256) avec une clé gardée **hors de la base**, dans les
secrets de l'application. Sans clé, ils restent en clair (avertissement dans les journaux). Dans Cloud Shell (Bash) :

```bash
CLE=$(openssl rand -base64 32); echo "$CLE"   # à copier dans votre coffre de mots de passe : sans elle, mots de passe illisibles
az containerapp secret set -g rg-parc-informatique -n ca-parc-informatique --secrets "chiffrement-cle=$CLE" -o none
az containerapp update -g rg-parc-informatique -n ca-parc-informatique --set-env-vars "Chiffrement__Cle=secretref:chiffrement-cle" -o none
unset CLE
```

Au redémarrage, les mots de passe déjà en base sont chiffrés automatiquement. Ne jamais changer ni supprimer cette clé.

## Étape 5 – Premier chargement

Se connecter avec un compte Gestionnaire, *Administration > Import Excel*, puis compléter les adresses mail des personnes.

## Après l'installation

- Remettre *Gestion de l'accès pour les ressources Azure* sur **Non** si vous l'aviez activé pour l'étape 0.
- Facultatif : retirer votre rôle « création de base PostgreSQL » (inutile une fois la base créée) :
  `az role assignment delete --assignee $(az ad signed-in-user show --query id -o tsv) --role "Parc informatique - creation de base PostgreSQL" --scope $(az postgres flexible-server show -g rg-vital-pilot-prod -n pg-vital-pilot-94eed4 --query id -o tsv)`

## Coût

Registre d'images Basic ≈ 4,50 €/mois + le conteneur de l'application (0,5 processeur, 1 Go, toujours allumé) : quelques euros par mois
selon l'usage et le quota gratuit mensuel de Container Apps (partagé avec Vital Pilot). Le serveur PostgreSQL est celui de Vital Pilot.
Suivi : *Gestion des coûts > Analyse des coûts*, groupe `rg-parc-informatique`.

## Exploitation

| Besoin | Où |
|---|---|
| Journaux | Portail > `rg-parc-informatique` > `ca-parc-informatique` > *Flux de journaux* |
| Redémarrer / revenir à une version précédente | `ca-parc-informatique` > *Révisions et réplicas* |
| Changer un réglage | *Conteneurs > Variables d'environnement*, ou relancer les scripts |
| Renouveler le secret SSO (tous les 2 ans) | `bash 2-configurer-sso.sh` |
| Changer le mot de passe de la base | `bash 1-creer-ressources.sh` (nouveau mot de passe généré ; nécessite l'accès au secret, étape 1) |
| Sauvegardes de la base | Celles du serveur PostgreSQL Vital Pilot |

## Désinstallation

1. Supprimer le groupe `rg-parc-informatique` (application, registre, identités).
2. Sur le serveur PostgreSQL : `az postgres flexible-server db delete -g rg-vital-pilot-prod -s pg-vital-pilot-94eed4 -n parcinfo`,
   puis `DROP ROLE parcinfo;` avec le compte administrateur.
3. Supprimer les rôles personnalisés « Parc informatique - … » (et leurs attributions) et l'inscription d'application « Parc informatique Vitalrest ».
