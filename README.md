# Gmail sur le bureau

Widget Windows 10/11 en WPF qui :

- affiche le nombre de messages reçus depuis le dernier clic sur **J'ai vérifié** ;
- affiche les derniers mails de la boîte avec expéditeur, objet et heure ;
- ouvre directement un mail dans Gmail quand tu cliques sur sa ligne ;
- vérifie Gmail automatiquement toutes les 60 secondes ;
- affiche une notification Windows lorsque le nombre augmente ;
- affiche le panneau Gmail sur toute la largeur du widget ;
- se place par défaut sur l'écran principal, dans la colonne droite du bureau ;
- se comporte comme une fenêtre widget normale sans premier plan : les fenêtres ouvertes le recouvrent ;
- se lance automatiquement au démarrage de Windows, sans demander les droits administrateur ;
- mémorise sa position, sa taille et l'instant du dernier check ;
- ne lit que les métadonnées nécessaires à l'affichage (expéditeur, objet, date) et n'enregistre pas le contenu des mails.

## Mises à jour automatiques

Le dépôt public **Widget personnalisé** publie automatiquement une version Windows
à chaque modification de la branche `main`. Le widget vérifie périodiquement la
version publiée et affiche une petite flèche dans son en-tête lorsqu'une mise à
jour est disponible. Un clic télécharge le paquet, vérifie son empreinte SHA-256,
préserve `credentials.json`, les réglages et le jeton Gmail, puis redémarre le
widget avec la nouvelle version.

La première installation doit donc être faite avec la version qui contient
`Update-Windows.ps1`. Après cela, les mises à jour se font depuis le widget, sans
relancer le SDK .NET ni refaire la configuration Gmail.

Pour gérer facilement l'application, utilise le bouton `⚙` dans l'en-tête du widget,
le menu `Gérer le widget` de l'icône près de l'horloge, ou le raccourci de bureau
`Gestion du widget`. Le centre de contrôle permet de vérifier une mise à jour,
actualiser Gmail, afficher le widget, réparer le démarrage automatique et ouvrir les
dossiers utiles.

Le dépôt et les paquets sont publics pour permettre le téléchargement sans jeton
GitHub dans l'application. Le fichier `credentials.json` est exclu du dépôt et
est toujours conservé uniquement sur le PC.

## Important : deux connexions distinctes

La connexion Gmail dans ChatGPT permet à ChatGPT de consulter Gmail quand tu le lui demandes. Le widget Windows est une application locale : il doit obtenir sa propre autorisation Google OAuth. Il ne réutilise pas le jeton de ChatGPT.

## Première configuration Gmail

1. Dans Google Cloud Console, crée ou sélectionne un projet.
2. Active **Gmail API**.
3. Configure l'écran de consentement OAuth. Pour un compte personnel, ajoute ton adresse comme utilisateur test si Google le demande.
4. Crée un identifiant client OAuth de type **Application de bureau**.
5. Télécharge le fichier JSON, renomme-le `credentials.json` et place-le à côté de `MailWidget.exe`.
6. Lance le widget. Le navigateur s'ouvre une fois pour autoriser la lecture Gmail.

L'autorisation demandée est en lecture seule (`gmail.readonly`). Le widget exclut les messages envoyés, brouillons, spam et corbeille, mais inclut les messages entrants archivés.

## Compiler sous Windows

### Installation en un clic

Le moyen le plus simple est de double-cliquer sur `Installer-Windows.cmd`. Le script installe le SDK .NET 8 si nécessaire, ferme automatiquement les anciennes instances pendant une mise à jour, fabrique la nouvelle version dans un dossier de publication neuf pour éviter les verrous Windows/antivirus, crée un raccourci sur le bureau et lance le widget.

Il faut toujours fournir `credentials.json` pour l'accès Gmail. Si tu le places dans le dossier `MailWidget` avant de lancer le script, il sera copié automatiquement à côté de l'exécutable.

### Préparation manuelle

Installe le SDK .NET 8, puis ouvre PowerShell dans ce dossier :

```powershell
dotnet restore
dotnet build -c Release
dotnet run -c Release
```

Pour créer un dossier autonome en 64 bits :

```powershell
dotnet publish -c Release -r win-x64 --self-contained true /p:PublishSingleFile=true
```

Le dossier publié se trouve dans `bin\Release\net8.0-windows\win-x64\publish`.

## Placement

Au premier lancement, le widget est placé sur l'écran principal avec :

- une marge de 18 px à droite ;
- un espace supérieur de 145 px pour rester sous la zone d'icônes en haut à droite ;
- un espace inférieur de 92 px pour rester au-dessus de l'icône Ordinateur.

Tu peux le déplacer simplement en faisant glisser la barre supérieure. Pour modifier sa forme, attrape la poignée visible dans le coin inférieur droit et tire-la jusqu'à la largeur et la hauteur souhaitées. La nouvelle position et la nouvelle taille sont mémorisées. Le contenu défile si le widget devient compact.

Quand Chrome ou une fenêtre de dossier est ouverte, elle recouvre naturellement le widget. Pour le faire réapparaître, ferme ou réduis la fenêtre ouverte, ou double-clique sur l'icône Gmail dans la zone de notification.

## Erreur Google 403 `org_internal`

Si Google affiche `Erreur 403 : org_internal`, l'application OAuth est probablement configurée en **Interne**. Pour un compte Gmail personnel, ouvre la console Google Cloud, va dans **Google Auth platform > Audience**, choisis **External**, puis ajoute l'adresse Gmail utilisée comme **Test user**. En mode test, Google peut afficher un avertissement d'application non vérifiée : il faut alors poursuivre avec l'option avancée proposée par Google.

Après cette modification, relance la connexion depuis le widget. Si une ancienne autorisation incomplète bloque encore la fenêtre Google, ferme le widget et supprime uniquement le dossier `%LOCALAPPDATA%\MailWidget\GoogleToken`, puis réessaie. Ne supprime pas `credentials.json`.

## Réinitialiser la configuration

Ferme le widget depuis l'icône de la zone de notification, puis supprime :

```text
%LOCALAPPDATA%\MailWidget\settings.json
```

Le jeton OAuth est stocké séparément dans `%LOCALAPPDATA%\MailWidget\GoogleToken`. Pour retirer complètement l'autorisation, supprime ce dossier et révoque l'accès de l'application dans ton compte Google.
