using System.Globalization;

namespace UltraKey;

/// <summary>UI text. Polish when Paint.NET runs in Polish, English otherwise.</summary>
internal static class Strings
{
    private static readonly bool Pl = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "pl";

    private static string T(string en, string pl) => Pl ? pl : en;

    public static string EffectName => T("UltraKey Background Remover", "UltraKey – usuwanie tła");
    public static string WindowTitle => T("UltraKey – Background Remover & Chroma Key", "UltraKey – usuwanie tła i chroma key");

    public static string TabBackground => T("Background", "Tło");
    public static string TabPoints => T("Points", "Punkty");
    public static string TabMatte => T("Edges & matte", "Krawędzie i maska");
    public static string TabOutput => T("Colour & output", "Kolor i wynik");

    public static string Mode => T("Background", "Tło");
    public static string ModeAi => T("AI – cut out the subject from any background (photos)", "AI – wytnij obiekt z dowolnego tła (zdjęcia)");
    public static string ModeAuto => T("Auto – uniform background detected from the edges (green screens too)", "Auto – jednolite tło wykryte z krawędzi (też green screen)");
    public static string ModeGreen => T("Green screen", "Green screen (zielone tło)");
    public static string ModeBlue => T("Blue screen", "Blue screen (niebieskie tło)");
    public static string ModeCustom => T("Custom key colour", "Własny kolor klucza");
    public static string ModePrimary => T("Primary colour (palette)", "Kolor podstawowy (paleta)");
    public static string ModePick => T("Pick a point in the image", "Wskaż punkt na obrazie");

    public static string AiModel => T("AI model", "Model AI");
    public static string AiBiRefNet => T("BiRefNet – best quality (GPU ~5 s, CPU ~20 s)", "BiRefNet – najlepsza jakość (GPU ok. 5 s, CPU ok. 20 s)");
    public static string AiBiRefNetLite => T("BiRefNet lite – faster (GPU ~1 s, CPU ~8 s)", "BiRefNet lite – szybszy (GPU ok. 1 s, CPU ok. 8 s)");
    public static string AiU2Net => T("U²-Net – built in, less accurate", "U²-Net – wbudowany, mniej dokładny");
    public static string AiInstalled => T("BiRefNet is installed.", "BiRefNet jest zainstalowany.");
    public static string AiOnlyFull => T("Only full BiRefNet is installed (used for lite too).", "Zainstalowany jest tylko pełny BiRefNet (używany też zamiast lite).");
    public static string AiOnlyLite => T("Only BiRefNet lite is installed (used for full too).", "Zainstalowany jest tylko BiRefNet lite (używany też zamiast pełnego).");
    public static string AiOnGpu => T("It runs on the graphics card.", "Działa na karcie graficznej.");
    public static string AiOnCpu => T("It runs on the processor (no DirectX 12 GPU available).", "Działa na procesorze (brak karty z DirectX 12).");
    public static string AiMissing(string dir) => T($"BiRefNet is not installed, so the built-in U²-Net is used. Run install.ps1 to download it (600 MB) into {dir}.",
                                                    $"BiRefNet nie jest zainstalowany, więc używany jest wbudowany U²-Net. Uruchom install.ps1, aby go pobrać (600 MB) do {dir}.");
    public static string AiFailed(string error) => T($"BiRefNet could not be loaded, so U²-Net is used: {error}", $"Nie udało się wczytać BiRefNet, więc używany jest U²-Net: {error}");

    public static string KeyColor => T("Key colour", "Kolor klucza");
    public static string PickPoint => T("Background sample point", "Punkt próbki tła");

    public static string KeepPointOn => T("AI: also keep the object at this point (e.g. a logo or caption)", "AI: zachowaj też obiekt w tym punkcie (np. napis, logo)");
    public static string KeepPoint => T("Keep object at (click the thumbnail)", "Zachowaj obiekt w punkcie (kliknij miniaturkę)");
    public static string RemovePointOn => T("AI: remove the object at this point", "AI: usuń obiekt w tym punkcie");
    public static string RemovePoint => T("Remove object at (click the thumbnail)", "Usuń obiekt w punkcie (kliknij miniaturkę)");
    public static string Tolerance => T("Tolerance", "Tolerancja");
    public static string ToleranceHint => T("Higher = removes more of the background.", "Więcej = usuwa więcej tła.");
    public static string Softness => T("Edge softness", "Miękkość krawędzi");
    public static string Luma => T("Brightness sensitivity", "Czułość na jasność");
    public static string LumaHint => T("Low ignores shadows/creases on the backdrop, high separates subjects by brightness.",
                                       "Nisko ignoruje cienie/zagięcia tła, wysoko rozdziela obiekty po jasności.");

    public static string Uneven => T("Correct uneven background (lighting, gradients, vignetting)", "Koryguj nierówne tło (oświetlenie, gradienty, winieta)");
    public static string Enclosed => T("Background enclosed by the subject", "Tło zamknięte wewnątrz obiektu");
    public static string EnclosedSmart => T("Smart (keep in Auto, remove for screens)", "Inteligentnie (Auto: zostaw, ekrany: usuń)");
    public static string EnclosedRemove => T("Remove", "Usuń");
    public static string EnclosedKeep => T("Keep (protect the subject)", "Zostaw (chroń obiekt)");
    public static string Specks => T("Clean up specks / holes smaller than (px)", "Usuń drobinki / dziury mniejsze niż (px)");

    public static string SolidCore => T("Solid subject (no see-through patches inside, only edges stay soft)", "Lity obiekt (bez prześwitów w środku, miękkie tylko krawędzie)");
    public static string EdgeDetail => T("Edge detail (hair, fur)", "Detal krawędzi (włosy, sierść)");
    public static string RefineRadius => T("Edge refine radius", "Promień dopasowania krawędzi");
    public static string ShrinkGrow => T("Shrink (-) / grow (+) matte", "Zmniejsz (-) / powiększ (+) maskę");
    public static string Feather => T("Feather", "Wygładzenie (feather)");
    public static string ClipBlack => T("Clip background (remove haze)", "Docisk tła (usuń mgiełkę)");
    public static string ClipWhite => T("Clip subject (fill holes)", "Docisk obiektu (zapełnij dziury)");

    public static string Despill => T("Spill suppression", "Usuwanie poświaty (despill)");
    public static string Decontaminate => T("Edge colour decontamination", "Oczyszczanie koloru krawędzi");
    public static string Shadows => T("Keep shadows", "Zachowaj cienie");
    public static string Invert => T("Invert (keep the background, remove the subject)", "Odwróć (zostaw tło, usuń obiekt)");

    public static string Output => T("Output", "Wynik");
    public static string OutputResult => T("Transparent background", "Przezroczyste tło");
    public static string OutputMatte => T("Matte (black & white mask)", "Maska (czarno-biała)");
    public static string OutputOver => T("On solid colour", "Na jednolitym kolorze");
    public static string OutputOverlay => T("Check overlay (red = removed)", "Podgląd kontrolny (czerwone = usunięte)");
    public static string Backdrop => T("Backdrop colour", "Kolor podkładu");
}
