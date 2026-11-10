#!/usr/bin/env bash
# Séparation, étape 3 (NETTOYAGE, avec l'accord de Thomas) : retire de Vital Pilot tout ce qui servait au Parc informatique.
# À lancer une à deux semaines après la bascule, quand tout fonctionne. Irréversible : plus de retour arrière ensuite.
#   bash GitGLPI/deploiement/azure/separation/3-nettoyer-vital-pilot.sh
# Facultatif, pour supprimer aussi le compte PostgreSQL « parcinfo » sur le serveur Vital Pilot : fournir le mot de passe
# administrateur de ce serveur sans l'afficher (voir deploiement/README.md, étape 1) :
#   MDP_ADMIN_PG=$(az keyvault secret show --vault-name kv-vital-pilot-94eed4 -n pg-admin-password --query value -o tsv) \
#     bash GitGLPI/deploiement/azure/separation/3-nettoyer-vital-pilot.sh
#
# Ce que fait le script :
#   - supprime l'application d'origine (arrêtée depuis la bascule)
#   - supprime de l'environnement Vital Pilot le certificat HTTPS de l'adresse du Parc
#   - supprime la base « parcinfo » (et, si le mot de passe est fourni, le compte « parcinfo ») du serveur PostgreSQL Vital Pilot
#     (elle reste dans les sauvegardes automatiques du serveur pendant leur durée de conservation)
#   - supprime les deux rôles personnalisés « Parc informatique - … » et leurs attributions
# Rien d'autre n'est touché chez Vital Pilot.

set -euo pipefail
cd "$(dirname "$0")"
source ../parametres.sh
source ./parametres-separation.sh

az extension add --name containerapp --upgrade --only-show-errors -y >/dev/null
verifier_abonnement
noms_separation
D="$DOMAINE_PERSONNALISE"
GROUPE_VP_SCOPE="/subscriptions/$ID_ABONNEMENT/resourceGroups/$GROUPE_VP"

# Sécurité : la nouvelle application doit tourner et porter l'adresse du Parc
az containerapp show -g "$GROUPE" -n "$APP_PARC" -o none 2>/dev/null \
    || erreur "la nouvelle application $APP_PARC n'existe pas : faites d'abord la bascule (2-basculer.sh)."
[[ -n "$(az containerapp hostname list -g "$GROUPE" -n "$APP_PARC" --query "[?name=='$D' && bindingType=='SniEnabled'].name" -o tsv)" ]] \
    || erreur "l'adresse $D n'est pas encore liée à $APP_PARC : terminez la bascule (2-basculer.sh)."
curl -fsS "https://$D/sante" >/dev/null 2>&1 || erreur "https://$D ne répond pas : ne nettoyez pas maintenant."

etape "Récapitulatif (IRRÉVERSIBLE)"
info "Application d'origine $APPLICATION : supprimée"
info "Certificat de $D dans l'environnement $ENVIRONNEMENT (Vital Pilot) : supprimé"
info "Base « $BASE » sur $SERVEUR_PG (Vital Pilot) : supprimée"
if [[ -n "${MDP_ADMIN_PG:-}" ]]; then
    info "Compte PostgreSQL « $BASE » sur $SERVEUR_PG : supprimé"
else
    info "Compte PostgreSQL « $BASE » sur $SERVEUR_PG : conservé (mot de passe administrateur non fourni)"
fi
info "Rôles « $ROLE_RATTACHEMENT » et « $ROLE_BASE_PG » : supprimés"
info "À faire uniquement avec l'accord de Thomas (Vital Pilot)."
confirmer "Nettoyer Vital Pilot ?"

# ---------------------------------------------------------------------------
etape "1. Application d'origine ($APPLICATION)"
if az containerapp show -g "$GROUPE" -n "$APPLICATION" -o none 2>/dev/null; then
    az containerapp delete -g "$GROUPE" -n "$APPLICATION" --yes -o none
    info "supprimée"
else
    info "déjà supprimée"
fi

# ---------------------------------------------------------------------------
etape "2. Certificat de $D dans $ENVIRONNEMENT"
for certificat in $(az containerapp env certificate list -g "$GROUPE_VP" -n "$ENVIRONNEMENT" --managed-certificates-only \
        --query "[?properties.subjectName=='$D'].name" -o tsv 2>/dev/null); do
    az containerapp env certificate delete -g "$GROUPE_VP" -n "$ENVIRONNEMENT" --certificate "$certificat" --yes -o none
    info "certificat $certificat supprimé"
done

# ---------------------------------------------------------------------------
etape "3. Base et compte « $BASE » sur $SERVEUR_PG"
if az postgres flexible-server db show -g "$GROUPE_VP" -s "$SERVEUR_PG" -n "$BASE" -o none 2>/dev/null; then
    az postgres flexible-server db delete -g "$GROUPE_VP" -s "$SERVEUR_PG" -n "$BASE" --yes -o none
    info "base supprimée"
else
    info "base déjà supprimée"
