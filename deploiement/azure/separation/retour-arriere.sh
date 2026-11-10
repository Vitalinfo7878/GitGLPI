#!/usr/bin/env bash
# Séparation, retour arrière : remet en service l'application d'origine (hébergée chez Vital Pilot) et son ancienne base.
# À utiliser seulement si la bascule (2-basculer.sh) s'est mal passée, et avant le nettoyage (3-nettoyer-vital-pilot.sh).
#   bash GitGLPI/deploiement/azure/separation/retour-arriere.sh
#
# Ce que fait le script :
#   - supprime la nouvelle application (si elle existe) : les saisies faites depuis la bascule sont PERDUES
#     (une prochaine bascule recopiera l'ancienne base, à jour)
#   - redémarre l'ancienne application et lui rend l'adresse https://informatique.vitalrest.com
#     (il faut remettre le CNAME vers l'ancienne adresse ; le script l'affiche et attend)
# Le nouvel environnement et le nouveau serveur PostgreSQL restent en place pour une prochaine tentative.

set -euo pipefail
cd "$(dirname "$0")"
source ../parametres.sh
source ./parametres-separation.sh

az extension add --name containerapp --upgrade --only-show-errors -y >/dev/null
verifier_abonnement
D="$DOMAINE_PERSONNALISE"
SOUS="${D%%.*}"
az containerapp show -g "$GROUPE" -n "$APPLICATION" -o none 2>/dev/null \
    || erreur "l'application d'origine $APPLICATION n'existe plus (nettoyage déjà fait) : retour arrière impossible."

resoudre() {
    if command -v dig >/dev/null; then dig +short "$1" "$2"
    else nslookup -type="$1" "$2" 2>/dev/null | sed -n 's/.*\(canonical name\|text\) = //p'
    fi | tr -d '"' | sed 's/\.$//'
}

etape "Récapitulatif"
if az containerapp show -g "$GROUPE" -n "$APP_PARC" -o none 2>/dev/null; then
    info "La nouvelle application $APP_PARC sera SUPPRIMÉE : les saisies faites depuis la bascule seront perdues."
fi
info "L'application d'origine $APPLICATION redémarre sur son ancienne base."
confirmer "Revenir à l'application d'origine ?"

# ---------------------------------------------------------------------------
etape "1. Nouvelle application"
if az containerapp show -g "$GROUPE" -n "$APP_PARC" -o none 2>/dev/null; then
    az containerapp delete -g "$GROUPE" -n "$APP_PARC" --yes -o none
    info "$APP_PARC supprimée"
else
    info "aucune nouvelle application"
fi
az containerapp job delete -g "$GROUPE" -n "$TACHE_MIGRATION" -y -o none 2>/dev/null || true
az postgres flexible-server firewall-rule delete -g "$GROUPE" -n "$SERVEUR_PG_PARC" --rule-name "$REGLE_MIGRATION" --yes -o none 2>/dev/null || true

# ---------------------------------------------------------------------------
etape "2. Redémarrage de l'application d'origine ($APPLICATION)"
DERNIERE=$(az containerapp show -g "$GROUPE" -n "$APPLICATION" --query properties.latestRevisionName -o tsv)
az containerapp revision activate -g "$GROUPE" -n "$APPLICATION" --revision "$DERNIERE" -o none 2>/dev/null || true
az containerapp ingress enable -g "$GROUPE" -n "$APPLICATION" --type external --target-port 8080 --transport auto -o none
az containerapp update -g "$GROUPE" -n "$APPLICATION" --min-replicas 1 --max-replicas 1 -o none
OK=false
for _ in $(seq 1 30); do
    if curl -fsS "https://$ADRESSE_APP/sante" >/dev/null 2>&1; then OK=true; break; fi
    sleep 10
done
$OK || erreur "l'application d'origine ne répond pas : portail > $GROUPE > $APPLICATION > Flux de journaux."
info "https://$ADRESSE_APP répond"

# ---------------------------------------------------------------------------
etape "3. Adresse https://$D"
if [[ "$(resoudre CNAME "$D")" != "$ADRESSE_APP" ]]; then
    echo
    echo "  ➜ Chez l'hébergeur DNS de ${D#*.}, remettre le CNAME « $SOUS » vers :"
    echo
    echo "        $ADRESSE_APP"
    echo
    echo "  Le script attend que le changement soit visible…"
    for i in $(seq 1 120); do
        [[ "$(resoudre CNAME "$D")" == "$ADRESSE_APP" ]] && break
        (( i % 4 == 0 )) && info "pas encore visible ($((i / 4)) min)…"
        sleep 15
    done
    [[ "$(resoudre CNAME "$D")" == "$ADRESSE_APP" ]] || erreur "CNAME pas encore visible après 30 min : relancez ce script quand il le sera."
fi
if [[ -z "$(az containerapp hostname list -g "$GROUPE" -n "$APPLICATION" --query "[?name=='$D'].name" -o tsv)" ]]; then
    reessayer az containerapp hostname add -g "$GROUPE" -n "$APPLICATION" --hostname "$D" -o none
fi
# Le certificat d'origine est toujours rangé dans l'environnement Vital Pilot : on le réutilise
CERTIFICAT=$(az containerapp env certificate list -g "$GROUPE_VP" -n "$ENVIRONNEMENT" --managed-certificates-only \
    --query "[?properties.subjectName=='$D'].name | [0]" -o tsv 2>/dev/null || true)
if [[ -n "$CERTIFICAT" ]]; then
    reessayer az containerapp hostname bind -g "$GROUPE" -n "$APPLICATION" --hostname "$D" \
        --environment "$ID_ENV" --certificate "$CERTIFICAT" -o none
else
    reessayer az containerapp hostname bind -g "$GROUPE" -n "$APPLICATION" --hostname "$D" \
        --environment "$ID_ENV" --validation-method CNAME -o none
fi
info "https://$D lié à l'application d'origine"

etape "Terminé"
cat <<FIN

  L'application d'origine est de nouveau en service sur https://$D, avec l'ancienne base.
  Le nouvel environnement et le nouveau serveur PostgreSQL restent en place : vous pourrez relancer
  2-basculer.sh plus tard (il recopiera la base, à jour).
FIN
