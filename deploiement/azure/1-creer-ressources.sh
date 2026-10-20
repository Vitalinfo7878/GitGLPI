#!/usr/bin/env bash
# Étape 1 : crée les ressources du Parc informatique dans son propre groupe « rg-parc-informatique ».
# À lancer dans Azure Cloud Shell (Bash) après 0-preparer-acces.sh :
#   MDP_ADMIN_PG=$(az keyvault secret show --vault-name kv-vital-pilot-94eed4 -n pg-admin-password --query value -o tsv) bash 1-creer-ressources.sh
#
# Ce que fait le script (rejouable sans risque) :
#   - dans rg-parc-informatique : registre d'images, identités, application Container Apps
#   - sur le serveur PostgreSQL Vital Pilot : la base « parcinfo » et un compte « parcinfo » qui n'a accès qu'à elle
#   - l'application tourne dans l'environnement Vital Pilot (rattachement), sans le modifier
# Aucune ressource Vital Pilot n'est modifiée.

set -euo pipefail
cd "$(dirname "$0")"
source ./parametres.sh

az extension add --name containerapp --upgrade --only-show-errors -y >/dev/null
verifier_abonnement

etape "Récapitulatif"
info "Abonnement        : $ABONNEMENT"
info "Notre groupe      : $GROUPE (toutes les ressources créées sont ici)"
info "Registre d'images : $REGISTRE (le nôtre)"
info "Application       : $APPLICATION → https://$ADRESSE_APP"
info "Vital Pilot       : rattachement à $ENVIRONNEMENT + base « $BASE » sur $SERVEUR_PG (rien d'autre)"
confirmer "Créer / mettre à jour les ressources du Parc informatique ?"

# ---------------------------------------------------------------------------
etape "1. Registre d'images ($REGISTRE)"
if az acr show -n "$REGISTRE" -o none 2>/dev/null; then
    info "le registre existe déjà"
else
    az acr create -g "$GROUPE" -n "$REGISTRE" --sku Basic --admin-enabled false --tags "${ETIQUETTES[@]}" -o none
    info "registre créé (formule Basic)"
fi
ID_REGISTRE=$(az acr show -n "$REGISTRE" --query id -o tsv)

etape "2. Identité de l'application ($IDENTITE_APP)"
az identity create -g "$GROUPE" -n "$IDENTITE_APP" -l "$REGION" --tags "${ETIQUETTES[@]}" -o none
ID_IDENTITE_APP=$(az identity show -g "$GROUPE" -n "$IDENTITE_APP" --query id -o tsv)
PRINCIPAL_APP=$(az identity show -g "$GROUPE" -n "$IDENTITE_APP" --query principalId -o tsv)
reessayer attribuer_role "$PRINCIPAL_APP" ServicePrincipal "AcrPull" "$ID_REGISTRE"

# Image de l'outil psql copiée dans notre registre (évite les limites de téléchargement des registres publics)
if az acr repository show -n "$REGISTRE" --image "$IMAGE_PSQL" -o none 2>/dev/null; then
    info "outil psql déjà présent dans le registre"
else
    for source in "${SOURCES_PSQL[@]}"; do
        if az acr import -n "$REGISTRE" --source "$source" --image "$IMAGE_PSQL" -o none; then
            info "outil psql copié depuis $source"; break
        fi
        # La copie peut avoir abouti malgré une erreur d'attente côté outil : on vérifie avant de passer à la source suivante
        if az acr repository show -n "$REGISTRE" --image "$IMAGE_PSQL" -o none 2>/dev/null; then
            info "outil psql présent dans le registre"; break
        fi
        info "copie impossible depuis $source, source suivante…"
    done
    reessayer az acr repository show -n "$REGISTRE" --image "$IMAGE_PSQL" -o none \
        || erreur "impossible de copier l'image PostgreSQL dans le registre : relancez le script dans quelques minutes."
fi

# ---------------------------------------------------------------------------
etape "3. Base PostgreSQL « $BASE » sur $SERVEUR_PG"
if az postgres flexible-server db show -g "$GROUPE_VP" -s "$SERVEUR_PG" -n "$BASE" -o none 2>/dev/null; then
    info "la base existe déjà"
else
    az postgres flexible-server db create -g "$GROUPE_VP" -s "$SERVEUR_PG" -n "$BASE" -o none
    info "base créée (les autres bases du serveur ne sont pas touchées)"
