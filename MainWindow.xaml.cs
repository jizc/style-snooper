using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Documents;
using System.Xml;
using System.Xml.Linq;

namespace StyleSnooper;

public sealed partial class MainWindow : INotifyPropertyChanged
{
    private readonly Style bracketStyle, elementStyle, quotesStyle, textStyle, attributeStyle;

    public MainWindow()
    {
        Styles = GetStyles(typeof(FrameworkElement).Assembly).ToList();

        InitializeComponent();

        // get syntax coloring styles
        bracketStyle = (Style)Resources["BracketStyle"];
        elementStyle = (Style)Resources["ElementStyle"];
        quotesStyle = (Style)Resources["QuotesStyle"];
        textStyle = (Style)Resources["TextStyle"];
        attributeStyle = (Style)Resources["AttributeStyle"];

        // start out by looking at Button
        CollectionViewSource.GetDefaultView(Styles).MoveCurrentTo(Styles.Single(s => s.ElementType == typeof(Button)));
    }

    public List<StyleModel> Styles { get; private set; }

    // Returns all types in the specified assembly that are non-abstract,
    // and non-generic, derive from FrameworkElement, and have a default constructor
    private static IEnumerable<Type> GetFrameworkElementTypesFromAssembly(Assembly assembly)
    {
        foreach (var type in assembly.GetTypes())
        {
            if (type is { IsAbstract: false, ContainsGenericParameters: false }
                && typeof(FrameworkElement).IsAssignableFrom(type)
                && (type.GetConstructor(Type.EmptyTypes) is not null
                    || type.GetConstructor(BindingFlags.NonPublic | BindingFlags.Instance, null, Type.EmptyTypes, null) is not null))
            {
                yield return type;
            }
        }
    }

    private static IEnumerable<StyleModel> GetStyles(Assembly assembly)
    {
        return GetFrameworkElementTypesFromAssembly(assembly)
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .SelectMany(GetStyles);
    }

    private static IEnumerable<StyleModel> GetStyles(Type type)
    {
        // make an instance of the type and get its default style key
        if (type.GetConstructor(Type.EmptyTypes) is not null
            && Activator.CreateInstance(type, false) is FrameworkElement element)
        {
            var defaultStyleKey = element.GetValue(DefaultStyleKeyProperty);

            yield return new StyleModel(type.Name, defaultStyleKey, type);

            foreach (var styleModel in GetStylesFromStaticProperties(element))
            {
                yield return styleModel;
            }
        }
    }

    private static IEnumerable<StyleModel> GetStylesFromStaticProperties(FrameworkElement element)
    {
        var properties = element.GetType()
            .GetProperties(BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public)
            .Where(p => p.Name.EndsWith("StyleKey") && p.PropertyType == typeof(ResourceKey));

        foreach (var property in properties)
        {
            var elementType = element.GetType();
            var resourceKey = property.GetValue(element);

            yield return new StyleModel($"{elementType.Name}.{property.Name}", resourceKey, elementType);
        }
    }

    private void OnLoadClick(object sender, RoutedEventArgs e)
    {
        // create the file open dialog
        var openFileDialog = new Microsoft.Win32.OpenFileDialog
        {
            CheckFileExists = true,
            Multiselect = false,
            Filter = "Assemblies (*.exe;*.dll)|*.exe;*.dll"
        };

        if (openFileDialog.ShowDialog(this) is not true)
        {
            return;
        }

        try
        {
            AsmName.Text = openFileDialog.FileName;
            var styles = GetStyles(Assembly.LoadFile(openFileDialog.FileName)).ToList();
            if (styles.Count is 0)
            {
                MessageBox.Show("Assembly does not contain any compatible types.");
            }
            else
            {
                Styles = styles;
                OnPropertyChanged(nameof(Styles));
            }
        }
        catch
        {
            MessageBox.Show("Error loading assembly.");
        }
    }

    private void ShowStyle(object sender, SelectionChangedEventArgs e)
    {
        if (StyleTextBox is null)
        {
            return;
        }

        // see which type is selected
        if (TypeComboBox.SelectedValue is StyleModel style)
        {
            var success = TrySerializeStyle(style.ResourceKey, out var serializedStyle);
            if (success)
            {
                serializedStyle = CleanupStyle(serializedStyle);
            }

            // show the style in a document viewer
            StyleTextBox.Document = CreateFlowDocument(success, serializedStyle);
        }
    }

