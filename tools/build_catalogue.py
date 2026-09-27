# -*- coding: utf-8 -*-
"""Construit data/catalogue.json à partir du référentiel de finitions K&D.

Sources :
- Referentiel_Finitions_Gammes_Projets_Locaux.md (profils de référence, sections 4 à 7 et 11)
- REFS/analyse_references/finitions/affectations_lues.json (lignes lues dans les grilles)
- REFS/analyse_references/finitions/manifest_sources.json (empreintes des fichiers)

Les profils sont des OBSERVATIONS de projets : leur statut reste « observation_source »
et toute réutilisation exige une validation pour le projet traité.

Usage : python tools/build_catalogue.py
"""
import json
import re
import unicodedata
from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]
DCE = ROOT.parent
LECTURE = DCE / "REFS" / "analyse_references" / "finitions" / "affectations_lues.json"
MANIFEST = DCE / "REFS" / "analyse_references" / "finitions" / "manifest_sources.json"
OUT = ROOT / "data" / "catalogue.json"

# ---------------------------------------------------------------------------
# 1. Finitions / systèmes normalisés : code -> (support, famille, désignation, alias sources)
# ---------------------------------------------------------------------------
F = {}


def fin(code, support, famille, designation, *aliases, remarque=None):
    F[code] = {
        "code": code,
        "support": support,
        "famille": famille,
        "designation": designation,
        "alias_sources": list(aliases),
        "remarque": remarque,
    }


# Sols
fin("SOL-CHAPE-BOUCH", "Sol", "sol_mineral_ciment", "Chape ciment bouchardée", "Chape ciment bourchardée",
    remarque="Libellé source « bourchardée » ; normalisation à confirmer.")
fin("SOL-CHAPE", "Sol", "sol_mineral_ciment", "Chape (finition détaillée à préciser)", "Chape")
fin("SOL-CHAPE-LISSE", "Sol", "sol_mineral_ciment", "Chape ciment lisse", "Chape ciment lisse")
fin("SOL-BETON-CIRE", "Sol", "sol_mineral_ciment", "Béton ciré", "Béton ciré")
fin("SOL-DALLAGE-TALOCHE", "Sol", "sol_mineral_ciment", "Dallage béton taloché", "Dallage béton taloché")
fin("SOL-DALLAGE-STRIE", "Sol", "sol_mineral_ciment", "Dallage béton strié", "Dallage béton strillé")
fin("SOL-DALLE-ANTIPOUSS", "Sol", "sol_mineral_ciment", "Dalle béton + finition anti-poussière",
    "Dalle en béton + finition anti poussière")
fin("SOL-BETON-DESACT-BEIGE", "Sol", "exterieur", "Béton désactivé teinte beige", "Béton desactivé Teinte Beige")
fin("SOL-BETON-DESACT-LAVE", "Sol", "exterieur", "Béton désactivé et lavé", "Béton desactivé et lavé")
fin("SOL-GRES-30x60", "Sol", "gres_cerame", "Grès cérame 30 × 60", "Grès cérame 30 x 60",
    remarque="Unité du format non indiquée dans les grilles.")
fin("SOL-GRES-60x120", "Sol", "gres_cerame", "Grès cérame 60 × 120", "Grès cérame 60 x 120")
fin("SOL-GRES-IMIT-TRAV", "Sol", "gres_imitation_pierre", "Grès cérame imitation travertin 60 × 120",
    "Grès cérame Imitation Travertin 60 x 120", "Grès cérame imitation travertin 60 x 120",
    remarque="Céramique, distincte du travertin naturel.")
fin("SOL-GRES-MOSAIQUE", "Sol", "gres_cerame", "Grès cérame mosaïque 30/60 × 75/110", "Grès cérame Mosaique 30/60 x 75/110")
fin("SOL-TRAV-30x60", "Sol", "pierre_naturelle", "Travertin 30 × 60", "Travertin 30 x 60")
fin("SOL-TRAV-60x120", "Sol", "pierre_naturelle", "Travertin 60 × 120", "Travertin 60 x 120")
fin("SOL-MARBRE-BLANC", "Sol", "pierre_naturelle", "Marbre blanc", "Marbre blanc")
fin("SOL-GRANITO-MARBRE", "Sol", "pierre_naturelle", "Granito de marbre blanc", "Granito de marbre blanc")
fin("SOL-MOQUETTE", "Sol", "sol_textile_souple", "Moquette", "Moquette")
fin("SOL-SOUPLE", "Sol", "sol_textile_souple", "Revêtement souple (nature à préciser)", "Revêtement souple",
    remarque="Ne pas convertir en moquette ou PVC sans précision.")
fin("SOL-FAUX-PLANCHER", "Sol", "systeme_sol", "Faux plancher technique (parement final à préciser)", "Faux plancher",
    remarque="Système de sol ; le revêtement final n'est pas précisé par l'intitulé.")
