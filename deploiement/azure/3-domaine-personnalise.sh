#!/usr/bin/env bash
# Étape 3 (facultative) : adresse personnalisée, ex. https://informatique.vitalrest.com (réglée dans parametres.sh).
# À lancer dans Azure Cloud Shell (Bash), après avoir créé chez l'hébergeur DNS de vitalrest.com les deux
# enregistrements affichés par le script (il s'arrête en les affichant tant qu'ils ne sont pas visibles).
#
# Ce que fait le script (rejouable) :
#   - vérifie les enregistrements DNS (CNAME vers l'application, TXT de preuve de propriété)
#   - certificat HTTPS gratuit géré et renouvelé par Azure, lié à l'application
#   - connexion Microsoft : ajoute l'adresse de retour du nouveau domaine (l'ancienne adresse reste valable)
#   - liens des mails : nouvelle adresse
# Sur Vital Pilot, seul ajout : le certificat, rangé dans l'environnement Container Apps (exigence d'Azure),
# sans effet sur les autres applications. Le rôle personnalisé de rattachement reçoit pour cela le droit de gérer
# les certificats gérés de l'environnement.

set -euo pipefail
cd "$(dirname "$0")"
source ./parametres.sh

az extension add --name containerapp --upgrade --only-show-errors -y >/dev/null
verifier_abonnement
[[ -n "${DOMAINE_PERSONNALISE:-}" ]] || erreur "renseignez DOMAINE_PERSONNALISE dans parametres.sh."
D="$DOMAINE_PERSONNALISE"
ID_APPLI=$(az ad app list --display-name "$NOM_APPLI_ENTRA" --query "[0].appId" -o tsv)
[[ -n "$ID_APPLI" ]] || erreur "inscription « $NOM_APPLI_ENTRA » introuvable : lancez d'abord 2-configurer-sso.sh."
VERIFICATION=$(az containerapp show -g "$GROUPE" -n "$APPLICATION" --query properties.customDomainVerificationId -o tsv)

# ---------------------------------------------------------------------------
etape "1. Enregistrements DNS"
resoudre() { # type, nom → valeurs (sans guillemets ni point final)
    if command -v dig >/dev/null; then dig +short "$1" "$2"
    else nslookup -type="$1" "$2" 2>/dev/null | sed -n 's/.*\(canonical name\|text\) = //p'
    fi | tr -d '"' | sed 's/\.$//'
}
CNAME_OK=false; TXT_OK=false
[[ "$(resoudre CNAME "$D")" == "$ADRESSE_APP" ]] && CNAME_OK=true
resoudre TXT "asuid.$D" | grep -qx "$VERIFICATION" && TXT_OK=true
if ! $CNAME_OK || ! $TXT_OK; then
    etat() { $1 && echo "✔ vu" || echo "✘ pas encore visible"; }
    SOUS="${D%%.*}"
    echo
    echo "  À créer chez l'hébergeur DNS de ${D#*.}, puis relancer ce script (propagation : quelques minutes à 1 h) :"
    echo
    printf '      %-6s %-24s %-s\n' Type Nom Valeur
    printf '      %-6s %-24s %-s   %s\n' CNAME "$SOUS" "$ADRESSE_APP" "$(etat $CNAME_OK)"
    printf '      %-6s %-24s %-s   %s\n' TXT "asuid.$SOUS" "$VERIFICATION" "$(etat $TXT_OK)"
    echo
    exit 0
fi
info "CNAME et TXT visibles"

etape "Récapitulatif"
info "Nouvelle adresse : https://$D (l'adresse actuelle https://$ADRESSE_APP reste valable)"
info "Vital Pilot      : un certificat ajouté dans $ENVIRONNEMENT, rien d'autre"
confirmer "Mettre en place l'adresse https://$D ?"

# ---------------------------------------------------------------------------
etape "2. Droit de gérer le certificat dans l'environnement"
DROITS='["Microsoft.App/managedEnvironments/managedCertificates/read","Microsoft.App/managedEnvironments/managedCertificates/write"]'
DEFINITION=$(mktemp); trap 'rm -f "$DEFINITION"' EXIT
az role definition list --name "$ROLE_RATTACHEMENT" --query "[0]" -o json \
    | jq --argjson d "$DROITS" '.permissions[0].actions = ((.permissions[0].actions + $d) | unique)' > "$DEFINITION"
[[ -s "$DEFINITION" && "$(jq -r .roleName "$DEFINITION")" == "$ROLE_RATTACHEMENT" ]] || erreur "rôle « $ROLE_RATTACHEMENT » introuvable : lancez 0-preparer-acces.sh."
az role definition update --role-definition @"$DEFINITION" -o none
info "rôle « $ROLE_RATTACHEMENT » : certificats gérés autorisés"

# ---------------------------------------------------------------------------
etape "3. Adresse et certificat HTTPS"
if [[ -z "$(az containerapp hostname list -g "$GROUPE" -n "$APPLICATION" --query "[?name=='$D'].name" -o tsv)" ]]; then
    az containerapp hostname add -g "$GROUPE" -n "$APPLICATION" --hostname "$D" -o none
fi
ID_ENV_COMPLET=$(az containerapp env show -g "$GROUPE_VP" -n "$ENVIRONNEMENT" --query id -o tsv)
info "émission du certificat (jusqu'à 20 minutes)…"
reessayer az containerapp hostname bind -g "$GROUPE" -n "$APPLICATION" --hostname "$D" \
    --environment "$ID_ENV_COMPLET" --validation-method CNAME -o none \
    || erreur "liaison impossible : vérifiez les enregistrements DNS, puis relancez le script."
info "https://$D lié à l'application"

# ---------------------------------------------------------------------------
etape "4. Connexion Microsoft et liens des mails"
az rest --method PATCH --uri "https://graph.microsoft.com/v1.0/applications(appId='$ID_APPLI')" \
    --headers "Content-Type=application/json" -o none --body "{\"web\": {
        \"redirectUris\": [\"https://$D/signin-oidc\", \"https://$ADRESSE_APP/signin-oidc\"],
        \"logoutUrl\": \"https://$D/signout-callback-oidc\",
        \"implicitGrantSettings\": {\"enableIdTokenIssuance\": true, \"enableAccessTokenIssuance\": false}
    }}"
info "adresses de retour : https://$D et https://$ADRESSE_APP"
az containerapp update -g "$GROUPE" -n "$APPLICATION" -o none --set-env-vars "Messagerie__UrlApplication=https://$D"
info "liens des mails : https://$D"

etape "Terminé"
cat <<FIN

  L'application répond sur https://$D (et toujours sur l'ancienne adresse).
  Le certificat est renouvelé automatiquement par Azure tant que le CNAME reste en place.
FIN
