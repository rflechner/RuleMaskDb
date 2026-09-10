# Banc d’intégration Docker

Ce banc exécute la vraie console RuleMaskDb publiée en Release, avec un YAML
pour SQL Server et un autre pour PostgreSQL. Les bases contiennent uniquement
40 personnes et 40 commandes synthétiques. Aucun port de base n’est exposé.

## Lancer et relancer

Prérequis : Docker avec les conteneurs Linux, Docker Compose v2 et PowerShell
(`pwsh` sur Linux/macOS, PowerShell sur Windows). SQL Server nécessite un hôte
x86-64 compatible et suffisamment de mémoire (prévoir au moins 4 Go pour Docker).
Le premier lancement télécharge les images et restaure les paquets NuGet.
Depuis la racine du dépôt :

```powershell
./tests/integration/run.ps1
# Réinitialisation complète des volumes des bases de ce banc :
./tests/integration/run.ps1 -Reset
```

Rapport : [http://localhost:8088](http://localhost:8088).
Changer le port avec `$env:RULEMASK_REPORT_PORT = '8090'` avant le lancement.
Le script retourne 0 si toutes les assertions réussissent, 1 en cas d’erreur.
Chaque relance recrée les deux tables du jeu de données ; `-Reset` supprime
également les volumes Docker du projet Compose `rulemaskdb-e2e`.
Ne pas lancer plusieurs instances simultanément avec ce nom de projet.

Les rapports sont disponibles dans `tests/integration/reports/` (ignoré par Git) :
`index.html`, `results.json`, journaux console et instantanés JSON avant/après.
Nginx reste actif après la fin du runner, même si celui-ci échoue.
En cas d’erreur de build ou de démarrage des bases, le script écrit un rapport
HTML d’échec. Si Docker ou Nginx ne peut pas démarrer, ouvrir ce fichier localement.
Un arrêt forcé du script ou de Docker ne permet pas de garantir un rapport final.

## Assertions

- Avant : les 40 lignes de chaque table correspondent exactement au jeu attendu.
- Après : nombre de lignes et clés primaires inchangés.
- Chaque `first_name` est remplacé par une valeur non vide sans marqueur source.
- Chaque `email` est remplacé par une adresse valide hors du domaine `.invalid`.
- Chaque `age` passe d’une sentinelle supérieure à 1000 à un entier entre 0 et 100,
  conformément au générateur `Age`.
- `reference`, `note` et ses NULL sont conservés exactement.
- La table `orders`, ses montants et ses relations restent intacts.

Le runner vérifie également le code de sortie de la console, limite chaque
exécution à trois minutes et poursuit le second moteur si le premier échoue.
Les assertions utilisent des lectures indépendantes des bases via ADO.NET ;
le runner ne référence pas la bibliothèque de production.
Les valeurs générées par Bogus sont aléatoires ; seuls le jeu initial et les
invariants vérifiés sont reproductibles. Les YAML ne demandent ni unicité
(non garantie par `Email`) ni masques (non implémentés par le moteur actuel).

## Vérifier le chemin d’échec

```powershell
./tests/integration/run.ps1 -BrokenConsole
# Doit retourner 1 et publier FAIL pour les deux moteurs.
./tests/integration/run.ps1
# Doit revenir à PASS après réinitialisation des fixtures.
```

Le mode `-BrokenConsole` pointe vers une DLL absente. Les bases restent dans
leur état initial : le code de sortie ET les assertions de transformation
échouent, ce qui démontre que le rapport ne dépend pas seulement du processus.

Pour exécuter manuellement après un premier lancement :

```powershell
docker compose -f tests/integration/compose.yaml run --rm runner
```

Ce raccourci produit les rapports des erreurs du runner ; utiliser le script
pour obtenir aussi un rapport en cas d’erreur d’infrastructure ou de build.
Pour arrêter et nettoyer :

```powershell
docker compose -f tests/integration/compose.yaml --profile report down --volumes --remove-orphans
```

Les identifiants présents dans les exemples sont exclusivement destinés aux
conteneurs jetables de ce banc. Les images utilisent des tags de versions
majeures : leurs correctifs peuvent évoluer entre deux reconstructions.

Contrôle négatif supplémentaire : `./tests/integration/run.ps1 -NoOpRules`
remplace les règles par une liste vide pour les deux moteurs. La console doit
retourner **0**, mais les assertions sur les données doivent faire échouer le
banc avec **1**. Relancer normalement pour republier un rapport de succès.

## Validation réalisée le 10 septembre 2026

Exécuté réellement avec Docker Engine 29.5.3 (conteneurs Linux), depuis Windows :

| Scénario | Résultat |
| --- | --- |
| Lancement normal | Code 0 ; 166 assertions par moteur, soit 332/332 |
| `-NoOpRules` | Code 1 ; 240 assertions de transformation échouent, consoles à 0 |
| `-BrokenConsole` | Code 1 ; 242 assertions échouent, dont les deux codes console |
| Docker inaccessible simulé | Code 1 ; rapport HTML d’erreur d’infrastructure |
| `-Reset`, reconstruction puis relance | Code 0 ; 332/332, aucune erreur ni avertissement de compilation |
| Rapport Nginx après échec puis succès | HTTP 200 et statut HTML correspondant |

Les contrôles portent sur les trois générateurs décrits ci-dessus et ce jeu
synthétique. Ils ne constituent pas un test de charge ni une validation de
l’ensemble des générateurs du projet.
