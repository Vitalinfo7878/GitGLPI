#!/usr/bin/env bash
# Séparation, étape 2 (BASCULE, ≈ 30 min de coupure) : déplace le Parc informatique dans son propre hébergement.
# À lancer dans Azure Cloud Shell (Bash), après 1-preparer.sh, de préférence le soir :
#   cd ~ && rm -rf GitGLPI && git clone https://github.com/Vitalinfo7878/GitGLPI.git
#   bash GitGLPI/deploiement/azure/separation/2-basculer.sh
#
# Ce que fait le script :
#   1. arrête l'application actuelle (plus aucune écriture dans l'ancienne base, plus aucun mail envoyé par elle)
#   2. copie la base « parcinfo » vers le nouveau serveur et vérifie que chaque table a le même nombre de lignes
#   3. crée la nouvelle application avec la même image, les mêmes réglages et les mêmes secrets (jamais affichés)
#   4. vous demande de modifier le CNAME chez l'hébergeur DNS, attend qu'il soit visible, puis émet le certificat HTTPS
#   5. ferme l'accès public du nouveau serveur PostgreSQL
# Rejouable : si le script s'interrompt (Cloud Shell fermé…), relancez-le, il reprend là où il en était.
# En cas de problème : bash GitGLPI/deploiement/azure/separation/retour-arriere.sh remet l'ancienne application en service.
# L'ancienne application et l'ancienne base restent en place (arrêtées) jusqu'au nettoyage (3-nettoyer-vital-pilot.sh).
# Ne pas publier de modification dans GitHub pendant la bascule.

set -euo pipefail
cd "$(dirname "$0")"
source ../parametres.sh
source ./parametres-separation.sh

az extension add --name containerapp --upgrade --only-show-errors -y >/dev/null
verifier_abonnement
noms_separation
D="$DOMAINE_PERSONNALISE"
SOUS="${D%%.*}"

az containerapp env show -g "$GROUPE" -n "$ENV_PARC" -o none 2>/dev/null \
    || erreur "environnement $ENV_PARC introuvable : lancez d'abord 1-preparer.sh."
az postgres flexible-server show -g "$GROUPE" -n "$SERVEUR_PG_PARC" -o none 2>/dev/null \
    || erreur "serveur $SERVEUR_PG_PARC introuvable : lancez d'abord 1-preparer.sh."
az network private-endpoint show -g "$GROUPE" -n "$POINT_PRIVE_PG" -o none 2>/dev/null \
    || erreur "accès privé au serveur PostgreSQL absent : relancez 1-preparer.sh jusqu'au bout."
ID_ENV_PARC=$(az containerapp env show -g "$GROUPE" -n "$ENV_PARC" --query id -o tsv)
ADRESSE_PARC="$APP_PARC.$(az containerapp env show -g "$GROUPE" -n "$ENV_PARC" --query properties.defaultDomain -o tsv)"
HOTE_PG_PARC=$(az postgres flexible-server show -g "$GROUPE" -n "$SERVEUR_PG_PARC" --query fullyQualifiedDomainName -o tsv)
VERSION_PG=$(az postgres flexible-server show -g "$GROUPE_VP" -n "$SERVEUR_PG" --query version -o tsv)
IMAGE_OUTIL="outils/postgres:${VERSION_PG}-alpine"
NOUVELLE_EXISTE=false
az containerapp show -g "$GROUPE" -n "$APP_PARC" -o none 2>/dev/null && NOUVELLE_EXISTE=true

TEMP=$(mktemp -d); chmod 700 "$TEMP"
trap 'rm -rf "$TEMP"' EXIT

resoudre() { # type, nom → valeurs (sans guillemets ni point final)
    if command -v dig >/dev/null; then dig +short "$1" "$2"
    else nslookup -type="$1" "$2" 2>/dev/null | sed -n 's/.*\(canonical name\|text\) = //p'
    fi | tr -d '"' | sed 's/\.$//'
}
retour_possible() {
    echo
    echo "  L'application est coupée. Pour remettre l'ancienne en service tout de suite :"
    echo "      bash $(pwd)/retour-arriere.sh"
    echo "  Ou corrigez le problème et relancez ce script : il reprend là où il en était."
}

etape "Récapitulatif"
if $NOUVELLE_EXISTE; then
    info "Reprise : la nouvelle application $APP_PARC existe déjà (base déjà copiée)."
    info "Il reste l'adresse https://$D et la fermeture de l'accès public du serveur."