    /// <summary>
    /// Serializes a style using XamlWriter.
    /// </summary>
    /// <param name="resourceKey"></param>
    /// <param name="serializedStyle"></param>
    /// <returns></returns>
    private static bool TrySerializeStyle(object? resourceKey, out string serializedStyle)
    {
        var success = false;
        serializedStyle = "[Style not found]";

        // try to get the default style for the type
        if (resourceKey is not null && Application.Current.TryFindResource(resourceKey) is Style style)
        {
            // try to serialize the style
            try
            {
                var stringWriter = new StringWriter();
                var xmlTextWriter = new XmlTextWriter(stringWriter) { Formatting = Formatting.Indented };
                System.Windows.Markup.XamlWriter.Save(style, xmlTextWriter);
                serializedStyle = stringWriter.ToString();

                success = true;
            }
            catch (Exception exception)
            {
                serializedStyle = $"[Exception thrown while serializing style]{Environment.NewLine}{Environment.NewLine}{exception}";
            }
        }

        return success;
    }

    /// <summary>
    /// Creates a FlowDocument from the serialized XAML with simple syntax coloring.
    /// </summary>
    /// <param name="success"></param>
    /// <param name="serializedStyle"></param>
    /// <returns></returns>
    private FlowDocument CreateFlowDocument(bool success, string serializedStyle)
    {
        var document = new FlowDocument();
        if (!success)
        {
            // no style found
            document.Blocks.Add(new Paragraph(new Run(serializedStyle)) { TextAlignment = TextAlignment.Left });
            return document;
        }

        using var reader = new XmlTextReader(serializedStyle, XmlNodeType.Document, null);

        var indent = 0;
        var paragraph = new Paragraph();
        while (reader.Read())
        {
            if (reader.IsStartElement()) // opening tag, e.g. <Button
            {
                var elementName = reader.Name;

                // indentation
                paragraph.AddRun(textStyle, new string(' ', indent * 4));

                paragraph.AddRun(bracketStyle, "<");
                paragraph.AddRun(elementStyle, elementName);
                if (reader.HasAttributes)
                {
                    // write tag attributes
                    while (reader.MoveToNextAttribute())
                    {
                        paragraph.AddRun(attributeStyle, " " + reader.Name);
                        paragraph.AddRun(bracketStyle, "=");
                        paragraph.AddRun(quotesStyle, "\"");
                        switch (reader.Name)
                        {
                            case "TargetType": // target type fix - should use the Type MarkupExtension
                                paragraph.AddRun(textStyle, "{x:Type " + reader.Value + "}");
                                break;
                            case "Margin" or "Padding":
                                paragraph.AddRun(textStyle, SimplifyThickness(reader.Value));
                                break;
                            default:
                                paragraph.AddRun(textStyle, reader.Value);
                                break;
                        }

                        paragraph.AddRun(quotesStyle, "\"");
                        paragraph.AddLineBreak();
                        paragraph.AddRun(textStyle, new string(' ', indent * 4 + elementName.Length + 1));
                    }

                    paragraph.RemoveLastLineBreak();
                    reader.MoveToElement();
                }

                if (reader.IsEmptyElement) // empty tag, e.g. <Button />
                {
                    paragraph.AddRun(bracketStyle, " />");
                    paragraph.AddLineBreak();
                    --indent;
                }
                else // non-empty tag, e.g. <Button>
                {
                    paragraph.AddRun(bracketStyle, ">");
                    paragraph.AddLineBreak();
                }

                ++indent;
            }
            else // closing tag, e.g. </Button>
            {
                --indent;

                // indentation
                paragraph.AddRun(textStyle, new string(' ', indent * 4));

                // text content of a tag, e.g. the text "Do This" in <Button>Do This</Button>
                if (reader.NodeType is XmlNodeType.Text)
                {
                    var value = reader.ReadContentAsString();
                    if (reader.Name is "Thickness")
                    {
                        value = SimplifyThickness(value);
                    }

                    paragraph.AddRun(textStyle, value);
                }

                paragraph.AddRun(bracketStyle, "</");
                paragraph.AddRun(elementStyle, reader.Name);
                paragraph.AddRun(bracketStyle, ">");
                paragraph.AddLineBreak();
            }
        }

        document.Blocks.Add(paragraph);

        return document;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? propertyName = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));

    private static readonly XNamespace Xmlns = "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace XmlnsS = "clr-namespace:System;assembly=mscorlib";
    private static readonly XNamespace XmlnsC = "clr-namespace:System;assembly=System.Private.CoreLib";
    private static readonly XNamespace XmlnsX = "http://schemas.microsoft.com/winfx/2006/xaml";

    private static string CleanupStyle(string serializedStyle)
    {
        var styleXml = XDocument.Parse(serializedStyle);

        RemoveEmptyResources(styleXml);
        SimplifyStyleSetterValues(styleXml);
        SimplifyAttributeValues(styleXml);

        return styleXml.ToString();
    }

    private static void RemoveEmptyResources(XDocument styleXml)
    {
        foreach (var elt in styleXml.Descendants())
        {
            var localName = elt.Name.LocalName;

            var eltResources = elt.Element(Xmlns + $"{localName}.Resources");
            if (eltResources is not null)
            {
                var eltResourceDictionary = eltResources.Element(Xmlns + "ResourceDictionary");
                if (eltResourceDictionary?.IsEmpty ?? false)
                {
                    eltResources.Remove();
                }
            }
        }
    }

    private static void SimplifyStyleSetterValues(XDocument styleXml)
    {
        foreach (var elt in styleXml.Descendants())
        {
            var eltValueNode = elt.Element(Xmlns + $"{elt.Name.LocalName}.Value");

            // ReSharper disable once UseNullPropagation
            if (eltValueNode is null)
            {
                continue;
            }

            var eltValue = eltValueNode.Elements().SingleOrDefault();
            if (eltValue?.Name is not { } name)
            {
                continue;
            }

            if (name == Xmlns + "SolidColorBrush")
            {
                elt.SetAttributeValue("Value", SimplifyHexColor(eltValue.Value));
                eltValueNode.Remove();
            }
            else if (name == Xmlns + "DynamicResource")
            {
                elt.SetAttributeValue("Value", $"{{DynamicResource {eltValue.Attribute("ResourceKey")?.Value}}}");
                eltValueNode.Remove();
            }
            else if (name == Xmlns + "StaticResource")
            {
                elt.SetAttributeValue("Value", $"{{StaticResource {eltValue.Attribute("ResourceKey")?.Value}}}");
                eltValueNode.Remove();
            }
            else if (name.Namespace == XmlnsS || name.Namespace == XmlnsC)
            {
                elt.SetAttributeValue("Value", eltValue.Value);
                eltValueNode.Remove();
            }
            else if (name == Xmlns + "Thickness")
            {
                elt.SetAttributeValue("Value", SimplifyThickness(eltValue.Value));
                eltValueNode.Remove();
            }
            else if (name == XmlnsX + "Static")
            {
                var value = eltValue.Attribute("Member")?.Value;
                value = value?.Split('.').Last();
                if (value is not null)
                {
                    elt.SetAttributeValue("Value", value);
                    eltValueNode.Remove();
                }
            }
        }
    }

    private static void SimplifyAttributeValues(XDocument styleXml)
    {
        foreach (var element in styleXml.Descendants())
        {
            foreach (var attribute in element.Attributes())
            {
                switch (attribute.Name.LocalName)
                {
                    case "Color":
                    case "BorderBrush":
                    case "Fill":
                    case "StrokeBrush":
                    case "Background":
                        attribute.Value = SimplifyHexColor(attribute.Value);
                        break;
                    case "BorderThickness":
                    case "StrokeThickness":
                    case "CornerRadius":
                        attribute.Value = SimplifyThickness(attribute.Value);
                        break;
                }
            }
        }
    }

    private static string SimplifyThickness(string s)
    {
        var four = ThicknessFourRegex().Match(s);
        if (four.Success)
        {
            return four.Groups[1].Value;
        }

        var two = ThicknessTwoRegex().Match(s);
        if (two.Success)
        {
            return $"{two.Groups[1].Value},{two.Groups[2].Value}";
        }

        return s;
    }

    private static string SimplifyHexColor(string hex)
    {
        if (hex.Length is 9 && hex.StartsWith("#FF"))
        {
            return "#" + hex[3..];
        }

        return hex;
    }

    [GeneratedRegex(@"(-?[\d+]),\1,\1,\1")]
    private static partial Regex ThicknessFourRegex();

    [GeneratedRegex(@"(-?[\d+]),(-?[\d+]),\1,\2")]
    private static partial Regex ThicknessTwoRegex();
}
