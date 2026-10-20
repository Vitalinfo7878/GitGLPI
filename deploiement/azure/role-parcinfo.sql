-- Crée le compte PostgreSQL de l'application et lui confie la base « parcinfo ».
-- À exécuter avec le compte administrateur du serveur, connecté à la base « postgres » :
--   psql "host=<serveur> dbname=postgres user=<admin> sslmode=require" -v mdp='<mot de passe>' -f role-parcinfo.sql
-- La base « parcinfo » doit déjà exister (créée par creer-ressources.sh).
-- Aucune autre base du serveur n'est modifiée.

\set ON_ERROR_STOP on

SELECT format('CREATE ROLE parcinfo LOGIN PASSWORD %L', :'mdp')
WHERE NOT EXISTS (SELECT 1 FROM pg_roles WHERE rolname = 'parcinfo') \gexec
SELECT format('ALTER ROLE parcinfo LOGIN PASSWORD %L', :'mdp') \gexec

-- Sur Azure, l'administrateur n'est pas super-utilisateur : il devient temporairement membre du rôle
-- pour pouvoir lui confier la base (et rejouer le script sans erreur).
GRANT parcinfo TO CURRENT_USER;

-- Seul le compte de l'application peut se connecter à cette base
REVOKE CONNECT, TEMPORARY ON DATABASE parcinfo FROM PUBLIC;
GRANT CONNECT, TEMPORARY ON DATABASE parcinfo TO parcinfo;
ALTER DATABASE parcinfo OWNER TO parcinfo;

REVOKE parcinfo FROM CURRENT_USER;
