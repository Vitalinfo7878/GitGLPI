# Mise en ligne sur Azure

L'application s'installe **à côté** du projet Vital Pilot, dans l'abonnement `VITAL-PILOT`, sans rien modifier de l'existant :

| Ressource existante (Vital Pilot) | Ce que le Parc informatique y ajoute |
|---|---|
| Environnement Container Apps `cae-vital-pilot` | L'application `ca-parc-informatique` (son propre conteneur, sa propre adresse) |
| Serveur PostgreSQL `pg-vital-pilot-94eed4` | La base `parcinfo` et un compte `parcinfo` qui n'a accès qu'à elle |
| Registre d'images `acrvitalpilot94eed4` | Le dossier d'images `parc-informatique` (+ `outils/postgres`, utilisé une fois à l'installation) |

Ressources nouvelles : les identités `id-parc-informatique` (l'application) et `id-parc-github` (le déploiement), un rôle personnalisé
« Parc informatique - rattachement a l'environnement », et l'inscription d'application Entra ID « Parc informatique Vitalrest ».
Tout porte l'étiquette `projet=parc-informatique`.

Adresse de l'application : `https://ca-parc-informatique.salmonmushroom-95a18e16.francecentral.azurecontainerapps.io`

> **Prévenez votre collègue avant de commencer** : on ajoute une base sur son serveur PostgreSQL et une application dans son environnement.
> Il vous faudra aussi le **mot de passe administrateur du serveur PostgreSQL** (étape 1).

## Prérequis

- Un compte avec le rôle **Propriétaire** sur le groupe `rg-vital-pilot-prod` (ou Contributeur + Administrateur de l'accès utilisateur), pour créer les identités et leurs droits.
- Pour l'étape 2 : un rôle **Administrateur d'application** (ou Administrateur général) dans Entra ID.
- Pour l'étape 4 : un rôle **Administrateur Exchange**.

## Étape 1 – Ressources Azure (≈ 10 min)

Dans le portail Azure, ouvrir **Cloud Shell** (icône `>_`), choisir **Bash**, puis :

```bash
git clone https://github.com/Vitalinfo7878/GitGLPI.git
cd GitGLPI/deploiement/azure
bash 1-creer-ressources.sh
```

Le script affiche un récapitulatif et demande confirmation, puis le mot de passe administrateur PostgreSQL (saisie masquée, jamais enregistrée en clair).

**Où trouver le mot de passe administrateur PostgreSQL ?** Azure l'exige à la création du serveur ; s'il a été généré automatiquement
(Bicep, Terraform…), il est en général rangé dans le coffre `kv-vital-pilot-94eed4`. Le script peut le lire directement, sans l'afficher :

```bash
MDP_ADMIN_PG=$(az keyvault secret show --vault-name kv-vital-pilot-94eed4 -n <nom-du-secret> --query value -o tsv) bash 1-creer-ressources.sh
```

Ne réinitialisez pas ce mot de passe sans l'accord de votre collègue : si ses applications utilisent le compte administrateur, elles cesseraient de fonctionner.
À la fin, il affiche **trois valeurs à copier dans GitHub** (étape 3).

Si le dépôt est privé, Cloud Shell demandera de vous identifier sur GitHub : utilisez un *personal access token* en guise de mot de passe,
ou téléversez le dossier `deploiement/azure` dans Cloud Shell (bouton « Charger des fichiers »).

## Étape 2 – Connexion Microsoft / SSO (≈ 5 min)

1. Ouvrir `parametres.sh` (`code parametres.sh` dans Cloud Shell) et compléter la liste `GESTIONNAIRES` avec les adresses de l'équipe informatique.
   `VALIDEURS` contient déjà `thomas.verchere@groupe-vitalrest.com`.
2. Lancer :

```bash
bash 2-configurer-sso.sh
```

Le script crée l'inscription d'application (adresse de retour, rôles *Gestionnaire* et *Valideur*, adresse mail dans le jeton),
donne le **consentement administrateur** (les salariés n'ont aucun écran d'accord à valider), enregistre le secret dans l'application
et attribue les rôles.

Fonctionnement du SSO :

- **Tous les comptes de l'organisation** peuvent se connecter, avec leur session Microsoft 365 déjà ouverte (pas de nouveau mot de passe).
  Ils accèdent à « Mon matériel », « Faire une demande », « Mes demandes » et aux confirmations de réception.
- Le rôle **Gestionnaire** donne accès à toute la gestion du parc ; le rôle **Valideur** à la validation des demandes.
  Les rôles se gèrent ensuite dans *Entra ID > Applications d'entreprise > Parc informatique Vitalrest > Utilisateurs et groupes*
  (attribution à des groupes possible avec Entra ID P1, inclus dans Microsoft 365 Business Premium / E3).
- Les comptes des sites (boîtes partagées type `villaduparc@vitalrest.com`) doivent pouvoir se connecter pour confirmer une réception.
- Le mode démo est désactivé dès que l'inscription d'application est configurée.

## Étape 3 – Déploiement automatique depuis GitHub (≈ 5 min)

Dans GitHub, dépôt **Vitalinfo7878/GitGLPI** :

1. *Settings > Environments > New environment* : créer `production`
   (facultatif : y ajouter des *Required reviewers* pour valider chaque mise en production).
2. *Settings > Secrets and variables > Actions > onglet **Variables*** : créer `AZURE_CLIENT_ID`, `AZURE_TENANT_ID` et `AZURE_SUBSCRIPTION_ID`
   avec les valeurs affichées à la fin de l'étape 1. Ce ne sont pas des secrets : la connexion à Azure se fait sans mot de passe
   (fédération d'identité, réservée à l'environnement `production` de ce dépôt).
3. *Actions > Tests et déploiement Azure > Run workflow* (ou pousser une modification sur `main`).

Le workflow lance les tests (SQLite et PostgreSQL 17), construit l'image, la publie dans le registre, met à jour l'application
et vérifie qu'elle répond sur `/sante`. Au premier démarrage, l'application crée ses tables dans la base `parcinfo`.

## Étape 4 – Envoi des mails (≈ 5 min)

Tant que cette étape n'est pas faite, les mails sont **simulés** (visibles dans *Administration > Courriels envoyés*).
La fin de l'étape 2 affiche les commandes exactes, avec les bons identifiants :

1. Dans **Exchange Online PowerShell** : autoriser l'application à envoyer **uniquement** depuis `informatique@vitalrest.com`
   (RBAC pour les applications d'Exchange Online — aucune permission Mail.Send n'est accordée dans Entra ID).
2. Dans Cloud Shell : passer `Messagerie__Mode` à `Graph`.

## Étape 5 – Premier chargement

1. Se connecter avec un compte **Gestionnaire**.
2. *Administration > Import Excel* : charger le classeur « Suivi du stock » (une seule fois, base vide).
3. Compléter les adresses mail des personnes (*Parcs > Personnes*) et vérifier celles des sites.

## Coût

Le serveur PostgreSQL et le registre étant déjà payés, seul le conteneur de l'application s'ajoute (0,5 processeur, 1 Go de mémoire,
toujours allumé) : quelques euros par mois selon l'usage et le quota gratuit mensuel de Container Apps (partagé avec Vital Pilot).
Suivi : *Gestion des coûts > Analyse des coûts*, filtrer sur l'étiquette `projet = parc-informatique`.

## Exploitation

| Besoin | Où |
|---|---|
| Journaux de l'application | Portail > `ca-parc-informatique` > *Flux de journaux* |
| Redémarrer | Portail > `ca-parc-informatique` > *Révisions et réplicas* > *Redémarrer* |
| Revenir à la version précédente | *Révisions et réplicas* : réactiver la révision précédente |
| Changer un réglage | *Conteneurs > Variables d'environnement* (ou relancer les scripts) |
| Renouveler le secret SSO (tous les 2 ans) | Relancer `2-configurer-sso.sh` |
| Changer le mot de passe de la base | Relancer `1-creer-ressources.sh` (nouveau mot de passe généré et enregistré) |
| Sauvegardes de la base | Celles du serveur PostgreSQL (rétention définie par votre collègue) |

## Désinstallation

Supprimer `ca-parc-informatique`, `id-parc-informatique`, `id-parc-github`, le rôle personnalisé, la base `parcinfo`
(`az postgres flexible-server db delete`), le compte PostgreSQL `parcinfo` (`DROP ROLE parcinfo;`), le dossier d'images `parc-informatique`
et l'inscription d'application « Parc informatique Vitalrest ». Rien d'autre n'est à défaire.
