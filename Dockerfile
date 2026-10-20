# Image de l'application « Parc informatique » pour Azure Container Apps
# Construction : docker build -t parc-informatique .

FROM mcr.microsoft.com/dotnet/sdk:10.0 AS construction
WORKDIR /src
COPY src/ParcInfo.Web/ParcInfo.Web.csproj src/ParcInfo.Web/
RUN dotnet restore src/ParcInfo.Web/ParcInfo.Web.csproj
COPY src/ src/
# Pas de --no-restore : le SDK n'ajoute le paquet des scripts Blazor (blazor.web.js) qu'en voyant les fichiers .razor
RUN dotnet publish src/ParcInfo.Web/ParcInfo.Web.csproj -c Release -o /publication

FROM mcr.microsoft.com/dotnet/aspnet:10.0
# Fuseau horaire de Paris pour l'affichage des heures (l'application stocke tout en UTC)
ENV TZ=Europe/Paris \
    ASPNETCORE_HTTP_PORTS=8080 \
    BaseDeDonnees__Fournisseur=Postgres
WORKDIR /app
COPY --from=construction /publication .
# Utilisateur non administrateur fourni par l'image Microsoft
USER $APP_UID
EXPOSE 8080
ENTRYPOINT ["dotnet", "ParcInfo.Web.dll"]
