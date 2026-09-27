# Avion par terre — plugin Revit « Esquisse → DCE »

Koffi & Diabaté Architectes · Cellule IA · version 0.2.0 (27 septembre 2026) · Revit 2025 (.NET 8)

Plugin décrit dans [Plan_Plugin_Revit_Esquisse_DCE.md](../Plan_Plugin_Revit_Esquisse_DCE.md). Il produit le dossier graphique DCE Architecture à partir de la maquette et des ressources K&D (cartouches APD‑DCE, gabarits `APD-DCE_*`, étiquettes DCE, titres de vue, vues de dessin des menuiseries) en suivant les **normes relevées dans la maquette ouverte**.

## Nouveautés de la v0.2 (retours sur la v0.1, `evals/commentaires sur la v0.1.docx`)

| Retour | Réponse |
|---|---|
| Cases impossibles à cocher | Case à cocher active au premier clic dans toutes les listes (y compris l’audit). « Afficher dans Revit » sélectionne les éléments des lignes cochées. |
| « Cartouche introuvable » alors que la maquette a des A1 / A3 | Résolution tolérante : noms comparés sans accents ni casse, famille rechargée reconnue (« Cartouche A3 Horizontale1 »), puis **cartouche disponible du même format** (mesuré sur les feuilles existantes), puis format le plus proche. Chaque remplacement est signalé. |
| Mode avion par terre avec un LLM choisi sur OpenRouter | Bouton **Avion par terre** : production complète en une commande ; l’IA décide ce que ni vous ni la maquette n’avez décidé. Modèle libre (liste OpenRouter chargée dans Paramètres). |
| Onglet Paramètres | Bouton **Paramètres** : clé OpenRouter (chiffrée DPAPI), modèle, recherche internet, étapes, ressources imposées par projet, normes relevées, bibliothèque et profil documentaire. |
| Revêtement choisi dans une liste ou tapé | Colonnes Sol / Mur / Plafond en listes déroulantes modifiables (finitions du référentiel ou saisie libre). |
| Locaux non classés classés par l’IA, avec petite recherche internet | Bouton « Décider par l’IA » dans Finitions et étape Finitions du mode avion par terre ; plugin « web » d’OpenRouter pour les locaux non classés. |
| Icône du mode avion par terre (`tools/icon-anim`) | Icône du ruban (image 0) et animation de 60 images dans la fenêtre d’attente, rendues par `tools/render_icon.py`. |
| Étiquettes « Etiquette de pièces DCE » | Étiquettes de pièces DCE du modèle (1:50 / 1:100 ; 50è pour les carnets à défaut d’étiquette de détail). |
| Cartouche A0 de la maquette | Le profil « Normes du projet » reprend le cartouche, le format et l’échelle des plans existants (ici APD‑DCE A0, 1:100). |
| Titres de vue en texte simple, sans pictogramme, en Century Gothic | Famille « TITRE DE VUE » copiée en « Avion par terre - Titre de vue » (Century Gothic) ; la famille de l’agence n’est pas modifiée. |
| Retirer « (paramètres de type Revit…) », « (PEM test) », « (non inventées…) » ; Century Gothic | Bloc descriptif épuré en Century Gothic 2,5 mm ; « Prescriptions : N/A », ou prescriptions proposées par l’IA (à valider) en mode avion par terre. |

## Normes du projet ouvert (relevées automatiquement)

Commande **Paramètres > Normes du projet** ou **Audit** (lignes « normes_projet »). Sur la maquette LESE TEST :

- plans généraux : `Cartouche APD-DCE (A0-A1):A0`, 1:100, numéros `1.10`, `1.11`…, titres « PLAN DU REZ-DE-CHAUSSEE », « PLAN DU 1ER ETAGE », Lot « Architecture » ; un niveau déjà couvert par un plan de l’agence n’est pas dupliqué ;
- fiches menuiseries : `AL-nn` (aluminium) et `CB-nn` (bois), **nom de feuille = repère**, `Cartouche A3 Horizontale1`, Phase « CALEPIN ALUMINIUM » / « CALEPIN BOIS », titre du repère en haut à gauche ; une fiche existante de l’agence est conservée ;
- nomenclature de localisation : copie du modèle « Tableau quantitatif_… » de l’agence, filtrée sur le type ;
- données de pièces : Niv « +/-0.00 », « +3.92 » ; HSP / HSD en mètres « 2.80 ».

## Installation

```bash
powershell -ExecutionPolicy Bypass -File deploy/install.ps1
```