fin("SOL-PAVES-TC", "Sol", "exterieur", "Pavés en terre cuite", "Pavés en terre cuite")
fin("SOL-MIGNONETTES", "Sol", "exterieur", "Mignonettes", "Mignonettes")
fin("SOL-GRAVILLON-LAVE", "Sol", "exterieur", "Gravillon lavé", "Grvaillon lavé")
# Murs
fin("MUR-P800-ENDUIT", "Mur", "peinture_murale", "Peinture Pantex 800 avec enduit repassé", "Pantex 800 avec enduit repassé")
fin("MUR-P800-SANS", "Mur", "peinture_murale", "Peinture Pantex 800 sans enduit repassé", "Pantex 800 sans enduit repassé")
fin("MUR-PVELOUR-ENDUIT", "Mur", "peinture_murale", "Peinture Pantex velour avec enduit repassé", "Pantex velour avec enduit repassé")
fin("MUR-P1300", "Mur", "peinture_murale", "Peinture Pantex 1300", "Pantex 1300")
fin("MUR-ENDUIT-MONO", "Mur", "peinture_murale", "Enduit monocouche", "Enduit monocouche")
fin("MUR-IGNIF-LAVABLE", "Mur", "peinture_murale", "Peinture « ignifugée et lavable » (à qualifier)", "Peinture ignifugée et lavable",
    remarque="Désignation source sans classement vérifiable.")
fin("MUR-FAIENCE", "Mur", "revetement_zone_humide", "Faïence murale", "Faïence", "Revêtement mural Faience",
    remarque="Hauteur et emprise non indiquées dans les grilles.")
fin("MUR-FAIENCE-30x60", "Mur", "revetement_zone_humide", "Faïence GC 30 × 60", "Revêtement Faience GC 30 X 60")
fin("MUR-GRES-30x60", "Mur", "revetement_zone_humide", "Grès cérame mural 30 × 60", "Grès cérame 30 x 60")
fin("MUR-TRAVERTIN", "Mur", "pierre_naturelle", "Revêtement mural travertin", "Revêtement mural travertin")
fin("MUR-MARBRE-BLANC", "Mur", "pierre_naturelle", "Revêtement mural marbre blanc", "Revêtement mural marbre blanc")
fin("MUR-HAB-BOIS", "Mur", "habillage", "Habillage bois", "Habillage bois")
fin("MUR-BRUT", "Mur", "brut", "Béton brut de décoffrage", "Brut de decoffrage", "Béton brute de decoffrage")
fin("MUR-BRIQUE-TERRE", "Mur", "maconnerie_apparente", "Murs en brique de terre", "Murs en brique de terre")
fin("MUR-MICROPERF", "Mur", "acoustique", "Panneaux microperforés", "Panneaux microperforés")
fin("MUR-MIKODAM", "Mur", "acoustique", "Panneaux bois microperforé MIKODAM", "Panneau en bois micro perforé de MIKODAM")
fin("MUR-ACOUSTIQUE", "Mur", "acoustique", "Habillage mural acoustique (composition à préciser)", "Habillage mural Acoustique")
fin("MUR-GARNYTEX", "Mur", "enveloppe", "Peinture Pantex Garnytex", "Pantex Garnytex",
    remarque="Saisi en colonne murs pour une façade (PLA ligne 69) : affecter à l'enveloppe.")
# Plafonds / sous-faces
fin("PLF-BA13", "Plafond", "plafond_rapporte", "Plafond BA13 standard + peinture", "Placo BA 13 standard + Peinture")
fin("PLF-BA13-ENDUIT", "Plafond", "plafond_rapporte", "Plafond BA13 avec enduit repassé + peinture", "Placo BA 13 avec enduit repassé + Peinture")
fin("PLF-BA13-H", "Plafond", "plafond_rapporte", "Plafond BA13 hydrofuge + peinture", "Placo BA 13 hydrofuge+Peinture", "Placo BA 13 Hydrofuge + Peinture")
fin("PLF-CIMENT-EXT", "Plafond", "plafond_rapporte", "Plafond en plaque de ciment pour extérieur",
    "Placo plaque de ciment pour extérieur", "Faux-plafonds en plaqude ciment pour extérieurs")
fin("PLF-BOIS", "Plafond", "plafond_rapporte", "Faux-plafond bois", "Faux-plafond bois")
fin("PLF-LAVABLE", "Plafond", "plafond_rapporte", "Faux-plafond lavable (système à préciser)", "Faux-plafond lavable")
fin("PLF-ACOUSTIQUE", "Plafond", "acoustique", "Plafond acoustique (composition à préciser)", "Acoustique")
fin("PLF-MIKODAM", "Plafond", "acoustique", "Plafond bois microperforé MIKODAM", "Panneau en bois micro perforé de MIKODAM")
fin("PLF-IGNIF-LAVABLE", "Plafond", "peinture_sous_face", "Peinture « ignifugée et lavable » (à qualifier)", "Peinture ignifugée et lavable")
fin("SF-P800-DALLE", "Plafond", "peinture_sous_face", "Peinture Pantex 800 pour dalle", "Peintue pantex 800 pour dalle")
fin("SF-DALLE-P800-SANS", "Plafond", "peinture_sous_face", "Dalle + peinture Pantex 800 sans enduit repassé",
    "Dalle + Peinture pantex 800 sans enduit repassé")
fin("SF-DALLE-P800-ENDUIT", "Plafond", "peinture_sous_face", "Dalle + peinture Pantex 800 avec enduit repassé",
    "Dalle + Peinture pantex 800 avec enduit repassé")
fin("SF-BETON-BRUT", "Plafond", "brut", "Sous-face béton brut de décoffrage", "Béton brut de décoffrage", "Béton brute de décoffrage")
# Façades / enveloppe
fin("FAC-SWISSPEARL", "Façade", "enveloppe", "Revêtement SWISS PEARL", "Revêtement en SWISS PEARL")
fin("FAC-BETON-GRC", "Façade", "enveloppe", "Béton + GRC", "BETON + GRC")
fin("FAC-MARBRE-AGRAFE", "Façade", "enveloppe", "Marbre agrafé", "Marbre Agafé")
fin("FAC-GARNYTEX", "Façade", "enveloppe", "Peinture Garnytex", "Peinture garnytex")
fin("FAC-BRUT", "Façade", "enveloppe", "Brut de décoffrage (façade)", "Brut de decoffrage")

