using System.Windows;
using System.Xml;
using ICSharpCode.AvalonEdit;
using ICSharpCode.AvalonEdit.Highlighting;
using ICSharpCode.AvalonEdit.Highlighting.Xshd;

namespace GullProxy.Ui;

/// <summary>
/// Makes AvalonEdit's <see cref="TextEditor"/> usable from XAML/MVVM: a two-way bindable
/// <c>BoundText</c> attached property, plus TalonFormat/TalonScript syntax highlighting.
/// </summary>
public static class AvalonEditHelper
{
    public static readonly DependencyProperty BoundTextProperty =
        DependencyProperty.RegisterAttached("BoundText", typeof(string), typeof(AvalonEditHelper),
            new FrameworkPropertyMetadata(null,
                FrameworkPropertyMetadataOptions.BindsTwoWayByDefault, OnBoundTextChanged));

    public static string GetBoundText(DependencyObject d) => (string)d.GetValue(BoundTextProperty);
    public static void SetBoundText(DependencyObject d, string value) => d.SetValue(BoundTextProperty, value);

    private static readonly HashSet<TextEditor> Hooked = new();

    private static void OnBoundTextChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not TextEditor ed) return;

        if (Hooked.Add(ed))
        {
            ed.TextChanged += (_, _) => SetBoundText(ed, ed.Text);
            if (Highlighting.Value is { } hl) ed.SyntaxHighlighting = hl;
        }

        string value = e.NewValue as string ?? "";
        if (ed.Text != value) ed.Text = value; // external update (e.g. Send to Talon); guarded to avoid loops
    }

    private static readonly Lazy<IHighlightingDefinition?> Highlighting = new(() =>
    {
        try
        {
            using var reader = XmlReader.Create(new StringReader(Xshd));
            return HighlightingLoader.Load(reader, HighlightingManager.Instance);
        }
        catch { return null; }
    });

    private const string Xshd = """
        <?xml version="1.0"?>
        <SyntaxDefinition name="TalonFormat" xmlns="http://icsharpcode.net/sharpdevelop/syntaxdefinition/2008">
          <Color name="Comment" foreground="#5A616D"/>
          <Color name="Var"     foreground="#33C4B3"/>
          <Color name="Interp"  foreground="#E0A64B" fontWeight="bold"/>
          <Color name="Method"  foreground="#4C93F0" fontWeight="bold"/>
          <Color name="Header"  foreground="#8AB4F8"/>
          <Color name="Str"     foreground="#5BC88A"/>
          <Color name="Num"     foreground="#C88AE0"/>
          <Color name="Kw"      foreground="#4C93F0"/>
          <Color name="Builtin" foreground="#33C4B3"/>
          <Color name="Delim"   foreground="#E0A64B" fontWeight="bold"/>

          <RuleSet ignoreCase="false">
            <Span color="Delim" ruleSet="Script" multiline="true">
              <Begin>[&lt;&gt;]\s*\{%</Begin>
              <End>%\}</End>
            </Span>
            <Span color="Comment"><Begin>\#</Begin></Span>
            <Span color="Comment"><Begin>//</Begin></Span>
            <Span color="Str"><Begin>"</Begin><End>"</End></Span>

            <Rule color="Interp">\{\{[^}]*\}\}</Rule>
            <Rule color="Var">(?&lt;=^\s*)@[\w.\-]+</Rule>
            <Rule color="Method">(?&lt;=^\s*)(GET|POST|PUT|PATCH|DELETE|HEAD|OPTIONS)\b</Rule>
            <Rule color="Header">(?&lt;=^\s*)[A-Za-z][A-Za-z0-9\-]*(?=\s*:)</Rule>
          </RuleSet>

          <RuleSet name="Script" ignoreCase="false">
            <Span color="Comment"><Begin>\#</Begin></Span>
            <Span color="Comment"><Begin>//</Begin></Span>
            <Span color="Str"><Begin>"</Begin><End>"</End></Span>
            <Span color="Str"><Begin>'</Begin><End>'</End></Span>
            <Rule color="Interp">\{\{[^}]*\}\}</Rule>
            <Keywords color="Kw">
              <Word>if</Word><Word>else</Word><Word>repeat</Word><Word>for</Word><Word>in</Word>
              <Word>while</Word><Word>break</Word><Word>continue</Word><Word>and</Word><Word>or</Word>
              <Word>not</Word><Word>log</Word><Word>true</Word><Word>false</Word><Word>null</Word>
            </Keywords>
            <Keywords color="Builtin">
              <Word>request</Word><Word>response</Word><Word>vars</Word><Word>PI</Word><Word>E</Word><Word>TAU</Word>
              <Word>str</Word><Word>num</Word><Word>bool</Word><Word>type</Word><Word>isNull</Word><Word>default</Word>
              <Word>len</Word><Word>json</Word><Word>jsonStringify</Word><Word>print</Word>
              <Word>abs</Word><Word>floor</Word><Word>ceil</Word><Word>round</Word><Word>trunc</Word><Word>int</Word>
              <Word>sqrt</Word><Word>pow</Word><Word>exp</Word><Word>ln</Word><Word>log10</Word><Word>sign</Word>
              <Word>sin</Word><Word>cos</Word><Word>tan</Word><Word>atan</Word><Word>atan2</Word>
              <Word>min</Word><Word>max</Word><Word>clamp</Word><Word>random</Word><Word>randomInt</Word>
              <Word>parseInt</Word><Word>parseFloat</Word>
              <Word>upper</Word><Word>lower</Word><Word>trim</Word><Word>contains</Word><Word>startsWith</Word>
              <Word>endsWith</Word><Word>indexOf</Word><Word>replace</Word><Word>split</Word><Word>join</Word>
              <Word>substring</Word><Word>repeat</Word><Word>padStart</Word><Word>padEnd</Word><Word>chars</Word>
              <Word>regexTest</Word><Word>regexMatch</Word><Word>regexReplace</Word><Word>regexAll</Word>
              <Word>urlEncode</Word><Word>urlDecode</Word><Word>base64</Word><Word>base64decode</Word>
              <Word>base64url</Word><Word>hex</Word><Word>md5</Word><Word>sha1</Word><Word>sha256</Word><Word>hmacSha256</Word>
              <Word>list</Word><Word>range</Word><Word>push</Word><Word>first</Word><Word>last</Word><Word>reverse</Word>
              <Word>sort</Word><Word>sum</Word><Word>slice</Word><Word>keys</Word><Word>values</Word><Word>has</Word>
              <Word>get</Word><Word>merge</Word><Word>object</Word>
              <Word>now</Word><Word>timestamp</Word><Word>timestampMs</Word><Word>uuid</Word><Word>env</Word>
            </Keywords>
            <Rule color="Num">\b\d+(\.\d+)?\b</Rule>
          </RuleSet>
        </SyntaxDefinition>
        """;
}
