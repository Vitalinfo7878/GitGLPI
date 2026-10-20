# Parc informatique Vitalrest

Application web de suivi du parc informatique : inventaire des PC, PC portables, écrans et imprimantes, mouvements de matériel (achat, attribution, restitution, réparation, rebut), fiches PDF numérotées, confirmation de réception par le destinataire, et demandes informatiques des salariés avec circuit de validation.

Elle remplace le classeur « Informatique Vitalrest – Suivi du stock » et reprend ses données à l'import.

## Fonctionnalités

| Écran | Rôle |
|---|---|
| **Tableau de bord** | Chiffres clés, réceptions en attente (avec relance), PC à changer, réparations, rebuts en attente |
| **Inventaire** | Tout le matériel : recherche instantanée, filtres (parc sites / personnes, type, statut, région), sélection multiple → mouvement, export Excel |
| **Fiche matériel** | Toutes les informations, modification, mot de passe de session (affichage journalisé), historique complet des mouvements et des fiches |
| **Nouveau mouvement** | Attribution, restitution, envoi / retour de réparation, renvoi fournisseur, vol / perte, attente de rebut, mise au rebut — sur un ou plusieurs matériels |
| **Réception d'achat** | Entrée en stock de matériel neuf, numéro interne attribué automatiquement (PC, PO, E, I + 4 chiffres) |
| **Fiches** | Fiches FIA (attribution), FIR (restitution), FIS (achat), FIRE (mise au rebut) : PDF stockés, régénération, relance |
| **Sites / Personnes** | Les deux parcs : cuisines (non présentes dans Entra ID) et salariés ; fusion des doublons de personnes |
| **Faire une demande / Mes demandes** | Pour tous les salariés : formulaire de demande (mêmes questions et obligations que le formulaire Microsoft Forms) et suivi de ses demandes |
| **Demandes à valider / traiter** | Pour le valideur et l'informatique : validation ou refus (avec motif), puis suivi du traitement (priorité, avancement, commentaire) |
| **Mon matériel** | Pour chaque salarié : son matériel, ses réceptions à confirmer, le mot de passe de session après confirmation |
| **Import Excel** | Chargement initial depuis le classeur, avec aperçu et rapport d'anomalies |
| **Courriels / Journal** | Trace des courriels envoyés et des actions sensibles |

### Cycle de vie du matériel

```
            ACHAT                      ATTRIBUTION
Fournisseur ─────▶ EN STOCK ─────────────────────────▶ ATTRIBUÉ (site ou personne)
                    ▲   │    ▲                              │
                    │   │    └──────── RESTITUTION ─────────┘
       RETOUR RÉPAR.│   │ ENVOI RÉPARATION / RENVOI FOURNISSEUR
                    │   ▼
                 EN RÉPARATION / RENVOYÉ FOURNISSEUR      (+ VOLÉ / PERDU)
                        │
          ──▶ REBUT EN ATTENTE ──(fiche FIRE, signature DAF)──▶ REBUTÉ
```

