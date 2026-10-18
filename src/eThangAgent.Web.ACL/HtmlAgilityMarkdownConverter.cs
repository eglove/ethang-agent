using System.Text;
using System.Text.RegularExpressions;
using eThangAgent.ToolDomain;
using HtmlAgilityPack;

namespace eThangAgent.Web.ACL;

/// <summary>HTML → Markdown over HtmlAgilityPack: covers the readable-text core
///     (headings, paragraphs, inline emphasis/code, links, images, lists, fenced
///     code, GFM tables, hr). Script/style/noscript are dropped; nav/aside/footer
///     chrome is excluded; relative URLs resolve against the document's base.
///     Malformed HTML degrades to best-effort text — never throws.</summary>
public sealed class HtmlAgilityMarkdownConverter : IHtmlToMarkdown
{
  private static readonly Regex LanguageClass = new(
      "(?:language|lang)-([A-Za-z0-9_+-]+)",
      RegexOptions.Compiled, TimeSpan.FromMilliseconds(200));

  public string Convert(string html, Uri baseUrl)
  {
    if (string.IsNullOrWhiteSpace(html))
    {
      return string.Empty;
    }

    HtmlDocument doc = new();
    doc.LoadHtml(html);
    HtmlNode body = doc.DocumentNode.SelectSingleNode("//body") ?? doc.DocumentNode;
    ExcludeChrome(body);
    StringBuilder md = new();
    RenderChildren(body, baseUrl, md);
    return Normalize(md.ToString());
  }

  private static void ExcludeChrome(HtmlNode scope)
  {
    foreach (HtmlNode node in scope.SelectNodes(".//script|.//style|.//noscript|.//template") ?? Enumerable.Empty<HtmlNode>())
    {
      node.Remove();
    }

    foreach (HtmlNode node in scope.SelectNodes(".//nav|.//aside|.//footer") ?? Enumerable.Empty<HtmlNode>())
    {
      node.Remove();
    }
  }

  private static void RenderChildren(HtmlNode parent, Uri baseUrl, StringBuilder md)
  {
    foreach (HtmlNode node in parent.ChildNodes)
    {
      RenderNode(node, baseUrl, md);
    }
  }

  /// <summary>Per-node-kind renderers for the closed block vocabulary; any name
  ///     without an entry renders its children (containers and unknown elements
  ///     behave identically).</summary>
  private static readonly Dictionary<string, Action<HtmlNode, Uri, StringBuilder>> NodeRenderers =
      new(StringComparer.Ordinal)
      {
        ["h1"] = (node, baseUrl, md) => RenderHeading(node, 1, baseUrl, md),
        ["h2"] = (node, baseUrl, md) => RenderHeading(node, 2, baseUrl, md),
        ["h3"] = (node, baseUrl, md) => RenderHeading(node, 3, baseUrl, md),
        ["h4"] = (node, baseUrl, md) => RenderHeading(node, 4, baseUrl, md),
        ["h5"] = (node, baseUrl, md) => RenderHeading(node, 5, baseUrl, md),
        ["h6"] = (node, baseUrl, md) => RenderHeading(node, 6, baseUrl, md),
        ["p"] = (node, baseUrl, md) => _ = md.Append(Inline(node, baseUrl)).Append("\n\n"),
        ["ul"] = (node, baseUrl, md) => RenderList(node, "-", baseUrl, md),
        ["ol"] = (node, baseUrl, md) => RenderList(node, "1.", baseUrl, md),
        ["pre"] = (node, baseUrl, md) => RenderPre(node, md),
        ["table"] = RenderTable,
        ["a"] = (node, baseUrl, md) => _ = md.Append(RenderAnchor(node, baseUrl)).Append("\n\n"),
        ["img"] = (node, baseUrl, md) => _ = md.Append(RenderImage(node, baseUrl)).Append("\n\n"),
        ["blockquote"] = RenderBlockquote,
        ["hr"] = (node, baseUrl, md) => _ = md.Append("---\n\n"),
        ["#text"] = (node, baseUrl, md) => RenderStrayText(node, md),
      };

