#!/usr/bin/env bash
# Étape 1 : crée les ressources Azure du Parc informatique à côté de celles de Vital Pilot.
# À lancer dans Azure Cloud Shell (Bash), depuis le dossier du dépôt :
#   git clone https://github.com/Vitalinfo7878/GitGLPI.git && cd GitGLPI/deploiement/azure && bash 1-creer-ressources.sh
#
# Ce que fait le script (rejouable sans risque) :
#   - identité « id-parc-informatique » autorisée à lire les images du registre
#   - base PostgreSQL « parcinfo » sur le serveur existant + compte « parcinfo » qui n'a accès qu'à elle
#   - application Container Apps « ca-parc-informatique » dans l'environnement existant (image provisoire)
#   - identité « id-parc-github » pour le déploiement automatique depuis GitHub (sans mot de passe)
# Il ne modifie aucune ressource existante de Vital Pilot.

set -euo pipefail
cd "$(dirname "$0")"
source ./parametres.sh

az extension add --name containerapp --upgrade --only-show-errors -y >/dev/null
verifier_abonnement

etape "Récapitulatif"
info "Abonnement     : $ABONNEMENT ($ID_ABONNEMENT)"
info "Groupe         : $GROUPE"
info "Environnement  : $ENVIRONNEMENT (existant, inchangé)"
info "Registre       : $REGISTRE (existant, ajout du dossier « $IMAGE »)"
info "PostgreSQL     : $SERVEUR_PG (existant, ajout de la base « $BASE »)"
info "Application    : $APPLICATION → https://$ADRESSE_APP"
confirmer "Créer / mettre à jour les ressources du Parc informatique ?"

ID_REGISTRE=$(az acr show -n "$REGISTRE" --query id -o tsv)
ID_ENV=$(az containerapp env show -g "$GROUPE" -n "$ENVIRONNEMENT" --query id -o tsv)

attribuer_role() { # principal, rôle, portée
    if [[ -z "$(az role assignment list --assignee "$1" --role "$2" --scope "$3" --query '[0].id' -o tsv)" ]]; then
        az role assignment create --assignee-object-id "$1" --assignee-principal-type ServicePrincipal \
            --role "$2" --scope "$3" -o none
        info "rôle « $2 » attribué"
    else
        info "rôle « $2 » déjà attribué"
    fi
}

# ---------------------------------------------------------------------------
etape "1. Identité de l'application ($IDENTITE_APP)"
az identity create -g "$GROUPE" -n "$IDENTITE_APP" --tags "${ETIQUETTES[@]}" -o none
ID_IDENTITE_APP=$(az identity show -g "$GROUPE" -n "$IDENTITE_APP" --query id -o tsv)
PRINCIPAL_APP=$(az identity show -g "$GROUPE" -n "$IDENTITE_APP" --query principalId -o tsv)
attribuer_role "$PRINCIPAL_APP" "AcrPull" "$ID_REGISTRE"

# ---------------------------------------------------------------------------
etape "2. Base PostgreSQL « $BASE »"
if az postgres flexible-server db show -g "$GROUPE" -s "$SERVEUR_PG" -n "$BASE" -o none 2>/dev/null; then
    info "la base existe déjà"
else
    az postgres flexible-server db create -g "$GROUPE" -s "$SERVEUR_PG" -n "$BASE" -o none
    info "base créée"
fi
HOTE_PG=$(az postgres flexible-server show -g "$GROUPE" -n "$SERVEUR_PG" --query fullyQualifiedDomainName -o tsv)
ADMIN_PG=$(az postgres flexible-server show -g "$GROUPE" -n "$SERVEUR_PG" --query administratorLogin -o tsv)

etape "3. Compte PostgreSQL « $BASE » (n'a accès qu'à sa base)"
info "Le serveur n'est joignable que depuis le réseau privé : le compte est créé par une tâche"
info "temporaire lancée dans l'environnement Container Apps, puis supprimée."
info "Il faut le mot de passe de l'administrateur PostgreSQL « $ADMIN_PG » (demandez-le à votre collègue)."
read -r -s -p "    Mot de passe de $ADMIN_PG : " MDP_ADMIN_PG; echo
[[ -n "$MDP_ADMIN_PG" ]] || erreur "mot de passe vide."
MDP_PARC=$(openssl rand -base64 48 | tr -dc 'A-Za-z0-9' | head -c 32)

# Image PostgreSQL (pour l'outil psql) copiée dans le registre, pour ne pas dépendre de Docker Hub
if ! az acr repository show -n "$REGISTRE" --image "outils/postgres:17-alpine" -o none 2>/dev/null; then
    az acr import -n "$REGISTRE" --source public.ecr.aws/docker/library/postgres:17-alpine --image outils/postgres:17-alpine -o none
fi

YAML_TACHE=$(mktemp --suffix=.yaml)
trap 'rm -f "$YAML_TACHE"' EXIT
yaml_quote() { printf "'%s'" "${1//\'/\'\'}"; }
SQL_B64=$(base64 -w0 role-parcinfo.sql)
cat > "$YAML_TACHE" <<EOF
location: $(az group show -n "$GROUPE" --query location -o tsv)
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
        image: $SERVEUR_REGISTRE/outils/postgres:17-alpine
        command: ["/bin/sh", "-c", "echo \"\$SQL_B64\" | base64 -d > /tmp/role.sql && psql -v mdp=\"\$MDP\" -f /tmp/role.sql"]
        env:
          - { name: PGHOST, value: $HOTE_PG }
          - { name: PGUSER, value: $(yaml_quote "$ADMIN_PG") }
          - { name: PGDATABASE, value: postgres }
          - { name: PGSSLMODE, value: require }
          - { name: PGPASSWORD, secretRef: mdp-admin }
          - { name: MDP, secretRef: mdp-parc }
          - { name: SQL_B64, value: $SQL_B64 }
        resources: { cpu: 0.25, memory: 0.5Gi }
