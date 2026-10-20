# shellcheck disable=SC2034  # variables utilisées par les scripts qui chargent ce fichier
# Paramètres communs aux scripts de déploiement (chargés par « source »).
# Les ressources existantes de Vital Pilot sont seulement utilisées, jamais modifiées.

# --- Ressources existantes (projet Vital Pilot) ---
ABONNEMENT="VITAL-PILOT"
GROUPE="rg-vital-pilot-prod"
ENVIRONNEMENT="cae-vital-pilot"
REGISTRE="acrvitalpilot94eed4"
SERVEUR_PG="pg-vital-pilot-94eed4"

# --- Ressources du projet Parc informatique (créées par les scripts) ---
APPLICATION="ca-parc-informatique"
IDENTITE_APP="id-parc-informatique"     # identité de l'application (lecture des images)
IDENTITE_GITHUB="id-parc-github"        # identité du déploiement automatique GitHub
BASE="parcinfo"                         # base PostgreSQL, et compte du même nom
IMAGE="parc-informatique"               # dossier dans le registre d'images
TACHE_INIT="caj-parc-init-bdd"          # tâche temporaire de création du compte PostgreSQL
ROLE_RATTACHEMENT="Parc informatique - rattachement a l'environnement"
ETIQUETTES=("projet=parc-informatique")

# --- Connexion Microsoft (SSO) ---
NOM_APPLI_ENTRA="Parc informatique Vitalrest"
# Comptes à qui donner les rôles (adresses de connexion Microsoft). À compléter avant de lancer 2-configurer-sso.sh.
GESTIONNAIRES=()   # équipe informatique, ex. ("prenom.nom@vitalrest.com" "autre.personne@vitalrest.com")
VALIDEURS=("thomas.verchere@groupe-vitalrest.com")

# --- Messagerie ---
BOITE_ENVOI="informatique@vitalrest.com"

# --- GitHub ---
DEPOT_GITHUB="Vitalinfo7878/GitGLPI"
ENVIRONNEMENT_GITHUB="production"

# --- Fonctions utilitaires ---
etape() { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
info()  { printf '    %s\n' "$*"; }
erreur() { printf '\n\033[1;31mERREUR : %s\033[0m\n' "$*" >&2; exit 1; }

confirmer() {
    local reponse
    read -r -p "$1 [o/N] " reponse
    [[ "$reponse" =~ ^[oOyY]$ ]] || { echo "Abandon, rien n'a été modifié après ce point."; exit 0; }
}

verifier_abonnement() {
    az account set --subscription "$ABONNEMENT" || erreur "abonnement $ABONNEMENT inaccessible."
    az group show -n "$GROUPE" -o none 2>/dev/null || erreur "groupe $GROUPE introuvable (droits insuffisants ?)."
    ID_ABONNEMENT=$(az account show --query id -o tsv)
    ID_TENANT=$(az account show --query tenantId -o tsv)
    SERVEUR_REGISTRE="$REGISTRE.azurecr.io"
    DOMAINE_ENV=$(az containerapp env show -g "$GROUPE" -n "$ENVIRONNEMENT" --query properties.defaultDomain -o tsv)
    ADRESSE_APP="$APPLICATION.$DOMAINE_ENV"
    export ID_ABONNEMENT ID_TENANT SERVEUR_REGISTRE DOMAINE_ENV ADRESSE_APP
}
