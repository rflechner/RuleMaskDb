


######################################################################
# Dockerfile PostgreSQL
#
# Ce Dockerfile s'appuie sur l'image officielle PostgreSQL et permet
# de lancer rapidement une base Postgres configurable via variables
# d'environnement. Les valeurs ci‑dessous servent de défauts et
# peuvent être surchargées au moment du `docker run` ou via Compose.
#
# Exemples d'utilisation:
#   Build (optionnel, on peut utiliser directement l'image officielle):
#     docker build -t local/postgres:latest dockerfiles/postgresql
#
#   Run:
#     docker run -d \
#       --name rulemaskdb-postgres \
#       -p 5432:5432 \
#       -e POSTGRES_USER=rulemask \
#       -e POSTGRES_PASSWORD=secret \
#       -e POSTGRES_DB=rulemaskdb \
#       -v rulemaskdb_pg_data:/var/lib/postgresql/data \
#       local/postgres:latest
#
#   Vérifier l'état:
#     docker logs -f rulemaskdb-postgres
#
# Remarques:
# - Les scripts placés dans /docker-entrypoint-initdb.d (si montés en volume)
#   seront exécutés au premier démarrage pour initialiser le schéma.
# - Le volume de données est /var/lib/postgresql/data.
######################################################################


# docker run -d --name rulemaskdb-postgres -p 5432:5432 -e POSTGRES_USER=rulemask -e POSTGRES_PASSWORD=secret -e POSTGRES_DB=rulemaskdb -v rulemaskdb_pg_data:/var/lib/postgresql/data postgres:latest
# jdbc:postgresql://localhost:5432/postgres?user=rulemask&password=secret

# Version de Postgres paramétrable
ARG POSTGRES_VERSION=16
FROM postgres:${POSTGRES_VERSION}-alpine

# Valeurs par défaut (surpassables à l'exécution via -e ...)
ENV POSTGRES_USER=postgres \
    POSTGRES_PASSWORD=postgres \
    POSTGRES_DB=postgres

# Port par défaut Postgres
EXPOSE 5432

# Healthcheck basique pour vérifier la disponibilité de Postgres
HEALTHCHECK --interval=10s --timeout=5s --start-period=10s --retries=5 \
  CMD pg_isready -h 127.0.0.1 -U "$POSTGRES_USER" -d "$POSTGRES_DB" || exit 1

# Point d'entrée et commande par défaut hérités de l'image officielle
# ENTRYPOINT ["docker-entrypoint.sh"]
# CMD ["postgres"]