fi
HOTE_PG=$(az postgres flexible-server show -g "$GROUPE_VP" -n "$SERVEUR_PG" --query fullyQualifiedDomainName -o tsv)
ADMIN_PG=$(az postgres flexible-server show -g "$GROUPE_VP" -n "$SERVEUR_PG" --query administratorLogin -o tsv)

etape "4. Compte PostgreSQL « $BASE » (n'a accès qu'à sa base)"
info "Le serveur n'est joignable que depuis le réseau privé : le compte est créé par une tâche temporaire"
info "de notre groupe, exécutée dans l'environnement Vital Pilot, puis supprimée."
# Le mot de passe administrateur peut être fourni par la variable MDP_ADMIN_PG (lu dans le coffre à secrets, voir en tête)
if [[ -z "${MDP_ADMIN_PG:-}" ]]; then
    info "Il faut le mot de passe de l'administrateur PostgreSQL « $ADMIN_PG » (voir deploiement/README.md)."
    read -r -s -p "    Mot de passe de $ADMIN_PG : " MDP_ADMIN_PG; echo
else
    info "mot de passe administrateur fourni par la variable MDP_ADMIN_PG"
fi
[[ -n "$MDP_ADMIN_PG" ]] || erreur "mot de passe vide."
MDP_PARC=$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | head -c 32)

YAML_TACHE=$(mktemp --suffix=.yaml)
trap 'rm -f "$YAML_TACHE"' EXIT
yaml_quote() { printf "'%s'" "${1//\'/\'\'}"; }
cat > "$YAML_TACHE" <<EOF
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
      - name: mdp-parc
        value: $(yaml_quote "$MDP_PARC")
  template:
    containers:
      - name: init
        image: $SERVEUR_REGISTRE/$IMAGE_PSQL
        command: ["/bin/sh", "-c", "echo \"\$SQL_B64\" | base64 -d > /tmp/role.sql && psql -v mdp=\"\$MDP\" -f /tmp/role.sql"]
        env:
          - { name: PGHOST, value: $HOTE_PG }
          - { name: PGUSER, value: $(yaml_quote "$ADMIN_PG") }
          - { name: PGDATABASE, value: postgres }
          - { name: PGSSLMODE, value: require }
          - { name: PGPASSWORD, secretRef: mdp-admin }
          - { name: MDP, secretRef: mdp-parc }
          - { name: SQL_B64, value: $(yaml_quote "$(base64 -w0 role-parcinfo.sql)") }
        resources: { cpu: 0.25, memory: 0.5Gi }
EOF
az containerapp job delete -g "$GROUPE" -n "$TACHE_INIT" -y -o none 2>/dev/null || true
reessayer az containerapp job create -g "$GROUPE" -n "$TACHE_INIT" --yaml "$YAML_TACHE" -o none --only-show-errors \
    || erreur "création de la tâche impossible (droit de rattachement à $ENVIRONNEMENT manquant ? relancez 0-preparer-acces.sh)."
rm -f "$YAML_TACHE"
EXECUTION=$(reessayer az containerapp job start -g "$GROUPE" -n "$TACHE_INIT" --query name -o tsv --only-show-errors) \
    || erreur "lancement de la tâche impossible. Vous pouvez la lancer à la main : portail Azure > $GROUPE > $TACHE_INIT > « Exécuter maintenant »,
    puis relancer ce script une fois l'exécution réussie."
[[ -n "$EXECUTION" ]] || erreur "lancement de la tâche : aucun identifiant d'exécution reçu."
info "tâche lancée ($EXECUTION), attente du résultat…"
STATUT=""
for _ in $(seq 1 60); do
    STATUT=$(az containerapp job execution show -g "$GROUPE" -n "$TACHE_INIT" --job-execution-name "$EXECUTION" --query properties.status -o tsv)
    [[ "$STATUT" == "Succeeded" || "$STATUT" == "Failed" ]] && break
    sleep 5
done
if [[ "$STATUT" != "Succeeded" ]]; then
    erreur "la création du compte PostgreSQL a échoué (statut : ${STATUT:-inconnu}).
    Journaux : portail Azure > $GROUPE > $TACHE_INIT > Historique des exécutions > Journaux de console.
    Cause fréquente : mot de passe administrateur incorrect. La tâche est conservée pour le diagnostic ;
    relancez simplement ce script une fois le problème corrigé."
fi
az containerapp job delete -g "$GROUPE" -n "$TACHE_INIT" -y -o none
info "compte « $BASE » prêt, tâche temporaire supprimée"
CHAINE_PG="Host=$HOTE_PG;Database=$BASE;Username=$BASE;Password=$MDP_PARC;SSL Mode=Require"