ALIAS = {}
for f in F.values():
    for a in f["alias_sources"]:
        ALIAS[(f["support"], a.strip().lower())] = f["code"]


def code_of(support, text):
    key = (support, text.strip().lower())
    if key not in ALIAS:
        raise KeyError(f"Finition non normalisée : {support} / {text}")
    return ALIAS[key]


# ---------------------------------------------------------------------------
# 2. Familles de locaux normalisées et mots-clés de classement (sans accents, minuscules)
# ---------------------------------------------------------------------------
FAMILLES = [
    ("sanitaire", "Sanitaires", ["wc", "toilette", "toilettes", "toil", "sanitaire", "sanitaires", "sde", "salle d eau",
                                 "salle de bain", "salle de bains", "sdb", "douche", "pmr", "lave mains", "vestiaire"]),
    ("cuisine_service", "Cuisine / restauration / service",
     ["cuisine", "kitchenette", "cafette", "cafe", "cafeteria", "office", "buanderie", "sechoir", "restaurant",
      "espace dejeuner", "refectoire", "lingerie"]),
    ("habitation", "Habitation", ["chambre", "sejour", "salon", "dressing", "sam", "salle a manger", "living",
                                  "suite", "salon prive", "sejour familial"]),
    ("administration", "Administration / travail",
     ["bureau", "bureaux", "bur", "direction", "directeur", "secretariat", "open space", "plateau", "reprographie",
      "repro", "comptabilite", "caisse", "gestion", "vigie", "operateur", "operateurs", "daf", "archives"]),
    ("accueil", "Accueil / représentation",
     ["accueil", "hall", "grand hall", "attente", "salle d attente", "reception", "lounge", "billetterie",
      "exposition", "espace exposition", "entree", "guichet", "guichets", "showroom", "espace convivial"]),
    ("reunion_spectacle", "Réunion / spectacle / pédagogie",
     ["reunion", "salle de reunion", "box reunion", "auditorium", "projection", "salle de projection", "conference",
      "atelier educatif", "ateliers educatif", "ateliers educatifs", "regie", "formation", "classe"]),
    ("production", "Production / artisanat", ["atelier", "bronzier", "potier", "ebeniste", "teinturier",
                                              "tisserand", "vannerie", "production"]),
    ("circulation", "Circulation", ["circulation", "degagement", "couloir", "sas", "escalier", "hall escalier",
                                    "cage d escalier", "palier", "coursive", "hall ascenseur", "passerelle",
                                    "circulation de secours"]),
    ("technique", "Technique / logistique",
     ["local technique", "local t", "chambre froide", "tgbt", "transfo", "groupe electrogene", "local ge", "cta", "drv", "serveur",
      "onduleur", "gaine", "niche", "ria", "cuve", "step", "menage", "entretien", "stockage", "reserve", "reserves",
      "poubelle", "guerite", "compteur", "cfa", "cfo", "plomberie", "local cvd", "pcs", "comptage", "reserve d eau",
      "local", "coffre fort"]),
    ("exterieur", "Extérieurs / enveloppe", ["terrasse", "cour", "parvis", "parking", "rampe", "balcon", "jardin",
                                             "loggia", "cheminement", "facade", "edicule", "sous face"]),
]

# ---------------------------------------------------------------------------
# 3. Bibliothèques de référence (section 11 du référentiel)
# ---------------------------------------------------------------------------
BIBLIOTHEQUES = [
    {"code": "KD_G1_VLG_ARTISANAT", "projet_source": "VLG", "gamme": "G1", "gamme_libelle": "Bas de gamme",
     "programme": "artisanat", "programme_libelle": "Village artisanal", "phase": "DCE", "date": "2024-10",
     "fichier": "TABLEAU DE FINITION - VLG - DCE.pdf", "reference_citation": "page PDF"},
    {"code": "KD_G2_APROMAC_TERTIAIRE", "projet_source": "APROMAC", "gamme": "G2", "gamme_libelle": "Moyen de gamme",
     "programme": "tertiaire", "programme_libelle": "Immeuble de bureaux et espaces associés", "phase": "APD",
     "date": "2025-09", "fichier": "TABLEAU DE FINITION APROMAC.pdf", "reference_citation": "page PDF"},
    {"code": "KD_G3_ROPAN_RESIDENCE", "projet_source": "ROPAN", "gamme": "G3", "gamme_libelle": "Haut de gamme",
     "programme": "residence", "programme_libelle": "Résidence officielle", "phase": "APD", "date": "2025-05",
     "affaire": "23VI1242", "fichier": "ROPAN_KDA_APD_TAB FINITION_V1.pdf",
     "reference_citation": "page PDF, couverture comprise (p.2 = grille imprimée 1)"},
    {"code": "KD_PUBLIC_PLANETARIUM", "projet_source": "PLANETARIUM", "gamme": None,
     "gamme_libelle": "Non précisée (bâtiment public)", "programme": "equipement_public_culturel",
     "programme_libelle": "Équipement public culturel et scientifique", "phase": None, "date": None,
     "fichier": "05 - GRILLE DE FINITION PLANETARIUM -QCC.xlsx", "reference_citation": "ligne Excel, feuille TABLEAU DE FINITION",
     "alerte": "Cellule A2 « TABLEAU DE FINITION - MAISON DE VILLE » : trace de titre à corriger, ne pas reclasser en habitat."},
]
LIB_OF = {"VLG": "KD_G1_VLG_ARTISANAT", "APO": "KD_G2_APROMAC_TERTIAIRE", "ROP": "KD_G3_ROPAN_RESIDENCE",
          "PLA": "KD_PUBLIC_PLANETARIUM"}

