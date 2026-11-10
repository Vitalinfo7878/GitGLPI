# shellcheck disable=SC2034  # variables utilisées par les scripts qui chargent ce fichier
# Paramètres de la séparation du Parc informatique (chargés par « source » après ../parametres.sh).
# Après la séparation, plus rien n'est partagé avec Vital Pilot : tout est dans le groupe $GROUPE.

# Ces scripts manipulent l'ancienne application ($APPLICATION) : on la garde comme cible même après la bascule
GARDER_ANCIENNE=1
ENV_PARC="$ENV_SEPARE"                      # environnement Container Apps du Parc
APP_PARC="$APPLICATION_SEPAREE"             # nouvelle application (une application ne peut pas changer d'environnement)
JOURNAUX="log-parc-informatique"            # espace de journaux de l'environnement
RESEAU="vnet-parc-informatique"             # réseau privé du Parc
PLAGE_RESEAU="10.60.0.0/16"
SOUS_RESEAU_ENV="snet-environnement"        # environnement Container Apps (délégué)
PLAGE_ENV="10.60.0.0/23"
SOUS_RESEAU_PG="snet-postgres"              # point de terminaison privé du serveur PostgreSQL
PLAGE_PG="10.60.2.0/27"
ADMIN_PG_PARC="parcadmin"                   # administrateur du nouveau serveur PostgreSQL
POINT_PRIVE_PG="pe-pg-parc-informatique"
ZONE_DNS_PG="privatelink.postgres.database.azure.com"
TACHE_MIGRATION="caj-parc-migration"        # tâche temporaire de copie de la base
REGLE_MIGRATION="migration-temporaire"      # règle de pare-feu temporaire (copie de la base)

# Nom du serveur PostgreSQL : unique au niveau mondial, dérivé de l'abonnement (à appeler après verifier_abonnement)
noms_separation() {
    SERVEUR_PG_PARC="pg-parc-${ID_ABONNEMENT:0:6}"
}

# Mot de passe aléatoire (majuscule, minuscule et chiffre garantis, exigence Azure), jamais affiché
mot_de_passe() { printf 'Pa1%s' "$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | head -c 32)"; }
