using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Documents;

namespace DemoBase.App.Services;

/// <summary>
/// Coloration syntaxique légère pour l'onglet "Code Sources" — pas de dépendance externe
/// (AvalonEdit et équivalents ne sont pas installés dans le projet, voir RESUME_PROJET.md).
/// Volontairement générique plutôt que spécifique à un langage : les archives "Code Sources"
/// couvrent des sources demoscene très variées (ASM 68k/x86, C, Pascal, BASIC…) sans info
/// fiable sur le langage exact d'un fichier donné. Reconnaît uniquement commentaires / chaînes
/// / un ensemble de mots-clés combiné C+Pascal+BASIC+ASM — un survol coloré "suffisant pour
/// s'y retrouver", pas une coloration exacte par grammaire de langage.
/// </summary>
public static class SimpleCodeHighlighter
{
    // Au-delà de cette taille, la coloration (des milliers de Run/Inline individuels) peut
    // geler l'UI de façon perceptible le temps de construire le FlowDocument — on affiche
    // alors le texte tel quel (un seul Run), toujours lisible, juste sans couleurs.
    private const int MaxHighlightChars = 200_000;

    // Un seul passage sur le texte, groupes nommés dans l'ordre de priorité (le premier
    // groupe qui matche à une position donnée gagne — c'est le fonctionnement standard de
    // l'alternation Regex). RegexOptions.Singleline pour que "." dans les commentaires bloc
    // traverse les fins de ligne ; Multiline pour que "$" arrête les commentaires ligne
    // correctement à chaque retour à la ligne.
    private static readonly Regex TokenRegex = new(
        @"(?<comment>/\*.*?\*/|//[^\r\n]*|;[^\r\n]*|(?<=^|\s)'[^\r\n]*)" +
        @"|(?<string>""(?:\\.|[^""\\\r\n])*""|'(?:\\.|[^'\\\r\n])*')" +
        @"|(?<keyword>\b(?:" + Keywords + @")\b)",
        RegexOptions.Compiled | RegexOptions.Singleline | RegexOptions.Multiline | RegexOptions.IgnoreCase);

    // Mélange volontaire C/C++ + Pascal + BASIC + ASM (mnémoniques 68k/x86 les plus courants)
    // — couvre la majorité des sources demoscene rencontrées sans essayer de deviner le
    // langage exact du fichier.
    private const string Keywords =
        "if|else|elseif|for|foreach|while|do|repeat|until|switch|case|default|break|continue|return|goto|" +
        "int|char|float|double|void|bool|byte|word|dword|long|short|unsigned|signed|const|static|struct|" +
        "typedef|enum|union|class|public|private|protected|namespace|include|define|ifdef|ifndef|endif|" +
        "sizeof|extern|volatile|inline|" +
        "begin|end|program|procedure|function|var|type|record|array|of|then|uses|interface|implementation|" +
        "dim|sub|end sub|end function|as|next|wend|gosub|then|input|print|let|" +
        "org|equ|dc|ds|db|dw|dd|macro|endm|section|global|extern|align|" +
        "mov|movea|move|lea|cmp|cmpi|jmp|jsr|bra|beq|bne|bsr|bcc|bcs|bpl|bmi|" +
        "add|adda|addi|sub|suba|subi|mul|muls|mulu|div|divs|divu|" +
        "and|or|xor|not|eor|asl|asr|lsl|lsr|rol|ror|" +
        "push|pop|call|ret|jz|jnz|je|jne|jg|jl|nop|" +
        "trap|rts|rte|dbra|dbf";

    /// <summary>
    /// Construit un FlowDocument coloré (lecture seule) à partir de texte brut. Le texte
    /// ENTIER est passé à TokenRegex en une seule fois (pas ligne par ligne) — nécessaire pour
    /// que les commentaires bloc /* ... */ qui s'étendent sur plusieurs lignes soient reconnus
    /// correctement d'un bout à l'autre ; chaque fragment (matché ou non) est ensuite éclaté
    /// sur les retours à la ligne en Run + LineBreak, en conservant la même couleur pour
    /// toutes les lignes d'un même token (utile pour les commentaires bloc multi-lignes).
    /// </summary>
    public static FlowDocument Build(string text)
    {
        var doc = new FlowDocument
        {
            FontFamily  = new System.Windows.Media.FontFamily("Consolas, Cascadia Mono, Courier New"),
            FontSize    = 12.5,
            PagePadding = new Thickness(10),
        };

        var paragraph = new Paragraph { Margin = new Thickness(0) };

        if (text != null && text.Length > MaxHighlightChars)
        {
            // Fichier trop volumineux pour justifier le coût de la coloration — texte brut,
            // toujours lisible (LineBreak préservés) mais sans Run colorés individuels.
            AppendWithLineBreaks(paragraph, text.Replace("\r\n", "\n").Replace('\r', '\n'), StyleKind.Plain);
        }
        else if (!string.IsNullOrEmpty(text))
        {
            var normalized = text.Replace("\r\n", "\n").Replace('\r', '\n');

            int lastEnd = 0;
            foreach (Match m in TokenRegex.Matches(normalized))
            {
                if (m.Index > lastEnd)
                    AppendWithLineBreaks(paragraph, normalized[lastEnd..m.Index], StyleKind.Plain);

                var kind = m.Groups["comment"].Success ? StyleKind.Comment
                         : m.Groups["string"].Success  ? StyleKind.String
                         : m.Groups["keyword"].Success  ? StyleKind.Keyword
                         : StyleKind.Plain;
                AppendWithLineBreaks(paragraph, m.Value, kind);

                lastEnd = m.Index + m.Length;
            }
            if (lastEnd < normalized.Length)
                AppendWithLineBreaks(paragraph, normalized[lastEnd..], StyleKind.Plain);
        }

        doc.Blocks.Add(paragraph);
        return doc;
    }

    private enum StyleKind { Plain, Comment, String, Keyword }

    private static void AppendWithLineBreaks(Paragraph paragraph, string chunk, StyleKind kind)
    {
        var parts = chunk.Split('\n');
        for (int i = 0; i < parts.Length; i++)
        {
            if (parts[i].Length > 0)
                paragraph.Inlines.Add(MakeRun(parts[i], kind));
            if (i < parts.Length - 1)
                paragraph.Inlines.Add(new LineBreak());
        }
    }

    private static Run MakeRun(string text, StyleKind kind)
    {
        var run = new Run(text);
        switch (kind)
        {
            case StyleKind.Comment:
                run.SetResourceReference(TextElement.ForegroundProperty, "TextMuted");
                run.FontStyle = FontStyles.Italic;
                break;
            case StyleKind.String:
                run.SetResourceReference(TextElement.ForegroundProperty, "Green");
                break;
            case StyleKind.Keyword:
                run.SetResourceReference(TextElement.ForegroundProperty, "Accent");
                run.FontWeight = FontWeights.SemiBold;
                break;
            default:
                run.SetResourceReference(TextElement.ForegroundProperty, "TextPrimary");
                break;
        }
        return run;
    }
}