else
    info "1. Arrêt de l'application actuelle $APPLICATION  →  DÉBUT DE LA COUPURE"
    info "2. Copie de la base vers $SERVEUR_PG_PARC (PostgreSQL $VERSION_PG)"
    info "3. Nouvelle application $APP_PARC dans $ENV_PARC"
    info "4. CNAME « $SOUS » à modifier chez l'hébergeur DNS → $ADRESSE_PARC, puis certificat HTTPS  →  FIN DE LA COUPURE"
fi
confirmer "Lancer la bascule maintenant ?"

if ! $NOUVELLE_EXISTE; then
# ---------------------------------------------------------------------------
etape "1. Arrêt de l'application actuelle ($APPLICATION)"
# Plus aucune requête, plus aucun réplica : ni écriture dans l'ancienne base, ni tâche de fond (relances, mails…)
az containerapp ingress disable -g "$GROUPE" -n "$APPLICATION" -o none 2>/dev/null || true
az containerapp update -g "$GROUPE" -n "$APPLICATION" --min-replicas 0 --max-replicas 1 -o none
replicas() { az containerapp replica list -g "$GROUPE" -n "$APPLICATION" --query "length(@)" -o tsv 2>/dev/null || echo 0; }
for _ in $(seq 1 18); do [[ "$(replicas)" == "0" ]] && break; sleep 10; done
if [[ "$(replicas)" != "0" ]]; then
    for revision in $(az containerapp revision list -g "$GROUPE" -n "$APPLICATION" --query "[?properties.active].name" -o tsv); do
        az containerapp revision deactivate -g "$GROUPE" -n "$APPLICATION" --revision "$revision" -o none || true
    done
    for _ in $(seq 1 18); do [[ "$(replicas)" == "0" ]] && break; sleep 10; done
fi
[[ "$(replicas)" == "0" ]] || { retour_possible; erreur "l'application actuelle tourne encore : arrêtez-la dans le portail (Révisions et réplicas > Désactiver), puis relancez."; }
info "application arrêtée"

# ---------------------------------------------------------------------------
etape "2. Copie de la base"
# Connexion actuelle, lue dans les secrets de l'application (jamais affichée)
NOM_SECRET_PG=$(az containerapp show -g "$GROUPE" -n "$APPLICATION" \
    --query "properties.template.containers[0].env[?name=='ConnectionStrings__Parc'].secretRef | [0]" -o tsv)
[[ -n "$NOM_SECRET_PG" ]] || erreur "chaîne de connexion introuvable dans $APPLICATION."
az containerapp secret list -g "$GROUPE" -n "$APPLICATION" --show-values -o json > "$TEMP/secrets.json"
CHAINE_ANCIENNE=$(jq -r --arg n "$NOM_SECRET_PG" '.[] | select(.name == $n) | .value' "$TEMP/secrets.json")
champ() { tr ';' '\n' <<<"$CHAINE_ANCIENNE" | sed -n "s/^[[:space:]]*$1[[:space:]]*=//Ip" | head -1; }
ANCIEN_HOTE=$(champ Host); ANCIEN_BASE=$(champ Database); ANCIEN_UTILISATEUR=$(champ Username); ANCIEN_MDP=$(champ Password)
[[ -n "$ANCIEN_HOTE" && -n "$ANCIEN_BASE" && -n "$ANCIEN_UTILISATEUR" && -n "$ANCIEN_MDP" ]] \
    || erreur "chaîne de connexion actuelle illisible."

# Outil psql de la même version que les serveurs, copié dans notre registre
if ! az acr repository show -n "$REGISTRE" --image "$IMAGE_OUTIL" -o none 2>/dev/null; then
    for source in "public.ecr.aws/docker/library/postgres:${VERSION_PG}-alpine" "docker.io/library/postgres:${VERSION_PG}-alpine"; do
        az acr import -n "$REGISTRE" --source "$source" --image "$IMAGE_OUTIL" -o none 2>/dev/null && break
        az acr repository show -n "$REGISTRE" --image "$IMAGE_OUTIL" -o none 2>/dev/null && break
    done
    az acr repository show -n "$REGISTRE" --image "$IMAGE_OUTIL" -o none 2>/dev/null \
        || { retour_possible; erreur "impossible de copier l'outil PostgreSQL $VERSION_PG dans le registre."; }
fi

