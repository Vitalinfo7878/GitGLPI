#!/usr/bin/env bash
# Étape 2 : connexion Microsoft (SSO Entra ID) de l'application.
# À lancer dans Azure Cloud Shell (Bash) après 1-creer-ressources.sh, avec un compte administrateur Entra ID
# (Administrateur d'application ou Administrateur général : le script donne un consentement administrateur).
#
# Ce que fait le script (rejouable) :
#   - inscription d'application « Parc informatique Vitalrest » : adresse de retour, rôles Gestionnaire / Valideur,
#     adresse mail dans le jeton, consentement administrateur (aucune fenêtre d'accord pour les salariés)
#   - secret client enregistré dans l'application Container Apps (jamais affiché)
#   - attribution des rôles aux comptes listés dans parametres.sh
#   - affiche les commandes Exchange Online pour autoriser l'envoi de mails depuis une seule boîte

set -euo pipefail
cd "$(dirname "$0")"
source ./parametres.sh

az extension add --name containerapp --upgrade --only-show-errors -y >/dev/null
verifier_abonnement
az containerapp show -g "$GROUPE" -n "$APPLICATION" -o none 2>/dev/null || erreur "lancez d'abord 1-creer-ressources.sh."

GRAPH="00000003-0000-0000-c000-000000000000"
# Identifiants fixes des rôles d'application (ne pas modifier : ils sont référencés par les attributions)
ROLE_GESTIONNAIRE_ID="5d6e2c1a-8b4f-4c6e-9a51-3f0c2b7d9e10"
ROLE_VALIDEUR_ID="a4b19e73-2f6d-4e8a-b0c5-7d3e1f9a6c22"

etape "Récapitulatif"
info "Inscription d'application : $NOM_APPLI_ENTRA (comptes de votre organisation uniquement)"
info "Adresse de retour         : https://$ADRESSE_APP/signin-oidc"
info "Gestionnaires             : ${GESTIONNAIRES[*]:-(aucun, à compléter dans parametres.sh)}"
info "Valideurs                 : ${VALIDEURS[*]:-(aucun)}"
confirmer "Configurer la connexion Microsoft ?"

# ---------------------------------------------------------------------------
etape "1. Inscription d'application"
FICHIER_ROLES=$(mktemp); FICHIER_REVENDICATIONS=$(mktemp)
trap 'rm -f "$FICHIER_ROLES" "$FICHIER_REVENDICATIONS"' EXIT
cat > "$FICHIER_ROLES" <<EOF
[
  { "allowedMemberTypes": ["User"], "id": "$ROLE_GESTIONNAIRE_ID", "isEnabled": true, "value": "Gestionnaire",
    "displayName": "Gestionnaire", "description": "Service informatique : gestion du parc, des mouvements et des demandes." },
  { "allowedMemberTypes": ["User"], "id": "$ROLE_VALIDEUR_ID", "isEnabled": true, "value": "Valideur",
    "displayName": "Valideur", "description": "Validation ou refus des demandes informatiques." }
]
EOF
# Adresse mail ajoutée au jeton (en plus de l'identifiant de connexion)
cat > "$FICHIER_REVENDICATIONS" <<EOF
{ "idToken": [ { "name": "email", "essential": false } ], "accessToken": [], "saml2Token": [] }
EOF

ID_APPLI=$(az ad app list --display-name "$NOM_APPLI_ENTRA" --query "[0].appId" -o tsv)
if [[ -z "$ID_APPLI" ]]; then
    ID_APPLI=$(az ad app create --display-name "$NOM_APPLI_ENTRA" --sign-in-audience AzureADMyOrg \
        --web-redirect-uris "https://$ADRESSE_APP/signin-oidc" --enable-id-token-issuance true \
        --app-roles @"$FICHIER_ROLES" --optional-claims @"$FICHIER_REVENDICATIONS" --query appId -o tsv)
    info "inscription créée ($ID_APPLI)"
else
    az ad app update --id "$ID_APPLI" --web-redirect-uris "https://$ADRESSE_APP/signin-oidc" --enable-id-token-issuance true \
        --app-roles @"$FICHIER_ROLES" --optional-claims @"$FICHIER_REVENDICATIONS"
    info "inscription existante mise à jour ($ID_APPLI)"
fi
# Adresses de retour et de déconnexion : via l'API Graph (« az ad app update --set web.… » ne fonctionne pas).
# Le bloc « web » est envoyé en entier, car Graph remplace tout le bloc à chaque modification.
az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications(appId='$ID_APPLI')" \
    --headers "Content-Type=application/json" -o none --body "{\"web\": {
        \"redirectUris\": [\"https://$ADRESSE_APP/signin-oidc\"],
        \"logoutUrl\": \"https://$ADRESSE_APP/signout-callback-oidc\",
        \"implicitGrantSettings\": {\"enableIdTokenIssuance\": true, \"enableAccessTokenIssuance\": false}
    }}"
info "adresses de retour et de déconnexion enregistrées"

# Application d'entreprise (principal de service) : ouverte à tous les comptes du tenant
ID_SP=$(az ad sp list --filter "appId eq '$ID_APPLI'" --query "[0].id" -o tsv)
[[ -n "$ID_SP" ]] || ID_SP=$(reessayer az ad sp create --id "$ID_APPLI" --query id -o tsv)

# Autorisations de connexion (openid, profile, email, offline_access, User.Read) + consentement administrateur :
# les salariés se connectent directement, sans écran d'accord.
az ad app permission add --id "$ID_APPLI" --api "$GRAPH" --api-permissions \
    37f7f235-527c-4136-accd-4a02d197296e=Scope 14dad69e-099b-42c9-810b-d002981feec1=Scope \
    64a6cdd6-aab1-4aaf-94b8-3cc8405e90d0=Scope 7427e0e9-2fba-42fe-b0c0-848c9e6a8182=Scope \
    e1fe6dd8-ba31-4d61-89e7-88639da4683d=Scope --only-show-errors