# ---------------------------------------------------------------------------
# 4. Profils de référence (combinaisons observées)
#    a(support, code, note=None, emprise=None)
# ---------------------------------------------------------------------------


def a(support, code, note=None):
    if code is None:
        return {"support": support, "finition": None, "etat": "non_indique", "note": note}
    assert code in F, code
    return {"support": support, "finition": code, "etat": "affecte_source", "note": note}


P = []


def prof(pid, libelle, famille, zone, locaux, applications, source, remarque=None):
    P.append({
        "id": pid,
        "bibliotheque": LIB_OF[pid.split("-")[0]],
        "libelle": libelle,
        "famille_local": famille,
        "zone": zone,
        "locaux_source": locaux,
        "applications": applications,
        "source": source,
        "remarque": remarque,
        "statut": "observation_source",
        "validation_pour_nouveau_projet": "requise",
    })


# VLG
prof("VLG-01", "Production courante", "production", "cours d'ateliers",
     ["Atelier bronzier", "Atelier potier", "Atelier vannerie", "Atelier ébéniste", "Atelier teinturier",
      "Atelier tisserand", "Atelier cuir", "Stockage"],
     [a("Sol", "SOL-CHAPE-BOUCH"), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "SF-BETON-BRUT")], "p.1–4")
prof("VLG-02", "Sanitaire associé aux cours", "sanitaire", "cours d'ateliers", ["Sanitaires des ensembles d'ateliers"],
     [a("Sol", "SOL-GRES-30x60"), a("Mur", "MUR-P800-ENDUIT"), a("Mur", "MUR-FAIENCE-30x60", "Emprise non déterminée"),
      a("Plafond", "SF-BETON-BRUT")], "p.1–4")
prof("VLG-03", "Sanitaire indépendant", "sanitaire", "sanitaires", ["Sanitaires F", "Sanitaires H"],
     [a("Sol", "SOL-GRES-30x60"), a("Mur", "MUR-P800-ENDUIT"), a("Mur", "MUR-FAIENCE-30x60", "Emprise non déterminée"),
      a("Plafond", None, "Affectation non indiquée")], "p.1")
prof("VLG-04", "Administration sèche", "administration", "administration",
     ["Gestion de stock", "Sélection", "Caisse", "Service commercial", "Comptabilité", "Secrétariat", "Accueil",
      "Circulation"],
     [a("Sol", "SOL-GRES-30x60"), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "PLF-BA13")], "p.2–3")
prof("VLG-05", "Sanitaire administration", "sanitaire", "administration", ["Sanitaire F", "Sanitaire H"],
     [a("Sol", "SOL-GRES-30x60"), a("Mur", "MUR-P800-ENDUIT"), a("Mur", "MUR-FAIENCE-30x60", "Emprise non déterminée"),
      a("Plafond", "PLF-BA13-H")], "p.2–3")
prof("VLG-06", "Showroom", "accueil", "showroom", ["Showroom", "Salle"],
     [a("Sol", "SOL-CHAPE-BOUCH"), a("Mur", "MUR-MICROPERF"), a("Plafond", "PLF-BA13")], "p.4")
prof("VLG-07", "Circulation du showroom", "circulation", "showroom", ["Circulation intérieure"],
     [a("Sol", "SOL-CHAPE-BOUCH"), a("Mur", "MUR-BRUT"), a("Plafond", "SF-BETON-BRUT")], "p.4")
prof("VLG-08", "Cheminement extérieur", "exterieur", "extérieurs", ["Circulations des cours", "Cheminement vers showroom"],
     [a("Sol", "SOL-PAVES-TC"), a("Mur", None), a("Plafond", None)], "p.4–5")
prof("VLG-09", "Pourtour showroom", "exterieur", "extérieurs", ["Circulation extérieure autour du showroom"],
     [a("Sol", "SOL-MIGNONETTES"), a("Mur", None), a("Plafond", None)], "p.5")
prof("VLG-10", "Cour extérieure", "exterieur", "extérieurs", ["Cour du métal", "Cour du cuir", "Cour des artefacts",
                                                            "Cour du bois", "Cour du textile"],
     [a("Sol", "SOL-BETON-DESACT-BEIGE"), a("Mur", "MUR-BRIQUE-TERRE"), a("Plafond", None)], "p.5")
# APROMAC
prof("APO-01", "Travail et réunion", "administration", "étages de bureaux",
     ["Bureau individuel", "Bureau directeur", "DAF", "Plateau", "Box réunion", "Salle de réunion", "Reprographie",
      "Cafette", "Rangement"],
     [a("Sol", "SOL-FAUX-PLANCHER", "Parement final non indiqué"), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "PLF-BA13")],
     "p.3–9")
prof("APO-02", "Accueil et représentation", "accueil", "accueil",
     ["Entrée", "Grand hall", "Lounge", "Espace convivial", "Espace exposition", "Hall ascenseur", "Espace déjeuner"],
     [a("Sol", "SOL-GRES-IMIT-TRAV"), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "PLF-BA13")], "p.2–5, 7, 9–11")
