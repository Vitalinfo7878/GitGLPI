#!/usr/bin/env bash
# Étape 4 (facultative) : droits d'écriture dans l'annuaire pour la page « Synchronisation organisation ».
# À lancer dans Azure Cloud Shell (Bash) avec un compte Administrateur général (consentement administrateur).
#
# Ce que fait le script (rejouable) : accorde à l'application deux autorisations d'application Microsoft Graph
#   - User.ReadWrite.All        : définir le responsable (manager) des comptes des sites
#   - GroupMember.ReadWrite.All : ajouter / retirer les comptes des sites des groupes REGION_SITE_…
# L'application ne fait ces modifications que depuis la page « Synchronisation organisation », réservée aux
# administrateurs principaux, après affichage des changements et confirmation. Chaque modification est notée dans le Journal.
# Pour retirer ces droits : Entra ID > Applications d'entreprise > « Parc informatique Vitalrest » > Autorisations.
set -euo pipefail
cd "$(dirname "$0")"
source ./parametres.sh
verifier_abonnement

GRAPH="00000003-0000-0000-c000-000000000000"
ID_APPLI=$(az ad app list --display-name "$NOM_APPLI_ENTRA" --query "[0].appId" -o tsv)
[[ -n "$ID_APPLI" ]] || erreur "inscription « $NOM_APPLI_ENTRA » introuvable : lancez d'abord 2-configurer-sso.sh."
ID_SP=$(az ad sp list --filter "appId eq '$ID_APPLI'" --query "[0].id" -o tsv)
ID_SP_GRAPH=$(az ad sp show --id "$GRAPH" --query id -o tsv)

etape "Récapitulatif"
info "Application : $NOM_APPLI_ENTRA ($ID_APPLI)"
info "Droits ajoutés : User.ReadWrite.All, GroupMember.ReadWrite.All (autorisations d'application)"
confirmer "Accorder ces droits d'écriture à l'application ?"

etape "Droits d'écriture dans l'annuaire"
for DROIT in User.ReadWrite.All GroupMember.ReadWrite.All; do
    ROLE=$(az ad sp show --id "$GRAPH" --query "appRoles[?value=='$DROIT'].id" -o tsv)
    az ad app permission add --id "$ID_APPLI" --api "$GRAPH" --api-permissions "$ROLE=Role" --only-show-errors 2>/dev/null || true
    if [[ -z "$(az rest --method GET --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$ID_SP/appRoleAssignments" \
            --query "value[?appRoleId=='$ROLE'].id" -o tsv)" ]]; then
        reessayer az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$ID_SP/appRoleAssignments" \
            --headers "Content-Type=application/json" -o none \
            --body "{\"principalId\": \"$ID_SP\", \"resourceId\": \"$ID_SP_GRAPH\", \"appRoleId\": \"$ROLE\"}"
    fi
    info "$DROIT accordé"
done

etape "Terminé"
info "Les droits sont pris en compte par l'application d'ici une heure au plus (renouvellement de son jeton)."
info "Les listes de diffusion Exchange et les groupes synchronisés depuis l'AD local restent à modifier à la main :"
info "la page les signale comme « non modifiables par l'application »."