# Consentement administrateur via l'API Graph (« az ad app permission admin-consent » ne fonctionne pas dans Cloud Shell) :
# autorisation donnée pour tous les comptes de l'organisation (consentType AllPrincipals).
PORTEES="openid profile email offline_access User.Read"
ID_SP_GRAPH=$(az ad sp show --id "$GRAPH" --query id -o tsv)
ID_ACCORD=$(az rest --method GET -o tsv --query "value[0].id" \
    --uri "https://graph.microsoft.com/v1.0/oauth2PermissionGrants?\$filter=clientId eq '$ID_SP' and consentType eq 'AllPrincipals' and resourceId eq '$ID_SP_GRAPH'")
if [[ -n "$ID_ACCORD" ]]; then
    az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/oauth2PermissionGrants/$ID_ACCORD" \
        --headers "Content-Type=application/json" --body "{\"scope\": \"$PORTEES\"}" -o none
else
    reessayer az rest --method POST --uri "https://graph.microsoft.com/v1.0/oauth2PermissionGrants" \
        --headers "Content-Type=application/json" -o none \
        --body "{\"clientId\": \"$ID_SP\", \"consentType\": \"AllPrincipals\", \"resourceId\": \"$ID_SP_GRAPH\", \"scope\": \"$PORTEES\"}" \
        || erreur "consentement impossible. À faire dans le portail : Entra ID > Inscriptions d'applications > $NOM_APPLI_ENTRA >
    Autorisations d'API > « Accorder un consentement d'administrateur », puis relancer ce script."
fi
info "consentement administrateur accordé ($PORTEES)"

# ---------------------------------------------------------------------------
etape "2. Secret client et réglages de l'application"
SECRET=$(az ad app credential reset --id "$ID_APPLI" --append --display-name "conteneur-$(date +%Y%m%d)" --years 2 --query password -o tsv --only-show-errors)
az containerapp secret set -g "$GROUPE" -n "$APPLICATION" --secrets "azuread-secret=$SECRET" -o none
unset SECRET
az containerapp update -g "$GROUPE" -n "$APPLICATION" -o none --set-env-vars \
    "AzureAd__Instance=https://login.microsoftonline.com/" \
    "AzureAd__TenantId=$ID_TENANT" \
    "AzureAd__ClientId=$ID_APPLI" \
    "AzureAd__ClientSecret=secretref:azuread-secret" \
    "Messagerie__UrlApplication=https://$ADRESSE_APP"
info "secret valable 2 ans, enregistré dans l'application (non affiché)"

# ---------------------------------------------------------------------------
etape "3. Attribution des rôles"
attribuer() { # adresse, id du rôle, libellé
    local id_utilisateur
    id_utilisateur=$(az ad user show --id "$1" --query id -o tsv 2>/dev/null \
        || az ad user list --filter "mail eq '$1'" --query "[0].id" -o tsv)
    if [[ -z "$id_utilisateur" ]]; then info "⚠ compte introuvable : $1"; return; fi
    if [[ -n "$(az rest --method GET --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$ID_SP/appRoleAssignedTo?\$top=999" \
            --query "value[?principalId=='$id_utilisateur' && appRoleId=='$2'].id" -o tsv)" ]]; then
        info "$1 : déjà $3"; return
    fi
    az rest --method POST --uri "https://graph.microsoft.com/v1.0/servicePrincipals/$ID_SP/appRoleAssignedTo" \
        --headers "Content-Type=application/json" \
        --body "{\"principalId\":\"$id_utilisateur\",\"resourceId\":\"$ID_SP\",\"appRoleId\":\"$2\"}" -o none
    info "$1 : $3"
}
for a in "${GESTIONNAIRES[@]}"; do attribuer "$a" "$ROLE_GESTIONNAIRE_ID" "Gestionnaire"; done
for a in "${VALIDEURS[@]}"; do attribuer "$a" "$ROLE_VALIDEUR_ID" "Valideur"; done
[[ ${#GESTIONNAIRES[@]} -gt 0 ]] || info "⚠ aucun gestionnaire : ajoutez l'équipe informatique dans parametres.sh et relancez,
      ou attribuez le rôle dans Entra ID > Applications d'entreprise > $NOM_APPLI_ENTRA > Utilisateurs et groupes."

# ---------------------------------------------------------------------------
etape "Terminé"
cat <<EOF

  Connexion SSO active sur https://$ADRESSE_APP (après le premier déploiement GitHub).
  Tous les comptes de l'organisation peuvent se connecter ; seuls les rôles donnent accès à la gestion.

  Envoi des mails (dernière étape, dans Exchange Online PowerShell — Install-Module ExchangeOnlineManagement) :
  l'application ne pourra envoyer QUE depuis $BOITE_ENVOI.

      Connect-ExchangeOnline
      New-ServicePrincipal -AppId $ID_APPLI -ObjectId $ID_SP -DisplayName "$NOM_APPLI_ENTRA"
      New-ManagementScope -Name "Parc informatique - envoi" -RecipientRestrictionFilter "PrimarySmtpAddress -eq '$BOITE_ENVOI'"
      New-ManagementRoleAssignment -App $ID_APPLI -Role "Application Mail.Send" -CustomResourceScope "Parc informatique - envoi"

  Puis activer l'envoi réel (les mails sont simulés jusque-là) :

      az containerapp update -g $GROUPE -n $APPLICATION --set-env-vars Messagerie__Mode=Graph \\
          Messagerie__Expediteur=$BOITE_ENVOI Messagerie__BoiteInformatique=$BOITE_ENVOI
EOF
