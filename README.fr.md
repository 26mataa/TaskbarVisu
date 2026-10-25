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
| **Analyseur de spectre** | Barres, points LED, courbe remplie ou ligne ; crêtes maintenues ; note détectée (Hz, note, cents) |
| **Imageur stéréo** | Vectorscope polaire coloré par bande + panorama par fréquence (ou largeur par fréquence) ; corrélation en axe coloré ou en barre |
| **Historique de sonie** | Courbe LUFS sur 15 s à 5 min (barres LED ou courbe), ligne de cible |
| **Accordeur** | 2 modes : note + justesse (±50 cents), tonalité (gamme) avec confiance et, pour un morceau modal, sa note centrale (« ◆F MIX ») ; mini-piano LED, notation C D E, Do Ré Mi ou Camelot |
| **Piano roll** | Notes réellement entendues, octave comprise, sur un clavier de 3 à 6 octaves ; leur historique défile au-dessus (couleur selon la hauteur ou l'intensité, clavier en bas ou en haut) |

Disposition par défaut : niveaux, spectrogramme et spectre à gauche ; imageur, accordeur et forme d'onde
à droite. VU-mètre, historique de sonie et piano roll sont disponibles mais masqués (onglet Disposition).

### Détection de tonalité

Analyse dédiée haute résolution (`Dsp/KeyAnalyzer.cs`, partagée par l'accordeur et le piano roll et
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

Rien n'est deviné : chaque note s'allume selon son énergie propre. Les harmoniques sont retirées par
soustraction, du grave à l'aigu : chaque note « explique » au plus 1,15/h de son amplitude au ×h, et
seulement si sa série harmonique est complète jusque-là (inharmonicité du piano estimée sur la série,
partiels masqués par une note voisine tolérés). Une basse presque pure n'efface donc pas la tierce qui
tombe sur son ×5, une octave doublée au clavier reste visible, et les harmoniques de plusieurs notes qui
se superposent s'additionnent au lieu de passer pour une note.

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

- Préréglages en un clic : Labo, MiniMeters, CRT Phosphore, Synthwave, Vaporwave, Chrome Y2K, Toxique, RGB.
- Police pixel 5×7 (ou monospace lissée), graduations et repères techniques, effet scanlines.
- Palette : Magma, Brasier, Spectrale, Phosphore, Glace, Arc-en-ciel RGB, Arc-en-ciel animé, Synthwave,
  Vaporwave, Toxique, Chrome Y2K, Monochrome, accentuation Windows ou personnalisée (grave → aigu).
- Textes et repères : lavande, teintés par la palette, accentuation Windows ou couleur choisie au sélecteur.
- Chaque module peut avoir ses propres couleurs : une autre palette ou une couleur unie.
- Fond des modules : aucun (fondu dans la barre), sombre translucide ou noir.

## Fonctionnement

- **Capture** : WASAPI en *loopback* sur la sortie par défaut (tout ce que le PC joue),
  avec bascule automatique quand le périphérique par défaut change.
- **Placement** : la barre des tâches est localisée (haut ou bas, compacte ou non, DPI de l'écran),
  puis ses éléments sont lus via UI Automation (Démarrer, icônes, zone de notification) et les fenêtres
  tierces posées dessus (FluentFlyout, widgets…) sont prises en compte. Les deux plus grands espaces
  libres deviennent la zone gauche et la zone droite. Si les icônes sont alignées à gauche, le grand
  espace unique est partagé en deux.
- **Rendu** : deux fenêtres Win32 *layered* transparentes aux clics, toujours au-dessus de la barre,
  dessinées par SkiaSharp (CPU) directement dans une DIB 32 bits. Aucun fond : seul le tracé est opaque,
  avec un fondu sur les bords de chaque module.
- **Masquage automatique** : application en plein écran, barre masquée automatiquement, pause.
- **Barre verticale** : non prise en charge (les visualiseurs restent masqués).

## Économie de ressources

- Un seul thread de rendu cadencé par un timer haute résolution (30 à 144 i/s, 60 par défaut).
- Une seule FFT par image pour tous les modules (Mid et Side calculés dans la même FFT complexe),
  recalculée au plus tous les ¼ de fenêtre.
- Parties statiques (graduations, grille, textes) mises en cache ; aucune allocation par image.
- En silence, une fois l'affichage figé, plus aucun rendu (≈ 0 % CPU).
- Détection de la barre déclenchée par événements (ouverture / fermeture d'applications) plus une
  vérification de secours espacée (3 s par défaut).
- Fenêtre de réglages libérée de la mémoire à la fermeture.

Mesure indicative (Ryzen 7 9800X3D, 2560×1440 à 125 %, 6 modules de 125 px, 60 i/s, signal continu) :
≈ 0,45 ms de rendu par image, ≈ 3 % d'un cœur, ≈ 37 Mo de mémoire privée.

## Langues

Français, English, Español, Deutsch : la langue de Windows est utilisée automatiquement
(anglais si elle n'est pas disponible), modifiable dans Général > Langue. Les traductions
sont dans `Core/Loc.cs` (le texte français sert de clé).

## Réglages

Clic sur l'icône de la zone de notification → fenêtre de réglages (style Windows 11) :

- **Général** : démarrer avec Windows, fermer vers la zone de notification, écran (principal par
  défaut, au choix si plusieurs), plein écran, i/s, résolution FFT, économie d'énergie.
- **Apparence** : style, palette, couleurs, fond, opacité.
- **Disposition** : modules à gauche / à droite, ordre et changement de côté (glisser-déposer ou
  boutons), largeur (60 à 400 px), étirement ou placement (contre le bord, au centre, contre les
  icônes / la zone de notification), espacement, fondu, marge verticale, réinitialisation.
- **Modules** : options propres à chaque module, réinitialisables.

Réglages et journal : `%APPDATA%\TaskbarVisu\` (`settings.json`, `log.txt`).

## Compiler et lancer

Télécharger : la page [Releases](../../releases) propose un **installeur** (sans droits administrateur,
menu Démarrer, désinstallation propre) et une version **portable** (un seul .exe autonome, rien à
installer). Windows 11, x64.

Prérequis pour compiler : SDK .NET 10.

```
cd src/TaskbarVisu
dotnet run -c Release
```

Construire la version portable (autonome) puis l'installeur (Inno Setup 6) :

```
dotnet publish src/TaskbarVisu -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -p:DebugType=none -o build/portable
ISCC.exe /DAppVersion=0.8.3 installer/TaskbarVisu.iss
```

Arguments : `--tray` (démarre sans ouvrir les réglages, utilisé par le démarrage avec Windows),
`--dump <dossier>` (diagnostic : PNG du rendu et profil toutes les 5 s),
`--selftest-ui` (diagnostic : construit la fenêtre de réglages hors écran puis quitte),
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
  Audio/       capture WASAPI loopback, tampon circulaire, signaux de test
  Dsp/         FFT, LUFS (BS.1770), tonalité et notes, analyse hors ligne
  Taskbar/     localisation de la barre, zones libres (UI Automation + fenêtres tierces)
  Rendering/   fenêtres overlay, boucle de rendu, thème, disposition, caches
  Modules/     les 9 visualiseurs + catalogue d'options
  UI/          fenêtre de réglages, icône de notification
  Core/        réglages, traductions, démarrage avec Windows, journal
installer/     script de l'installeur (Inno Setup)
tests/         banc de test (tonalités, notes, sons non musicaux)
docs/          captures
```

## Licence

GPL-3.0, voir [LICENSE](LICENSE). Bibliothèques : [NAudio](https://github.com/naudio/NAudio) (MIT) et
[SkiaSharp](https://github.com/mono/SkiaSharp) (MIT). Inspiré de MiniMeters, sans lien avec ce logiciel.
