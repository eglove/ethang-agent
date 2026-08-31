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

  private static void RenderNode(HtmlNode node, Uri baseUrl, StringBuilder md)
  {
    switch (node.Name)
    {
      case "h1" or "h2" or "h3" or "h4" or "h5" or "h6":
        _ = md.Append(new string('#', node.Name[1] - '0')).Append(' ').Append(Inline(node, baseUrl)).Append("\n\n");
        break;
      case "p":
        _ = md.Append(Inline(node, baseUrl)).Append("\n\n");
        break;
      case "ul":
        RenderList(node, "-", baseUrl, md);
        break;
      case "ol":
        RenderList(node, "1.", baseUrl, md);
        break;
      case "pre":
        RenderPre(node, md);
        break;
      case "table":
        RenderTable(node, baseUrl, md);
        break;
      case "a":
        _ = md.Append(RenderAnchor(node, baseUrl)).Append("\n\n");
        break;
      case "img":
        _ = md.Append(RenderImage(node, baseUrl)).Append("\n\n");
        break;
      case "blockquote":
        _ = md.Append("> ").Append(Inline(node, baseUrl).Replace("\n", "\n> ", StringComparison.Ordinal)).Append("\n\n");
        break;
      case "hr":
        _ = md.Append("---\n\n");
        break;
      case "div" or "article" or "section" or "main" or "header" or "figure" or "figcaption":
      case "span" or "center" or "font" or "body":
        RenderChildren(node, baseUrl, md);
        break;
      case "#text":
        string stray = Collapse(node.InnerText);
        if (stray.Length > 0)
        {
          _ = md.Append(stray).Append("\n\n");
        }
        break;
      default:
        RenderChildren(node, baseUrl, md);
        break;
    }
  }

  private static string Inline(HtmlNode node, Uri baseUrl) => Collapse(InlineChildren(node, baseUrl));

  private static string InlineChildren(HtmlNode node, Uri baseUrl)
  {
    StringBuilder sb = new();
    foreach (HtmlNode child in node.ChildNodes)
    {
      switch (child.Name)
      {
        case "b" or "strong":
          _ = sb.Append("**").Append(Inline(child, baseUrl)).Append("**");
          break;
        case "i" or "em":
          _ = sb.Append('*').Append(Inline(child, baseUrl)).Append('*');
          break;
        case "code":
          _ = sb.Append('`').Append(Inline(child, baseUrl)).Append('`');
          break;
        case "a":
          string href = child.GetAttributeValue("href", string.Empty);
          string text = Inline(child, baseUrl);
          _ = sb.Append(href.Length > 0
              ? $"[{text}]({Absolute(href, baseUrl)})"
              : text);
          break;
        case "img":
          string src = child.GetAttributeValue("src", string.Empty);
          string alt = child.GetAttributeValue("alt", string.Empty);
          _ = sb.Append(src.Length > 0 ? $"![{alt}]({Absolute(src, baseUrl)})" : alt);
          break;
        case "br":
          _ = sb.Append(' ');
          break;
        case "#text":
          _ = sb.Append(HtmlEntity.DeEntitize(child.InnerText));
          break;
        default:
          _ = sb.Append(InlineChildren(child, baseUrl));
          break;
      }
    }

    return sb.ToString();
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