Revit fermé. Le script compile, copie le plugin dans `%APPDATA%\Autodesk\Revit\Addins\2025\AvionParTerre\` et installe `AvionParTerre.addin`. Au redémarrage, l’onglet **Avion par terre** apparaît.

## Commandes (onglet « Avion par terre »)

| Panneau | Commande | Rôle |
|---|---|---|
| Mode avion par terre | **Avion par terre** | Analyse (lecture seule) → consultation de l’IA → revue des décisions → production : données de pièces, finitions, plans, carnets, fiches, registre, PDF brouillon (option). Un seul groupe d’opérations : **Ctrl+Z annule tout**. |
| Mode avion par terre | **Paramètres** | OpenRouter (clé, modèle, recherche internet, température), étapes, confirmation avant écriture, ressources imposées, normes du projet. |
| Préparer | **Audit** | Contrôle sans écriture : ressources (et remplacements), normes du projet, informations projet, pièces, menuiseries, débordements. |
| Préparer | **Finitions** | Profils du référentiel, listes modifiables par support, « Décider par l’IA » ; écrit seulement les lignes cochées, conserve les saisies manuelles. |
| Produire | **Plans généraux** | Vue `DCE_<niveau>` par niveau, étiquettes DCE, feuille des normes du projet (« PLAN DU … », série 1.1x). |
| Produire | **Carnets de pièces** | Plan agrandi, 3D découpée, tableau, élévations `En-a…d` sur A3 (`Dn.00`, `Dn.01`…), titres en texte simple Century Gothic. |
| Produire | **Fiches menuiseries** | Fiche A3 par type selon la convention du lot (AL‑nn / CB‑nn), vue de dessin K&D ou vues générées, nomenclature de l’agence, bloc descriptif. |
| Émettre | **Registre**, **Export PDF**, **Référentiel** | Inchangés (v0.1). |

## Mode avion par terre : qui décide quoi

Ordre de priorité, jamais inversé : **utilisateur** (Paramètres, saisies) → **projet** (maquette, normes relevées) → **référentiel K&D** (profil clairement identifié) → **règles automatiques** → **IA** (modèle OpenRouter).

| Domaine | Projet / règle | IA |
|---|---|---|
| Niv, HSP, HSD | même local ou valeur usuelle du niveau ; altitude par rapport au RDC ; faux plafond ou plancher modélisé | — |
| Bibliothèque de finitions | choix de Paramètres | choisie d’après le projet |
| Finitions | valeurs déjà saisies conservées ; profil du référentiel si la famille et le libellé sont sans ambiguïté | classement des locaux non classés (recherche internet), profil, finitions manquantes ; hors référentiel signalé |
| Plans | niveaux couverts par un plan de l’agence conservés ; normes de numérotation et de cartouche | — |
| Carnets | familles proposées (sanitaires, cuisines, habitation, accueil) ; un carnet par local répété | classement des locaux non classés |
| Fiches | lot par préfixe ; fiches existantes conservées | lot des repères hors convention, prescriptions (à valider) |

Chaque décision est affichée avant écriture (option), puis enregistrée dans `<dossier du .rvt>\AvionParTerre\<modèle>\decisions\` (JSON + CSV) avec sa source, sa justification, les sources internet consultées, le modèle et le coût.

**Données envoyées à OpenRouter** : noms, niveaux et surfaces des locaux, repères et familles de menuiseries, informations projet (nom, client, adresse) et le référentiel de finitions. Aucune géométrie, aucun fichier Revit.

## Données

| Fichier | Contenu |
|---|---|
| `data/catalogue.json` | Base de finitions : 59 finitions, 4 bibliothèques, 65 profils, 379 lignes observées, familles de locaux. `python -X utf8 tools/build_catalogue.py`. |
| `data/profile.json` | Ressources K&D (noms acceptés), paramètres, profils DCE, numérotation par défaut, marges des cartouches, préfixes de menuiseries (dont CAV, ENSAV, PBD, PBT, ENSPLR). |
| `%APPDATA%\AvionParTerre\settings.json` | Paramètres utilisateur (clé chiffrée, modèle, étapes). |
| `<sorties>\choix-projet.json` | Ressources imposées, bibliothèque et profil documentaire du projet. |

Une copie de `profile.json` ou `catalogue.json` dans `<dossier du .rvt>\AvionParTerre\` prime sur celle du plugin.

## Architecture

- `src/AvionParTerre.Core` (net8.0, sans Revit) : catalogue, classement, suggestions, numérotation, titres de niveaux, composition, menuiseries, registre, **client OpenRouter**, **conseiller IA** (questions, validation des réponses contre le référentiel), **journal des décisions**.
- `src/AvionParTerre.Revit` (net8.0‑windows, WPF) : ruban, commandes, ressources K&D tolérantes, **normes du projet**, générateurs, **pilote automatique**, données de pièces, interface à la charte K&D.
- `tests/AvionParTerre.Core.Tests` : 48 tests (`dotnet test`).
- `Diagnostics.SmokeTest` / `Diagnostics.AutopilotTest` (IA simulée) / `Diagnostics.UiSnapshot` : tests de développement **dans un groupe de transactions annulé**.

## Limites connues de la v0.2

- Pas de test réel avec OpenRouter dans cette livraison (aucune clé disponible) : le circuit complet est vérifié avec une IA simulée ; la liste publique des modèles OpenRouter a été vérifiée.
- L’IA ne décide ni géométrie, ni dimensions, ni quantités : une HSP sans source dans la maquette reste vide.
- Une fiche existante de l’agence est conservée telle quelle (pas de complément automatique).
- Liens Revit et bâtiments répétés non comptés ; carnets limités à une pièce par groupe ; coupes/façades et plans thématiques à venir.