  private static void RenderNode(HtmlNode node, Uri baseUrl, StringBuilder md)
  {
    if (NodeRenderers.TryGetValue(node.Name, out Action<HtmlNode, Uri, StringBuilder>? render))
    {
      render(node, baseUrl, md);
      return;
    }

    RenderChildren(node, baseUrl, md);
  }

  private static void RenderHeading(HtmlNode node, int level, Uri baseUrl, StringBuilder md)
      => _ = md.Append(new string('#', level)).Append(' ').Append(Inline(node, baseUrl)).Append("\n\n");

  private static void RenderBlockquote(HtmlNode node, Uri baseUrl, StringBuilder md)
      => _ = md.Append("> ").Append(Inline(node, baseUrl).Replace("\n", "\n> ", StringComparison.Ordinal)).Append("\n\n");

  private static void RenderStrayText(HtmlNode node, StringBuilder md)
  {
    string stray = Collapse(node.InnerText);
    if (stray.Length > 0)
    {
      _ = md.Append(stray).Append("\n\n");
    }
  }

  private static string Inline(HtmlNode node, Uri baseUrl) => Collapse(InlineChildren(node, baseUrl));

  /// <summary>Per-node-kind inline renderers; any name without an entry recurses
  ///     into its children (unknown containers behave identically).</summary>
  private static readonly Dictionary<string, Action<HtmlNode, Uri, StringBuilder>> InlineRenderers =
      new(StringComparer.Ordinal)
      {
        ["b"] = (node, baseUrl, sb) => _ = sb.Append("**").Append(Inline(node, baseUrl)).Append("**"),
        ["strong"] = (node, baseUrl, sb) => _ = sb.Append("**").Append(Inline(node, baseUrl)).Append("**"),
        ["i"] = (node, baseUrl, sb) => _ = sb.Append('*').Append(Inline(node, baseUrl)).Append('*'),
        ["em"] = (node, baseUrl, sb) => _ = sb.Append('*').Append(Inline(node, baseUrl)).Append('*'),
        ["code"] = (node, baseUrl, sb) => _ = sb.Append('`').Append(Inline(node, baseUrl)).Append('`'),
        ["a"] = (node, baseUrl, sb) => _ = sb.Append(InlineAnchor(node, baseUrl)),
        ["img"] = (node, baseUrl, sb) => _ = sb.Append(InlineImage(node, baseUrl)),
        ["br"] = (node, baseUrl, sb) => _ = sb.Append(' '),
        ["#text"] = (node, baseUrl, sb) => _ = sb.Append(HtmlEntity.DeEntitize(node.InnerText)),
      };

  private static string InlineChildren(HtmlNode node, Uri baseUrl)
  {
    StringBuilder sb = new();
    foreach (HtmlNode child in node.ChildNodes)
    {
      if (InlineRenderers.TryGetValue(child.Name, out Action<HtmlNode, Uri, StringBuilder>? render))
      {
        render(child, baseUrl, sb);
      }
      else
      {
        _ = sb.Append(InlineChildren(child, baseUrl));
      }
    }

    return sb.ToString();
  }

  private static string InlineAnchor(HtmlNode anchor, Uri baseUrl)
  {
    string href = anchor.GetAttributeValue("href", string.Empty);
    string text = Inline(anchor, baseUrl);
    return href.Length > 0 ? $"[{text}]({Absolute(href, baseUrl)})" : text;
  }

  private static string InlineImage(HtmlNode image, Uri baseUrl)
  {
    string src = image.GetAttributeValue("src", string.Empty);
    string alt = image.GetAttributeValue("alt", string.Empty);
    return src.Length > 0 ? $"![{alt}]({Absolute(src, baseUrl)})" : alt;
  }