prof("APO-03", "Sanitaires", "sanitaire", "sanitaires", ["Toilettes H", "Toilettes D", "Toilettes PMR", "WC"],
     [a("Sol", "SOL-GRES-30x60"), a("Mur", "MUR-FAIENCE"), a("Plafond", "PLF-BA13-H")], "p.2–3, 4–8, 10")
prof("APO-04", "Circulation courante", "circulation", "circulations", ["Circulation", "SAS"],
     [a("Sol", "SOL-GRES-30x60"), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "PLF-BA13")], "p.1–2, 4–6, 8, 10")
prof("APO-05", "Escalier / circulation à dalle peinte", "circulation", "cages d'escalier",
     ["Cage d'escalier", "Escalier", "Dégagement", "SAS sous-sol 1"],
     [a("Sol", "SOL-GRES-30x60"), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "SF-P800-DALLE")], "p.1, 3–7, 9–11")
prof("APO-06", "Hall ascenseur sous-sol 2", "circulation", "sous-sol 2", ["Hall ascenseur"],
     [a("Sol", "SOL-GRES-30x60"), a("Mur", "MUR-TRAVERTIN"), a("Plafond", "PLF-BA13")], "p.1")
prof("APO-07", "Hall ascenseur sous-sol 1", "circulation", "sous-sol 1", ["Hall ascenseur"],
     [a("Sol", "SOL-GRES-30x60"), a("Mur", "MUR-TRAVERTIN"), a("Plafond", "SF-P800-DALLE")], "p.1")
prof("APO-08", "Auditorium", "reunion_spectacle", "RDC", ["Auditorium"],
     [a("Sol", "SOL-MOQUETTE"), a("Mur", "MUR-MIKODAM"), a("Plafond", "PLF-MIKODAM")], "p.3")
prof("APO-09", "Parking intérieur", "exterieur", "sous-sols", ["Parking"],
     [a("Sol", "SOL-DALLAGE-TALOCHE"), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "SF-P800-DALLE")], "p.1–2")
prof("APO-10", "Technique STEP", "technique", "sous-sol", ["Local technique STEP"],
     [a("Sol", "SOL-CHAPE", "Finition détaillée non indiquée"), a("Mur", "MUR-BRUT"), a("Plafond", "SF-P800-DALLE")], "p.1")
prof("APO-11", "Technique plomberie / RIA", "technique", "sous-sol", ["Local technique plomberie", "RIA sous rampe"],
     [a("Sol", "SOL-CHAPE", "Finition détaillée non indiquée"), a("Mur", "MUR-BRUT"), a("Plafond", "SF-BETON-BRUT")], "p.1")
prof("APO-12a", "Technique à chape lisse — Local T RDC", "technique", "RDC", ["Local T"],
     [a("Sol", "SOL-CHAPE-LISSE"), a("Mur", "MUR-P800-SANS"), a("Plafond", "SF-P800-DALLE")], "p.2")
prof("APO-12b", "Technique à chape lisse — transfo, TGBT, Local T étage 1", "technique", "RDC / étage 1",
     ["Local transfo", "TGBT", "Local T"],
     [a("Sol", "SOL-CHAPE-LISSE"), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "PLF-BA13")], "p.2–3")
prof("APO-12c", "Technique à chape lisse — Local T étage 3", "technique", "étage 3", ["Local T"],
     [a("Sol", "SOL-CHAPE-LISSE"), a("Mur", "MUR-P800-SANS"), a("Plafond", "PLF-BA13")], "p.6")
prof("APO-13a", "Technique carrelé — local technique étage 2", "technique", "étage 2", ["Local technique"],
     [a("Sol", "SOL-GRES-30x60"), a("Mur", "MUR-P800-SANS"), a("Plafond", "PLF-BA13")], "p.5")
prof("APO-13b", "Technique carrelé — locaux T étages 4–6, onduleur, PCS, serveur, comptage", "technique",
     "RDC / étages 4 à 6", ["Local T", "Local technique", "Onduleur", "PCS", "Serveur", "Comptage"],
     [a("Sol", "SOL-GRES-30x60"), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "PLF-BA13")], "p.2, 8, 10")
prof("APO-14", "Groupe électrogène", "technique", "RDC", ["Local GE", "Groupe électrogène"],
     [a("Sol", "SOL-DALLAGE-TALOCHE"), a("Mur", "MUR-P800-SANS"), a("Plafond", "PLF-BA13")], "p.3")
prof("APO-15", "Cuve", "technique", "RDC", ["Local cuve"],
     [a("Sol", "SOL-DALLE-ANTIPOUSS"), a("Mur", "MUR-IGNIF-LAVABLE"), a("Plafond", "PLF-IGNIF-LAVABLE")], "p.3",
     remarque="« Ignifugée et lavable » : désignation source à qualifier techniquement.")
prof("APO-16", "Extérieurs", "exterieur", "extérieurs", ["Parking extérieur", "Parvis"],
     [a("Sol", "SOL-GRAVILLON-LAVE"), a("Mur", None), a("Plafond", None)], "p.11")
prof("APO-17", "Rampe", "exterieur", "extérieurs", ["Rampe d'accès sous-sol"],
     [a("Sol", "SOL-DALLAGE-TALOCHE"), a("Mur", None), a("Plafond", None)], "p.11")