EOF
az containerapp job create -g "$GROUPE" -n "$TACHE_INIT" --yaml "$YAML_TACHE" -o none
rm -f "$YAML_TACHE"
EXECUTION=$(az containerapp job start -g "$GROUPE" -n "$TACHE_INIT" --query name -o tsv)
info "tâche lancée ($EXECUTION), attente du résultat…"
STATUT=""
for _ in $(seq 1 60); do
    STATUT=$(az containerapp job execution show -g "$GROUPE" -n "$TACHE_INIT" --job-execution-name "$EXECUTION" --query properties.status -o tsv)
    [[ "$STATUT" == "Succeeded" || "$STATUT" == "Failed" ]] && break
    sleep 5
done
if [[ "$STATUT" != "Succeeded" ]]; then
    erreur "la création du compte PostgreSQL a échoué (statut : ${STATUT:-inconnu}).
    Consultez les journaux : portail Azure > $TACHE_INIT > Historique des exécutions > Journaux de console.
    Causes fréquentes : mot de passe administrateur incorrect. La tâche est conservée pour le diagnostic ;
    relancez simplement ce script une fois le problème corrigé."
fi
az containerapp job delete -g "$GROUPE" -n "$TACHE_INIT" -y -o none
info "compte « $BASE » prêt, tâche temporaire supprimée"
CHAINE_PG="Host=$HOTE_PG;Database=$BASE;Username=$BASE;Password=$MDP_PARC;SSL Mode=Require"

# ---------------------------------------------------------------------------
etape "4. Application Container Apps ($APPLICATION)"
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
    # Image provisoire de Microsoft : la vraie image sera publiée par le déploiement GitHub (étape 3 du guide)
    az containerapp create -g "$GROUPE" -n "$APPLICATION" --environment "$ENVIRONNEMENT" \
        --image mcr.microsoft.com/k8se/quickstart:latest --ingress external --target-port 80 \
        --min-replicas 1 --max-replicas 1 --cpu 0.5 --memory 1.0Gi \
        --user-assigned "$ID_IDENTITE_APP" --registry-server "$SERVEUR_REGISTRE" --registry-identity "$ID_IDENTITE_APP" \
        --secrets "connexion-pg=$CHAINE_PG" --env-vars "${VARIABLES[@]}" \
        --tags "${ETIQUETTES[@]}" -o none
    info "application créée : https://$ADRESSE_APP (page d'attente Microsoft jusqu'au premier déploiement)"
fi
ID_APPLICATION=$(az containerapp show -g "$GROUPE" -n "$APPLICATION" --query id -o tsv)

# ---------------------------------------------------------------------------
etape "5. Identité du déploiement GitHub ($IDENTITE_GITHUB)"
az identity create -g "$GROUPE" -n "$IDENTITE_GITHUB" --tags "${ETIQUETTES[@]}" -o none
PRINCIPAL_GITHUB=$(az identity show -g "$GROUPE" -n "$IDENTITE_GITHUB" --query principalId -o tsv)
CLIENT_GITHUB=$(az identity show -g "$GROUPE" -n "$IDENTITE_GITHUB" --query clientId -o tsv)
SUJET="repo:$DEPOT_GITHUB:environment:$ENVIRONNEMENT_GITHUB"
if [[ -z "$(az identity federated-credential list -g "$GROUPE" --identity-name "$IDENTITE_GITHUB" --query "[?subject=='$SUJET'].name" -o tsv)" ]]; then
    az identity federated-credential create -g "$GROUPE" --identity-name "$IDENTITE_GITHUB" --name github-production \
        --issuer https://token.actions.githubusercontent.com --subject "$SUJET" --audiences api://AzureADTokenExchange -o none
fi
info "confiance accordée au dépôt $DEPOT_GITHUB (environnement GitHub « $ENVIRONNEMENT_GITHUB » uniquement)"

# Droits minimaux : publier des images, mettre à jour NOTRE application, et s'y rattacher à l'environnement
attribuer_role "$PRINCIPAL_GITHUB" "AcrPush" "$ID_REGISTRE"
attribuer_role "$PRINCIPAL_GITHUB" "Contributor" "$ID_APPLICATION"
if [[ -z "$(az role definition list --name "$ROLE_RATTACHEMENT" --query '[0].id' -o tsv)" ]]; then
    az role definition create -o none --role-definition "{
        \"Name\": \"$ROLE_RATTACHEMENT\",
        \"Description\": \"Permet de déployer une application dans l'environnement Container Apps, sans pouvoir le modifier.\",
        \"Actions\": [\"Microsoft.App/managedEnvironments/read\", \"Microsoft.App/managedEnvironments/join/action\"],
        \"AssignableScopes\": [\"/subscriptions/$ID_ABONNEMENT/resourceGroups/$GROUPE\"]
    }"
    info "rôle personnalisé créé, attente de sa propagation…"
    sleep 30
fi
attribuer_role "$PRINCIPAL_GITHUB" "$ROLE_RATTACHEMENT" "$ID_ENV"

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
