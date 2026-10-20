#!/usr/bin/env bash
# Étape 0 (une seule fois) : prépare un espace séparé pour le Parc informatique et les droits minimaux pour l'installer.
# À lancer dans Azure Cloud Shell (Bash) avec un compte qui peut attribuer des rôles Azure
# (par exemple un administrateur général qui a activé « Gestion de l'accès pour les ressources Azure »).
#
# Ce que fait le script :
#   - crée le groupe de ressources « rg-parc-informatique » et vous en donne la propriété
#     (un droit Contributeur temporaire sur l'abonnement sert UNIQUEMENT à créer ce groupe, et il est retiré aussitôt)
#   - crée deux rôles personnalisés limités et vous les attribue sur les seules ressources Vital Pilot concernées :
#       * « rattachement environnement » sur cae-vital-pilot : y rattacher une application, sans pouvoir modifier l'environnement
#       * « création de base PostgreSQL » sur pg-vital-pilot-94eed4 : créer une base, sans pouvoir modifier ni supprimer quoi que ce soit
# Aucune ressource Vital Pilot n'est créée, modifiée ou supprimée.

set -euo pipefail
cd "$(dirname "$0")"
source ./parametres.sh

az extension add --name containerapp --upgrade --only-show-errors -y >/dev/null
verifier_abonnement sans-groupe
ABONNEMENT_SCOPE="/subscriptions/$ID_ABONNEMENT"
GROUPE_SCOPE="$ABONNEMENT_SCOPE/resourceGroups/$GROUPE"
GROUPE_VP_SCOPE="$ABONNEMENT_SCOPE/resourceGroups/$GROUPE_VP"

etape "Vos rôles Azure actuels"
az role assignment list --assignee "$MOI" --all --include-inherited --query "[].{Role:roleDefinitionName, Portee:scope}" -o table

etape "Récapitulatif"
info "Nouveau groupe de ressources : $GROUPE ($REGION), dont vous serez propriétaire"
info "Rôle « $ROLE_RATTACHEMENT » sur l'environnement $ENVIRONNEMENT"
info "Rôle « $ROLE_BASE_PG » sur le serveur $SERVEUR_PG"
info "Aucune ressource Vital Pilot ne sera modifiée."
confirmer "Préparer les accès ?"

# ---------------------------------------------------------------------------
etape "1. Groupe de ressources $GROUPE"
if az group show -n "$GROUPE" -o none 2>/dev/null; then
    info "le groupe existe déjà"
else
    # Si vous êtes déjà Contributeur de l'abonnement, on ne touche pas à ce droit ; sinon on l'ajoute puis on le retire.
    if [[ -z "$(az role assignment list --assignee "$MOI" --role "Contributor" --scope "$ABONNEMENT_SCOPE" --query '[0].id' -o tsv)" ]]; then
        info "droit Contributeur TEMPORAIRE sur l'abonnement, pour créer le groupe uniquement"
        retirer_temporaire() {
            az role assignment delete --assignee "$MOI" --role "Contributor" --scope "$ABONNEMENT_SCOPE" -o none 2>/dev/null || true
            info "droit Contributeur temporaire retiré"
        }
        trap retirer_temporaire EXIT
        attribuer_role "$MOI" User "Contributor" "$ABONNEMENT_SCOPE"
    fi
    reessayer az group create -n "$GROUPE" -l "$REGION" --tags "${ETIQUETTES[@]}" -o none \
        || erreur "création du groupe impossible : réessayez dans quelques minutes."
    info "groupe créé"
fi
reessayer attribuer_role "$MOI" User "Owner" "$GROUPE_SCOPE"
if declare -F retirer_temporaire >/dev/null; then
    retirer_temporaire
    trap - EXIT
fi

# ---------------------------------------------------------------------------
etape "2. Rôles personnalisés (droits minimaux sur Vital Pilot)"
creer_role() { # nom, description, actions (JSON)
    if [[ -n "$(az role definition list --name "$1" --query '[0].id' -o tsv)" ]]; then
        info "rôle « $1 » déjà défini"
        return
    fi
    az role definition create -o none --role-definition "{
        \"Name\": \"$1\",
        \"Description\": \"$2\",
        \"Actions\": $3,
        \"AssignableScopes\": [\"$GROUPE_VP_SCOPE\"]
    }"
    info "rôle « $1 » défini"
}
creer_role "$ROLE_RATTACHEMENT" \
    "Parc informatique : rattacher une application à l'environnement Container Apps, sans pouvoir le modifier." \
    '["Microsoft.App/managedEnvironments/read", "Microsoft.App/managedEnvironments/join/action"]'
creer_role "$ROLE_BASE_PG" \
    "Parc informatique : créer une base sur le serveur PostgreSQL, sans pouvoir modifier ni supprimer le serveur ou ses bases." \
    '["Microsoft.DBforPostgreSQL/flexibleServers/read", "Microsoft.DBforPostgreSQL/flexibleServers/databases/read", "Microsoft.DBforPostgreSQL/flexibleServers/databases/write"]'

info "attente de la prise en compte des rôles…"
reessayer attribuer_role "$MOI" User "$ROLE_RATTACHEMENT" "$ID_ENV"
reessayer attribuer_role "$MOI" User "$ROLE_BASE_PG" "$ID_SERVEUR_PG"

etape "Terminé"
cat <<EOF

  Vous êtes propriétaire de $GROUPE et pouvez y installer le Parc informatique.
  Sur Vital Pilot, vous ne pouvez que : rattacher une application à $ENVIRONNEMENT, créer une base sur $SERVEUR_PG.

  Patientez 2 à 3 minutes que les droits s'appliquent, puis : bash 1-creer-ressources.sh
EOF
