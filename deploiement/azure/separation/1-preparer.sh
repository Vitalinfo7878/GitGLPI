#!/usr/bin/env bash
# Séparation, étape 1 (SANS COUPURE) : crée l'hébergement propre au Parc informatique dans rg-parc-informatique.
# À lancer dans Azure Cloud Shell (Bash), avec un compte propriétaire de l'abonnement :
#   cd ~ && rm -rf GitGLPI && git clone https://github.com/Vitalinfo7878/GitGLPI.git
#   bash GitGLPI/deploiement/azure/separation/1-preparer.sh
#
# Ce que fait le script (rejouable sans risque, ≈ 15 min) :
#   - réseau privé, espace de journaux et environnement Container Apps du Parc
#   - serveur PostgreSQL du Parc (petite taille, sauvegardes 7 jours), joignable uniquement depuis ce réseau privé
#   - droit du déploiement GitHub sur le nouvel environnement
# L'application actuelle continue de tourner sans changement : elle n'est déplacée qu'à l'étape 2 (bascule).
# Aucune ressource Vital Pilot n'est modifiée.

set -euo pipefail
cd "$(dirname "$0")"
source ../parametres.sh
source ./parametres-separation.sh

az extension add --name containerapp --upgrade --only-show-errors -y >/dev/null
verifier_abonnement
noms_separation
# Même version de PostgreSQL que le serveur actuel : la copie de la base se fait sans conversion
VERSION_PG=$(az postgres flexible-server show -g "$GROUPE_VP" -n "$SERVEUR_PG" --query version -o tsv)

etape "Récapitulatif"
info "Groupe            : $GROUPE (tout est créé ici)"
info "Réseau privé      : $RESEAU ($PLAGE_RESEAU)"
info "Environnement     : $ENV_PARC"
info "Serveur PostgreSQL: $SERVEUR_PG_PARC (Burstable B1ms, 32 Go, PostgreSQL $VERSION_PG, sauvegardes 7 jours)"
info "Coût supplémentaire estimé : 20 à 25 €/mois"
info "L'application actuelle ($APPLICATION) n'est pas touchée."
confirmer "Créer l'hébergement du Parc informatique ?"

# ---------------------------------------------------------------------------
etape "1. Services Azure de l'abonnement"
for fournisseur in Microsoft.App Microsoft.OperationalInsights Microsoft.DBforPostgreSQL Microsoft.Network; do
    az provider register -n "$fournisseur" --wait -o none
done
info "services activés"

# ---------------------------------------------------------------------------
etape "2. Réseau privé ($RESEAU)"
if az network vnet show -g "$GROUPE" -n "$RESEAU" -o none 2>/dev/null; then
    info "le réseau existe déjà"
else
    az network vnet create -g "$GROUPE" -n "$RESEAU" -l "$REGION" --address-prefixes "$PLAGE_RESEAU" \
        --tags "${ETIQUETTES[@]}" -o none
    info "réseau créé"
fi
if ! az network vnet subnet show -g "$GROUPE" --vnet-name "$RESEAU" -n "$SOUS_RESEAU_ENV" -o none 2>/dev/null; then
    az network vnet subnet create -g "$GROUPE" --vnet-name "$RESEAU" -n "$SOUS_RESEAU_ENV" \
        --address-prefixes "$PLAGE_ENV" --delegations Microsoft.App/environments -o none
fi
if ! az network vnet subnet show -g "$GROUPE" --vnet-name "$RESEAU" -n "$SOUS_RESEAU_PG" -o none 2>/dev/null; then
    az network vnet subnet create -g "$GROUPE" --vnet-name "$RESEAU" -n "$SOUS_RESEAU_PG" \
        --address-prefixes "$PLAGE_PG" -o none
fi
ID_SOUS_RESEAU_ENV=$(az network vnet subnet show -g "$GROUPE" --vnet-name "$RESEAU" -n "$SOUS_RESEAU_ENV" --query id -o tsv)
info "sous-réseaux : $SOUS_RESEAU_ENV ($PLAGE_ENV), $SOUS_RESEAU_PG ($PLAGE_PG)"

# ---------------------------------------------------------------------------
etape "3. Espace de journaux ($JOURNAUX)"
if ! az monitor log-analytics workspace show -g "$GROUPE" -n "$JOURNAUX" -o none 2>/dev/null; then
    az monitor log-analytics workspace create -g "$GROUPE" -n "$JOURNAUX" -l "$REGION" \
        --retention-time 30 --tags "${ETIQUETTES[@]}" -o none
    info "espace créé (conservation 30 jours)"
else
    info "l'espace existe déjà"
fi

# ---------------------------------------------------------------------------
etape "4. Environnement Container Apps ($ENV_PARC)"
if az containerapp env show -g "$GROUPE" -n "$ENV_PARC" -o none 2>/dev/null; then
    info "l'environnement existe déjà"
else
    info "création (5 à 10 minutes)…"
    ID_CLIENT_JOURNAUX=$(az monitor log-analytics workspace show -g "$GROUPE" -n "$JOURNAUX" --query customerId -o tsv)
    CLE_JOURNAUX=$(az monitor log-analytics workspace get-shared-keys -g "$GROUPE" -n "$JOURNAUX" --query primarySharedKey -o tsv)
    az containerapp env create -g "$GROUPE" -n "$ENV_PARC" -l "$REGION" \
        --enable-workload-profiles --infrastructure-subnet-resource-id "$ID_SOUS_RESEAU_ENV" \
        --logs-workspace-id "$ID_CLIENT_JOURNAUX" --logs-workspace-key "$CLE_JOURNAUX" \
        --tags "${ETIQUETTES[@]}" -o none
    unset CLE_JOURNAUX
    info "environnement créé"