# ---------------------------------------------------------------------------
etape "5. Application Container Apps ($APPLICATION)"
VARIABLES=(
    "ASPNETCORE_ENVIRONMENT=Production"
    "BaseDeDonnees__Fournisseur=Postgres"
    "ConnectionStrings__Parc=secretref:connexion-pg"
    "Messagerie__UrlApplication=https://$ADRESSE_APP"
    "Authentification__ModeDemo=false"
)
if az containerapp show -g "$GROUPE" -n "$APPLICATION" -o none 2>/dev/null; then
    az containerapp secret set -g "$GROUPE" -n "$APPLICATION" --secrets "connexion-pg=$CHAINE_PG" -o none
    az containerapp update -g "$GROUPE" -n "$APPLICATION" --set-env-vars "${VARIABLES[@]}" -o none
    info "application existante mise à jour (nouveau mot de passe de base enregistré)"
else
    # Image provisoire de Microsoft : la vraie image sera publiée par le déploiement GitHub
    az containerapp create -g "$GROUPE" -n "$APPLICATION" --environment "$ID_ENV" \
        --image mcr.microsoft.com/k8se/quickstart:latest --ingress external --target-port 80 \
        --min-replicas 1 --max-replicas 1 --cpu 0.5 --memory 1.0Gi \
        --user-assigned "$ID_IDENTITE_APP" --registry-server "$SERVEUR_REGISTRE" --registry-identity "$ID_IDENTITE_APP" \
        --secrets "connexion-pg=$CHAINE_PG" --env-vars "${VARIABLES[@]}" \
        --tags "${ETIQUETTES[@]}" -o none
    info "application créée : https://$ADRESSE_APP (page d'attente Microsoft jusqu'au premier déploiement)"
fi
ID_APPLICATION=$(az containerapp show -g "$GROUPE" -n "$APPLICATION" --query id -o tsv)

# ---------------------------------------------------------------------------
etape "6. Identité du déploiement GitHub ($IDENTITE_GITHUB)"
az identity create -g "$GROUPE" -n "$IDENTITE_GITHUB" -l "$REGION" --tags "${ETIQUETTES[@]}" -o none
PRINCIPAL_GITHUB=$(az identity show -g "$GROUPE" -n "$IDENTITE_GITHUB" --query principalId -o tsv)
CLIENT_GITHUB=$(az identity show -g "$GROUPE" -n "$IDENTITE_GITHUB" --query clientId -o tsv)
SUJET="repo:$DEPOT_GITHUB:environment:$ENVIRONNEMENT_GITHUB"
if [[ -z "$(az identity federated-credential list -g "$GROUPE" --identity-name "$IDENTITE_GITHUB" --query "[?subject=='$SUJET'].name" -o tsv)" ]]; then
    az identity federated-credential create -g "$GROUPE" --identity-name "$IDENTITE_GITHUB" --name github-production \
        --issuer https://token.actions.githubusercontent.com --subject "$SUJET" --audiences api://AzureADTokenExchange -o none
fi
info "confiance accordée au dépôt $DEPOT_GITHUB (environnement GitHub « $ENVIRONNEMENT_GITHUB » uniquement)"
# Droits : publier dans NOTRE registre, mettre à jour NOTRE application, s'y rattacher à l'environnement (sans le modifier)
reessayer attribuer_role "$PRINCIPAL_GITHUB" ServicePrincipal "AcrPush" "$ID_REGISTRE"
reessayer attribuer_role "$PRINCIPAL_GITHUB" ServicePrincipal "Contributor" "$ID_APPLICATION"
reessayer attribuer_role "$PRINCIPAL_GITHUB" ServicePrincipal "$ROLE_RATTACHEMENT" "$ID_ENV"

# ---------------------------------------------------------------------------
etape "Terminé"
cat <<EOF

  Application : https://$ADRESSE_APP

  À renseigner dans GitHub : dépôt $DEPOT_GITHUB > Settings > Secrets and variables > Actions > onglet « Variables »
      AZURE_CLIENT_ID        $CLIENT_GITHUB
      AZURE_TENANT_ID        $ID_TENANT
      AZURE_SUBSCRIPTION_ID  $ID_ABONNEMENT
  et créer l'environnement GitHub « $ENVIRONNEMENT_GITHUB » (Settings > Environments > New environment).

  Étape suivante : bash 2-configurer-sso.sh
EOF