# ROPAN
prof("ROP-01", "Réception à sous-face brute", "accueil", "zone de fonction", ["Bureau", "Salle à manger", "Salon de fonction"],
     [a("Sol", "SOL-TRAV-60x120"), a("Mur", "MUR-PVELOUR-ENDUIT"), a("Plafond", "SF-BETON-BRUT")], "p.2")
prof("ROP-02", "Réception à plafond rapporté", "accueil", "zone de fonction",
     ["Cuisine européenne", "Circulation de fonction", "Salle d'attente"],
     [a("Sol", "SOL-TRAV-60x120"), a("Mur", "MUR-PVELOUR-ENDUIT"), a("Plafond", "PLF-BA13")], "p.2")
prof("ROP-03", "Terrasse de fonction", "exterieur", "zone de fonction", ["Terrasse de fonction"],
     [a("Sol", "SOL-TRAV-60x120"), a("Mur", "MUR-PVELOUR-ENDUIT"), a("Plafond", "PLF-BA13", "Répartition non indiquée"),
      a("Plafond", "SF-BETON-BRUT", "Répartition non indiquée")], "p.2")
prof("ROP-04", "WC réception", "sanitaire", "zone de fonction", ["WC visiteurs"],
     [a("Sol", "SOL-TRAV-60x120"), a("Mur", "MUR-TRAVERTIN"), a("Plafond", "PLF-BA13")], "p.2",
     remarque="Plafond standard en zone de fonction, hydrofuge en zone privée (ROP-08) : arbitrage requis.")
prof("ROP-05", "Habitation privée sèche", "habitation", "zone privée",
     ["Chambre 2", "Chambre 3", "Chambre 4", "Chambre principale", "Salon privé", "SAM privée", "Séjour familial"],
     [a("Sol", "SOL-TRAV-60x120"), a("Mur", "MUR-PVELOUR-ENDUIT"), a("Plafond", "SF-BETON-BRUT")], "p.3")
prof("ROP-06", "Dressing et circulation nuit", "habitation", "zone privée",
     ["Dressing 2", "Dressing 3", "Dressing 4", "Dressing Mr", "Dressing Mme", "Circulation zone nuit"],
     [a("Sol", "SOL-TRAV-60x120"), a("Mur", "MUR-PVELOUR-ENDUIT"), a("Plafond", "PLF-BA13")], "p.2–3")
prof("ROP-07", "Salles d'eau marbre", "sanitaire", "zone privée", ["Salle d'eau 2", "Salle d'eau 3", "Salle d'eau 4"],
     [a("Sol", "SOL-MARBRE-BLANC"), a("Mur", "MUR-MARBRE-BLANC"), a("Plafond", "PLF-BA13-H")], "p.2")
prof("ROP-08", "Sanitaires privés travertin", "sanitaire", "zone privée", ["WC P", "WC visiteurs", "Salle de bain 1"],
     [a("Sol", "SOL-TRAV-60x120"), a("Mur", "MUR-TRAVERTIN"), a("Plafond", "PLF-BA13-H")], "p.2–3")
prof("ROP-09", "Terrasses / coursives privées", "exterieur", "zone privée", ["Parvis 2", "Coursive", "Terrasse privée", "Terrasse"],
     [a("Sol", "SOL-TRAV-60x120"), a("Mur", "MUR-ENDUIT-MONO"), a("Plafond", "SF-BETON-BRUT")], "p.2–3")
prof("ROP-10", "Services", "cuisine_service", "zone services",
     ["Cour de service", "Séchoir", "Cuisine africaine", "Buanderie", "Réserve", "Chambre de service",
      "Séjour de service", "Circulation", "SDE SCE"],
     [a("Sol", "SOL-GRES-60x120"), a("Mur", "MUR-PVELOUR-ENDUIT"), a("Plafond", "PLF-LAVABLE")], "p.3–4",
     remarque="SDE de service sans revêtement mural humide coché : à rapprocher des détails avant réemploi.")
prof("ROP-11", "Locaux techniques / guérite", "technique", "locaux techniques",
     ["TGBT", "Groupe électrogène", "Transfo", "Guérite", "Local poubelle", "Réserve d'eau"],
     [a("Sol", "SOL-GRES-60x120"), a("Mur", "MUR-P1300"), a("Plafond", "SF-BETON-BRUT")], "p.4–5")
prof("ROP-12", "Sanitaire technique", "sanitaire", "locaux techniques", ["Sanitaire"],
     [a("Sol", "SOL-GRES-60x120"), a("Mur", "MUR-GRES-30x60"), a("Plafond", "SF-BETON-BRUT")], "p.4")
prof("ROP-13", "Circulation technique", "circulation", "locaux techniques", ["Circulation", "Coursive"],
     [a("Sol", "SOL-GRES-60x120"), a("Mur", "MUR-ENDUIT-MONO"), a("Plafond", "SF-BETON-BRUT")], "p.4")
# PLANETARIUM
prof("PLA-01", "Accueil avec acoustique", "accueil", "RDC", ["Espace exposition", "Dessous escalier", "Billetterie"],
     [a("Sol", "SOL-GRES-IMIT-TRAV"), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "PLF-ACOUSTIQUE", "Emprise à préciser"),
      a("Plafond", "SF-DALLE-P800-SANS", "Emprise à préciser")], "lignes 12, 14")
prof("PLA-02", "Hall principal", "accueil", "RDC", ["Hall", "Dessous escalier"],
     [a("Sol", "SOL-GRES-IMIT-TRAV"), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "SF-DALLE-P800-SANS")], "ligne 13")
