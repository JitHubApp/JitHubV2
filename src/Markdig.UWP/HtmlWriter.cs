﻿using HtmlAgilityPack;
using Markdig.UWP.Renderers;
using Markdig.UWP.TextElements.Html;
using Markdig.UWP.TextElements;
using System.Linq;

namespace Markdig.UWP;

internal class HtmlWriter
{
    public static void WriteHtml(UWPRenderer renderer, HtmlNodeCollection nodes)
    {
        if (nodes == null || nodes.Count == 0) return;
        foreach (var node in nodes)
        {
            if (node.NodeType == HtmlNodeType.Text)
            {
                renderer.WriteText(node.InnerText);
            }
            else if (node.NodeType == HtmlNodeType.Element && node.Name.TagToType() == TextElements.HtmlElementType.Inline)
            {
                // detect br here
                var inlineTagName = node.Name.ToLower();
                if (inlineTagName == "br")
                {
                    renderer.WriteInline(new MyLineBreak());
                }
                else if (inlineTagName == "a")
                {
                    IAddChild hyperLink;
                    if (node.ChildNodes.Any(n => n.Name != "#text"))
                    {
                        hyperLink = new MyHyperlinkButton(node, renderer.Config.BaseUrl);
                    }
                    else
                    {
                        hyperLink = new MyHyperlink(node, renderer.Config.BaseUrl);
                    }
                    renderer.Push(hyperLink);
                    WriteHtml(renderer, node.ChildNodes);
                    renderer.Pop();
                }
                else if (inlineTagName == "img")
                {
                    var image = new MyImage(node, renderer.Config);
                    renderer.WriteInline(image);
                }
                else
                {
                    var inline = new MyInline(node);
                    renderer.Push(inline);
                    WriteHtml(renderer, node.ChildNodes);
                    renderer.Pop();
                }
            }
            else if (node.NodeType == HtmlNodeType.Element && node.Name.TagToType() == TextElements.HtmlElementType.Block)
            {
                IAddChild block = null;
                var tag = node.Name.ToLower();
                if (tag == "details")
                {
                    block = new MyDetails(node);
                    node.ChildNodes.Remove(node.ChildNodes.FirstOrDefault(x => x.Name == "summary" || x.Name == "header"));
                    renderer.Push(block);
                    WriteHtml(renderer, node.ChildNodes);
                }
                else if (tag.IsHeading())
                {
                    var heading = new MyHeading(node);
                    renderer.Push(heading);
                    WriteHtml(renderer, node.ChildNodes);
                }
                else
                {
                    block = new MyBlock(node);
                    renderer.Push(block);
                    WriteHtml(renderer, node.ChildNodes);
                }
                renderer.Pop();
            }
        }
    }
}