Les règles (statuts de départ autorisés, statut d'arrivée, fiche générée) sont centralisées dans `Domain/RegleMouvement.cs`.

### Confirmation de réception

1. L'informatique enregistre une **attribution** et coche « Demander au destinataire de confirmer la réception ».
2. Le destinataire (personne, ou boîte mail du site) reçoit un courriel avec un lien.
3. Il se connecte avec son compte Microsoft et clique sur **Je confirme la réception**.
4. La date, le nom et l'adresse de la personne qui confirme sont enregistrés, la fiche d'attribution PDF est régénérée avec la mention « Réception confirmée électroniquement », et le **mot de passe de session** devient visible pour lui.
5. L'informatique est prévenue (si `Messagerie:BoiteInformatique` est renseigné) ; tant que ce n'est pas fait, la réception apparaît « en attente » sur le tableau de bord avec un bouton **Relancer**.

Le mot de passe n'est plus imprimé sur les fiches PDF : il n'est visible que dans l'application, par l'informatique et par le détenteur après confirmation, et chaque affichage est journalisé.

### Demandes informatiques

1. Un salarié remplit **Faire une demande** (nom et adresse pré-remplis depuis son compte Microsoft). Les questions obligatoires sont les mêmes que dans le formulaire Forms : nom, objet (plusieurs choix + « Autre »), « concerne » (un choix + « Autre »), raison, adresse de livraison, mail et téléphone.
2. La demande reçoit un numéro (à la suite de la feuille ETAT DEMANDES : 592, 593…). Chaque adresse de `Demandes:Destinataires` reçoit un mail récapitulatif avec deux boutons **Valider** / **Refuser** ; le demandeur reçoit un accusé de réception.
3. Le bouton ouvre la demande dans l'application (connexion Microsoft) avec le choix présélectionné ; la décision est confirmée d'un clic, le **motif est obligatoire en cas de refus**. Les boutons du mail ne modifient rien à eux seuls : les antivirus de messagerie qui « cliquent » sur les liens ne peuvent donc pas valider une demande par erreur.
4. Seuls les **valideurs** (`Demandes:Valideurs` ou rôle Entra ID `Valideur`) et l'équipe informatique (rôle `Gestionnaire`) peuvent décider. Si deux personnes décident en même temps, seule la première décision est retenue.
5. Le demandeur est prévenu de la décision ; les autres destinataires aussi (pour savoir que la demande est traitée). Une demande validée passe dans l'onglet **À traiter** de l'informatique ; quand elle est marquée **Réalisée**, le demandeur reçoit un mail.

## Démarrer en local

Prérequis : [SDK .NET 10](https://dotnet.microsoft.com/download).

```bash
cd src/ParcInfo.Web
dotnet run
```

Ouvrir http://localhost:5080. Tant qu'Entra ID n'est pas configuré, une **connexion de démonstration** permet de choisir son profil (service informatique ou utilisateur, avec n'importe quelle adresse mail). La base est un fichier SQLite `parc.db` créé automatiquement.

Pour repartir de zéro : arrêter l'application et supprimer `parc.db`. **Après une mise à jour qui modifie la base** (nouvelle migration), il faut aussi supprimer `parc.db` en local puis réimporter l'Excel : la base SQLite de développement n'est pas migrée automatiquement (la base Azure SQL, elle, l'est).

### Import du fichier Excel

Menu **Administration → Import Excel**, choisir le classeur `.xlsm`. Un aperçu affiche le nombre de matériels, mouvements, sites et personnes, et la liste des anomalies corrigées ou à vérifier (numéros en double, statuts contradictoires, doublons probables de personnes…). Rien n'est enregistré avant d'avoir cliqué sur **Importer**. L'import n'est possible que sur une base vide.

Ce que fait l'import :

- **Harmonisation** : « Pc Portable » / « PC portable » → *PC portable*, « W11 » / « 11 » → *Windows 11*, « DR  CENTRE-OUEST » → *DR CENTRE-OUEST*, etc.
- **Deux parcs** : la colonne « SITES / UTILISATEURS » est classée en *site* (586, S012, G124, 440-1…) ou *personne* (Prénom NOM).
- **Statut** : déduit de « SERVICE / LIEU » (STOCK, REBUT, Voler, Retour Amazon, réparation…) et du dernier mouvement.
- **Sites** : téléphone, responsable et adresse mail (profil Microsoft le plus fréquent sur les PC du site) repris des lignes d'inventaire — **à vérifier**.
- **Personnes** : sans adresse mail à l'import (la colonne « Profil Microsoft » est celle du PC, pas forcément de la personne) — à compléter dans **Personnes** pour activer la confirmation de réception et « Mon matériel ».
- **Mouvements** : « SORTIE INITIALE » et « ENTREE (SI) » (mise en place de l'inventaire) restent distincts des sorties / entrées ordinaires. La numérotation continue à la suite du dernier numéro de l'Excel.

> Le classeur contient des données personnelles et des mots de passe : il ne doit jamais être ajouté au dépôt Git (`.gitignore` exclut `*.xlsx` / `*.xlsm`).

### Tests

```bash
dotnet test
# Avec le vrai classeur (non versionné) :
PARC_EXCEL=/chemin/vers/Suivi_du_stock.xlsm dotnet test
```

## Déploiement sur Azure

Architecture : **Azure App Service (Linux, .NET 10)** + **Azure SQL Database** + **Entra ID** pour la connexion + **Microsoft Graph** pour l'envoi des courriels.

### 1. Ressources Azure

1. **Groupe de ressources** (ex. `rg-parc-informatique`).
2. **Azure SQL Database** : un serveur logique + une base `ParcInfo` (le niveau *Serverless* ou *Basic* suffit). Autoriser l'accès depuis les services Azure.
3. **App Service** : plan Linux B1 (ou supérieur), pile **.NET 10**.
   - *Configuration → Paramètres généraux* : activer **Web sockets** et **Affinité ARR** (l'interface Blazor Server garde une connexion temps réel).
   - *Toujours actif* : activé.

### 2. Inscription d'application Entra ID

Dans le portail Entra ID → *Inscriptions d'applications* → *Nouvelle inscription* :

- Nom : « Parc informatique » ; comptes de cet annuaire uniquement.
- URI de redirection (Web) : `https://<votre-app>.azurewebsites.net/signin-oidc`
- *Authentification* : URL de déconnexion `https://<votre-app>.azurewebsites.net/signout-callback-oidc`, cocher **Jetons d'ID**.
- *Certificats et secrets* : créer un **secret client** (noter la valeur).
- *Rôles d'application* : créer un rôle **Gestionnaire** (valeur `Gestionnaire`) et un rôle **Valideur** (valeur `Valideur`), membres autorisés : utilisateurs/groupes. Dans *Applications d'entreprise → Parc informatique → Utilisateurs et groupes*, affecter *Gestionnaire* à l'équipe informatique (idéalement via un groupe) et *Valideur* aux personnes qui valident les demandes.
- *Autorisations d'API* : **Microsoft Graph → Autorisations d'application → Mail.Send**, puis *Accorder le consentement administrateur*.

Tous les salariés du tenant peuvent se connecter (pour « Mon matériel » et la confirmation de réception) ; seuls les membres du rôle *Gestionnaire* accèdent à la gestion du parc.

> En attendant de configurer les rôles, on peut lister les adresses dans `Authentification:Gestionnaires` (équipe informatique) et `Demandes:Valideurs` (valideurs).

### 3. Limiter l'envoi de courriels à une seule boîte

La permission `Mail.Send` d'application permet par défaut d'envoyer depuis **n'importe quelle** boîte du tenant. Restreignez-la à la boîte d'envoi (ex. `informatique@vitalrest.com`) avec le **RBAC pour les applications** d'Exchange Online (méthode recommandée par Microsoft, qui remplace les *Application Access Policies*) :

```powershell
Connect-ExchangeOnline
# Principal de service correspondant à l'application (AppId = ClientId, ObjectId = celui de l'application d'entreprise)
New-ServicePrincipal -AppId <ClientId> -ObjectId <ObjectId-application-entreprise> -DisplayName "Parc informatique"
# Étendue limitée à la boîte d'envoi
New-ManagementScope -Name "Parc - boite informatique" -RecipientRestrictionFilter "PrimarySmtpAddress -eq 'informatique@vitalrest.com'"
New-ManagementRoleAssignment -App <ClientId> -Role "Application Mail.Send" -CustomResourceScope "Parc - boite informatique"
```

Dans ce cas, retirez la permission `Mail.Send` accordée dans Entra ID (le RBAC Exchange la remplace).

### 4. Paramètres de l'App Service

*Configuration → Paramètres d'application* (les `:` deviennent `__`) :

| Paramètre | Valeur |
|---|---|
| `BaseDeDonnees__Fournisseur` | `SqlServer` |
| `ConnectionStrings__Parc` | chaîne de connexion Azure SQL (de préférence dans *Chaînes de connexion*, type SQLAzure) |
| `AzureAd__TenantId` | ID de l'annuaire (tenant) |
| `AzureAd__ClientId` | ID d'application (client) |
| `AzureAd__ClientSecret` | secret client (idéalement une référence Key Vault : `@Microsoft.KeyVault(SecretUri=…)`) |
| `Messagerie__Mode` | `Graph` |
| `Messagerie__Expediteur` | `informatique@vitalrest.com` |
| `Messagerie__BoiteInformatique` | boîte à prévenir quand une réception est confirmée |
| `Messagerie__UrlApplication` | `https://<votre-app>.azurewebsites.net` |
| `Demandes__Destinataires__0`, `__1`… | boîtes qui reçoivent les nouvelles demandes avec les boutons Valider / Refuser (par défaut `informatique@vitalrest.com` et `thomas.verchere@groupe-vitalrest.com`) |
| `Demandes__Valideurs__0`… | comptes autorisés à valider, en plus des gestionnaires |
| `Demandes__PremierNumero` | premier numéro de demande (592, à la suite de la feuille ETAT DEMANDES) |

Au démarrage, l'application applique automatiquement les migrations de base de données (`Data/Migrations`).

### 5. Publication

```bash
cd src/ParcInfo.Web
dotnet publish -c Release -o ../../publication
# puis déployer le dossier « publication » (zip deploy, VS Code / Visual Studio, ou le workflow GitHub Actions fourni)
```

Un workflow GitHub Actions est fourni dans `.github/workflows/deploiement-azure.yml` : il compile, lance les tests et déploie à chaque push sur `main`. Il nécessite deux éléments dans les paramètres du dépôt GitHub : la variable `AZURE_WEBAPP_NAME` (nom de l'App Service) et le secret `AZURE_WEBAPP_PUBLISH_PROFILE` (profil de publication téléchargé depuis le portail Azure).

## Structure du code

```
src/ParcInfo.Web
├── Domain/          Entités (Materiel, Mouvement, Fiche, Site, Personne…) et règles de mouvement
├── Data/            DbContext EF Core, migrations SQL Server, initialisation
├── Import/          Lecture du classeur Excel, harmonisation, import
├── Services/        Mouvements, fiches PDF, courriels, utilisateur connecté, exports / téléchargements
├── Components/      Interface Blazor (MudBlazor) : pages et composants
└── Resources/       Logo et polices embarquées pour les PDF
tests/ParcInfo.Tests Tests unitaires (règles, import, PDF, confirmation de réception)
```

Bibliothèques : ASP.NET Core / Blazor Server (.NET 10), EF Core (SQL Server / SQLite), MudBlazor, Microsoft.Identity.Web, Microsoft Graph, ClosedXML (Excel), PDFsharp-MigraDoc 6.2.4 (MIT — ne pas passer en 6.5+, sous licence AGPL ou commerciale).

## Évolutions prévues

- Tickets d'assistance (pannes, problèmes) en plus des demandes de matériel.
- Reprise de l'historique de la feuille « ETAT DEMANDES » (n° 1 à 591).
- Lien entre une demande validée et le mouvement d'attribution qui la réalise.
- Recherche des personnes directement dans Entra ID lors d'une attribution.
- Création de comptes et suivi des arrivées / départs, en lien avec Entra ID.