prof("PLA-03", "Sas publics RDC", "circulation", "RDC", ["SAS Entrée 1", "SAS Entrée 2"],
     [a("Sol", "SOL-GRES-IMIT-TRAV"), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "PLF-BA13-ENDUIT")], "lignes 20, 27")
prof("PLA-04", "Passerelle", "circulation", "RDC", ["Passerelle"],
     [a("Sol", "SOL-GRES-IMIT-TRAV"), a("Mur", None), a("Plafond", "PLF-BA13-ENDUIT")], "ligne 28")
prof("PLA-05", "Travail, réunion et pédagogie", "administration", "RDC / R+1",
     ["Salle de réunion", "Ateliers éducatifs", "Bureau", "Bureau direction", "Secrétariat", "Hall escalier 3",
      "Circulation"],
     [a("Sol", "SOL-GRES-60x120"), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "PLF-ACOUSTIQUE", "Emprise à préciser"),
      a("Plafond", "SF-DALLE-P800-SANS", "Emprise à préciser")], "lignes 18, 21–25, 49–58")
prof("PLA-06", "Sanitaires RDC", "sanitaire", "RDC", ["Sanitaires H", "Sanitaires PMR", "Sanitaires F"],
     [a("Sol", "SOL-GRES-60x120"), a("Mur", "MUR-FAIENCE"), a("Plafond", "PLF-BA13-ENDUIT", "Colonne L (standard)")],
     "lignes 15–17", remarque="RDC standard (L) contre R+1 hydrofuge (M) : conserver et arbitrer.")
prof("PLA-07", "WC R+1", "sanitaire", "R+1", ["WC 1", "WC 2", "WC"],
     [a("Sol", "SOL-GRES-60x120"), a("Mur", "MUR-FAIENCE"), a("Plafond", "PLF-BA13-H", "Colonne M")], "lignes 43, 44, 48")
prof("PLA-08", "Technique courant", "technique", "RDC", ["AEP/RIA", "Stockage", "Ménage", "CFA", "SAS 1", "SAS 2"],
     [a("Sol", "SOL-BETON-CIRE"), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "SF-DALLE-P800-SANS")], "lignes 29–32, 38, 40")
prof("PLA-09", "Petits locaux techniques", "technique", "RDC", ["Niche compteur électrique", "Gaine PLB"],
     [a("Sol", "SOL-BETON-CIRE"), a("Mur", "MUR-P800-SANS"), a("Plafond", "SF-DALLE-P800-SANS")], "lignes 33, 36")
prof("PLA-10", "TGBT", "technique", "RDC", ["Local TGBT"],
     [a("Sol", "SOL-BETON-CIRE"), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "SF-DALLE-P800-ENDUIT")], "ligne 34")
prof("PLA-11", "Technique traité acoustiquement", "technique", "RDC", ["Local DRV", "Local serveur", "Local CTA"],
     [a("Sol", "SOL-BETON-CIRE"), a("Mur", "MUR-P800-SANS"), a("Mur", "MUR-ACOUSTIQUE", "Emprise à préciser"),
      a("Plafond", "PLF-ACOUSTIQUE"), a("Plafond", "SF-DALLE-P800-SANS")], "lignes 35, 37, 39")
prof("PLA-12", "Local CVD", "technique", "RDC", ["Local CVD"],
     [a("Sol", "SOL-GRES-60x120"), a("Mur", "MUR-P800-SANS"), a("Mur", "MUR-ACOUSTIQUE", "Emprise à préciser"),
      a("Plafond", "PLF-ACOUSTIQUE"), a("Plafond", "SF-DALLE-P800-SANS")], "ligne 10")
prof("PLA-13", "Gaines / niches, variante courante", "technique", "RDC / R+1", ["Niche RIA 1", "Gaine CVD", "Niche RIA"],
     [a("Sol", "SOL-GRES-60x120"), a("Mur", "MUR-P800-SANS"), a("Plafond", "PLF-ACOUSTIQUE", "Emprise à vérifier"),
      a("Plafond", "SF-DALLE-P800-SANS")], "lignes 26, 45–47, 59–60")
prof("PLA-14", "Niche RIA, variante accueil", "technique", "RDC", ["Niche RIA 2"],
     [a("Sol", "SOL-GRES-IMIT-TRAV"), a("Mur", "MUR-P800-SANS"), a("Plafond", "PLF-ACOUSTIQUE"),
      a("Plafond", "SF-DALLE-P800-SANS")], "ligne 11")
prof("PLA-15", "Gaine CFO/CFA", "technique", "RDC", ["Gaine CFO/CFA"],
     [a("Sol", "SOL-GRES-60x120"), a("Mur", "MUR-P800-SANS"), a("Plafond", "SF-DALLE-P800-SANS")], "ligne 19")
prof("PLA-16", "CFA R+1", "technique", "R+1", ["Local CFA 1", "Local CFA 2"],
     [a("Sol", "SOL-BETON-CIRE"), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "PLF-ACOUSTIQUE"),
      a("Plafond", "SF-DALLE-P800-SANS")], "lignes 61–62")
prof("PLA-17", "Galerie de projection", "reunion_spectacle", "R+1", ["Galerie technique"],
     [a("Sol", "SOL-SOUPLE"), a("Mur", "MUR-P800-ENDUIT", "Teinte noire (H63)"), a("Plafond", "PLF-ACOUSTIQUE"),
      a("Plafond", "SF-DALLE-P800-SANS", "Teinte noire (O63)")], "ligne 63")
