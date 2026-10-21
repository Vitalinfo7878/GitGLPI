# Parc informatique Vitalrest

Application web de suivi du parc informatique : inventaire des PC, PC portables, écrans et imprimantes, mouvements de matériel (achat, entrée dans le SI, sortie / attribution, mise au rebut), fiches PDF numérotées, demandes informatiques des salariés avec circuit de validation, et tickets d'assistance avec analyse.

Elle remplace le classeur « Informatique Vitalrest – Suivi du stock » et reprend ses données à l'import.

## Fonctionnalités

| Écran | Rôle |
|---|---|
| **Tableau de bord** | Chiffres clés, demandes et tickets à traiter, PC à changer |
| **Inventaire** | Tout le matériel : recherche instantanée, filtres (catégorie PC Site / PC Enrôlé, type, statut, région), sélection multiple → mouvement ou catégorie PC, export Excel |
| **Fiche matériel** | Toutes les informations, modification, mot de passe de session (affichage journalisé), historique complet des mouvements, des fiches et des tickets |
| **Nouveau mouvement** | Achat, entrée (dans le SI), sortie (attribution), mise au rebut — sur un ou plusieurs matériels |
| **Réception d'achat** | Entrée en stock de matériel neuf, numéro interne attribué automatiquement (PC, PO, E, I + 4 chiffres) |
| **Fiches** | Fiches FIA (attribution), FIR (restitution), FIS (achat), FIRE (mise au rebut) : PDF stockés, régénération |
| **Faire une demande / Mes demandes** | Pour tous les salariés : formulaire de demande (mêmes questions et obligations que le formulaire Microsoft Forms) et suivi de ses demandes |
| **Demandes à valider / traiter** | Pour le valideur et l'informatique : validation ou refus (avec motif), puis suivi du traitement (priorité, avancement, commentaire) |
| **Créer un ticket / Mes tickets** | Pour tous les salariés : déclaration d'un problème, échanges avec l'informatique, confirmation de la résolution avec une note |
| **Tickets à traiter / Analyse** | Pour l'informatique et les administrateurs des tickets : traitement, analyse (cause, solution, mode de résolution, temps passé) et statistiques |
| **Mon matériel** | Pour chaque salarié : le matériel qui lui est attribué |
| **Import Excel** | Chargement initial depuis le classeur, avec aperçu et rapport d'anomalies |
| **Courriels / Journal** | Trace des courriels envoyés et des actions sensibles |

### Cycle de vie du matériel

```
            ACHAT                 SORTIE (attribution)
Fournisseur ─────▶ EN STOCK ─────────────────────────▶ ATTRIBUÉ (site ou personne)
                    │    ▲                              │
                    │    └──── ENTRÉE (dans le SI) ─────┘
                    │                                   │
                    └───────────▶ MISE AU REBUT ◀───────┘   (fiche FIRE) ─▶ REBUTÉ
```

Les règles (statuts de départ autorisés, statut d'arrivée, fiche générée) sont centralisées dans `Domain/RegleMouvement.cs`.

### Mots de passe de session

Ils ne sont visibles que par l'informatique, dans la fiche du matériel (chaque affichage est journalisé), et ne sont pas imprimés sur les fiches PDF.
En base, ils sont chiffrés (AES-256-GCM) avec une clé conservée hors de la base (réglage `Chiffrement:Cle`, voir `deploiement/README.md`).

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

Pour repartir de zéro : arrêter l'application et supprimer `parc.db`. **Après une mise à jour qui modifie la base** (nouvelle migration), il faut aussi supprimer `parc.db` en local puis réimporter l'Excel : la base SQLite de développement n'est pas migrée automatiquement (la base PostgreSQL de production, elle, l'est).

### Import du fichier Excel

Menu **Administration → Import Excel**, choisir le classeur `.xlsm`. Un aperçu affiche le nombre de matériels, mouvements, sites et personnes, et la liste des anomalies corrigées ou à vérifier (numéros en double, statuts contradictoires, doublons probables de personnes…). Rien n'est enregistré avant d'avoir cliqué sur **Importer**. L'import n'est possible que sur une base vide.

Ce que fait l'import :

