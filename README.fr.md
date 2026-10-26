# TaskbarVisu

*[English](README.md) · Français*

![TaskbarVisu](docs/showcase.png)

Visualiseurs audio intégrés à la barre des tâches de Windows 11, façon MiniMeters :
des modules compacts (125 px par défaut) dans un style « labo / y2k » — police matricielle 5×7,
graduations fines, segments LED, cases de lecture façon afficheur, couleurs selon la fréquence —
posés dans les espaces libres à gauche et à droite des icônes, sans cadre.

## Modules

| Module | Ce qu'il montre |
|---|---|
| **Spectrogramme** | Historique des fréquences qui défile, encoches 100 Hz / 1 kHz / 10 kHz |
| **Forme d'onde** | Miroir, enveloppe ou oscilloscope déclenché ; couleur selon le contenu fréquentiel |
| **Niveaux crête + LUFS** | Crête-mètre L/R à segments, graduations, maintien des crêtes, écrêtage, sonie BS.1770 avec cible et lecture inversée |
| **VU-mètre** | Style LED (arc à segments, tête lumineuse et traînée) ou analogique (aiguille), balistique ~300 ms, mono ou stéréo |
| **Analyseur de spectre** | Relief façon MiniMeters (courbe lissée ombrée selon le niveau, trace fine du canal « côtés »), barres, points LED, courbe remplie ou ligne ; crêtes maintenues ; lecture du pic (niveau, fréquence, note) |
| **Imageur stéréo** | Vectorscope polaire coloré par bande + panorama par fréquence (ou largeur par fréquence) ; corrélation en axe coloré ou en barre |
| **Historique de sonie** | Courbe LUFS sur 15 s à 5 min (barres LED ou courbe), ligne de cible |
| **Accordeur** | 2 modes : note + justesse (±50 cents), tonalité (gamme) avec confiance et, pour un morceau modal, sa note centrale (« ◆F MIX ») ; mini-piano LED, notation C D E, Do Ré Mi ou Camelot ; en option, les tonalités précédentes du morceau (« <C <Aᵐ ») |
| **Piano roll** | Notes réellement entendues, octave comprise, justes à quelques cents (analyse dédiée en deux résolutions), sur un clavier de 3 à 6 octaves ; leur historique défile au-dessus (couleur selon la hauteur ou l'intensité, clavier en bas ou en haut) |
| **Oscilloscope XY** | Goniomètre (milieu / côtés) ou Lissajous (gauche / droite) tracé en phosphore qui s'éteint lentement, réticule gradué ; par moments, le tracé lui-même se change en dessin façon « musique d'oscilloscope » (étoile, cœur, petit personnage…), à un moment et un endroit au hasard, tremble avec le son puis redevient le son |
| **Données brutes** | Les échantillons tels que la carte son les voit : lignes hexadécimales qui défilent ou matrice de bits. Purement expérimental |

Disposition par défaut : niveaux, spectrogramme et spectre à gauche ; imageur, accordeur et forme d'onde
à droite. VU-mètre, historique de sonie, piano roll, oscilloscope XY et données brutes
sont disponibles mais masqués (page Disposition). Au lancement, chaque module fait un court
*self-test* (balayage LED, « OK ») avant de s'allumer ; désactivable dans Général.

### Détection de tonalité

Analyse dédiée haute résolution (`Dsp/KeyAnalyzer.cs`, utilisée par l'accordeur et
calculée une seule fois par image) : FFT 16384 points (~2,9 Hz par case), séparation harmonique /
percussive par médiane temporelle, pics tonaux interpolés, accordage global estimé (432 Hz, voix
pitchée…), chromagramme HPCP avec harmoniques, chroma de basse, vote de trois profils
(Krumhansl-Kessler, Temperley, Albrecht-Shanahan) + accord de tonique et basse pour départager
majeur / mineur relatif, hystérésis et indice de confiance.

Quand le mode n'est pas tranché (tierce absente, ou tierces majeure et mineure à égalité), l'accordeur
l'indique (« MAJ? » / « MIN? ») et atténue sur le piano les notes qui dépendent du mode, plutôt que
d'affirmer un résultat arbitraire. L'hystérésis s'adapte : un changement net de tonalité est pris en
compte en 0,5 s, un écart minime en 2,5 s.

Seule la musique tenue compte. Chaque bloc reçoit un score : énergie concentrée dans des pics × pics qui
restent exactement en place d'un bloc à l'autre (la voix glisse sans arrêt, une toux n'a pas de pics
stables). Lissé sur ~1,5 s, ce score doit dépasser 0,45 pour que la musique « commence », et la musique
continue tant qu'il reste au-dessus de 0,30 (passages très chantés). Mesuré : voix type Discord, bavardage,
toux et notification plafonnent à 0,39 ; la musique reste au-dessus de 0,43 neuf fois sur dix. Chaque
bloc compte aussi pour la tonalité en proportion de son score. Sans musique depuis 3 s, l'accordeur
affiche « -- » ; après 10 s, la tonalité est oubliée et le morceau suivant repart de zéro.

Morceaux modaux : beaucoup de pop actuelle joue les notes d'une gamme en se posant sur une autre note que sa
tonique (gamme de Si♭ majeur posée sur Fa = Fa mixolydien, dans *Tonight* de PinkPantheress). L'accordeur garde
la gamme comme tonalité et ajoute alors la note centrale et le mode en abrégé (« ◆F MIX » ; ION, DOR, PHR, LYD,
MIX, AEO, LOC), avec un curseur au-dessus de la touche sur le mini-piano. La note centrale est celle qui domine la
tonique à la fois dans la basse (+25 %, au-dessus de La1 pour ignorer les grosses caisses) et dans les notes
réellement jouées (+10 %, harmoniques retirées), pendant au moins 3 s. Sur les morceaux non modaux, rien ne
s'ajoute. Les sites de référence ne s'accordent pas sur ces morceaux : Tunebat donne tantôt la gamme
(*House Tour* : F♯), tantôt le centre (*Tonight* : Fm).
La tonalité est estimée sur les 30 dernières secondes de musique par défaut (réglable de 5 à 90 s) : plus
proche des outils qui donnent une tonalité par morceau (Tunebat, Mixed In Key), tout en suivant un
changement de morceau grâce à l'oubli après un silence.

### Notes jouées (piano roll)

Rien n'est deviné : chaque note s'allume selon son énergie propre. Le piano roll a sa propre analyse
(`Dsp/NoteTracker.cs`), recalculée toutes les 40 ms :

- deux résolutions : FFT de 16384 points sous 150 Hz (demi-tons serrés de la basse), 8192 au-dessus
  (fenêtre de 170 ms, plus réactive) ;
- fréquence de chaque pic par réallocation (fenêtre de Hann et sa dérivée dans une seule FFT complexe),
  juste à quelques cents près même entre deux cases ;
- un pic ne compte que s'il dépasse d'au moins 16 dB le plancher de son voisinage : le bruit, la batterie
  et les lobes des notes voisines d'un accord serré n'en produisent pas ;
- hystérésis : une note s'allume après deux analyses au-dessus du seuil et ne s'éteint qu'après deux
  analyses 6 dB plus bas, sans clignoter.

Les harmoniques sont retirées par un crible partagé avec l'analyse de tonalité (`Dsp/HarmonicSieve.cs`),
du grave à l'aigu : chaque note « explique » au plus 1,15/h de son amplitude au ×h, et seulement si sa
série harmonique monte jusque-là (inharmonicité du piano estimée sur la série, fenêtre élargie côté aigu
pour les partiels confondus avec ceux d'une autre note, partiels masqués par une note voisine tolérés).
Une basse presque pure n'efface donc pas la tierce qui tombe sur son ×5, une octave doublée au clavier
reste visible, et les harmoniques de plusieurs notes qui se superposent s'additionnent au lieu de passer
pour une note.

Limites connues : une note sous ~40 Hz (avant Mi1) n'est pas vue ; des octaves empilées en dents de scie
(Do2 + Do4 + Do5) peuvent faire apparaître faiblement l'octave du dessus (~ -18 dB).

### Banc de test

`tests/key-bench.csv` : extraits réels (réponses justifiées par les notes présentes, recoupées avec
Tunebat et Auto-Key 2), morceaux entiers (MP3, références Tunebat / Hooktheory / Beatport : la bonne
tonalité doit être affichée au moins 70 % du temps), morceaux synthétiques à réponse connue, sons non
musicaux, et accords tenus dont il faut retrouver exactement les notes (`chord:…`, `notes:C2,C4,D#4…`).
Les fichiers audio ne sont pas fournis : ils sont cherchés dans les dossiers donnés (WAV, MP3, M4A, FLAC…).

```
TaskbarVisu.exe --headless --bench rapport.txt tests\key-bench.csv <dossier> [<dossier>…]
TaskbarVisu.exe --headless --analyze rapport.txt morceau.mp3   (KEY_SLOTS=1 : notes et basse par tranches de 20 s)
TaskbarVisu.exe --headless --notetrace trace.txt notes:C2,C4,D#4,G4,C5,D5   (notes entendues, image par image)
```

Résultat actuel : 56/60 — 23 tonalités d'extraits (6 réels, dont 3 correctement signalés comme ambigus, et
17 synthétiques, dont désaccordages de -32 à +35 cents et bruit fort), 5 morceaux entiers sur 9 (Stateside
B♭m 100 %, House Tour D♭ 98 %, Capable of Love G 90 %, take me by the hand A 100 %, Watcher A♭ 71 %), 6 cas
sans musique ou enchaînés (voix, toux, notification, silence entre deux morceaux) et 22 accords.

Cas non résolus, tous modaux ou à sections :
- *Flower In A Hurricane* (Justice), C♯m pour Hooktheory, Beatport et Ultimate Guitar : affiché B 52 % du
  temps. Le morceau alterne des sections centrées sur Do♯ et d'autres sur Si, et le profil de Temperley, issu
  du répertoire classique, pénalise la septième mineure (Si) très présente dans cette pop.
- *Tonight* (PinkPantheress), Fm pour Tunebat (Cm pour BPM Supreme) : gamme de Si♭ majeur posée sur Fa,
  affiché « B♭ · ◆F MIX ».
- *going green* (Dom Corleo), G pour Tunebat : gamme de Ré majeur, basse et notes sur F♯, affiché
  « Bm · ◆F♯ PHR ».
- *plague dogs* (Oklou), G♯m pour Tunebat (Dm pour Songdata) : le centre change selon les passages
  (G♯m/B, D♯m, F♯).

Octaves scientifiques : Do4 = 261,6 Hz (FL Studio numérote une octave plus haut). Variable `KEY_PEAKS=1` :
la trace liste aussi les pics de chaque image (fréquence:amplitude:part non expliquée).

## Apparence

- Préréglages en un clic : Labo, MiniMeters, CRT Phosphore, Synthwave, Vaporwave, Chrome Y2K, Toxique, RGB,
  et trois afficheurs matériels : VFD (tube fluorescent cyan), LCD (encre sombre sur fond olive), Nixie
  (tubes orange).
- Police pixel 5×7 (ou monospace lissée), graduations et repères techniques, effet scanlines.
- Palette : Magma, Brasier, Spectrale, Phosphore, Glace, RGB (arc-en-ciel), Synthwave,
  Vaporwave, Toxique, Chrome Y2K, VFD, LCD, Nixie, Monochrome, accentuation Windows ou dégradé
  personnalisé (grave → aigu) de 2 à 8 couleurs : clic sur la barre pour ajouter une couleur, repères à
  glisser, clic droit pour retirer, inversion, point de départ pris d'une palette existante.
- Animation des couleurs, pour toutes les palettes : défilement vers les aigus ou les graves, une couleur à la fois, va-et-vient,
  pulsation ou aucune, avec la durée du cycle (0,2 à 60 s). La palette RGB défile par défaut.
- Textes et repères : lavande, teintés par la palette, accentuation Windows ou couleur choisie au sélecteur.
- Chaque module peut avoir ses propres couleurs : une autre palette, son propre dégradé ou une couleur unie.
- Pas de fond : les modules se fondent dans la barre (fond écran LCD olive clair en option, pour le style LCD).
- Mes styles : le style actuel (couleurs, effets, fond, couleurs des modules) enregistré sous un nom et réappliqué
  en un clic depuis Apparence ou Accueil ; le menu « ⋯ » le met à jour, le renomme, copie son code ou le supprime.
- Partager un style : code court `TBV1:…` (JSON compressé, à coller sur Discord) ou fichier `.tbvstyle`.
  Le style comprend palette, effets, fond et couleurs de chaque module, pas la disposition.

## Fonctionnement

- **Capture** : WASAPI, au choix :
  - tout le son du PC (*loopback* sur la sortie par défaut, qui suit le changement de casque ou d'enceintes) ;
  - une sortie précise (plusieurs cartes son) ;
  - une seule application (*process loopback* de Windows 11 : FL Studio sans Discord, par exemple ;
    TaskbarVisu attend l'application si elle est fermée et la reprend quand elle redémarre) ;
  - une entrée (micro, ligne, platine).
- **Placement** : la barre des tâches est localisée (haut ou bas, compacte ou non, DPI de l'écran),
  puis ses éléments sont lus via UI Automation (Démarrer, icônes, zone de notification) et les fenêtres
  tierces posées dessus (FluentFlyout, widgets…) sont prises en compte. Les deux plus grands espaces
  libres deviennent la zone gauche et la zone droite. Si les icônes sont alignées à gauche, le grand
  espace unique est partagé en deux, ou tous les modules (gauche puis droite) sont groupés en son centre.
- **Multi-écran** : écran principal, un écran choisi, ou tous les écrans à la fois (une barre suivie et
  un rendu par écran, sur la même capture audio).
- **Rendu** : deux fenêtres Win32 *layered* par barre, transparentes aux clics, toujours au-dessus de la
  barre, dessinées par SkiaSharp (CPU) directement dans une DIB 32 bits. Aucun fond : seul le tracé est
  opaque, avec un fondu sur les bords de chaque module.
- **Masquage** : application en plein écran, barre masquée automatiquement, pause, et raccourci clavier
  global (Ctrl + Alt + V par défaut, ou Ctrl + Maj + V, Ctrl + Alt + H) qui fonctionne même en jeu.
- **Mises à jour** : au démarrage puis une fois par jour, la dernière release GitHub est comparée à la
  version installée (désactivable). Version installée : un clic télécharge l'installeur, vérifie son
  empreinte SHA-256 publiée par GitHub, l'installe en silence et relance TaskbarVisu. Version portable :
  la page de téléchargement s'ouvre. Rien n'est installé sans clic.
- **Barre verticale** : non prise en charge (les visualiseurs restent masqués).

## Économie de ressources

- Un seul thread de rendu cadencé par un timer haute résolution (30 à 144 i/s, 60 par défaut).
- Une seule FFT par image pour tous les modules (Mid et Side calculés dans la même FFT complexe),
  recalculée au plus tous les ¼ de fenêtre.
- Parties statiques (graduations, grille, textes) mises en cache ; aucune allocation par image.
- En silence, une fois l'affichage figé, plus aucun rendu (≈ 0 % CPU).
- Détection de la barre déclenchée par événements (ouverture / fermeture d'applications) plus une
  vérification de secours espacée (3 s par défaut).
- Fenêtre de réglages libérée à la fermeture, puis mémoire rendue au système (collecte compactante et
  ensemble de travail réduit) : pas d'accumulation au fil des ouvertures.
- Exe non compressé : les assemblys .NET sont lues directement dans le fichier au lieu d'être
  décompressées en mémoire (≈ 80 Mo de mémoire privée en moins ; le zip et l'installeur restent compressés).
- Petites collectes forcées tous les 4 Mo alloués : sur un processeur à gros cache L3, le budget par défaut
  de la génération 0 laisserait la mémoire grimper de plusieurs dizaines de Mo avant la première collecte.
- Courbes du spectre tracées en traits fins de 1 px superposés (≈ 10× plus rapide qu'un trait épais lissé).

Mesure indicative (Ryzen 7 9800X3D, 2560×1440 à 125 %, 6 modules de 125 px, 60 i/s, signal continu) :
≈ 0,54 ms de rendu par image, ≈ 4 % d'un cœur, ≈ 35 Mo de mémoire privée et ≈ 25 à 40 Mo d'ensemble de
travail, stables une fois les historiques remplis. Ouvrir les réglages charge WPF ; à la fermeture,
l'ensemble de travail redescend sous ≈ 30 Mo.

## Langues

Français, English, Español, Deutsch : la langue de Windows est utilisée automatiquement
(anglais si elle n'est pas disponible), modifiable dans Général > Langue. Les traductions
sont dans `Core/Loc.cs` (le texte français sert de clé).

## Réglages

Clic sur l'icône de la zone de notification → fenêtre de réglages, comme les Paramètres de Windows 11 :
menu à gauche, et chaque réglage est une carte avec une icône et une phrase d'explication.

- **Accueil** : ce qui est écouté, la place libre sur la barre, boutons Masquer / Pause, les styles en un
  clic, les modules affichés (un clic ouvre le module dans Disposition) et les raccourcis utiles.
- **Général** : démarrage (avec Windows, fermer vers la zone de notification, animation d'allumage),
  source audio, langue, écran (principal, tous, ou un écran précis), plein écran, raccourci clavier,
  mises à jour, i/s, résolution FFT, économie d'énergie, dossier des réglages.
- **Apparence** : préréglages en cartes (le style actif est entouré), couleurs (palette, animation des couleurs,
  textes, clair / sombre), effets, Mes styles (enregistrer le style actuel), partage de style replié.
- **Disposition** : la barre des tâches en miniature, modules à l'échelle et place restante par zone.
  Un clic sur un module ouvre ses réglages (largeur de 60 à 400 px, décaler, changer de côté, retirer,
  options) ; on peut aussi le glisser. En dessous : ajout des modules masqués (+ Gauche / + Droite),
  icônes alignées à gauche (deux zones ou tout au centre), puis les réglages fins repliés (étirement ou
  placement, espacement, fondu, marge verticale).
- **Guide de démarrage** : au premier lancement, cinq étapes avec « Passer » toujours visible. Les zones
  libres clignotent sur la vraie barre, choix d'une ambiance (préréglage) et d'un pack de modules
  (Studio, Musicien, Minimal ou « Je choisis moi-même », qui part d'une barre vide), source audio, démarrage et raccourci, puis une
  flèche sur la barre montre l'icône de la zone de notification. Il se rejoue depuis Accueil ou Général.

  ![Guide de démarrage](docs/welcome.png)

- **Modules** : options propres à chaque module, réinitialisables ; modules affichés d'abord, puis les masqués
  (avec « Afficher sur la barre »).

Réglages et journal : `%APPDATA%\TaskbarVisu\` (`settings.json`, `log.txt`). Mode portable : avec un fichier
`portable.txt` à côté de `TaskbarVisu.exe` (fourni dans le zip portable), ils vont dans le dossier `data`
à côté de l'exe.

## Compiler et lancer

Télécharger : la page [Releases](../../releases) propose un **installeur** (sans droits administrateur,
menu Démarrer, désinstallation propre) et une version **portable** (un seul .exe autonome, rien à
installer). Windows 11, x64.

Prérequis pour compiler : SDK .NET 10.

```
cd src/TaskbarVisu
dotnet run -c Release
```

Construire une release complète en une commande (version lue dans le `.csproj`) :

```
powershell -ExecutionPolicy Bypass -File tools/build-release.ps1
```

Le script produit dans `build/` l'exe autonome (`portable/`), le zip portable (avec `portable.txt` et
`LISEZMOI.txt`), l'installeur Inno Setup 6, `SHA256SUMS.txt` et les manifestes winget
(`winget/manifests/2/26mataa/TaskbarVisu/<version>/`, identifiant `26mataa.TaskbarVisu`). Rien n'est
publié : la release GitHub et la soumission winget (pull request sur `microsoft/winget-pkgs`, une fois
l'installeur en ligne à l'adresse du manifeste) se font à la main. L'installeur accepte `/RELAUNCH=1`
(utilisé par la mise à jour intégrée) pour relancer TaskbarVisu après une installation silencieuse.

Arguments : `--tray` (démarre sans ouvrir les réglages, utilisé par le démarrage avec Windows),
`--dump <dossier>` (diagnostic : PNG du rendu et profil toutes les 5 s : temps et octets alloués par
module, mémoire, collectes et types les plus alloués),
`--selftest-ui` (diagnostic : construit la fenêtre de réglages et le guide hors écran puis quitte ; avec
`--dump`, captures de chaque onglet et de chaque étape du guide),
`--guide zones|tray` (diagnostic : affiche un repère du guide de démarrage sur la barre),
`--test-signal` (diagnostic : signal synthétique à la place de la capture, rien n'est joué),
`--headless` (diagnostic : instance séparée qui n'affiche aucune fenêtre),
`--lang fr|en|es|de` (diagnostic : force une langue),
`--test-key Am[:h|:n|:s] [--test-detune cents] [--test-noise 0.3]` (diagnostic : morceau synthétique dans une tonalité),
`--test-file fichier` (diagnostic : lit un WAV ou un MP3 en boucle à la place de la capture),
`--analyze rapport fichier…` et `--bench rapport csv dossier…` (diagnostic : analyse hors ligne en accéléré),
`--selftest-reorder` (diagnostic : déplace un module au bout de 4 s pour tester le changement à chaud).
La variable d'environnement `TASKBARVISU_SETTINGS` permet d'utiliser un autre fichier de réglages.

L'icône est générée par `tools/make-icon.ps1`.

## Structure

```
src/TaskbarVisu/
  Audio/       capture WASAPI (loopback, sortie, application, entrée), tampon circulaire, signaux de test
  Dsp/         FFT, LUFS (BS.1770), tonalité, notes jouées et crible harmonique, analyse hors ligne
  Taskbar/     localisation de la barre, zones libres (UI Automation + fenêtres tierces)
  Rendering/   fenêtres overlay, boucle de rendu, thème, disposition, caches, animation d'allumage
  Modules/     les 11 visualiseurs + catalogue d'options
  UI/          fenêtre de réglages, icône de notification
  Core/        réglages, traductions, démarrage avec Windows, journal, raccourci, mises à jour, codes de style
installer/     script de l'installeur (Inno Setup)
tools/         script de release, génération de l'icône
tests/         banc de test (tonalités, notes, sons non musicaux)
docs/          captures
```

## Licence

GPL-3.0, voir [LICENSE](LICENSE). Bibliothèques : [NAudio](https://github.com/naudio/NAudio) (MIT) et
[SkiaSharp](https://github.com/mono/SkiaSharp) (MIT). Inspiré de MiniMeters, sans lien avec ce logiciel.
