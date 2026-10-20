# shellcheck disable=SC2034  # variables utilisées par les scripts qui chargent ce fichier
# Paramètres communs aux scripts de déploiement (chargés par « source »).

# --- Ressources existantes du projet Vital Pilot : utilisées, JAMAIS modifiées ---
ABONNEMENT="VITAL-PILOT"
GROUPE_VP="rg-vital-pilot-prod"
ENVIRONNEMENT="cae-vital-pilot"          # notre application s'y rattache (droit de rattachement uniquement)
SERVEUR_PG="pg-vital-pilot-94eed4"       # on y ajoute seulement la base « parcinfo »

# --- Ressources du Parc informatique : toutes dans notre propre groupe ---
GROUPE="rg-parc-informatique"
REGION="francecentral"
APPLICATION="ca-parc-informatique"
IDENTITE_APP="id-parc-informatique"      # identité de l'application (lecture des images)
IDENTITE_GITHUB="id-parc-github"         # identité du déploiement automatique GitHub
BASE="parcinfo"                          # base PostgreSQL, et compte du même nom
IMAGE="parc-informatique"                # image de l'application dans notre registre
TACHE_INIT="caj-parc-init-bdd"           # tâche temporaire de création du compte PostgreSQL
# Outil psql : image officielle PostgreSQL, copiée une fois dans notre registre (sources essayées dans l'ordre)
SOURCES_PSQL=("public.ecr.aws/docker/library/postgres:17-alpine" "docker.io/library/postgres:17-alpine")
IMAGE_PSQL="outils/postgres:17-alpine"
ETIQUETTES=("projet=parc-informatique")

# Rôles personnalisés : droits minimaux sur les ressources Vital Pilot
ROLE_RATTACHEMENT="Parc informatique - rattachement environnement"
ROLE_BASE_PG="Parc informatique - creation de base PostgreSQL"

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
# Noms sous lesquels GitHub se présente à Azure : format classique, et format avec les identifiants numériques
# du compte et du dépôt (utilisé par GitHub pour ce dépôt)
SUJETS_GITHUB=(
    "repo:$DEPOT_GITHUB:environment:$ENVIRONNEMENT_GITHUB"
    "repo:Vitalinfo7878@344823626/GitGLPI@1425810904:environment:$ENVIRONNEMENT_GITHUB"
)

# --- Fonctions utilitaires ---
etape() { printf '\n\033[1;34m==> %s\033[0m\n' "$*"; }
info()  { printf '    %s\n' "$*"; }
erreur() { printf '\n\033[1;31mERREUR : %s\033[0m\n' "$*" >&2; exit 1; }

confirmer() {
    local reponse
    read -r -p "$1 [o/N] " reponse
    [[ "$reponse" =~ ^[oOyY]$ ]] || { echo "Abandon, rien n'a été modifié après ce point."; exit 0; }
}

# Charge les identifiants communs. Avec « sans-groupe », ne vérifie pas que notre groupe existe (script 0).
verifier_abonnement() {
    az account set --subscription "$ABONNEMENT" || erreur "abonnement $ABONNEMENT inaccessible."
    az group show -n "$GROUPE_VP" -o none 2>/dev/null || erreur "groupe $GROUPE_VP introuvable (droits de lecture insuffisants ?)."
    if [[ "${1:-}" != "sans-groupe" ]]; then
        az group show -n "$GROUPE" -o none 2>/dev/null || erreur "groupe $GROUPE introuvable : lancez d'abord 0-preparer-acces.sh."
    fi
    ID_ABONNEMENT=$(az account show --query id -o tsv)
    ID_TENANT=$(az account show --query tenantId -o tsv)
    MOI=$(az ad signed-in-user show --query id -o tsv)
    # Nom de registre unique au niveau mondial, dérivé de l'abonnement (comme ceux de Vital Pilot)
    REGISTRE="acrparcinfo${ID_ABONNEMENT:0:6}"
    SERVEUR_REGISTRE="$REGISTRE.azurecr.io"
    ID_ENV=$(az containerapp env show -g "$GROUPE_VP" -n "$ENVIRONNEMENT" --query id -o tsv)
    ID_SERVEUR_PG=$(az postgres flexible-server show -g "$GROUPE_VP" -n "$SERVEUR_PG" --query id -o tsv)
    DOMAINE_ENV=$(az containerapp env show -g "$GROUPE_VP" -n "$ENVIRONNEMENT" --query properties.defaultDomain -o tsv)
    ADRESSE_APP="$APPLICATION.$DOMAINE_ENV"
}

# Attribue un rôle s'il ne l'est pas déjà. Paramètres : id du principal, type (User / ServicePrincipal), rôle, portée.
attribuer_role() {
    if [[ -z "$(az role assignment list --assignee "$1" --role "$3" --scope "$4" --query '[0].id' -o tsv 2>/dev/null)" ]]; then
        # « || return 1 » : sans lui, un échec passerait inaperçu quand la fonction est appelée par « reessayer »
        az role assignment create --assignee-object-id "$1" --assignee-principal-type "$2" --role "$3" --scope "$4" -o none || return 1
        info "rôle « $3 » attribué"
    else
        info "rôle « $3 » déjà attribué"
    fi
}

# Réessaie une commande pendant quelques minutes (les nouveaux droits Azure mettent du temps à s'appliquer).
reessayer() {
    local essai
    for essai in $(seq 1 20); do
        "$@" && return 0
        # Sur la sortie d'erreur : la commande réessayée peut être utilisée dans $(…) pour récupérer son résultat
        info "droits pas encore actifs, nouvel essai dans 15 s ($essai/20)…" >&2
        sleep 15
    done
    return 1
}
