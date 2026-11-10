#!/bin/sh
# Copie de la base « parcinfo » de l'ancien serveur PostgreSQL vers le nouveau.
# Exécuté par la tâche temporaire de 2-basculer.sh (image postgres:17-alpine), dans l'environnement qui voit
# l'ancien serveur ; le nouveau serveur est joint par son adresse publique, ouverte le temps de la copie.
# Variables : ANCIEN_HOTE, ANCIEN_UTILISATEUR, ANCIEN_MDP, ANCIEN_BASE, NOUVEAU_HOTE, NOUVEAU_ADMIN, NOUVEAU_ADMIN_MDP,
#             NOUVEAU_MDP (compte parcinfo), SQL_B64 (role-parcinfo.sql en base64),
#             NOUVEAU_IP (facultatif : adresse publique du nouveau serveur, au cas où le réseau de l'environnement
#             résoudrait son nom vers une adresse privée qu'il ne peut pas joindre).
# Aucun mot de passe n'est affiché ; seuls les noms de tables et leurs nombres de lignes le sont.
set -eu
export PGSSLMODE=require PGCONNECT_TIMEOUT=20 PGPORT=5432

ancien() { PGPASSWORD="$ANCIEN_MDP" psql -h "$ANCIEN_HOTE" -U "$ANCIEN_UTILISATEUR" -d "$ANCIEN_BASE" -v ON_ERROR_STOP=1 "$@"; }
# Avec NOUVEAU_IP, la connexion se fait à cette adresse ; le nom reste utilisé pour le chiffrement (TLS)
ADRESSE=""
[ -n "${NOUVEAU_IP:-}" ] && ADRESSE="PGHOSTADDR=$NOUVEAU_IP"
# shellcheck disable=SC2086  # $ADRESSE : vide ou un seul mot
nouveau() { env PGPASSWORD="$NOUVEAU_MDP" $ADRESSE psql -h "$NOUVEAU_HOTE" -U parcinfo -d parcinfo -v ON_ERROR_STOP=1 "$@"; }

echo "1. Compte « parcinfo » sur le nouveau serveur"
echo "$SQL_B64" | base64 -d > /tmp/role.sql
# shellcheck disable=SC2086
env PGPASSWORD="$NOUVEAU_ADMIN_MDP" $ADRESSE psql -h "$NOUVEAU_HOTE" -U "$NOUVEAU_ADMIN" -d postgres -q -v mdp="$NOUVEAU_MDP" -f /tmp/role.sql

echo "2. Nouvelle base vidée (utile si la copie est relancée)"
nouveau -q -c "DROP SCHEMA IF EXISTS public CASCADE" -c "CREATE SCHEMA public"

echo "3. Export de l'ancienne base"
PGPASSWORD="$ANCIEN_MDP" pg_dump -h "$ANCIEN_HOTE" -U "$ANCIEN_UTILISATEUR" -d "$ANCIEN_BASE" \
    --no-owner --no-privileges --format=plain -f /tmp/parc.sql
echo "   $(wc -c < /tmp/parc.sql) octets"

echo "4. Import dans la nouvelle base (tout ou rien)"
nouveau -q --single-transaction -f /tmp/parc.sql > /dev/null
rm -f /tmp/parc.sql

echo "5. Vérification : nombre de lignes de chaque table"
REQUETE="SELECT string_agg(format('SELECT %L, count(*) FROM public.%I', tablename, tablename), ' UNION ALL ') FROM pg_tables WHERE schemaname = 'public'"
COMPTAGE="$(ancien -At -c "$REQUETE") ORDER BY 1"
ancien -At -F ' ' -c "$COMPTAGE" > /tmp/avant.txt
nouveau -At -F ' ' -c "$COMPTAGE" > /tmp/apres.txt
cat /tmp/apres.txt
if ! diff /tmp/avant.txt /tmp/apres.txt; then
    echo "ERREUR : les deux bases ne sont pas identiques (différences ci-dessus)."
    exit 1
fi
echo "COPIE TERMINÉE : $(wc -l < /tmp/apres.txt) tables identiques."