fi
ID_ENV_PARC=$(az containerapp env show -g "$GROUPE" -n "$ENV_PARC" --query id -o tsv)
DOMAINE_ENV_PARC=$(az containerapp env show -g "$GROUPE" -n "$ENV_PARC" --query properties.defaultDomain -o tsv)

# ---------------------------------------------------------------------------
etape "5. Serveur PostgreSQL ($SERVEUR_PG_PARC)"
if az postgres flexible-server show -g "$GROUPE" -n "$SERVEUR_PG_PARC" -o none 2>/dev/null; then
    info "le serveur existe déjà"
else
    info "création (5 à 10 minutes)…"
    # Accès public sans aucune règle de pare-feu : personne ne peut s'y connecter depuis Internet.
    # Le réseau privé du Parc y accède par un point de terminaison privé (étape 6).
    # Le mot de passe administrateur n'est conservé nulle part : la bascule en génère un nouveau quand elle en a besoin.
    MDP=$(mot_de_passe)
    az postgres flexible-server create -g "$GROUPE" -n "$SERVEUR_PG_PARC" -l "$REGION" \
        --tier Burstable --sku-name Standard_B1ms --storage-size 32 --version "$VERSION_PG" \
        --admin-user "$ADMIN_PG_PARC" --admin-password "$MDP" \
        --public-access None --backup-retention 7 --high-availability Disabled \
        --tags "${ETIQUETTES[@]}" --yes -o none
    unset MDP
    info "serveur créé"
fi
if ! az postgres flexible-server db show -g "$GROUPE" -s "$SERVEUR_PG_PARC" -n "$BASE" -o none 2>/dev/null; then
    az postgres flexible-server db create -g "$GROUPE" -s "$SERVEUR_PG_PARC" -n "$BASE" -o none
fi
info "base « $BASE » prête (vide : elle sera remplie à la bascule)"
ID_PG_PARC=$(az postgres flexible-server show -g "$GROUPE" -n "$SERVEUR_PG_PARC" --query id -o tsv)

# ---------------------------------------------------------------------------
etape "6. Accès privé au serveur PostgreSQL"
if ! az network private-endpoint show -g "$GROUPE" -n "$POINT_PRIVE_PG" -o none 2>/dev/null; then
    az network private-endpoint create -g "$GROUPE" -n "$POINT_PRIVE_PG" -l "$REGION" \
        --vnet-name "$RESEAU" --subnet "$SOUS_RESEAU_PG" \
        --private-connection-resource-id "$ID_PG_PARC" --group-id postgresqlServer \
        --connection-name "$POINT_PRIVE_PG" --tags "${ETIQUETTES[@]}" -o none
fi
if ! az network private-dns zone show -g "$GROUPE" -n "$ZONE_DNS_PG" -o none 2>/dev/null; then
    az network private-dns zone create -g "$GROUPE" -n "$ZONE_DNS_PG" --tags "${ETIQUETTES[@]}" -o none
fi
if ! az network private-dns link vnet show -g "$GROUPE" -z "$ZONE_DNS_PG" -n "lien-$RESEAU" -o none 2>/dev/null; then
    az network private-dns link vnet create -g "$GROUPE" -z "$ZONE_DNS_PG" -n "lien-$RESEAU" \
        -v "$RESEAU" --registration-enabled false -o none
fi
if [[ -z "$(az network private-endpoint dns-zone-group list -g "$GROUPE" --endpoint-name "$POINT_PRIVE_PG" --query '[0].name' -o tsv)" ]]; then
    az network private-endpoint dns-zone-group create -g "$GROUPE" --endpoint-name "$POINT_PRIVE_PG" \
        -n defaut --private-dns-zone "$ZONE_DNS_PG" --zone-name postgres -o none
fi
info "le serveur est joignable depuis le réseau $RESEAU uniquement"

# ---------------------------------------------------------------------------
etape "7. Droits du déploiement GitHub ($IDENTITE_GITHUB)"
PRINCIPAL_GITHUB=$(az identity show -g "$GROUPE" -n "$IDENTITE_GITHUB" --query principalId -o tsv)
# Rattacher l'application à l'environnement lors des mises à jour (le droit sur l'application elle-même est donné à la bascule)
reessayer attribuer_role "$PRINCIPAL_GITHUB" ServicePrincipal "Contributor" "$ID_ENV_PARC"

# ---------------------------------------------------------------------------
etape "Terminé"
VERIFICATION=$(az containerapp show -g "$GROUPE" -n "$APPLICATION" --query properties.customDomainVerificationId -o tsv)
SOUS="${DOMAINE_PERSONNALISE%%.*}"
cat <<FIN

  L'hébergement du Parc informatique est prêt. L'application actuelle tourne toujours, sans changement.

  Pour préparer la bascule (étape 2) :
    • La veille, chez l'hébergeur DNS de ${DOMAINE_PERSONNALISE#*.} : réduire la durée de vie (TTL) de l'enregistrement
      CNAME « $SOUS » à 5 minutes (300 s), pour que le changement du jour J soit vu rapidement.
    • Le jour J, le CNAME « $SOUS » devra pointer vers :
          $APP_PARC.$DOMAINE_ENV_PARC
      (le TXT « asuid.$SOUS » = $VERIFICATION reste inchangé)
    • Prévoir 30 minutes de coupure, de préférence le soir, puis :
          bash GitGLPI/deploiement/azure/separation/2-basculer.sh
FIN