fi
if [[ -n "${MDP_ADMIN_PG:-}" ]]; then
    # Le serveur n'est joignable que depuis le réseau Vital Pilot : tâche temporaire exécutée dans son environnement
    HOTE_PG=$(az postgres flexible-server show -g "$GROUPE_VP" -n "$SERVEUR_PG" --query fullyQualifiedDomainName -o tsv)
    ADMIN_PG=$(az postgres flexible-server show -g "$GROUPE_VP" -n "$SERVEUR_PG" --query administratorLogin -o tsv)
    VERSION_PG=$(az postgres flexible-server show -g "$GROUPE_VP" -n "$SERVEUR_PG" --query version -o tsv)
    IMAGE_OUTIL="outils/postgres:${VERSION_PG}-alpine"
    az acr repository show -n "$REGISTRE" --image "$IMAGE_OUTIL" -o none 2>/dev/null || IMAGE_OUTIL="$IMAGE_PSQL"
    ID_IDENTITE_APP=$(az identity show -g "$GROUPE" -n "$IDENTITE_APP" --query id -o tsv)
    YAML=$(mktemp --suffix=.yaml); chmod 600 "$YAML"
    trap 'rm -f "$YAML"' EXIT
    yaml_quote() { printf "'%s'" "${1//\'/\'\'}"; }
    cat > "$YAML" <<EOF
location: $REGION
identity:
  type: UserAssigned
  userAssignedIdentities:
    $ID_IDENTITE_APP: {}
properties:
  environmentId: $ID_ENV
  configuration:
    triggerType: Manual
    replicaTimeout: 300
    replicaRetryLimit: 0
    manualTriggerConfig:
      parallelism: 1
      replicaCompletionCount: 1
    registries:
      - server: $SERVEUR_REGISTRE
        identity: $ID_IDENTITE_APP
    secrets:
      - name: mdp-admin
        value: $(yaml_quote "$MDP_ADMIN_PG")
  template:
    containers:
      - name: nettoyage
        image: $SERVEUR_REGISTRE/$IMAGE_OUTIL
        command: ["psql", "-v", "ON_ERROR_STOP=1", "-c", "DROP ROLE IF EXISTS $BASE"]
        env:
          - { name: PGHOST, value: $HOTE_PG }
          - { name: PGUSER, value: $(yaml_quote "$ADMIN_PG") }
          - { name: PGDATABASE, value: postgres }
          - { name: PGSSLMODE, value: require }
          - { name: PGPASSWORD, secretRef: mdp-admin }
        resources: { cpu: 0.25, memory: 0.5Gi }
EOF
    az containerapp job delete -g "$GROUPE" -n "$TACHE_INIT" -y -o none 2>/dev/null || true
    reessayer az containerapp job create -g "$GROUPE" -n "$TACHE_INIT" --yaml "$YAML" -o none --only-show-errors
    rm -f "$YAML"
    EXECUTION=$(az containerapp job start -g "$GROUPE" -n "$TACHE_INIT" --query name -o tsv --only-show-errors)
    STATUT=""
    for _ in $(seq 1 60); do
        STATUT=$(az containerapp job execution show -g "$GROUPE" -n "$TACHE_INIT" --job-execution-name "$EXECUTION" --query properties.status -o tsv)
        [[ "$STATUT" == "Succeeded" || "$STATUT" == "Failed" ]] && break
        sleep 5
    done
    if [[ "$STATUT" == "Succeeded" ]]; then
        az containerapp job delete -g "$GROUPE" -n "$TACHE_INIT" -y -o none
        info "compte « $BASE » supprimé"
    else
        info "suppression du compte « $BASE » échouée (statut : ${STATUT:-inconnu}) : journaux dans portail > $GROUPE > $TACHE_INIT."
        info "Le reste du nettoyage continue ; vous pourrez relancer ce script."
    fi
fi

# ---------------------------------------------------------------------------
etape "4. Rôles personnalisés"
for role in "$ROLE_RATTACHEMENT" "$ROLE_BASE_PG"; do
    if [[ -z "$(az role definition list --name "$role" --scope "$GROUPE_VP_SCOPE" --query '[0].id' -o tsv 2>/dev/null)" ]]; then
        info "« $role » : déjà supprimé"
        continue
    fi
    for attribution in $(az role assignment list --role "$role" --all --query "[].id" -o tsv); do
        az role assignment delete --ids "$attribution" -o none
    done
    az role definition delete --name "$role" --scope "$GROUPE_VP_SCOPE" -o none
    info "« $role » : attributions et définition supprimées"
done

etape "Terminé"
cat <<FIN

  Le Parc informatique n'utilise plus rien chez Vital Pilot. Tout est dans le groupe $GROUPE.
  Facultatif : dans Entra ID > Inscriptions d'applications > $NOM_APPLI_ENTRA > Authentification,
  supprimer l'ancienne adresse de retour https://$ADRESSE_APP/signin-oidc (inutile désormais).
FIN