prof("PLA-18", "Sas de projection", "circulation", "R+1", ["SAS Sortie", "SAS Entrée"],
     [a("Sol", "SOL-SOUPLE"), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "PLF-ACOUSTIQUE"),
      a("Plafond", "SF-DALLE-P800-SANS")], "lignes 64–65", remarque="La teinte noire de la galerie ne se propage pas aux sas.")
prof("PLA-19", "Régie", "reunion_spectacle", "R+1", ["Régie"],
     [a("Sol", "SOL-SOUPLE"), a("Mur", "MUR-P800-SANS"), a("Mur", "MUR-ACOUSTIQUE"), a("Plafond", "PLF-ACOUSTIQUE"),
      a("Plafond", "SF-DALLE-P800-SANS")], "ligne 66")
prof("PLA-20", "Projection", "reunion_spectacle", "R+1", ["Salle de projection"],
     [a("Sol", "SOL-SOUPLE"), a("Mur", "MUR-P800-SANS", "Teinte noire (G67)"), a("Mur", "MUR-ACOUSTIQUE", "Emprise à préciser"),
      a("Plafond", "PLF-ACOUSTIQUE", "Composition à préciser"), a("Plafond", "SF-DALLE-P800-SANS", "Teinte noire (O67)")],
     "ligne 67")
prof("PLA-21", "Édicule", "exterieur", "toiture", ["Édicule"],
     [a("Sol", None), a("Mur", "MUR-P800-ENDUIT"), a("Plafond", "SF-DALLE-P800-ENDUIT")], "ligne 68")
prof("PLA-22", "Façade", "exterieur", "enveloppe", ["Façade"],
     [a("Façade", "FAC-GARNYTEX", "Saisi en colonne murs J69 ; affecter à l'enveloppe")], "ligne 69")

# ---------------------------------------------------------------------------
# 5. Observations brutes : lignes lues dans les grilles, avec rubrique courante
# ---------------------------------------------------------------------------
PROJ_TO_LIB = {"VLG": "KD_G1_VLG_ARTISANAT", "APROMAC": "KD_G2_APROMAC_TERTIAIRE", "ROPAN": "KD_G3_ROPAN_RESIDENCE",
               "PLANETARIUM": "KD_PUBLIC_PLANETARIUM"}


def build_observations():
    data = json.loads(LECTURE.read_text(encoding="utf-8"))
    obs = []
    rubrique = {}
    for i, r in enumerate(data["rows"]):
        proj = r["project"]
        sel = r["selected"]
        # Une ligne sans sélection est une rubrique (zone, niveau, cour...)
        if not sel:
            rubrique[proj] = r["label"].strip()
            continue
        ref = f"p.{r['page']}" if "page" in r else f"ligne {r['row']}"
        apps = []
        for s in sel:
            sup = s["support"]
            code = code_of(sup, s["finish"])
            app = {"support": sup, "finition": code, "libelle_source": s["finish"]}
            if s.get("cell"):
                app["cellule"] = s["cell"]
            if s.get("note"):
                app["note"] = s["note"]
            apps.append(app)
        obs.append({
            "id": f"{proj}-{i:03d}",
            "bibliotheque": PROJ_TO_LIB[proj],
            "libelle_source": r["label"].strip(),
            "rubrique": rubrique.get(proj),
            "reference": ref,
            "applications": apps,
        })
    return obs, data["catalogue"]


def main():
    obs, catalogue_entetes = build_observations()
    # statut "propose_en_entete" vs "affecte" par bibliothèque
    utilises = {}
    for o in obs:
        for ap in o["applications"]:
            utilises.setdefault(o["bibliotheque"], set()).add(ap["finition"])
    entetes = {}
    for proj, items in catalogue_entetes.items():
        lib = PROJ_TO_LIB[proj]
        entetes[lib] = []
        for it in items:
            c = code_of(it["support"], it["text"])
            entetes[lib].append({"finition": c, "libelle_source": it["text"],
                                 "etat": "affecte_dans_une_ligne" if c in utilises.get(lib, set()) else "propose_en_entete"})

    manifest = json.loads(MANIFEST.read_text(encoding="utf-8"))
    sha = {m["file"]: m["sha256"] for m in manifest}
    for b in BIBLIOTHEQUES:
        b["sha256"] = sha.get(b["fichier"])
        b["colonnes_entete"] = entetes.get(b["code"], [])

    out = {
        "schema": "avion-par-terre/catalogue",
        "schema_version": 1,
        "version": "1.0.0",
        "date": "2026-09-27",
        "source": "Referentiel_Finitions_Gammes_Projets_Locaux.md v1.0",
        "avertissement": "Profils et observations = combinaisons observées sur des projets précis. "
                         "Validation obligatoire avant usage comme prescription d'un autre projet.",
        "supports": ["Sol", "Mur", "Plafond", "Façade"],
        "familles_locaux": [{"code": c, "libelle": l, "mots_cles": k} for c, l, k in FAMILLES],
        "finitions": list(F.values()),
        "bibliotheques": BIBLIOTHEQUES,
        "profils": P,
        "observations": obs,
    }
    OUT.parent.mkdir(parents=True, exist_ok=True)
    OUT.write_text(json.dumps(out, ensure_ascii=False, indent=1), encoding="utf-8")
    print(f"{OUT} : {len(F)} finitions, {len(P)} profils, {len(obs)} observations")


if __name__ == "__main__":
    main()