  private static void RenderList(HtmlNode list, string marker, Uri baseUrl, StringBuilder md, int depth = 0)
  {
    bool ordered = marker == "1.";
    int index = 1;
    foreach (HtmlNode li in list.SelectNodes("./li") ?? Enumerable.Empty<HtmlNode>())
    {
      string indent = new(' ', depth * 2);
      _ = md.Append(indent).Append(ordered ? $"{index}." : marker).Append(' ').Append(Inline(li, baseUrl)).Append('\n');
      foreach (HtmlNode nested in li.SelectNodes("./ul|./ol") ?? Enumerable.Empty<HtmlNode>())
      {
        RenderList(nested, nested.Name == "ol" ? "1." : "-", baseUrl, md, depth + 1);
      }

      index++;
    }

    _ = md.Append('\n');
  }

  private static void RenderPre(HtmlNode pre, StringBuilder md)
  {
    HtmlNode? code = pre.SelectSingleNode(".//code");
    HtmlNode target = code ?? pre;
    string lang = LanguageClass.Match(code?.GetAttributeValue("class", string.Empty) ?? string.Empty) is { Success: true } m
        ? m.Groups[1].Value
        : string.Empty;
    _ = md.Append("```").Append(lang).Append('\n')
        .Append(HtmlEntity.DeEntitize(target.InnerText).TrimEnd('\n')).Append("\n```").Append("\n\n");
  }

  private static void RenderTable(HtmlNode table, Uri baseUrl, StringBuilder md)
  {
    HtmlNodeCollection? rows = table.SelectNodes(".//tr");
    if (rows is null)
    {
      return;
    }

    bool headerDone = false;
    foreach (HtmlNode row in rows)
    {
      HtmlNodeCollection? cells = row.SelectNodes("./th|./td");
      if (cells is null)
      {
        continue;
      }

      _ = md.Append('|').Append(' ').Append(string.Join(" | ", cells.Select(c => Inline(c, baseUrl)))).Append(" |\n");
      if (!headerDone)
      {
        _ = md.Append('|').Append(string.Join('|', cells.Select(_ => " --- "))).Append('|').Append('\n');
        headerDone = true;
      }
    }

    _ = md.Append('\n');
  }

  /// <summary>[text](absolute-url); a bare anchor with no href renders as its text.</summary>
  private static string RenderAnchor(HtmlNode anchor, Uri baseUrl)
  {
    string href = anchor.GetAttributeValue("href", string.Empty);
    string text = Collapse(HtmlEntity.DeEntitize(anchor.InnerText));
    return href.Length > 0 ? $"[{text}]({Absolute(href, baseUrl)})" : text;
  }

  /// <summary>![alt](absolute-src); src-less images degrade to their alt text.</summary>
  private static string RenderImage(HtmlNode img, Uri baseUrl)
  {
    string src = img.GetAttributeValue("src", string.Empty);
    string alt = img.GetAttributeValue("alt", string.Empty);
    return src.Length > 0 ? $"![{alt}]({Absolute(src, baseUrl)})" : alt;
  }

  private static string Absolute(string url, Uri baseUrl) =>
      Uri.TryCreate(url, UriKind.RelativeOrAbsolute, out Uri? parsed)
          ? new Uri(baseUrl, parsed).ToString()
          : url;

  private static string Collapse(string text) =>
      Regex.Replace(text.Replace('\r', ' ').Replace('\n', ' '), "\\s+", " ", RegexOptions.None, TimeSpan.FromMilliseconds(200)).Trim();

  /// <summary>Trims outer whitespace, collapses 3+ blank lines to one blank line.</summary>
  private static string Normalize(string markdown)
  {
    string[] lines = markdown.Replace("\r\n", "\n", StringComparison.Ordinal).Split('\n');
    StringBuilder sb = new();
    int blanks = 0;
    foreach (string line in lines)
    {
      if (line.Trim().Length == 0)
      {
        blanks++;
        if (blanks > 2)
        {
          continue;
        }
      }
      else
      {
        blanks = 0;
      }

      _ = sb.Append(line.TrimEnd()).Append('\n');
    }

    return sb.ToString().Trim().TrimEnd('\n');
  }
}
