using Markdig;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using MdBlock   = Markdig.Syntax.Block;
using MdInline  = Markdig.Syntax.Inlines.Inline;
using MdDoc     = Markdig.Syntax.MarkdownDocument;

namespace DemoBase.App.Controls;

public partial class MarkdownViewer : UserControl
{
    public static readonly DependencyProperty MarkdownProperty =
        DependencyProperty.Register(
            nameof(Markdown), typeof(string), typeof(MarkdownViewer),
            new PropertyMetadata(string.Empty, OnMarkdownChanged));

    public string Markdown
    {
        get => (string)GetValue(MarkdownProperty);
        set => SetValue(MarkdownProperty, value);
    }

    private static readonly MarkdownPipeline Pipeline = new MarkdownPipelineBuilder()
        .UseAdvancedExtensions()
        .Build();

    private readonly RichTextBox _rtb;

    public MarkdownViewer()
    {
        InitializeComponent();

        _rtb = new RichTextBox
        {
            IsReadOnly        = true,
            BorderThickness   = new Thickness(0),
            Background        = Brushes.Transparent,
            IsDocumentEnabled = true,
            Padding           = new Thickness(0),
        };
        _rtb.SetResourceReference(ForegroundProperty, "TextPrimary");

        // Supprimer le style par défaut
        _rtb.Resources.Add(SystemColors.HighlightBrushKey,
            SystemColors.HighlightBrush);

        Content = _rtb;
    }

    private static void OnMarkdownChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is MarkdownViewer v)
            v.Render((string?)e.NewValue ?? string.Empty);
    }

    private void Render(string markdown)
    {
        if (string.IsNullOrWhiteSpace(markdown))
        {
            _rtb.Document = new FlowDocument();
            return;
        }

        var doc  = Markdig.Markdown.Parse(markdown, Pipeline);
        var flow = new FlowDocument
        {
            FontFamily = new FontFamily("Segoe UI"),
            FontSize   = 12,
            LineHeight = 18,
            PagePadding = new Thickness(0),
        };
        flow.SetResourceReference(FlowDocument.ForegroundProperty, "TextPrimary");

        foreach (MdBlock block in doc)
            flow.Blocks.Add(ConvertBlock(block));

        _rtb.Document = flow;
    }

    // ─── Blocs ────────────────────────────────────────────────────────────────

    private System.Windows.Documents.Block ConvertBlock(MdBlock block)
    {
        switch (block)
        {
            case Markdig.Syntax.HeadingBlock h:
                var hp = new Paragraph { Margin = new Thickness(0, 6, 0, 2) };
                hp.FontSize   = h.Level switch { 1 => 18, 2 => 16, 3 => 14, _ => 13 };
                hp.FontWeight = FontWeights.SemiBold;
                AddInlines(hp.Inlines, h.Inline);
                return hp;

            case Markdig.Syntax.ListBlock lb:
                var list = new List { Margin = new Thickness(0, 2, 0, 2) };
                list.MarkerStyle = lb.IsOrdered
                    ? TextMarkerStyle.Decimal
                    : TextMarkerStyle.Disc;
                foreach (var item in lb.OfType<Markdig.Syntax.ListItemBlock>())
                {
                    var li = new ListItem();
                    foreach (var child in item)
                        li.Blocks.Add(ConvertBlock(child));
                    list.ListItems.Add(li);
                }
                return list;

            case Markdig.Syntax.QuoteBlock qb:
                var section = new Section
                {
                    BorderBrush     = new SolidColorBrush(Color.FromRgb(100, 130, 200)),
                    BorderThickness = new Thickness(3, 0, 0, 0),
                    Padding         = new Thickness(8, 0, 0, 0),
                    Margin          = new Thickness(0, 4, 0, 4),
                };
                foreach (var child in qb)
                    section.Blocks.Add(ConvertBlock(child));
                return section;

            case Markdig.Syntax.FencedCodeBlock cb:
            case Markdig.Syntax.CodeBlock cb2:
                var code = (block as Markdig.Syntax.LeafBlock)!;
                var cp   = new Paragraph { Margin = new Thickness(0, 4, 0, 4) };
                cp.FontFamily = new FontFamily("Consolas");
                cp.FontSize   = 11;
                cp.Background = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128));
                cp.Padding    = new Thickness(6);
                cp.Inlines.Add(new Run(code.Lines.ToString()));
                return cp;

            case Markdig.Syntax.ParagraphBlock pb:
                var p = new Paragraph { Margin = new Thickness(0, 2, 0, 2) };
                AddInlines(p.Inlines, pb.Inline);
                return p;

            case Markdig.Syntax.ThematicBreakBlock:
                return new BlockUIContainer(new Separator { Margin = new Thickness(0, 4, 0, 4) });

            default:
                var fallback = new Paragraph { Margin = new Thickness(0, 1, 0, 1) };
                if (block is Markdig.Syntax.LeafBlock lb2)
                    fallback.Inlines.Add(new Run(lb2.Lines.ToString()));
                return fallback;
        }
    }

    // ─── Inlines ──────────────────────────────────────────────────────────────

    private void AddInlines(InlineCollection col, Markdig.Syntax.Inlines.ContainerInline? container)
    {
        if (container == null) return;
        foreach (var inline in container)
            col.Add(ConvertInline(inline));
    }

    private System.Windows.Documents.Inline ConvertInline(MdInline inline)
    {
        switch (inline)
        {
            case Markdig.Syntax.Inlines.LiteralInline li:
                return new Run(li.Content.ToString());

            case Markdig.Syntax.Inlines.EmphasisInline ei:
                var span = new Span();
                if (ei.DelimiterCount == 2)
                    span.FontWeight = FontWeights.Bold;
                else
                    span.FontStyle = FontStyles.Italic;
                AddInlines(span.Inlines, ei);
                return span;

            case Markdig.Syntax.Inlines.CodeInline ci:
                var code = new Run(ci.Content)
                {
                    FontFamily  = new FontFamily("Consolas"),
                    FontSize    = 11,
                    Background  = new SolidColorBrush(Color.FromArgb(30, 128, 128, 128)),
                };
                return code;

            case Markdig.Syntax.Inlines.LinkInline link:
                var hl = new Hyperlink { NavigateUri = Uri.TryCreate(link.Url, UriKind.Absolute, out var u) ? u : null };
                hl.RequestNavigate += (_, e) => { try { Process.Start(new ProcessStartInfo(e.Uri.ToString()) { UseShellExecute = true }); } catch { } };
                hl.SetResourceReference(Hyperlink.ForegroundProperty, "Accent");
                AddInlines(hl.Inlines, link);
                return hl;

            case Markdig.Syntax.Inlines.LineBreakInline:
                return new LineBreak();

            case Markdig.Syntax.Inlines.HtmlInline:
                return new Run();

            case Markdig.Syntax.Inlines.ContainerInline ci2:
                var s2 = new Span();
                AddInlines(s2.Inlines, ci2);
                return s2;

            default:
                return new Run(inline.ToString() ?? string.Empty);
        }
    }
}
