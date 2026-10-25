namespace TaskbarVisu.Dsp;

/// <summary>Noms de notes, de tonalités, notation Camelot et gammes.</summary>
internal static class Harmony
{
    private static readonly string[] Sharps = { "C", "C#", "D", "D#", "E", "F", "F#", "G", "G#", "A", "A#", "B" };
    private static readonly string[] Flats = { "C", "D♭", "D", "E♭", "E", "F", "G♭", "G", "A♭", "A", "B♭", "B" };
    private static readonly string[] LatinSharps = { "DO", "DO#", "RÉ", "RÉ#", "MI", "FA", "FA#", "SOL", "SOL#", "LA", "LA#", "SI" };
    private static readonly string[] LatinFlats = { "DO", "RÉ♭", "RÉ", "MI♭", "MI", "FA", "SOL♭", "SOL", "LA♭", "LA", "SI♭", "SI" };

    // Orthographe usuelle des tonalités (Mi♭ majeur plutôt que Ré# majeur, etc.).
    private static readonly bool[] MajorUsesFlats = { false, true, false, true, false, true, false, false, true, false, true, false };
    private static readonly bool[] MinorUsesFlats = { true, false, true, true, false, true, false, true, false, false, true, false };

    public static readonly int[] MajorScale = { 0, 2, 4, 5, 7, 9, 11 };
    public static readonly int[] MinorScale = { 0, 2, 3, 5, 7, 8, 10 };

    private static int Wrap(int pc) => ((pc % 12) + 12) % 12;

    public static string NoteName(int pitchClass, bool latin, bool flats = false) =>
        (latin ? flats ? LatinFlats : LatinSharps : flats ? Flats : Sharps)[Wrap(pitchClass)];

    /// <summary>Faut-il écrire les notes de cette tonalité avec des bémols ?</summary>
    public static bool KeyUsesFlats(int tonic, bool minor) => (minor ? MinorUsesFlats : MajorUsesFlats)[Wrap(tonic)];

    public static string KeyName(int tonic, bool minor, bool latin) => NoteName(tonic, latin, KeyUsesFlats(tonic, minor));

    /// <summary>Roue Camelot des DJ : 8B = Do majeur, 8A = La mineur.</summary>
    public static string Camelot(int tonic, bool minor)
    {
        int fifths = Wrap(tonic) * 7 % 12;
        int number = (fifths + (minor ? 4 : 7)) % 12 + 1;
        return $"{number}{(minor ? 'A' : 'B')}";
    }

    public static int[] Scale(int tonic, bool minor) =>
        (minor ? MinorScale : MajorScale).Select(i => (tonic + i) % 12).ToArray();

    private static readonly string[] ModeNames = { "ION", "DOR", "PHR", "LYD", "MIX", "AEO", "LOC" };

    /// <summary>
    /// Mode d'une note centrale dans la gamme d'une tonalité : la gamme de Si♭ majeur centrée sur Fa est
    /// Fa mixolydien (« MIX »). Null si la note n'appartient pas à la gamme.
    /// </summary>
    public static string? ModeName(int centre, int tonic, bool minor)
    {
        int root = minor ? tonic + 3 : tonic; // gamme majeure de même armure
        int degree = Array.IndexOf(MajorScale, Wrap(centre - root));
        return degree < 0 ? null : ModeNames[degree];
    }
}