# Nouveaux mots de passe (administrateur du nouveau serveur, compte de l'application), jamais affichés
MDP_ADMIN_NOUVEAU=$(mot_de_passe)
MDP_PARC_NOUVEAU=$(mot_de_passe)
az postgres flexible-server update -g "$GROUPE" -n "$SERVEUR_PG_PARC" --admin-password "$MDP_ADMIN_NOUVEAU" -o none

# La tâche de copie tourne dans l'environnement Vital Pilot (seul endroit d'où l'ancien serveur est joignable) ;
# le nouveau serveur lui est ouvert le temps de la copie (règle « services Azure »), puis refermé.
fermer_pare_feu() {
    az postgres flexible-server firewall-rule delete -g "$GROUPE" -n "$SERVEUR_PG_PARC" --rule-name "$REGLE_MIGRATION" --yes -o none 2>/dev/null || true
}
trap 'fermer_pare_feu; rm -rf "$TEMP"' EXIT
az postgres flexible-server firewall-rule create -g "$GROUPE" -n "$SERVEUR_PG_PARC" --rule-name "$REGLE_MIGRATION" \
    --start-ip-address 0.0.0.0 --end-ip-address 0.0.0.0 -o none

ID_IDENTITE_APP=$(az identity show -g "$GROUPE" -n "$IDENTITE_APP" --query id -o tsv)
# Adresse publique du nouveau serveur, vue d'ici (hors réseau privé)
IP_PG_PARC=$(resoudre A "$HOTE_PG_PARC" | grep -E '^[0-9.]+$' | tail -1 || true)
yaml_quote() { printf "'%s'" "${1//\'/\'\'}"; }
cat > "$TEMP/tache.yaml" <<EOF
location: $REGION
identity:
  type: UserAssigned
  userAssignedIdentities:
    $ID_IDENTITE_APP: {}
properties:
  environmentId: $ID_ENV
  configuration:
    triggerType: Manual
    replicaTimeout: 1800
    replicaRetryLimit: 0
    manualTriggerConfig:
      parallelism: 1
      replicaCompletionCount: 1
    registries:
      - server: $SERVEUR_REGISTRE
        identity: $ID_IDENTITE_APP
    secrets:
      - name: ancien-mdp
        value: $(yaml_quote "$ANCIEN_MDP")
      - name: admin-mdp
        value: $(yaml_quote "$MDP_ADMIN_NOUVEAU")
      - name: parc-mdp
        value: $(yaml_quote "$MDP_PARC_NOUVEAU")
  template:
    containers:
      - name: migration
        image: $SERVEUR_REGISTRE/$IMAGE_OUTIL
        command: ["/bin/sh", "-c", "echo \"\$SCRIPT_B64\" | base64 -d > /tmp/migration.sh && sh /tmp/migration.sh"]
        env:
          - { name: ANCIEN_HOTE, value: $(yaml_quote "$ANCIEN_HOTE") }
          - { name: ANCIEN_BASE, value: $(yaml_quote "$ANCIEN_BASE") }
          - { name: ANCIEN_UTILISATEUR, value: $(yaml_quote "$ANCIEN_UTILISATEUR") }
          - { name: ANCIEN_MDP, secretRef: ancien-mdp }
          - { name: NOUVEAU_HOTE, value: $HOTE_PG_PARC }
          - { name: NOUVEAU_IP, value: $(yaml_quote "$IP_PG_PARC") }
          - { name: NOUVEAU_ADMIN, value: $ADMIN_PG_PARC }
          - { name: NOUVEAU_ADMIN_MDP, secretRef: admin-mdp }
          - { name: NOUVEAU_MDP, secretRef: parc-mdp }
          - { name: SQL_B64, value: $(yaml_quote "$(base64 -w0 ../role-parcinfo.sql)") }
          - { name: SCRIPT_B64, value: $(yaml_quote "$(base64 -w0 migration.sh)") }
        resources: { cpu: 0.5, memory: 1Gi }
EOF
az containerapp job delete -g "$GROUPE" -n "$TACHE_MIGRATION" -y -o none 2>/dev/null || true
reessayer az containerapp job create -g "$GROUPE" -n "$TACHE_MIGRATION" --yaml "$TEMP/tache.yaml" -o none --only-show-errors \
    || { retour_possible; erreur "création de la tâche de copie impossible."; }
rm -f "$TEMP/tache.yaml"
EXECUTION=$(reessayer az containerapp job start -g "$GROUPE" -n "$TACHE_MIGRATION" --query name -o tsv --only-show-errors) \
    || { retour_possible; erreur "lancement de la tâche de copie impossible."; }