- **Harmonisation** : « Pc Portable » / « PC portable » → *PC portable*, « W11 » / « 11 » → *Windows 11*, « DR  CENTRE-OUEST » → *DR CENTRE-OUEST*, etc.
- **Deux parcs** : la colonne « SITES / UTILISATEURS » est classée en *site* (586, S012, G124, 440-1…) ou *personne* (Prénom NOM).
- **Statut** : déduit de « SERVICE / LIEU » (STOCK, REBUT, Voler, Retour Amazon, réparation…) et du dernier mouvement.
- **Sites** : téléphone, responsable et adresse mail (profil Microsoft le plus fréquent sur les PC du site) repris des lignes d'inventaire — **à vérifier**.
- **Personnes** : sans adresse mail à l'import (la colonne « Profil Microsoft » est celle du PC, pas forcément de la personne) ; « Mon matériel » ne fonctionne que pour les personnes créées avec leur adresse lors d'une attribution.
- **Catégorie PC** : déduite du détenteur à l'import (site → PC Site, personne → PC Enrôlé), modifiable sur la fiche ou par sélection dans l'inventaire.
- **Mouvements** : « SORTIE INITIALE » et « ENTREE (SI) » (mise en place de l'inventaire) restent distincts des sorties / entrées ordinaires. La numérotation continue à la suite du dernier numéro de l'Excel.

> Le classeur contient des données personnelles et des mots de passe : il ne doit jamais être ajouté au dépôt Git (`.gitignore` exclut `*.xlsx` / `*.xlsm`).

### Tests

```bash
dotnet test
# Avec le vrai classeur (non versionné) :
PARC_EXCEL=/chemin/vers/Suivi_du_stock.xlsm dotnet test
# Sur un vrai serveur PostgreSQL (une base temporaire est créée avec les migrations, puis supprimée) :
PARC_TEST_POSTGRES="Host=localhost;Username=postgres;Password=..." dotnet test
```

Le workflow GitHub exécute les tests sur SQLite et sur PostgreSQL 17 à chaque modification.

## Déploiement sur Azure

L'application a son propre groupe de ressources `rg-parc-informatique` et s'appuie sur l'infrastructure du projet Vital Pilot
sans la modifier :

- **Azure Container Apps** : l'application `ca-parc-informatique` (notre groupe) tourne dans l'environnement `cae-vital-pilot` ;
- **Azure Container Registry** : notre registre d'images `acrparcinfo94eed4` ;
- **Azure Database for PostgreSQL** : une base `parcinfo` dédiée sur le serveur `pg-vital-pilot-94eed4` ;
- **Entra ID** : connexion SSO des salariés avec leur compte Microsoft 365, rôles *Gestionnaire* et *Valideur* ;
- **Exchange Online / Microsoft Graph** : envoi des mails depuis `informatique@vitalrest.com` uniquement ;
- **GitHub Actions** : tests puis déploiement automatique à chaque modification de `main`, sans mot de passe (fédération d'identité).

**Guide pas à pas et scripts : [`deploiement/README.md`](deploiement/README.md).**

Points propres à l'hébergement en conteneur, déjà gérés par l'application :

- les clés de chiffrement des cookies (session SSO) sont stockées dans la base : un redémarrage ne déconnecte personne ;
- les en-têtes `X-Forwarded-*` de l'entrée HTTPS de Container Apps sont pris en compte (adresse de retour de la connexion Microsoft) ;
- la culture `fr-FR` et le fuseau `Europe/Paris` sont fixés dans l'image ;
- `/sante` répond `ok` quand l'application et sa base fonctionnent ;
- au démarrage, les migrations de base de données sont appliquées automatiquement.

## Structure du code

```
src/ParcInfo.Web
├── Domain/          Entités (Materiel, Mouvement, Fiche, Site, Personne…) et règles de mouvement
├── Data/            DbContext EF Core, migrations PostgreSQL, initialisation
├── Import/          Lecture du classeur Excel, harmonisation, import
├── Services/        Mouvements, fiches PDF, courriels, utilisateur connecté, exports / téléchargements
├── Components/      Interface Blazor (MudBlazor) : pages et composants
└── Resources/       Logo et polices embarquées pour les PDF
tests/ParcInfo.Tests Tests unitaires (règles, import, PDF, chiffrement, demandes, tickets)
deploiement/         Scripts de mise en place sur Azure et guide
Dockerfile           Image de l'application
```

Bibliothèques : ASP.NET Core / Blazor Server (.NET 10), EF Core (PostgreSQL / SQLite), MudBlazor, Microsoft.Identity.Web, Microsoft Graph, ClosedXML (Excel), PDFsharp-MigraDoc 6.2.4 (MIT — ne pas passer en 6.5+, sous licence AGPL ou commerciale).

## Évolutions prévues

- Tickets d'assistance (pannes, problèmes) en plus des demandes de matériel.
- Reprise de l'historique de la feuille « ETAT DEMANDES » (n° 1 à 591).
- Lien entre une demande validée et le mouvement d'attribution qui la réalise.
- Recherche des personnes directement dans Entra ID lors d'une attribution.
- Création de comptes et suivi des arrivées / départs, en lien avec Entra ID.
