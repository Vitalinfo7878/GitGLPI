# Séparer le Parc informatique de Vital Pilot

Aujourd'hui, le Parc informatique a ses propres ressources (`rg-parc-informatique`), mais il **utilise deux ressources de Vital Pilot** :
l'environnement Container Apps `cae-vital-pilot` (où tourne l'application) et le serveur PostgreSQL `pg-vital-pilot-94eed4`
(où se trouve la base `parcinfo`). Ces scripts lui donnent son propre hébergement : ensuite, plus rien n'est partagé.

| | Avant | Après |
|---|---|---|
| Environnement | `cae-vital-pilot` (Vital Pilot) | `cae-parc-informatique`, dans un réseau privé `vnet-parc-informatique` |
| Application | `ca-parc-informatique` | `ca-informatique` (une application ne peut pas changer d'environnement) |
| Base de données | `parcinfo` sur `pg-vital-pilot-94eed4` | `parcinfo` sur `pg-parc-94eed4`, joignable uniquement depuis le réseau privé du Parc |
| Adresse | https://informatique.vitalrest.com | inchangée |

Inchangés : données (copiées à l'identique et vérifiées table par table), clé de chiffrement et secrets (recopiés sans être affichés),
sessions des utilisateurs, connexion Microsoft, mails, annuaire, Exchange, déploiement automatique GitHub (il bascule tout seul
sur la nouvelle application).

**Coût supplémentaire** : environ 20 à 25 €/mois (serveur PostgreSQL Burstable B1ms 32 Go + accès privé). L'environnement n'a pas de coût fixe.

Prérequis : être **propriétaire** de l'abonnement VITAL-PILOT. Toutes les commandes se lancent dans **Azure Cloud Shell (Bash)** :

```bash
az account set --subscription "VITAL-PILOT"
cd ~ && rm -rf GitGLPI && git clone https://github.com/Vitalinfo7878/GitGLPI.git
```

## Étape 1 – Préparation (sans coupure, ≈ 15 min)

```bash
bash GitGLPI/deploiement/azure/separation/1-preparer.sh
```

Crée le réseau privé, l'environnement, le serveur PostgreSQL (vide) et son accès privé. L'application actuelle n'est pas touchée.
À la fin, le script affiche la **nouvelle valeur du CNAME** à utiliser le jour de la bascule.

**La veille de la bascule** : chez l'hébergeur DNS de vitalrest.com, réduire la durée de vie (TTL) du CNAME `informatique` à 300 s.

## Étape 2 – Bascule (≈ 30 min de coupure, de préférence le soir)

Prévenir les utilisateurs, ne rien publier dans GitHub pendant la bascule, puis :

```bash
bash GitGLPI/deploiement/azure/separation/2-basculer.sh
```

1. arrêt de l'application actuelle (**début de la coupure**) ;
2. copie de la base et vérification du nombre de lignes de chaque table ;
3. création de la nouvelle application (même image, mêmes réglages, mêmes secrets) ;
4. le script affiche le **CNAME à modifier** chez l'hébergeur DNS et attend qu'il soit visible, puis émet le certificat HTTPS
   (**fin de la coupure**) ;
5. fermeture de l'accès public du nouveau serveur PostgreSQL.

Si Cloud Shell se ferme ou si une étape échoue : **relancer le même script**, il reprend là où il en était.

Ensuite, vérifier : connexion, matériel, tickets, demandes, *Administration > Courriels* (envoi des mails), *Indicateurs*.

## En cas de problème : retour arrière

```bash
bash GitGLPI/deploiement/azure/separation/retour-arriere.sh
```

Supprime la nouvelle application, redémarre l'ancienne sur l'ancienne base et lui rend l'adresse (le script indique le CNAME à remettre).
Les saisies faites depuis la bascule sont perdues. Possible tant que l'étape 3 n'est pas faite.

## Étape 3 – Nettoyage chez Vital Pilot (une à deux semaines plus tard, avec l'accord de Thomas)

```bash
bash GitGLPI/deploiement/azure/separation/3-nettoyer-vital-pilot.sh
```

Supprime l'ancienne application, le certificat du Parc dans l'environnement Vital Pilot, la base `parcinfo` du serveur Vital Pilot
et les deux rôles personnalisés « Parc informatique - … ». **Irréversible.** Le compte PostgreSQL `parcinfo` du serveur Vital Pilot
n'est supprimé que si le mot de passe administrateur est fourni (voir l'en-tête du script).

## Après la séparation

| Besoin | Où |
|---|---|
| Journaux | Portail > `rg-parc-informatique` > `ca-informatique` > *Flux de journaux* (conservés 30 jours dans `log-parc-informatique`) |
| Sauvegardes de la base | Portail > `pg-parc-94eed4` > *Sauvegarde et restauration* (automatiques, 7 jours) |
| Mot de passe administrateur PostgreSQL | Conservé nulle part : en cas de besoin, le redéfinir dans le portail (`pg-parc-94eed4` > *Réinitialiser le mot de passe*). Sans effet sur l'application, qui utilise son propre compte `parcinfo`. |
| Renouveler le secret SSO, régler les mails | Scripts `../2-configurer-sso.sh` etc. : ils visent automatiquement la nouvelle application |