info "copie en cours ($EXECUTION)…"
STATUT=""
for _ in $(seq 1 180); do
    STATUT=$(az containerapp job execution show -g "$GROUPE" -n "$TACHE_MIGRATION" --job-execution-name "$EXECUTION" --query properties.status -o tsv)
    [[ "$STATUT" == "Succeeded" || "$STATUT" == "Failed" ]] && break
    sleep 10
done
if [[ "$STATUT" != "Succeeded" ]]; then
    retour_possible
    erreur "la copie de la base a échoué (statut : ${STATUT:-inconnu}).
    Journaux : portail Azure > $GROUPE > $TACHE_MIGRATION > Historique des exécutions > Journaux de console.
    La tâche est conservée pour le diagnostic. Rien n'a été modifié dans l'ancienne base."
fi
az containerapp job logs show -g "$GROUPE" -n "$TACHE_MIGRATION" --execution "$EXECUTION" --container migration \
    --format text --tail 200 2>/dev/null | sed 's/^/    /' || true
az containerapp job delete -g "$GROUPE" -n "$TACHE_MIGRATION" -y -o none || true
fermer_pare_feu
info "base copiée et vérifiée (chaque table a le même nombre de lignes), tâche temporaire supprimée"

# ---------------------------------------------------------------------------
etape "3. Nouvelle application ($APP_PARC)"
# Même image, mêmes variables et mêmes secrets que l'application actuelle ; seule la chaîne de connexion change.
CHAINE_NOUVELLE="Host=$HOTE_PG_PARC;Database=$BASE;Username=$BASE;Password=$MDP_PARC_NOUVEAU;SSL Mode=Require"
az containerapp show -g "$GROUPE" -n "$APPLICATION" -o json > "$TEMP/app.json"
umask 077
jq -n --slurpfile app "$TEMP/app.json" --slurpfile sec "$TEMP/secrets.json" \
      --arg env "$ID_ENV_PARC" --arg nom "$NOM_SECRET_PG" --arg val "$CHAINE_NOUVELLE" '
  $app[0] as $a
  | {
      location: $a.location,
      tags: $a.tags,
      identity: (if ($a.identity.type // "None") == "None" then null else {
          type: $a.identity.type,
          userAssignedIdentities: (($a.identity.userAssignedIdentities // {}) | with_entries(.value = {})
                                   | if length == 0 then null else . end)
      } end),
      properties: {
        environmentId: $env,
        workloadProfileName: "Consumption",
        configuration: {
          activeRevisionsMode: "Single",
          ingress: {external: true, targetPort: 8080, transport: "auto", allowInsecure: false},
          registries: $a.properties.configuration.registries,
          secrets: [$sec[0][] | if .name == $nom then {name: .name, value: $val} else . end]
        },
        template: {
          containers: ($a.properties.template.containers | map(.resources |= del(.ephemeralStorage))),
          volumes: $a.properties.template.volumes,
          scale: {minReplicas: 1, maxReplicas: 1}
        }
      }
    }
  | del(.. | nulls)' > "$TEMP/app.yaml"
rm -f "$TEMP/secrets.json"
unset ANCIEN_MDP CHAINE_ANCIENNE MDP_ADMIN_NOUVEAU
reessayer az containerapp create -g "$GROUPE" -n "$APP_PARC" --yaml "$TEMP/app.yaml" -o none --only-show-errors \
    || { retour_possible; erreur "création de la nouvelle application impossible."; }
rm -f "$TEMP/app.yaml"
unset MDP_PARC_NOUVEAU CHAINE_NOUVELLE
info "application créée"
PRINCIPAL_GITHUB=$(az identity show -g "$GROUPE" -n "$IDENTITE_GITHUB" --query principalId -o tsv)
ID_APP_PARC=$(az containerapp show -g "$GROUPE" -n "$APP_PARC" --query id -o tsv)
reessayer attribuer_role "$PRINCIPAL_GITHUB" ServicePrincipal "Contributor" "$ID_APP_PARC"
info "les prochains déploiements GitHub iront sur $APP_PARC"
fi  # fin de la partie « nouvelle application »

# ---------------------------------------------------------------------------
etape "Vérification de la nouvelle application"
OK=false
for _ in $(seq 1 30); do
    if curl -fsS "https://$ADRESSE_PARC/sante" >/dev/null 2>&1; then OK=true; break; fi
    sleep 10
done
$OK || { retour_possible; erreur "la nouvelle application ne répond pas : portail > $GROUPE > $APP_PARC > Flux de journaux."; }
info "https://$ADRESSE_PARC/sante répond"

# ---------------------------------------------------------------------------
etape "4. Adresse https://$D"
# L'adresse est retirée de l'ancienne application, puis ajoutée à la nouvelle
az containerapp hostname delete -g "$GROUPE" -n "$APPLICATION" --hostname "$D" --yes -o none 2>/dev/null || true
VERIFICATION=$(az containerapp show -g "$GROUPE" -n "$APP_PARC" --query properties.customDomainVerificationId -o tsv)
if ! resoudre TXT "asuid.$D" | grep -qx "$VERIFICATION"; then
    echo
    echo "  Chez l'hébergeur DNS de ${D#*.}, remplacer la valeur du TXT « asuid.$SOUS » par :"
    echo "      $VERIFICATION"
    echo "  et le CNAME « $SOUS » par :"
    echo "      $ADRESSE_PARC"
    echo "  puis relancer ce script (la nouvelle application tourne déjà)."
    exit 0
fi
if [[ -z "$(az containerapp hostname list -g "$GROUPE" -n "$APP_PARC" --query "[?name=='$D'].name" -o tsv)" ]]; then
    reessayer az containerapp hostname add -g "$GROUPE" -n "$APP_PARC" --hostname "$D" -o none
fi
if [[ "$(resoudre CNAME "$D")" != "$ADRESSE_PARC" ]]; then
    echo
    echo "  ➜ MAINTENANT, chez l'hébergeur DNS de ${D#*.}, modifier le CNAME « $SOUS » pour qu'il pointe vers :"
    echo
    echo "        $ADRESSE_PARC"
    echo
    echo "  (le TXT « asuid.$SOUS » ne change pas). Le script attend que le changement soit visible…"
    for i in $(seq 1 120); do
        [[ "$(resoudre CNAME "$D")" == "$ADRESSE_PARC" ]] && break
        (( i % 4 == 0 )) && info "pas encore visible ($((i / 4)) min)…"
        sleep 15
    done
    [[ "$(resoudre CNAME "$D")" == "$ADRESSE_PARC" ]] \
        || erreur "CNAME pas encore visible après 30 min : relancez ce script quand il le sera (la nouvelle application tourne déjà)."
fi
info "CNAME visible"
info "émission du certificat HTTPS (jusqu'à 20 minutes)…"
reessayer az containerapp hostname bind -g "$GROUPE" -n "$APP_PARC" --hostname "$D" \
    --environment "$ID_ENV_PARC" --validation-method CNAME -o none \
    || erreur "certificat pas encore émis : relancez ce script dans quelques minutes (la nouvelle application tourne déjà)."
OK=false
for _ in $(seq 1 30); do
    if curl -fsS "https://$D/sante" >/dev/null 2>&1; then OK=true; break; fi
    sleep 10
done
$OK && info "https://$D répond" || info "https://$D ne répond pas encore (cache DNS) : réessayez dans quelques minutes."

# ---------------------------------------------------------------------------
etape "5. Fermeture de l'accès public du serveur PostgreSQL"
az postgres flexible-server firewall-rule delete -g "$GROUPE" -n "$SERVEUR_PG_PARC" --rule-name "$REGLE_MIGRATION" --yes -o none 2>/dev/null || true
if az postgres flexible-server update -g "$GROUPE" -n "$SERVEUR_PG_PARC" --public-access Disabled -o none 2>/dev/null; then
    info "accès public désactivé : serveur joignable uniquement depuis le réseau privé du Parc"
else
    info "aucune règle de pare-feu : serveur injoignable depuis Internet"
    info "(pour aller plus loin : portail > $SERVEUR_PG_PARC > Mise en réseau > Accès public : désactiver)"
fi

etape "Terminé"
cat <<FIN

  Le Parc informatique tourne maintenant dans son propre hébergement : https://$D
  Plus rien n'est partagé avec Vital Pilot.

  À vérifier dans les prochains jours : connexion, tickets, demandes, envoi des mails (Administration > Courriels).
  L'ancienne application ($APPLICATION, arrêtée) et l'ancienne base (sur $SERVEUR_PG) restent en place en secours.
  Retour arrière possible tant que le nettoyage n'est pas fait : bash $(pwd)/retour-arriere.sh
    (les saisies faites depuis la bascule seraient perdues).

  Après une à deux semaines sans problème, avec l'accord de Thomas : bash $(pwd)/3-nettoyer-vital-pilot.sh
FIN
